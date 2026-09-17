using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 76.1 on a live desk: a restart the operator asks for with the outputs live is a handover
/// — the old desk keeps its windows until the replacement asks for the screens and then leaves with
/// the handover code, its record kept; a replacement that never comes is given up on; a start that
/// finds a run still playing opens its own picture first and takes the screens after; a deliberate
/// record puts the show back whatever AutoRestore says; the clips' and the music's playheads are
/// written each second and resumed where they would be by now.
/// </summary>
public class RestartHandoverAppTests
{
    private sealed class FakeProbe : IProcessProbe
    {
        public readonly Dictionary<int, long> Alive = new();
        public readonly Dictionary<int, string> Paths = new();
        public readonly List<int> Killed = new();

        public long? StartTicks(int pid) => Alive.TryGetValue(pid, out var t) ? t : null;

        public string ExePath(int pid) => Paths.TryGetValue(pid, out var p) ? p : "";

        public ProcessSight Look(int pid)
            => Alive.TryGetValue(pid, out var t) ? ProcessSight.Alive(t, ExePath(pid)) : ProcessSight.Gone;

        public bool Kill(int pid)
        {
            Killed.Add(pid);
            Alive.Remove(pid);
            return true;
        }
    }

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
        => new(pid, started, beatUtc, beatUtc, targets, Environment.MachineName, Environment.ProcessPath ?? "Patterns");

    private static void GoLive(TestApp.Booted b)
    {
        var on = b.Services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
        Assert.True(on.Ok, on.Message);
        Dispatcher.UIThread.RunJobs();
        Assert.True(b.Services.Outputs.IsLive);
    }

