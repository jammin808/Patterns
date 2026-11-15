using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 65: the shutdown as phases on a report. Every step runs in its phase's order on a clean
/// close; a failure injected into every step in turn never stops the steps after it — the show
/// lock, the final save, recovery, ownership and the mutex run whatever failed before them; and an
/// autosave stuck on a dead share cannot hold the exit: the final save waits a bounded time, the
/// recovery record is kept, and the next start is told why. A desk these tests close with a step
/// failed on purpose holds none of its six listener ports and runs none of its dispatcher timers
/// (<see cref="DeskTimers"/>) after the test — worker threads and other timers are not observed
/// here: the release the failure left undone is run once
/// the assertions are made (<see cref="AppServices.ReleaseWhatTheInjectedFailureLeftUndone"/>), and
/// every desk asks for ports of its own, so a leak here could never hold a port another desk asks for.
/// </summary>
public class ShutdownPhaseTests
{
    private static readonly string[] Critical = { "show lock", "final save", "ownership", "instance mutex" };

    /// <summary>
    /// The ports one desk of these tests listens on, picked for it: the control's three (the wire, the web remote, the
    /// audience listener), the twin's, OSC's and the beacon's. A desk that accepts callers listens on the twin's port and the
    /// beacon's, and these desks turn OSC and the audience listener on as well, so every listener a step closes is open.
    /// </summary>
    private sealed record DeskPorts(int Wire, int Web, int Audience, int Twin, int Osc, int Beacon)
    {
        public static DeskPorts Pick()
        {
            var tcp = TestPorts.Distinct(4);
            var udp = TestPorts.DistinctUdp(2);
            return new DeskPorts(tcp[0], tcp[1], tcp[2], tcp[3], udp[0], udp[1]);
        }

        /// <summary>The desk's settings file, on these ports (the boot's prepare).</summary>
        public void Write(string dir)
        {
            var s = SettingsStore.Fresh();
            s.Control.Enabled = true;
            s.Control.TcpPort = Wire;
            s.Control.HttpPort = Web;
            s.Control.AudienceEnabled = true;
            s.Control.AudiencePort = Audience;
            s.Control.OscEnabled = true;
            s.Control.OscPort = Osc;
            s.Twin.Port = Twin;
            s.Watchdog.BeaconListenPort = Beacon;
            s.Watchdog.BeaconPort = Beacon;                                                       // its own beacon, never another desk's port
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        }

        /// <summary>Each port that will not bind on the any address, by its listener's name — what a closed desk still holds.</summary>
        public IReadOnlyList<string> StillHeld()
        {
            var held = new List<string>();
            foreach (var (name, port) in new[] { ("wire", Wire), ("web remote", Web), ("audience", Audience), ("twin", Twin) })
            {
                if (!TcpBinds(port)) held.Add($"{name} {port} (held by {PortHolder.Of(port)})");
            }
            foreach (var (name, port) in new[] { ("osc", Osc), ("beacon", Beacon) })
            {
                if (!UdpBinds(port)) held.Add($"{name} {port} (udp)");
            }
            return held;
        }

        private static bool TcpBinds(int port)
        {
            var probe = new TcpListener(IPAddress.Any, port);
            try
            {
                probe.Start();
                return true;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return false;
            }
            finally
            {
                probe.Dispose();
            }
        }

        private static bool UdpBinds(int port)
        {
            try
            {
                using var probe = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                return true;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return false;
            }
        }
    }

    /// <summary>A desk on ports of its own, booted into a folder the caller removes after the release has run.</summary>
    private static TestApp.Booted BootOn(DeskPorts ports, string dir,
                                         [System.Runtime.CompilerServices.CallerMemberName] string member = "")
        => TestApp.Boot(prepare: ports.Write, dir: dir, callerMember: member);

    /// <summary>The steps a clean close takes, by name, from a desk closed here: no local of the caller keeps that desk alive.</summary>
    private static List<string> CleanCloseSteps([System.Runtime.CompilerServices.CallerMemberName] string member = "")
    {
        using var home = TempRun.Folder("patterns-tests-");
        var probe = BootOn(DeskPorts.Pick(), home, member);
        probe.Dispose();
        return probe.Services.ShutdownReport.Select(s => s.Step).ToList();
    }

    [AvaloniaFact]
    public void TheStepsRunInTheirPhasesOrderAndEveryOneRanOnACleanClose()
    {
        using var home = TempRun.Folder("patterns-tests-");
        var b = BootOn(DeskPorts.Pick(), home);
        var services = b.Services;
        b.Dispose();
        var report = services.ShutdownReport;
        Assert.NotEmpty(report);
        Assert.All(report, s => Assert.Equal("ran", s.Outcome));
        var phases = (IList<string>)AppServices.ShutdownPhases;
        var order = report.Select(s => phases.IndexOf(s.Phase)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.OrderBy(i => i), order);
        foreach (var step in Critical) Assert.Contains(report, s => s.Step == step);
        Assert.Contains(report, s => s.Step == "recovery cleared");
        Assert.Equal("audience", report[0].Phase);
        Assert.Equal("process", report[^1].Phase);
        Assert.Equal("nothing was skipped", services.ReleaseWhatTheInjectedFailureLeftUndone());   // no failure named: nothing left undone
    }

