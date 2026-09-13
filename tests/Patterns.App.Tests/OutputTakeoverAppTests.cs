using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 20: the render windows a crash or a hang leaves playing get re-owned. On a real desk:
/// the ownership record written and beaten while the outputs are live and gone when they close,
/// a start that finds a live owner asking it for the screens, a start that finds a hung one
/// ending it, and a desk standing down when a newer one asks.
/// </summary>
public class OutputTakeoverAppTests
{
    /// <summary>A process tree the test writes: which ids exist, when they started, and who was ended.</summary>
    private sealed class FakeProbe : IProcessProbe
    {
        public readonly Dictionary<int, long> Alive = new();
        public readonly Dictionary<int, string> Paths = new();
        public readonly List<int> Killed = new();
        public readonly HashSet<int> Unreadable = new();    // up, and not this process's to read
        public bool KillWorks = true;

        public long? StartTicks(int pid) => Alive.TryGetValue(pid, out var t) ? t : null;

        public string ExePath(int pid) => Paths.TryGetValue(pid, out var p) ? p : "";

        public ProcessSight Look(int pid)
            => Unreadable.Contains(pid) ? ProcessSight.Unreadable()
                : Alive.TryGetValue(pid, out var t) ? ProcessSight.Alive(t, ExePath(pid))
                : ProcessSight.Gone;

