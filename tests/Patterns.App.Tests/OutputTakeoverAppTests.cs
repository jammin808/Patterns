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
        public bool KillWorks = true;

        public long? StartTicks(int pid) => Alive.TryGetValue(pid, out var t) ? t : null;

        public string ExePath(int pid) => Paths.TryGetValue(pid, out var p) ? p : "";

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
            Assert.Null(new OutputOwnerStore(dir).Read());   // the litter is gone
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
            Assert.Null(store.Read());
            Assert.Null(store.ReadRequest());
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
            Assert.Null(store.ReadRequest());
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
            Assert.NotNull(store.Read());        // left exactly as it was
            Assert.Null(store.ReadRequest());    // and never asked for
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
            Assert.Null(store.Read());   // nothing open: nobody's screens

            services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Outputs.IsLive);

            Assert.True(WaitFor(() => store.Read() is not null), "the record is written when the outputs open");
            var owner = store.Read();
            Assert.NotNull(owner);
            Assert.Equal(Environment.ProcessId, owner!.Pid);
            Assert.Equal(Environment.MachineName, owner.Machine);
            Assert.NotEmpty(owner.Targets);
            Assert.True(services.Ownership.Held);

            // The beat moves on with the desk's own poll — the silence of a hung one is the signal.
            var first = store.Read()!.HeartbeatUtc;
            Thread.Sleep(OutputOwnership.HeartbeatEvery + TimeSpan.FromMilliseconds(50));
            b.Vm.PollNow();
            Assert.True(WaitFor(() => store.Read() is { } beat && beat.HeartbeatUtc > first), "the poll beats the record");

            services.Actions.Execute(ShowActionKind.OutputsOff, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();

            Assert.True(WaitFor(() => store.Read() is null && !services.Ownership.Held), "the record goes with the windows");
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
            Assert.Null(store.Read());
            Assert.Null(store.ReadRequest());   // answered once, never again
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
            Assert.NotNull(store.ReadRequest());   // its own ask is left for it to clear at the end

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