    [AvaloniaFact]
    public void ADeliberateRestartWithLiveOutputsIsAHandoverAndTheOldDeskLeavesOnlyWhenAsked()
    {
        var b = TestApp.Boot("patterns-handover-");
        try
        {
            var services = b.Services;
            services.Updates.SupervisedOverride = () => true;
            int? exitCode = null;
            services.ExitRequest = code =>
            {
                exitCode = code;
                return true;
            };
            GoLive(b);

            var words = services.TryHandoverRestart();
            Assert.Contains("stay lit", words);
            Assert.True(services.HandingOver);
            Assert.Equal(SupervisorPolicy.HandoverBeat, WatchdogBeat.Value);       // the supervisor is asked for a replacement
            Assert.True(services.Outputs.IsLive, "nothing closes: the room keeps its picture while the replacement boots");
            Assert.Null(exitCode);
            TestApp.FlushFiles(services);
            var record = services.Recovery.Read();
            Assert.NotNull(record);
            Assert.True(record!.Deliberate);
            Assert.True(record.Live);
            Assert.True(services.TryHandoverRestart().Length > 0, "asked twice is asked once");

            // The replacement has its own picture over these screens and asks for them: this desk
            // closes its outputs and leaves through the supervisor's door with the handover code.
            new OutputOwnerStore(b.Dir).Ask(new HandoverRequest(Environment.ProcessId + 1, DateTime.UtcNow));
            services.Ownership.Tick();
            Assert.True(WaitFor(() => exitCode is not null), "the ask is answered within the tick and the exit asked for");
            Assert.Equal(SupervisorPolicy.ReplacedExitCode, exitCode);
            Assert.False(services.Outputs.IsLive);
            Assert.False(services.HandingOver);
            Assert.Equal(SupervisorPolicy.AliveBeat, WatchdogBeat.Value);
            Assert.NotNull(services.Recovery.Read());                                   // the replacement's to read

            // The exit itself keeps the record (a restart's does) and leaves the replacement's ownership record alone.
            new OutputOwnerStore(b.Dir).Write(Record(Environment.ProcessId + 1, 1000, DateTime.UtcNow, "Main wall"));
            services.Shutdown();
            Assert.NotNull(new RecoveryStore(b.Dir).Read());
            var owner = new OutputOwnerStore(b.Dir).Read();
            Assert.True(owner.IsValid);
            Assert.Equal(Environment.ProcessId + 1, owner.Value!.Pid);
        }
        finally
        {
            WatchdogBeat.Value = SupervisorPolicy.AliveBeat;
            HealthMonitor.WatchdogNote = "";
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AReplacementThatNeverAsksIsGivenUpOnAndTheDeskCarriesOnWithItsOutputs()
    {
        var b = TestApp.Boot("patterns-handover-");
        try
        {
            var services = b.Services;
            services.Updates.SupervisedOverride = () => true;
            var clock = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            services.HandoverClock = () => clock;
            GoLive(b);
            Assert.True(services.TryHandoverRestart().Length > 0);
            services.PollHandover();
            Assert.True(services.HandingOver);

            clock += AppServices.HandoverPatience + TimeSpan.FromSeconds(1);
            services.PollHandover();
            Assert.False(services.HandingOver);
            Assert.Equal(SupervisorPolicy.AliveBeat, WatchdogBeat.Value);
            Assert.True(services.Outputs.IsLive, "the show never went dark for a restart that did not come");
            Assert.Contains("did not come", HealthMonitor.WatchdogNote);
            TestApp.FlushFiles(services);
            Assert.False(services.Recovery.Read()!.Deliberate);                          // the record no longer promises a restart

            // Without a supervisor, or with nothing on the screens, there is no handover to have.
            services.Updates.SupervisedOverride = () => false;
            Assert.Equal("", services.TryHandoverRestart());
        }
        finally
        {
            WatchdogBeat.Value = SupervisorPolicy.AliveBeat;
            HealthMonitor.WatchdogNote = "";
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStartOverARunStillPlayingOpensItsOwnPictureFirstAndTakesTheScreensAfter()
    {
        OutputTakeover.Reset();
        var probe = new FakeProbe();
        var t = DateTime.UtcNow;
        OutputTakeover.ProbeOverride = probe;
        OutputTakeover.WaitOverride = _ => { };
        OutputTakeover.ClockOverride = () =>
        {
            t = t.AddSeconds(2);   // every look at the clock is two seconds on: the grace runs out without a real wait
            return t;
        };
        TakeoverResult? claim = null;
        var b = TestApp.Boot("patterns-handover-", dir =>
        {
            // A desk alive and beating now: the old desk of a handover, or a hung one still decoding.
            new OutputOwnerStore(dir).Write(Record(4242, 1000, DateTime.UtcNow, "Main wall"));
            probe.Alive[4242] = 1000;
            probe.Paths[4242] = Environment.ProcessPath ?? "Patterns";
            new RecoveryStore(dir).Write(new RecoverySnapshot(true, false, DateTime.UtcNow, Deliberate: true));
            claim = OutputTakeover.ClaimAtStart(dir, enabled: true, probe, wait: _ => { }, defer: true);
        });
        try
        {
            Assert.NotNull(claim);
            Assert.True(claim!.Deferred);
            Assert.False(claim.TookOver);
            Assert.Equal(OutputClaim.HeldByLiveDesk, claim.Claim);
            Assert.Empty(probe.Killed);
            Assert.True(new OutputOwnerStore(b.Dir).ReadRequest().IsMissing, "nothing is asked before this desk's picture is up");
            Assert.True(b.Services.Takeover.Deferred);
            Assert.True(b.Services.Ownership.HoldWrites, "the record on disk is the other run's until the claim is finished");

            b.Services.TryRecover(b.Vm);
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Services.Outputs.IsLive, "this desk's picture goes up first, over the other run's");

            // Then the ask, the grace, and the ending of a run that never answers — on a worker, landing back on the desk.
            Assert.True(WaitFor(() => b.Services.Takeover.TookOver), b.Services.Takeover.Words);
            Assert.Equal(new[] { 4242 }, probe.Killed);
            Assert.True(b.Services.Takeover.EndedOwner);
            Assert.False(b.Services.Takeover.Deferred);
            Assert.Contains("pid 4242", b.Services.Takeover.Words);
            Assert.False(b.Services.Ownership.HoldWrites);
            Assert.True(WaitFor(() => b.Services.Ownership.Held), "this desk keeps the record from here");
            var owner = new OutputOwnerStore(b.Dir).Read();
            Assert.True(owner.IsValid);
            Assert.Equal(Environment.ProcessId, owner.Value!.Pid);
            Assert.Contains("pid 4242", b.Vm.StatusMessage);
        }
        finally
        {
            OutputTakeover.ProbeOverride = null;
            OutputTakeover.WaitOverride = null;
            OutputTakeover.ClockOverride = null;
            HealthMonitor.WatchdogNote = "";
            b.Dispose();
            OutputTakeover.Reset();
        }
    }

    [AvaloniaFact]
    public void ADeliberateRecordPutsTheShowBackWithAutoRestoreOffAndACrashsRecordDoesNot()
    {
        var b = TestApp.Boot("patterns-handover-", dir =>
        {
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), "{ \"Watchdog\": { \"AutoRestore\": false } }");
            new RecoveryStore(dir).Write(new RecoverySnapshot(true, false, DateTime.UtcNow, Deliberate: true));
        });
        try
        {
            Assert.False(b.Services.State.Watchdog.AutoRestore);
            HealthMonitor.Restarts = 2;   // the supervisor counts a handover as a restart; the words still say the operator asked
            b.Services.TryRecover(b.Vm);
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Services.Outputs.IsLive, "the operator asked for this restart: AutoRestore is about a crash they did not ask for");
            Assert.StartsWith("Restarted —", b.Vm.StatusMessage);
            Assert.DoesNotContain("watchdog", b.Vm.StatusMessage);
        }
        finally
        {
            HealthMonitor.Restarts = 0;
            b.Dispose();
        }

        var crash = TestApp.Boot("patterns-handover-", dir =>
        {
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), "{ \"Watchdog\": { \"AutoRestore\": false } }");
            new RecoveryStore(dir).Write(new RecoverySnapshot(true, false, DateTime.UtcNow));
        });
        try
        {
            crash.Services.TryRecover(crash.Vm);
            Dispatcher.UIThread.RunJobs();
            Assert.False(crash.Services.Outputs.IsLive, "a crash's record with AutoRestore off is the operator's choice, as before");
        }
        finally
        {
            crash.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheClipsPlayheadIsWrittenEachSecondAndResumedWhereItWouldBeByNow()
    {
        var b = TestApp.Boot("patterns-handover-");
        try
        {
            var (services, vm, _) = b;
            var fakes = AudioFakes.Install(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Video;
            vm.State.Pattern.Media.VideoPath = AudioFakes.TempFile("resume.mp4");
            vm.State.Pattern.Media.Loop = false;   // a clip that plays once: the ended case below is real
            Dispatcher.UIThread.RunJobs();
            services.ReconcileInputs();
            var onAir = services.VideoOnAir();
            Assert.NotNull(onAir);
            // The programme's mount, as it is now: the loop change above reopened it once, and the preview has a mount of its own.
            var fake = fakes.Sources.Last(f => f.Wanted.Key == onAir!.Key);
            fake.Position = 40;
            fake.Length = 100;

            services.WritePlayhead();
            TestApp.FlushFiles(services);
            var record = services.Playhead.Read();
            Assert.NotNull(record);
            var clip = record!.Clips.Single(c => c.Key == onAir!.Key);
            Assert.Equal(40, clip.Seconds, 1);
            Assert.False(clip.Loops);

            // A restart five seconds on: the clip is moved to where it would be by now, once its decoder plays.
            services.Video.ResumeAt(record.Clips, record.UpdatedUtc.AddSeconds(-5));
            Assert.Equal(1, services.Video.PendingResumes);
            services.Video.ApplyResumes();
            Assert.Equal(0, services.Video.PendingResumes);
            Assert.NotNull(fake.SeekedTo);
            Assert.InRange(fake.SeekedTo!.Value, 44.5, 46.5);

            // A clip that would have ended by now is not moved: it plays from its start, as an ended clip does.
            fake.SeekedTo = null;
            fake.Position = 98;
            services.WritePlayhead();
            TestApp.FlushFiles(services);
            var late = services.Playhead.Read();
            services.Video.ResumeAt(late!.Clips, late.UpdatedUtc.AddSeconds(-5));
            services.Video.ApplyResumes();
            Assert.Null(fake.SeekedTo);
            Assert.Equal(0, services.Video.PendingResumes);

            // A looping clip wraps around its length instead: 98 s plus five seconds is 3 s into the next pass.
            vm.State.Pattern.Media.Loop = true;
            Dispatcher.UIThread.RunJobs();
            services.ReconcileInputs();   // a loop change reopens the mount: a new source
            var looping = fakes.Sources.Last(f => f.Wanted.Key == onAir!.Key);
            Assert.NotSame(fake, looping);
            looping.Position = 98;
            looping.Length = 100;
            services.WritePlayhead();
            TestApp.FlushFiles(services);
            var loopRecord = services.Playhead.Read();
            Assert.True(loopRecord!.Clips.Single(c => c.Key == onAir!.Key).Loops);
            services.Video.ResumeAt(loopRecord.Clips, loopRecord.UpdatedUtc.AddSeconds(-5));
            services.Video.ApplyResumes();
            Assert.NotNull(looping.SeekedTo);
            Assert.InRange(looping.SeekedTo!.Value, 2.5, 4.5);

            // Nothing with a playhead any more: the sidecar goes.
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            services.ReconcileInputs();
            services.WritePlayhead();
            TestApp.FlushFiles(services);
            Assert.Null(services.Playhead.Read());
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheMusicComesBackInTheSameTrackAtTheSameBar()
    {
        var b = TestApp.Boot("patterns-handover-");
        try
        {
            var (services, vm, _) = b;
            vm.State.AudioPlayer.Items.Add(new AudioTrackConfig { Path = AudioFakes.TempFile("one.wav") });
            vm.State.AudioPlayer.Items.Add(new AudioTrackConfig { Path = AudioFakes.TempFile("two.wav") });
            services.AudioPlayer.ResumeAt(1, 30, DateTime.UtcNow.AddSeconds(-5));
            Assert.InRange(services.AudioPlayer.PendingSeekSeconds, 34, 36.5);

            vm.State.AudioPlayer.Playing = true;
            services.AudioPlayer.Poll();
            Assert.Equal(1, services.AudioPlayer.NowIndex);                            // the second track, not the top of the list

            // A record too old to trust moves nothing: the track comes back from its start.
            services.AudioPlayer.ResumeAt(0, 30, DateTime.UtcNow - PlayheadResume.MaxAge - TimeSpan.FromSeconds(5));
            Assert.Equal(-1, services.AudioPlayer.PendingSeekSeconds);
        }
        finally
        {
            b.Services.State.AudioPlayer.Playing = false;
            b.Dispose();
        }
    }
}