    /// <summary>
    /// The fence on the step table: every step a shutdown takes names its release — the step itself, or the words that say
    /// why it holds nothing to let go — so a new step cannot land without saying what a failure in it would leave open.
    /// </summary>
    [AvaloniaFact]
    public void EveryShutdownStepNamesItsReleaseOrSaysWhyItNeedsNone()
    {
        using var home = TempRun.Folder("patterns-tests-");
        var b = BootOn(DeskPorts.Pick(), home);
        var services = b.Services;
        b.Dispose();
        var unnamed = services.ShutdownReport.Select(s => s.Step).Where(s => !AppServices.ShutdownReleases.ContainsKey(s)).ToList();
        Assert.True(unnamed.Count == 0, $"shutdown steps with no release named in AppServices.ShutdownReleases: {string.Join(", ", unnamed)}");
        Assert.All(AppServices.ShutdownReleases, r => Assert.False(string.IsNullOrWhiteSpace(r.Value), $"{r.Key} names no release and no reason"));
        // The steps that hold nothing, pinned: a step moved to words would leave its release unrun with every other fence green,
        // so a new entry here is argued in review, never slipped in.
        var holdNothing = AppServices.ShutdownReleases.Where(r => r.Value != AppServices.ReleasedByItsOwnStep).Select(r => r.Key).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "final save", "quality profile", "recovery cleared", "recovery kept" }, holdNothing);
    }

    /// <summary>
    /// Runs the release the injected failure left undone, and keeps whatever it threw for the caller to assert after its own
    /// assertions: a throw in a finally would take the place of the failure the injection found.
    /// </summary>
    private static (string? Released, Exception? Fault) Release(AppServices services)
    {
        try
        {
            return (services.ReleaseWhatTheInjectedFailureLeftUndone(), null);
        }
        catch (Exception e)
        {
            return (null, e);
        }
    }

    [AvaloniaFact]
    public void AFailureInjectedIntoEveryStepInTurnNeverStopsTheStepsAfterIt()
    {
        var steps = CleanCloseSteps();
        Assert.True(steps.Count > 30, $"only {steps.Count} steps");
        var seen = new HashSet<string>(steps, StringComparer.Ordinal);
        try
        {
            foreach (var step in steps)
            {
                using var home = TempRun.Folder("patterns-tests-");
                var ports = DeskPorts.Pick();
                var running = DeskTimers.RunningIds();                                       // what ran before this desk was built
                AppServices.FailShutdownStep = step;
                var b = BootOn(ports, home);
                var services = b.Services;
                b.Dispose();
                var report = services.ShutdownReport;
                (string? Released, Exception? Fault) release;
                try
                {
                    foreach (var s in report) seen.Add(s.Step);
                    Assert.Contains(report, s => s.Step == step && s.Outcome.StartsWith("failed: injected", StringComparison.Ordinal));
                    foreach (var other in steps.Where(s => s != step))
                    {
                        if (other == "recovery cleared" && step == "final save")
                        {
                            Assert.Contains(report, s => s.Step == "recovery kept" && s.Outcome == "ran");   // no save landed: the record stays
                            continue;
                        }
                        Assert.True(report.Any(s => s.Step == other && s.Outcome == "ran"), $"with {step} failing, {other} did not run: {string.Join("; ", report.Select(s => $"{s.Step}={s.Outcome}"))}");
                    }
                }
                finally
                {
                    release = Release(services);                                            // what the failure left undone, whatever the assertions found
                }
                Assert.True(release.Fault is null, $"with {step} failing, its release threw: {release.Fault}");
                var released = release.Released;
                Assert.Equal(AppServices.ShutdownReleases[step] == AppServices.ReleasedByItsOwnStep ? $"released: {step}" : $"{step} needs none: {AppServices.ShutdownReleases[step]}", released);
                TestApp.DrainDispatcher();
                var left = DeskTimers.RunningOwnersNotIn(running);
                Assert.True(left.Count == 0, $"with {step} failing and its release run ({released}), the desk's timers still run: {string.Join(", ", left)}");
                var held = ports.StillHeld();
                Assert.True(held.Count == 0, $"with {step} failing and its release run ({released}), the desk still holds: {string.Join(", ", held)}");
            }
        }
        finally
        {
            AppServices.FailShutdownStep = null;
        }
        // The table names every step a shutdown took on any of these paths, and nothing else.
        Assert.Equal(AppServices.ShutdownReleases.Keys.Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A step that holds a socket, failed on purpose: the closed desk still holds that step's ports — the leak a desk closed
    /// this way used to leave for the rest of the run, which made every later desk on those ports fail its start — and once the
    /// step's own release has run, each binds on the any address.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("control", "wire|web remote|audience")]
    [InlineData("twin", "twin")]
    [InlineData("osc", "osc")]
    [InlineData("beacon", "beacon")]
    public void AStepThatHoldsASocketFailedOnPurposeLetsItsPortsGoOnceItsReleaseRuns(string step, string held)
    {
        var listeners = held.Split('|');
        using var home = TempRun.Folder("patterns-tests-");
        var ports = DeskPorts.Pick();
        AppServices.FailShutdownStep = step;
        TestApp.Booted b;
        try
        {
            b = BootOn(ports, home);
            b.Dispose();
        }
        finally
        {
            AppServices.FailShutdownStep = null;
        }
        (string? Released, Exception? Fault) release;
        IReadOnlyList<string> before;
        try
        {
            Assert.Contains(b.Services.ShutdownReport, s => s.Step == step && s.Outcome.StartsWith("failed: injected", StringComparison.Ordinal));
            before = ports.StillHeld();
        }
        finally
        {
            release = Release(b.Services);
        }
        Assert.True(release.Fault is null, $"with {step} failing, its release threw: {release.Fault}");
        var released = release.Released;
        foreach (var listener in listeners) Assert.Contains(before, h => h.StartsWith(listener + " ", StringComparison.Ordinal));   // the leak, before its release
        Assert.All(before, h => Assert.Contains(listeners, l => h.StartsWith(l + " ", StringComparison.Ordinal)));                // and only this step's
        Assert.Equal($"released: {step}", released);
        var after = ports.StillHeld();
        Assert.True(after.Count == 0, $"after {released}: {string.Join(", ", after)}");
    }

    /// <summary>
    /// A desk whose start failed, closed, and then polled: the poll's retry (<see cref="AppServices.RetryListeners"/>) asks a
    /// closed desk for nothing — before, it opened the wire and the web remote again on a service nobody would close, and the
    /// beacon's listener with them. The same for the control service asked directly after its Dispose.
    /// </summary>
    [AvaloniaFact]
    public void AClosedDeskWhoseStartFailedOpensNothingWhenItIsPolledAfterItsClose()
    {
        using var home = TempRun.Folder("patterns-tests-");
        var ports = DeskPorts.Pick();
        var wire = new TcpListener(IPAddress.Any, ports.Wire);
        var beacon = new UdpClient(new IPEndPoint(IPAddress.Any, ports.Beacon));
        TestApp.Booted? b = null;
        var wasRetry = AppServices.ListenerRetry;
        try
        {
            AppServices.ListenerRetry = TimeSpan.Zero;                                          // the poll's retry asks at once: no clock between the close and the ask
            wire.Start();
            b = BootOn(ports, home);
            var services = b.Services;
            Assert.True(services.Control.StartFailed, services.Control.Status);
            Assert.True(services.Beacon.BindFailed, services.Beacon.Status);
            b.Dispose();
            wire.Dispose();
            beacon.Dispose();
            services.RetryListeners();
            services.Control.Reconcile();
            Assert.False(services.Control.Listening);
            var held = ports.StillHeld();
            Assert.True(held.Count == 0, $"a closed desk, polled: {string.Join(", ", held)}");
        }
        finally
        {
            AppServices.ListenerRetry = wasRetry;
            wire.Dispose();
            beacon.Dispose();
            b?.Dispose();
        }
    }

    [AvaloniaFact]
    public void AnAutosaveStuckOnADeadShareCannotHoldTheExitAndTheRecoveryRecordIsKept()
    {
        using var home = TempRun.Folder("patterns-tests-");                                    // read after the desk has closed
        var b = BootOn(DeskPorts.Pick(), home);
        var services = b.Services;
        var gate = new ManualResetEventSlim(false);
        var wasWait = AppServices.ExitSaveWait;
        AppServices.ExitSaveWait = TimeSpan.FromMilliseconds(400);
        try
        {
            services.QueueFileWork("a share that never answers", () => gate.Wait());
            var sw = Stopwatch.StartNew();
            b.Dispose();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"the exit took {sw.Elapsed}");
            var report = services.ShutdownReport;
            Assert.Contains(report, s => s.Step == "final save" && s.Outcome == "ran");                  // the step ran; the save did not land
            Assert.Contains(report, s => s.Step == "recovery kept" && s.Outcome == "ran");
            Assert.DoesNotContain(report, s => s.Step == "recovery cleared");
            Assert.Contains(report, s => s.Step == "ownership" && s.Outcome == "ran");
            Assert.Contains(report, s => s.Step == "instance mutex" && s.Outcome == "ran");
            Assert.True(File.Exists(Path.Combine(b.Dir, WatchdogMarker.FileName)));
            Assert.Contains("could not be written at exit", WatchdogMarker.ReadAndClear(b.Dir));
        }
        finally
        {
            gate.Set();
            AppServices.ExitSaveWait = wasWait;
            TestApp.FlushFiles(services);                                                          // the released save lands before its folder goes
        }
    }
}