        public bool Kill(int pid)
        {
            if (!KillWorks) return false;
            Killed.Add(pid);
            Alive.Remove(pid);
            return true;
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-takeover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The ownership record is kept on a worker (a slow disk must never cost the desk its second): pump and wait for it.</summary>
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

    private static OutputOwner Record(int pid, long started, DateTime beatUtc, params string[] targets)
        => new(pid, started, beatUtc, beatUtc, targets.Length == 0 ? new[] { "Main wall" } : targets,
            Environment.MachineName, Environment.ProcessPath ?? "Patterns");

    [Fact]
    public void AStartWithNoRecordDoesNothingAtAll()
    {
        var dir = TempDir();
        try
        {
            var result = OutputTakeover.ClaimAtStart(dir, enabled: true, new FakeProbe());
            Assert.Equal(OutputClaim.Free, result.Claim);
            Assert.False(result.TookOver);
            Assert.Equal("", result.Words);
            Assert.False(File.Exists(Path.Combine(dir, "patterns.handover.json")));
        }
        finally
        {
            OutputTakeover.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ARecordFromAProcessThatDiedIsSweptUpAndNothingIsTaken()
    {
        var dir = TempDir();
        try
        {
            new OutputOwnerStore(dir).Write(Record(4242, 1000, DateTime.UtcNow));
            var result = OutputTakeover.ClaimAtStart(dir, enabled: true, new FakeProbe()); // nothing alive
            Assert.Equal(OutputClaim.Stale, result.Claim);
            Assert.False(result.TookOver);
            Assert.True(new OutputOwnerStore(dir).Read().IsMissing);   // the litter is gone
        }
        finally
        {
            OutputTakeover.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AHungRunIsAskedFirstAndThenEndedAndTheScreensAreThisDesks()
    {
        var dir = TempDir();
        try
        {
            var store = new OutputOwnerStore(dir);
            // Up, its windows playing, but its desk stopped beating long ago.
            var silent = DateTime.UtcNow - OutputOwnership.HeartbeatSilent - TimeSpan.FromSeconds(5);
            store.Write(Record(4242, 1000, silent, "Main wall", "Foyer"));
            var probe = new FakeProbe();
            probe.Alive[4242] = 1000;
            probe.Paths[4242] = Environment.ProcessPath ?? "Patterns";

            var result = OutputTakeover.ClaimAtStart(dir, enabled: true, probe, wait: _ => { });

            Assert.Equal(OutputClaim.HeldByHungDesk, result.Claim);
            Assert.True(result.TookOver);
            Assert.True(result.EndedOwner);
            Assert.Equal(new[] { 4242 }, probe.Killed);
            Assert.Contains("2 screens (Main wall, Foyer)", result.Words);
            // Both sidecars are cleared: this desk owns nothing until its own outputs open.
            Assert.True(store.Read().IsMissing);
            Assert.True(store.ReadRequest().IsMissing);
        }
        finally
        {
            OutputTakeover.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AHungRunThatCannotBeReadIsNeverEndedAndTheScreensAreNotTaken()
    {
        var dir = TempDir();
        try
        {
            var store = new OutputOwnerStore(dir);
            // Up, silent — and not this process's to read: another user's session, or elevated.
            var silent = DateTime.UtcNow - OutputOwnership.HeartbeatSilent - TimeSpan.FromSeconds(5);
            store.Write(Record(4242, 1000, silent, "Main wall"));
            var probe = new FakeProbe();
            probe.Unreadable.Add(4242);

            var result = OutputTakeover.ClaimAtStart(dir, enabled: true, probe, wait: _ => { });

            // A fence, not an absence: asked, not answered, not ended, not taken — and said so.
            Assert.Equal(OutputClaim.HeldByHungDesk, result.Claim);
            Assert.False(result.TookOver);
            Assert.False(result.EndedOwner);
            Assert.Empty(probe.Killed);
            Assert.Contains("(pid 4242) and cannot be read from here", result.Words);
            Assert.Contains("close it by hand, then OUTPUTS ON here", result.Words);
            // The record stays for the next start to read; the ask, which was ours, is cleared.
            Assert.True(store.Read().IsValid);
            Assert.True(store.ReadRequest().IsMissing);
        }
        finally
        {
            OutputTakeover.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ARunThatLetsGoOnTheAskIsNeverEnded()
    {
        var dir = TempDir();
        try
        {
            var store = new OutputOwnerStore(dir);
            store.Write(Record(4242, 1000, DateTime.UtcNow));   // beating: a second desk, not a ghost
            var probe = new FakeProbe();
            probe.Alive[4242] = 1000;
            probe.Paths[4242] = Environment.ProcessPath ?? "Patterns";

            // It answers the ask on the first look, the way a living desk's poll does.
            var looks = 0;
            var result = OutputTakeover.ClaimAtStart(dir, enabled: true, probe, wait: _ =>
            {
                if (++looks == 1) store.Clear();
            });

            Assert.Equal(OutputClaim.HeldByLiveDesk, result.Claim);
            Assert.True(result.TookOver);
            Assert.False(result.EndedOwner);
            Assert.Empty(probe.Killed);
            Assert.Contains("stood down", result.Words);
            Assert.True(store.ReadRequest().IsMissing);
        }
        finally
        {
            OutputTakeover.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NothingIsEverEndedThatIsNotPatterns()
    {
        var dir = TempDir();
        try
        {
            var store = new OutputOwnerStore(dir);
            var silent = DateTime.UtcNow - OutputOwnership.HeartbeatSilent - TimeSpan.FromSeconds(5);
            store.Write(new OutputOwner(4242, 1000, silent, silent, new[] { "Main wall" },
                Environment.MachineName, @"C:\Windows\explorer.exe"));
            var probe = new FakeProbe();
            probe.Alive[4242] = 1000;
            probe.Paths[4242] = @"C:\Windows\explorer.exe";

            var result = OutputTakeover.ClaimAtStart(dir, enabled: true, probe, wait: _ => { });

            Assert.Empty(probe.Killed);
            Assert.False(result.EndedOwner);
        }
        finally
        {
            OutputTakeover.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TurnedOffTheScreensAreLeftWhereTheyAreAndSaidSo()
    {
        var dir = TempDir();
        try
        {
            var store = new OutputOwnerStore(dir);
            var silent = DateTime.UtcNow - OutputOwnership.HeartbeatSilent - TimeSpan.FromSeconds(5);
            store.Write(Record(4242, 1000, silent));
            var probe = new FakeProbe();
            probe.Alive[4242] = 1000;
            probe.Paths[4242] = Environment.ProcessPath ?? "Patterns";

            var result = OutputTakeover.ClaimAtStart(dir, enabled: false, probe, wait: _ => { });

            Assert.False(result.TookOver);
            Assert.Empty(probe.Killed);
            Assert.Contains("Machine → Watchdog", result.Words);
            Assert.True(store.Read().IsValid);        // left exactly as it was
            Assert.True(store.ReadRequest().IsMissing);    // and never asked for
        }
        finally
        {
            OutputTakeover.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void TheDeskWritesTheRecordWhileItsOutputsAreLiveAndClearsItWhenTheyClose()
    {
        OutputTakeover.Reset();
        var b = TestApp.Boot();
        try
        {
            var services = b.Services;
            var store = new OutputOwnerStore(b.Dir);
            Assert.True(store.Read().IsMissing);   // nothing open: nobody's screens

            services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Outputs.IsLive);

            Assert.True(WaitFor(() => store.Read().IsValid), "the record is written when the outputs open");
            var owner = store.Read().Value;
            Assert.NotNull(owner);
            Assert.Equal(Environment.ProcessId, owner!.Pid);
            Assert.Equal(Environment.MachineName, owner.Machine);
            Assert.NotEmpty(owner.Targets);
            Assert.True(services.Ownership.Held);

            // The beat moves on with the desk's own poll — the silence of a hung one is the signal.
            var first = store.Read().Value!.HeartbeatUtc;
            Thread.Sleep(OutputOwnership.HeartbeatEvery + TimeSpan.FromMilliseconds(50));
            b.Vm.PollNow();
            Assert.True(WaitFor(() => store.Read().Value is { } beat && beat.HeartbeatUtc > first), "the poll beats the record");

            services.Actions.Execute(ShowActionKind.OutputsOff, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();

            Assert.True(WaitFor(() => store.Read().IsMissing && !services.Ownership.Held), "the record goes with the windows");
        }
        finally
        {
            b.Dispose();
            OutputTakeover.Reset();
        }
    }

    [AvaloniaFact]
    public void ADeskAskedForTheScreensStandsDownAndSaysSo()
    {
        OutputTakeover.Reset();
        var b = TestApp.Boot();
        try
        {
            var services = b.Services;
            var store = new OutputOwnerStore(b.Dir);
            var told = "";
            services.Ownership.StoodDown += words => told = words;

            // Another Patterns starting asks for the screens; the desk's own poll answers it.
            store.Ask(new HandoverRequest(999, DateTime.UtcNow));
            b.Vm.PollNow();
            Assert.True(WaitFor(() => told.Length > 0), "the desk answers the ask");

            Assert.Contains("999", told);
            Assert.False(services.Outputs.IsLive);
            Assert.True(store.Read().IsMissing);
            Assert.True(store.ReadRequest().IsMissing);   // answered once, never again
            Assert.Contains("999", HealthMonitor.WatchdogNote);
        }
        finally
        {
            HealthMonitor.WatchdogNote = "";
            b.Dispose();
            OutputTakeover.Reset();
        }
    }

    [AvaloniaFact]
    public void ADeskNeverStandsDownOnItsOwnAskNorOnAStaleOne()
    {
        OutputTakeover.Reset();
        var b = TestApp.Boot();
        try
        {
            var store = new OutputOwnerStore(b.Dir);
            var told = 0;
            b.Services.Ownership.StoodDown += _ => told++;

            store.Ask(new HandoverRequest(Environment.ProcessId, DateTime.UtcNow));
            b.Vm.PollNow();
            Assert.False(WaitFor(() => told > 0, 600));
            Assert.True(store.ReadRequest().IsValid);   // its own ask is left for it to clear at the end

            store.Ask(new HandoverRequest(999, DateTime.UtcNow - OutputTakeover.RequestFresh - TimeSpan.FromSeconds(5)));
            b.Vm.PollNow();
            Assert.False(WaitFor(() => told > 0, 600));
        }
        finally
        {
            b.Dispose();
            OutputTakeover.Reset();
        }
    }

    [AvaloniaFact]
    public void ATakeoverPutsTheShowBackEvenWithAutoRestoreOff()
    {
        OutputTakeover.Reset();
        var probe = new FakeProbe();
        var b = TestApp.Boot("patterns-takeover-", dir =>
        {
            // The operator has asked the watchdog not to put the show back by itself — a choice
            // about a watchdog's own restart, which must not leave a dark room behind a takeover.
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"),
                "{ \"Watchdog\": { \"AutoRestore\": false } }");
            var silent = DateTime.UtcNow - OutputOwnership.HeartbeatSilent - TimeSpan.FromSeconds(5);
            new OutputOwnerStore(dir).Write(Record(4242, 1000, silent, "Main wall"));
            probe.Alive[4242] = 1000;
            probe.Paths[4242] = Environment.ProcessPath ?? "Patterns";
            OutputTakeover.ClaimAtStart(dir, enabled: true, probe, wait: _ => { });
        });
        try
        {
            Assert.False(b.Services.State.Watchdog.AutoRestore);
            Assert.True(b.Services.Takeover.TookOver);
            Assert.Equal(new[] { 4242 }, probe.Killed);

            b.Services.TryRecover(b.Vm);
            Dispatcher.UIThread.RunJobs();

            Assert.True(b.Services.Outputs.IsLive, "the screens we took come straight back on");
            Assert.Contains("Main wall", b.Vm.StatusMessage);
        }
        finally
        {
            HealthMonitor.WatchdogNote = "";
            b.Dispose();
            OutputTakeover.Reset();
        }
    }

    /// <summary>Real files on a temp folder with faults thrown in: the night's file system, chosen per test.</summary>
    private sealed class FaultyFiles : ISidecarFiles
    {
        public bool WriteThrows;
        public bool ReadThrows;
        public string? DeleteThrowsFor;
        public int Writes;

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public bool Exists(string path) => File.Exists(path);

        public string ReadAllText(string path)
        {
            if (ReadThrows) throw new IOException("The process cannot access the file because it is being used by another process.");
            return File.ReadAllText(path);
        }

        public void WriteAllText(string path, string text)
        {
            Writes++;
            if (WriteThrows) throw new UnauthorizedAccessException("Access to the path is denied.");
            File.WriteAllText(path, text);
        }

        public void Move(string from, string to) => File.Move(from, to, overwrite: true);

        public void Delete(string path)
        {
            if (DeleteThrowsFor == path) throw new IOException("The file is locked.");
            File.Delete(path);
        }
    }

    [Fact]
    public void AnUnreadableRecordIsAFenceNothingIsAskedEndedOrOpenedAndTheWordsSayOutputsOn()
    {
        var dir = TempDir();
        try
        {
            var store = new OutputOwnerStore(dir);
            var silent = DateTime.UtcNow - OutputOwnership.HeartbeatSilent - TimeSpan.FromSeconds(5);
            store.Write(Record(4242, 1000, silent, "Main wall"));
            var probe = new FakeProbe();
            probe.Alive[4242] = 1000;
            probe.Paths[4242] = Environment.ProcessPath ?? "Patterns";
            var files = new FaultyFiles { ReadThrows = true };     // the record is locked by something else

            var result = OutputTakeover.ClaimAtStart(dir, enabled: true, probe, wait: _ => { }, files: files);

            Assert.Equal(OutputClaim.Unknown, result.Claim);
            Assert.True(result.Uncertain);
            Assert.False(result.TookOver);
            Assert.False(result.EndedOwner);
            Assert.Empty(probe.Killed);                                  // a desk that may be playing is not ended
            Assert.Equal(0, files.Writes);                               // and not even asked
            Assert.Contains("could not be read", result.Words);
            Assert.Contains("another process", result.Words);
            Assert.Contains("OUTPUTS ON", result.Words);
            Assert.False(File.Exists(Path.Combine(dir, "patterns.handover.json")));

            // Junk on disk reads the same way, with real files.
            File.WriteAllText(store.OwnerPath, "{ torn");
            var junk = OutputTakeover.ClaimAtStart(dir, enabled: true, probe, wait: _ => { });
            Assert.Equal(OutputClaim.Unknown, junk.Claim);
            Assert.Empty(probe.Killed);
        }
        finally
        {
            OutputTakeover.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AHungRunWhoseAskNeverReachedTheDiskIsNeverEnded()
    {
        var dir = TempDir();
        try
        {
            var store = new OutputOwnerStore(dir);
            var silent = DateTime.UtcNow - OutputOwnership.HeartbeatSilent - TimeSpan.FromSeconds(5);
            store.Write(Record(4242, 1000, silent, "Main wall"));
            var probe = new FakeProbe();
            probe.Alive[4242] = 1000;
            probe.Paths[4242] = Environment.ProcessPath ?? "Patterns";
            var files = new FaultyFiles { WriteThrows = true };    // the folder will not take the ask

            var result = OutputTakeover.ClaimAtStart(dir, enabled: true, probe, wait: _ => { }, files: files);

            // Asked, it might have stood down; unasked, its silence means nothing — so it is not ended.
            Assert.Equal(OutputClaim.HeldByHungDesk, result.Claim);
            Assert.False(result.TookOver);
            Assert.False(result.EndedOwner);
            Assert.Empty(probe.Killed);
            Assert.Contains("could not be asked for them", result.Words);
            Assert.Contains("denied", result.Words);
            Assert.Contains("close it by hand, then OUTPUTS ON here", result.Words);
            Assert.True(store.Read().IsValid);                           // the record stays for the next start
        }
        finally
        {
            OutputTakeover.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ARecordThatCannotBeReadWhileWaitingIsNotTakenForALetGo()
    {
        var dir = TempDir();
        try
        {
            var store = new OutputOwnerStore(dir);
            var silent = DateTime.UtcNow - OutputOwnership.HeartbeatSilent - TimeSpan.FromSeconds(5);
            store.Write(Record(4242, 1000, silent, "Main wall"));
            var probe = new FakeProbe();
            probe.Alive[4242] = 1000;
            probe.Paths[4242] = Environment.ProcessPath ?? "Patterns";
            var files = new FaultyFiles();
            var looks = 0;
            var result = OutputTakeover.ClaimAtStart(dir, enabled: true, probe, wait: _ =>
            {
                // The first look reads; every look after it finds the file locked.
                if (++looks == 1) files.ReadThrows = true;
            }, files: files);

            Assert.False(result.TookOver);
            Assert.Empty(probe.Killed);
            Assert.Contains("could not be read while waiting", result.Words);
        }
        finally
        {
            OutputTakeover.Reset();
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void ADeskWhoseRecordCannotBeKeptSaysSoHoldsNothingAndRecoversWhenTheFolderTakesWritesAgain()
    {
        OutputTakeover.Reset();
        var files = new FaultyFiles { WriteThrows = true };
        OutputOwnershipService.SidecarFiles = () => files;
        var b = TestApp.Boot();
        try
        {
            var services = b.Services;
            var trouble = new List<string>();
            services.Ownership.TroubleChanged += words => trouble.Add(words);
            services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Outputs.IsLive);

            Assert.True(WaitFor(() => services.Ownership.Trouble.Length > 0), "the failed write is said");
            Assert.False(services.Ownership.Held);                       // the windows are ours; the folder does not say so
            Assert.Contains("could not be written", services.Ownership.Trouble);
            Assert.Contains("denied", services.Ownership.Trouble);
            Assert.True(WaitFor(() => trouble.Count == 1), "said once");
            b.Vm.PollNow();
            Assert.Contains("could not be written", b.Vm.ScreensOwnedText);
            Assert.Contains("could not be written", HealthMonitor.WatchdogNote);

            // The folder takes writes again: the next beat commits, the trouble clears, said once more.
            files.WriteThrows = false;
            Thread.Sleep(OutputOwnership.HeartbeatEvery + TimeSpan.FromMilliseconds(50));
            b.Vm.PollNow();
            Assert.True(WaitFor(() => services.Ownership.Held), "the record lands once the folder takes it");
            Assert.Equal("", services.Ownership.Trouble);
            Assert.True(WaitFor(() => trouble.Count == 2 && trouble[1] == ""), "the clearing is said");
            Assert.True(new OutputOwnerStore(b.Dir).Read().IsValid);
        }
        finally
        {
            OutputOwnershipService.SidecarFiles = null;
            HealthMonitor.WatchdogNote = "";
            b.Dispose();
            OutputTakeover.Reset();
        }
    }

    [AvaloniaFact]
    public void ACorruptAskIsNotObeyedAndAnAskWhoseClearFailsIsAnsweredOnce()
    {
        OutputTakeover.Reset();
        var files = new FaultyFiles();
        OutputOwnershipService.SidecarFiles = () => files;
        var b = TestApp.Boot();
        try
        {
            var services = b.Services;
            var store = new OutputOwnerStore(b.Dir);
            var told = 0;
            services.Ownership.StoodDown += _ => told++;

            File.WriteAllText(store.RequestPath, "{ torn");           // junk where an ask would be
            b.Vm.PollNow();
            Assert.False(WaitFor(() => told > 0, 600));
            Assert.True(File.Exists(store.RequestPath));               // not an ask: left alone, not cleared, not obeyed

            // A real ask whose clear then fails: answered once, and not again next second.
            files.DeleteThrowsFor = store.RequestPath;
            store.Ask(new HandoverRequest(999, DateTime.UtcNow));
            b.Vm.PollNow();
            Assert.True(WaitFor(() => told == 1), "the ask is answered");
            Assert.True(File.Exists(store.RequestPath));               // the clear failed, so it is still there
            b.Vm.PollNow();
            b.Vm.PollNow();
            Assert.False(WaitFor(() => told > 1, 600));                // and not answered twice
        }
        finally
        {
            OutputOwnershipService.SidecarFiles = null;
            HealthMonitor.WatchdogNote = "";
            b.Dispose();
            OutputTakeover.Reset();
        }
    }

    [AvaloniaFact]
    public void ARestartThatCannotReadTheRecordPutsNothingBackByItselfAndOutputsOnStillWorks()
    {
        OutputTakeover.Reset();
        var b = TestApp.Boot("patterns-takeover-", dir =>
        {
            var air = new ShowState();
            air.Pattern.Kind = PatternKind.LedWall;
            new RecoveryStore(dir).Write(new RecoverySnapshot(true, false, DateTime.UtcNow, Air: air, Sandboxed: true));
            File.WriteAllText(Path.Combine(dir, "patterns.outputs.json"), "{ torn");
            OutputTakeover.ClaimAtStart(dir, enabled: true, new FakeProbe(), wait: _ => { });
        });
        try
        {
            Assert.True(b.Services.Takeover.Uncertain);
            Assert.Contains("could not be read", HealthMonitor.WatchdogNote);

            b.Services.TryRecover(b.Vm);
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Services.Outputs.IsLive, "nothing opens by itself on an unreadable record");
            Assert.Contains("OUTPUTS ON", b.Vm.StatusMessage);

            // The operator, sure, opens them by hand.
            Assert.True(b.Services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Services.Outputs.IsLive);
        }
        finally
        {
            HealthMonitor.WatchdogNote = "";
            b.Dispose();
            OutputTakeover.Reset();
        }
    }

    [AvaloniaFact]
    public void TheTakeoverIsToldOnTheHealthLineAndTakenOnlyOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-takeover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new OutputOwnerStore(dir);
            var silent = DateTime.UtcNow - OutputOwnership.HeartbeatSilent - TimeSpan.FromSeconds(5);
            store.Write(Record(4242, 1000, silent, "Main wall"));
            var probe = new FakeProbe();
            probe.Alive[4242] = 1000;
            probe.Paths[4242] = Environment.ProcessPath ?? "Patterns";
            var claimed = OutputTakeover.ClaimAtStart(dir, enabled: true, probe, wait: _ => { });
            Assert.True(claimed.TookOver);

            var services = new AppServices(new SettingsStore(dir));
            try
            {
                Assert.True(services.Takeover.TookOver);
                Assert.Contains("Main wall", HealthMonitor.WatchdogNote);
                // Consumed: a second desk in this process starts with a clean story.
                Assert.False(new AppServices(new SettingsStore(dir)).Takeover.TookOver);
            }
            finally
            {
                services.Shutdown();
            }
        }
        finally
        {
            HealthMonitor.WatchdogNote = "";
            OutputTakeover.Reset();
            AppServices.Instance = null!;
            Directory.Delete(dir, recursive: true);
        }
    }
}
