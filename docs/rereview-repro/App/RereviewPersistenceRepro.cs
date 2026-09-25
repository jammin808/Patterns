using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Re-review reproducers (kept under docs/rereview-repro, outside the test projects). A PASS confirms the finding at the head under test.
/// PR-2 (the raw recovery record the field folder asks for carries the pairing token), PB-1 (the newer-schema words
/// reach no desk surface at boot), PB-3 (a desk that stood down clears the incoming desk's record at its exit),
/// PB-4 (a plain start with a fresh live record leaves the outputs closed).
/// </summary>
public class RereviewPersistenceRepro
{
    private static bool WaitFor(Func<bool> done, int timeoutMs = 5000)
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
    public void PR2_TheRawRecoveryRecordCarriesThePairingTokenInClearAndOnlyTheBundleMasksIt()
    {
        OutputTakeover.Reset();
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.State.Control.Token = "field-secret-7Q";
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            vm.IsSandboxActive = true;                                                    // EDIT SAFE, the shipped default
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            var path = Path.Combine(services.Store.BaseDirectory, "patterns.recovery.json");
            Assert.True(WaitFor(() => { TestApp.FlushFiles(services); return File.Exists(path); }), "the record is written while the outputs are live");
            var raw = File.ReadAllText(path);
            Assert.Contains("field-secret-7Q", raw);                                      // FINDING: the token in clear in the file the field README says to commit
            Assert.DoesNotContain("field-secret-7Q", SupportBundle.Redact(raw));          // the bundle's copy masks it
        }
        finally { vm_reset(b); b.Dispose(); OutputTakeover.Reset(); }

        static void vm_reset(TestApp.Booted booted) { booted.Vm.State.Control.Token = ""; Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void PB1_ANewerShowFileRunsUnsavedAndNoDeskSurfaceSaysSoAtBoot()
    {
        var b = TestApp.Boot(prepare: dir =>
        {
            var newer = new ShowState { Name = "From tomorrow", SchemaVersion = ShowState.CurrentSchemaVersion + 1 };
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(newer));
        });
        try
        {
            var (services, vm, _) = b;
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Kernel.NewerSchema > 0);
            Assert.False(services.Persistence.Autosave);                                   // saving is off, as designed
            Assert.DoesNotContain("newer Patterns", vm.StatusMessage);                     // FINDING: not on the status line
            Assert.DoesNotContain("newer Patterns", HealthMonitor.WatchdogNote);           // FINDING: not on the health line
            var rows = SuperCheck.Run(services.Metrics.GatherFacts()).Rows;
            Assert.DoesNotContain(rows, r => (r.Value + " " + r.Note).Contains("newer", StringComparison.OrdinalIgnoreCase));   // FINDING: no Super Check row
            Assert.DoesNotContain("newer", new CommandRouter(services).StateJson(), StringComparison.OrdinalIgnoreCase);         // FINDING: STATE does not carry it
        }
        finally { HealthMonitor.WatchdogNote = ""; b.Dispose(); }
    }

    [AvaloniaFact]
    public void PB3_ADeskThatStoodDownClearsTheIncomingDesksRecoveryRecordAtItsExit()
    {
        OutputTakeover.Reset();
        var b = TestApp.Boot();
        try
        {
            var services = b.Services;
            Assert.True(services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            new OutputOwnerStore(b.Dir).Ask(new HandoverRequest(999, DateTime.UtcNow));    // another Patterns takes the screens
            b.Vm.PollNow();
            Assert.True(WaitFor(() => !services.Outputs.IsLive), "the desk stands down");
            TestApp.FlushFiles(services);
            services.Recovery.Write(live: true, audioPlaying: false, airLook: "Incoming"); // the incoming desk's record on the shared folder
            Assert.Equal("Incoming", services.Recovery.Read()?.AirLook);
            services.Shutdown();                                                           // the operator closes the old window
            Assert.Null(services.Recovery.Read());                                         // FINDING: the live desk's record (and .bak) is gone
        }
        finally { HealthMonitor.WatchdogNote = ""; b.Dispose(); OutputTakeover.Reset(); }
    }

    [AvaloniaFact]
    public void PB4_APlainStartWithAFreshLiveRecordLeavesTheOutputsClosed()
    {
        OutputTakeover.Reset();
        var b = TestApp.Boot(prepare: dir => new RecoveryStore(dir).Write(live: true, audioPlaying: false, airLook: "Before the power cut"));
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(b.Services.PendingRecovery);                                    // the record was there, fresh
            Assert.True(RecoveryStore.IsFresh(b.Services.PendingRecovery!, DateTime.UtcNow));
            Assert.False(WaitFor(() => b.Services.Outputs.IsLive, 1500));                  // FINDING: nothing puts the air back (QUALIFICATION §24/§25 expect it)
        }
        finally { b.Dispose(); OutputTakeover.Reset(); }
    }
}
