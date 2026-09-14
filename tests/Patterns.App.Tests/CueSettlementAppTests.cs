using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A cue's device lines settle the row that fired them and no other: an accepted receipt makes
/// the row Done, a rejection, silence or an observation that disagrees makes it FailedLate with
/// the box's own words, a datagram is Done at once because nothing comes back, two cues in
/// flight keep their own rows, and a delayed step's line reopens its cue's row until its receipt.
/// Nothing is re-fired.
/// </summary>
public class CueSettlementAppTests
{
    private static bool WaitFor(Func<bool> done, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return true;
            Thread.Sleep(15);
        }
        Dispatcher.UIThread.RunJobs();
        return done();
    }

    /// <summary>A box on the Interactive page with a fake link behind it; several boxes share one factory keyed by name.</summary>
    private static (DeviceConfig Device, FakeDeviceLink Fake) Box(TestApp.Booted b, Dictionary<string, FakeDeviceLink> fakes, string name, ConfirmLevel confirm, int timeoutMs = 400, DeviceLink link = DeviceLink.Tcp)
    {
        var fake = new FakeDeviceLink();
        fakes[name] = fake;
        b.Services.Devices.LinkFactory = d => fakes.TryGetValue(d.Name, out var f) ? f : new FakeDeviceLink();
        var device = new DeviceConfig { Name = name, Link = link, Profile = link == DeviceLink.Udp ? DeviceProfile.Osc : DeviceProfile.PjLink, Port = "10.0.0.7", NetPort = link == DeviceLink.Udp ? 9000 : 4352, Confirm = confirm, ConfirmTimeoutMs = timeoutMs, HearsShow = false };
        b.Vm.State.Interactive.Devices.Add(device);
        b.Vm.State.Interactive.Enabled = true;
        b.Services.Devices.Reconcile();
        return (device, fake);
    }

    private static RunCueConfig Cue(TestApp.Booted b, string number, string name, params (string Device, string Line, double Delay)[] steps)
    {
        var cue = new RunCueConfig { Number = number, Name = name };
        foreach (var (device, line, delay) in steps)
        {
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.DeviceSend, Target = device, Value = line, DelaySeconds = delay });
        }
        CueStacks.Caller(b.Vm.State).Cues.Add(cue);
        return cue;
    }

    private static ActionResult Go(TestApp.Booted b)
    {
        var stack = b.Services.CueStack;
        if (!stack.Armed) stack.SetArmed(true, ActionOrigin.Desk);
        var result = stack.Go(ActionOrigin.Desk);
        Dispatcher.UIThread.RunJobs();
        return result;
    }

    private static CueExecutionRecord Row(TestApp.Booted b, string number) => b.Services.CueStack.History.First(r => r.Number == number);

    [AvaloniaFact]
    public void AnAcceptedReceiptSettlesTheCueThatSentTheLineAsDone()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = new Dictionary<string, FakeDeviceLink>();
            var (_, fake) = Box(b, fakes, "Proj", ConfirmLevel.Accepted);
            Cue(b, "01.010", "Wall to main", ("Proj", "INPUT HDMI 1", 0));

            var fired = Go(b);
            Assert.True(fired.Ok, fired.Message);
            Assert.Equal(ActionStatus.Requested, fired.Status);                          // dispatched, awaiting the box: never Done before the receipt
            Assert.Contains("1 receipt awaited", fired.Message);
            var row = Row(b, "01.010");
            Assert.Equal(CueOutcome.Requested, row.Outcome);
            Assert.Equal(1, row.Pending);
            Assert.Equal(8, row.ExecutionId.Length);
            Assert.Equal("Awaiting 1 receipt", row.OutcomeWords);
            Assert.False(row.Settling);

            // The box says yes: the row is Done, the journal says the cue settled, the sidecar carries it.
            fake.Say("%1INPT=OK");
            Assert.True(WaitFor(() => Row(b, "01.010").Outcome == CueOutcome.Done), Row(b, "01.010").OutcomeWords);
            row = Row(b, "01.010");
            Assert.Equal(0, row.Pending);
            Assert.Equal("Done", row.OutcomeWords);
            Assert.Contains(services.Journal.Tail(6), e => e.Kind == "CueSettled" && e.Outcome == "Done" && e.Message.Contains("accepted (INPT: OK)"));
            var sidecar = new RecoveryStore(b.Dir).Read();
            Assert.Equal(CueOutcome.Done, sidecar!.Run!.History[0].Outcome);
            Assert.Equal(row.ExecutionId, sidecar.Run.History[0].ExecutionId);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ARejectionMakesTheCueFailedLateWithTheBoxsWordsAndNothingIsReFired()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = new Dictionary<string, FakeDeviceLink>();
            var (_, fake) = Box(b, fakes, "Proj", ConfirmLevel.Accepted);
            Cue(b, "01.010", "Wall to main", ("Proj", "INPUT HDMI 1", 0));
            Go(b);
            var written = fake.Written.Count;

            fake.Say("%1INPT=ERR2");
            Assert.True(WaitFor(() => Row(b, "01.010").Outcome == CueOutcome.FailedLate), Row(b, "01.010").OutcomeWords);
            var row = Row(b, "01.010");
            Assert.Equal("Failed late", row.OutcomeWords);
            Assert.True(row.IsFailure);
            Assert.Contains("later: Proj: INPUT HDMI 1 — rejected", row.Detail);
            Assert.Equal(0, row.Pending);
            Assert.Contains(services.Journal.Tail(6), e => e.Kind == "CueSettled" && e.Outcome == "FailedLate");
            Assert.Equal(written, fake.Written.Count);                                      // no retry
            // The poll leaves a settled row alone.
            services.CueStack.Poll(DateTime.UtcNow.AddSeconds(30));
            Assert.Equal(CueOutcome.FailedLate, Row(b, "01.010").Outcome);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void SilenceMakesTheCueFailedLateWhenTheBoxsTimeoutRunsOutAndTheClockNeverSettlesItFirst()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = new Dictionary<string, FakeDeviceLink>();
            Box(b, fakes, "Proj", ConfirmLevel.Accepted, timeoutMs: 600);
            Cue(b, "01.010", "Wall to main", ("Proj", "INPUT HDMI 1", 0));
            Go(b);
            // The settle window would flip a Requested row to Done: not while a box owes it an answer.
            services.CueStack.Poll(DateTime.UtcNow.AddSeconds(30));
            Assert.Equal(CueOutcome.Requested, Row(b, "01.010").Outcome);
            Assert.True(WaitFor(() => Row(b, "01.010").Outcome == CueOutcome.FailedLate, 3000), Row(b, "01.010").OutcomeWords);
            Assert.Contains("no answer in 0.6 s", Row(b, "01.010").Detail);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AnObservationThatDisagreesMakesTheCueFailedLate()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = new Dictionary<string, FakeDeviceLink>();
            var (device, fake) = Box(b, fakes, "Proj", ConfirmLevel.Observed);
            device.ObserveQuery = "INPUT ?";
            device.ObserveExpect = "31";
            Cue(b, "01.010", "Wall to main", ("Proj", "INPUT HDMI 1", 0));
            Go(b);
            Assert.Equal(CueOutcome.Requested, Row(b, "01.010").Outcome);
            fake.Say("%1INPT=OK");                                                          // accepted: the question goes out
            Assert.True(WaitFor(() => fake.Written.Contains("%1INPT ?\r")), string.Join("|", fake.Written));
            Assert.Equal(CueOutcome.Requested, Row(b, "01.010").Outcome);                     // accepted is not observed: still waiting
            fake.Say("%1INPT=32");                                                          // the box is on the wrong input
            Assert.True(WaitFor(() => Row(b, "01.010").Outcome == CueOutcome.FailedLate, 3000), Row(b, "01.010").OutcomeWords);
            Assert.Contains("(accepted, not observed)", Row(b, "01.010").Detail);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ADatagramIsDoneAtOnceBecauseNothingComesBack()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = new Dictionary<string, FakeDeviceLink>();
            Box(b, fakes, "Lights", ConfirmLevel.Accepted, link: DeviceLink.Udp);
            Cue(b, "01.010", "Wash up", ("Lights", "/wall/main 1", 0));
            var fired = Go(b);
            Assert.True(fired.Ok, fired.Message);
            Assert.Equal(ActionStatus.Done, fired.Status);
            var row = Row(b, "01.010");
            Assert.Equal(CueOutcome.Done, row.Outcome);
            Assert.Equal(0, row.Pending);
            Assert.True(WaitFor(() => Row(b, "01.010").Outcome == CueOutcome.Done, 500));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TwoCuesInFlightSettleTheirOwnRowsAndNeverEachOthers()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = new Dictionary<string, FakeDeviceLink>();
            var (_, proj) = Box(b, fakes, "Proj", ConfirmLevel.Accepted, timeoutMs: 3000);
            var (_, matrix) = Box(b, fakes, "Matrix", ConfirmLevel.Accepted, timeoutMs: 3000);
            Cue(b, "01.010", "Wall to main", ("Proj", "INPUT HDMI 1", 0));
            Cue(b, "01.020", "Route the foyer", ("Matrix", "INPUT HDMI 2", 0));
            Go(b);
            Thread.Sleep(GoGate.Lockout + TimeSpan.FromMilliseconds(50));                    // the gate's lockout after an accepted GO
            Go(b);
            Assert.Equal(CueOutcome.Requested, Row(b, "01.010").Outcome);
            Assert.Equal(CueOutcome.Requested, Row(b, "01.020").Outcome);
            Assert.NotEqual(Row(b, "01.010").ExecutionId, Row(b, "01.020").ExecutionId);

            proj.Say("%1INPT=ERR2");                                                        // the projector says no to the first cue
            Assert.True(WaitFor(() => Row(b, "01.010").Outcome == CueOutcome.FailedLate), Row(b, "01.010").OutcomeWords);
            Assert.Equal(CueOutcome.Requested, Row(b, "01.020").Outcome);                     // the second still waits on its own box
            Assert.Equal(1, Row(b, "01.020").Pending);

            matrix.Say("%1INPT=OK");                                                        // the matrix says yes to the second
            Assert.True(WaitFor(() => Row(b, "01.020").Outcome == CueOutcome.Done), Row(b, "01.020").OutcomeWords);
            Assert.Equal(CueOutcome.FailedLate, Row(b, "01.010").Outcome);                    // and the first stays as the projector left it
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ADelayedDeviceStepReopensItsCuesRowAndItsReceiptSettlesIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = new Dictionary<string, FakeDeviceLink>();
            var (_, fake) = Box(b, fakes, "Proj", ConfirmLevel.Accepted, timeoutMs: 3000);
            var cue = Cue(b, "01.010", "Wall to main, then the projector", ("Proj", "INPUT HDMI 1", 4));
            cue.Actions.Insert(0, new CueActionConfig { Kind = ShowActionKind.ClockOn });
            var fired = Go(b);
            Assert.True(fired.Ok, fired.Message);
            Assert.Equal(ActionStatus.Done, fired.Status);                                   // the immediate part is done; the line is still to come
            var row = Row(b, "01.010");
            Assert.Equal(CueOutcome.Done, row.Outcome);
            Assert.Equal(0, row.Pending);
            Assert.Equal(1, services.Tail.Count);

            // The tail on a held clock: the same runner, the cue's execution carried on the step.
            var now = 100.0;
            using var tail = new CueTail(() => now) { Run = services.Tail.Run };
            var plan = CueSteps.Plan(cue.Actions).Where(p => !p.IsImmediate).ToList();
            services.Tail.DropAll();
            tail.Schedule(CueStacks.Caller(vm.State).Id, cue, "01.010 Wall to main, then the projector", plan, now, row.ExecutionId);
            now = 104;
            tail.Poll();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("%1INPT 31\r", fake.Written);
            row = Row(b, "01.010");
            Assert.Equal(CueOutcome.Requested, row.Outcome);                                  // reopened: a receipt awaited
            Assert.Equal(1, row.Pending);

            fake.Say("%1INPT=OK");
            Assert.True(WaitFor(() => Row(b, "01.010").Outcome == CueOutcome.Done), Row(b, "01.010").OutcomeWords);
            Assert.Equal(0, Row(b, "01.010").Pending);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AReceiptForALineNoCueSentSettlesNothing()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = new Dictionary<string, FakeDeviceLink>();
            var (_, fake) = Box(b, fakes, "Proj", ConfirmLevel.Accepted, timeoutMs: 3000);
            Cue(b, "01.010", "Wall to main", ("Proj", "INPUT HDMI 1", 0));
            Go(b);
            var receipts = new List<DeviceReceipt>();
            services.Devices.Receipt += r => receipts.Add(r);
            // The page's SEND — no cue in hand — and its rejection: the cue's row is untouched.
            var sent = services.Devices.Send("Proj", "POWER OFF");
            Assert.Equal(ActionStatus.Requested, sent.Status);
            fake.Say("%1POWR=ERR2");
            Assert.True(WaitFor(() => receipts.Count == 1), "the page's line answered");
            Assert.Equal("", receipts[0].Execution);
            Assert.Equal(CueOutcome.Requested, Row(b, "01.010").Outcome);
            Assert.Equal(1, Row(b, "01.010").Pending);
            fake.Say("%1INPT=OK");
            Assert.True(WaitFor(() => Row(b, "01.010").Outcome == CueOutcome.Done), Row(b, "01.010").OutcomeWords);
            Assert.Equal(Row(b, "01.010").ExecutionId, receipts[1].Execution);
        }
        finally
        {
            b.Dispose();
        }
    }
}
