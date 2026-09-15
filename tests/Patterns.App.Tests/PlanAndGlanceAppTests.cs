using Patterns.Devices;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The day's slip on the wire, the countdown that follows the plan, and the glance line and the
/// health line that say what the last box answered.
/// </summary>
public class PlanAndGlanceAppTests
{
    private static bool WaitFor(Func<bool> done, int timeoutMs = 4000)
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

    [AvaloniaFact]
    public void ThePlanSlipsOnTheWireAndTheCountdownThatFollowsItMovesWithIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var stack = CueStacks.Caller(vm.State);
            stack.Cues.Add(new RunCueConfig { Number = "01", Name = "Walk-in", PlannedStart = "10:00" });
            stack.Cues.Add(new RunCueConfig { Number = "02", Name = "Welcome", PlannedStart = "10:30" });
            stack.Cues.Add(new RunCueConfig { Number = "03", Name = "Keynote", PlannedStart = "11:00" });
            services.CueStack.Standby(stack.Cues[0].Id);
            var router = new CommandRouter(services);

            // PLAN SHIFT +2:00: every planned start from the standby cue on moves; the words say how many.
            var shifted = TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("PLAN SHIFT +2:00")));
            Assert.StartsWith("OK", shifted);
            Assert.Equal(new[] { "10:02", "10:32", "11:02" }, stack.Cues.Select(c => c.PlannedStart));
            Assert.Contains(services.Kernel.Journal.Tail(4), e => e.Kind == "PlanShift" && e.Message.Contains("3 planned starts moved +2 min"));
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("PLAN SHIFT soon"))));

            // COUNTDOWN FOLLOW ON: the countdown's target is the standby cue's planned start, as a time of day —
            // and it moves when the plan does, and when the standby moves.
            var follow = TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("COUNTDOWN FOLLOW ON")));
            Assert.StartsWith("OK", follow);
            Assert.True(services.AirState.Countdown.FollowPlan);
            Assert.True(services.AirState.Countdown.Enabled);
            Assert.Equal(CountdownTargetKind.TimeOfDay, services.AirState.Countdown.TargetKind);
            Assert.Equal("10:02", services.AirState.Countdown.TargetTime);
            TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("PLAN SHIFT -1:00")));
            Assert.Equal("10:01", services.AirState.Countdown.TargetTime);
            services.CueStack.Standby(stack.Cues[1].Id);
            Assert.Equal("10:31", services.AirState.Countdown.TargetTime);
            Assert.Contains("\"nextAt\":\"10:31\"", services.Stage.StatusJson());

            // FOLLOW OFF: the countdown stays where it was, whatever the plan does next.
            TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("COUNTDOWN FOLLOW OFF")));
            Assert.False(services.AirState.Countdown.FollowPlan);
            TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("PLAN SHIFT +5:00")));
            Assert.Equal("10:31", services.AirState.Countdown.TargetTime);
            Assert.Equal("10:36", stack.Cues[1].PlannedStart);

            // The other two verbs answer in words.
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("PLAN CATCHUP"))));
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("PLAN RESUME"))));
            Assert.Contains(services.Kernel.Journal.Tail(4), e => e.Kind == "PlanResume");
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheGlanceLineAndTheHealthLineSayWhichBoxSaidNoUntilItAnswersAgain()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fake = new FakeDeviceLink();
            services.Devices.LinkFactory = _ => fake;
            var device = new DeviceConfig { Name = "Proj", Link = DeviceLink.Tcp, Profile = DeviceProfile.PjLink, Port = "10.0.0.7", Confirm = ConfirmLevel.Accepted, ConfirmTimeoutMs = 400, HearsShow = false };
            vm.State.Interactive.Devices.Add(device);
            vm.State.Interactive.Enabled = true;
            services.Devices.Reconcile();
            vm.PollNow();
            Assert.Equal("", services.Devices.HealthWords);
            Assert.DoesNotContain("DEVICE:", vm.Run.GlanceText);

            // The box says no: the glance line and the health line say which, and what it said.
            Assert.True(services.Devices.Send("Proj", "INPUT HDMI 1").Ok);
            fake.Say("%1INPT=ERR2");
            Assert.True(WaitFor(() => services.Devices.LastFailed is not null), "the no lands");
            Assert.StartsWith("DEVICE: Proj: INPUT HDMI 1 — rejected", services.Devices.HealthWords);
            vm.PollNow();
            vm.Run.Tick();
            Assert.Contains("DEVICE: Proj: INPUT HDMI 1 — rejected", vm.Run.GlanceText);
            Assert.True(vm.Run.HasGlance);
            Assert.Contains("DEVICE: Proj: INPUT HDMI 1 — rejected", vm.HealthText);

            // It answers yes to the next line: the clause goes.
            Assert.True(services.Devices.Send("Proj", "POWER ON").Ok);
            fake.Say("%1POWR=OK");
            Assert.True(WaitFor(() => services.Devices.LastFailed is null), "the yes clears it");
            vm.PollNow();
            vm.Run.Tick();
            Assert.DoesNotContain("DEVICE:", vm.Run.GlanceText);
            Assert.DoesNotContain("DEVICE:", vm.HealthText);

            // Outputs live: the lock's word joins the line.
            Assert.True(services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            vm.Run.Tick();
            Assert.Contains("LOCK ON", vm.Run.GlanceText);                            // the lock engages with the outputs, by itself
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheMetricsCsvStartsAgainUnderANewHeaderAndKeepsTheOldFile()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var path = Path.Combine(services.Store.BaseDirectory, "patterns.metrics.csv");
            File.WriteAllText(path, "utc,cpuAppPct,oldColumn" + Environment.NewLine + "2026-09-13T20:00:00Z,1,2" + Environment.NewLine);
            vm.State.Admin.MetricsCsv = true;
            for (var i = 0; i < MetricsHistory.AggregateEvery; i++) services.Metrics.Ingest(new MetricSample { Utc = DateTime.UtcNow, P95FrameMs = 8.1, MissedSlots = 1 });
            var lines = File.ReadAllLines(path);
            Assert.Equal(MetricsCsv.Header, lines[0]);
            Assert.EndsWith(",8.1,1,,0,,,0,,,,,0", lines[1]);                             // …p95, missed, then the switch, GO, lag, faults, memory, live-age and retiring columns unmeasured (a fed sample); no frame starved
            Assert.True(File.Exists(path + ".old"));
            Assert.StartsWith("utc,cpuAppPct,oldColumn", File.ReadAllText(path + ".old"));
        }
        finally
        {
            b.Dispose();
        }
    }
}
