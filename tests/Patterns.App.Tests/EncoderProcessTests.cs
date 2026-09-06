using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>A scripted host: says what the test tells it, dies or falls silent on request, and records what the desk sent.</summary>
internal sealed class FakeHost : IChildHandle
{
    private readonly BlockingCollection<string> _toDesk = new();
    private readonly List<string> _fromDesk = new();

    public int Pid { get; init; }

    public bool HasExited { get; private set; }

    public int ExitCode { get; private set; }

    public bool Killed { get; private set; }

    /// <summary>The real host exits on QUIT; a fake that lingers past it is a host that will not let go (killed by the desk after 3 s).</summary>
    public bool LeavesOnQuit { get; set; } = true;

    public bool WriteLine(string line)
    {
        lock (_fromDesk) _fromDesk.Add(line);
        if (LeavesOnQuit && HostProtocol.Parse(line).Word == HostProtocol.Quit) Die(0);
        return !HasExited;
    }

    public string? ReadLine()
    {
        try
        {
            return _toDesk.Take();
        }
        catch (InvalidOperationException)
        {
            return null;   // completed: the host is gone
        }
    }

    public void Kill()
    {
        Killed = true;
        Die(-1);
    }

    public void Say(string word, string rest = "")
    {
        if (!_toDesk.IsAddingCompleted) _toDesk.Add(HostProtocol.Line(word, rest));
    }

    public void Die(int code)
    {
        HasExited = true;
        ExitCode = code;
        if (!_toDesk.IsAddingCompleted) _toDesk.CompleteAdding();
    }

    public string Sent(int i)
    {
        lock (_fromDesk) return _fromDesk[i];
    }

    public int SentCount
    {
        get
        {
            lock (_fromDesk) return _fromDesk.Count;
        }
    }

    public void Dispose()
    {
        if (!_toDesk.IsAddingCompleted) _toDesk.CompleteAdding();
    }
}

internal sealed class FakeLauncher : IChildLauncher
{
    public readonly List<FakeHost> Hosts = new();

    public bool Refuse { get; set; }

    public IChildHandle Launch(string role, Action<string> log)
    {
        if (Refuse) throw new InvalidOperationException("no exe to start");
        var host = new FakeHost { Pid = 1000 + Hosts.Count };
        Hosts.Add(host);
        return host;
    }
}

/// <summary>
/// The desk's side of a host process: the plan sent, the beats read, a dead host back with backoff
/// and the same plan, a silent one ended, a crash loop stood down from, an error kept for the owner
/// — with a scripted host and a clock turned by hand; then the real host in a real process.
/// </summary>
public class EncoderProcessTests
{
    private static bool Until(Func<bool> condition, int ms = 5000) => SpinWait.SpinUntil(condition, ms);

    /// <summary>Waits for the host's frame count to reach n, asking for a beat every 20 ms: a frame written is a frame taken, whatever the timing between them.</summary>
    private static bool Counted(ChildProcess child, long n, int ms = 5000)
    {
        var deadline = Environment.TickCount64 + ms;
        while (child.Frames < n)
        {
            if (Environment.TickCount64 > deadline) return false;
            child.Ping();
            Thread.Sleep(20);
        }
        return true;
    }

    [Fact]
    public void TheDeskStartsTheHostSendsThePlanAndReadsWhatItSays()
    {
        var launcher = new FakeLauncher();
        var log = new List<string>();
        using var child = new ChildProcess("encoder", launcher, l => { lock (log) log.Add(l); });
        Assert.Equal(ChildPhase.Idle, child.Phase);
        child.Start("{\"plan\":1}");
        Assert.Equal(ChildPhase.Starting, child.Phase);
        var host = Assert.Single(launcher.Hosts);
        Assert.Equal(1000, child.Pid);
        Assert.Equal("START {\"plan\":1}", host.Sent(0));
        Assert.Contains("starting the encoder", child.Words);

        host.Say(HostProtocol.Hello, JsonUtil.Serialize(new HostHello(1000, "encoder", true)));
        host.Say(HostProtocol.Started);
        host.Say(HostProtocol.Beat, JsonUtil.Serialize(new HostBeat(7, "Playing")));
        host.Say(HostProtocol.Status, "encoding at 30 fps");
        host.Say(HostProtocol.Log, "hello from the host");
        Assert.True(Until(() => child.Phase == ChildPhase.Running && child.Frames == 7 && child.StatusText.Length > 0), child.Words);
        Assert.True(child.SaidHello);
        Assert.True(child.HostHasLibVlc);
        Assert.Equal("Playing", child.HostState);
        Assert.Equal("encoding at 30 fps", child.StatusText);
        Assert.True(Until(() => { lock (log) return log.Any(l => l.Contains("hello from the host")); }));
        Assert.False(child.Failed);
        Assert.Contains("running", child.Words);

        // A tick with the host alive and beating changes nothing.
        child.Poll();
        Assert.Equal(ChildPhase.Running, child.Phase);

        // START again with a new plan ends the host that runs the old one first — never two on one stream.
        child.Start("{\"plan\":2}");
        Assert.True(Until(() => host.SentCount >= 3));
        Assert.Equal("STOP", host.Sent(1));
        Assert.Equal("QUIT", host.Sent(2));
        Assert.Equal(2, launcher.Hosts.Count);
        var second = launcher.Hosts[1];
        Assert.Equal("START {\"plan\":2}", second.Sent(0));
        Assert.Equal(ChildPhase.Starting, child.Phase);
        Assert.Equal(0, child.Restarts);
        host.Say(HostProtocol.Beat, JsonUtil.Serialize(new HostBeat(99, "Playing")));   // the old host's last words are not read
        second.Say(HostProtocol.Started);
        Assert.True(Until(() => child.Phase == ChildPhase.Running));
        Assert.NotEqual(99, child.Frames);

        // The host's own report is kept for the owner, code word first; it is not a crash.
        second.Say(HostProtocol.Error, $"{HostProtocol.ErrorStart} Encoder failed to start — check the destination URLs.");
        Assert.True(Until(() => child.Failed));
        Assert.Equal((HostProtocol.ErrorStart, "Encoder failed to start — check the destination URLs."), HostProtocol.SplitError(child.LastError));
        child.Poll();
        Assert.Equal(ChildPhase.Running, child.Phase);

        child.Stop();
        Assert.Equal(ChildPhase.Stopped, child.Phase);
        Assert.True(Until(() => second.SentCount >= 3));
        Assert.Equal("STOP", second.Sent(1));
        Assert.Equal("QUIT", second.Sent(2));
        child.Poll();
        Assert.Equal(ChildPhase.Stopped, child.Phase);
    }

    [Fact]
    public void AHostThatSaysNothingGetsAColdStartsPatienceThenIsEndedAndSaidSo()
    {
        var launcher = new FakeLauncher();
        var now = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        using var child = new ChildProcess("encoder", launcher, _ => { }, () => now);
        child.Start("plan");
        now = now + HostProtocol.BeatTimeout + TimeSpan.FromSeconds(2);   // eight seconds of nothing: a slow start, not a hung host
        child.Poll();
        Assert.Equal(ChildPhase.Starting, child.Phase);
        Assert.False(launcher.Hosts[0].Killed);
        now = now + HostProtocol.HelloTimeout;                             // past the patience a cold start gets
        child.Poll();
        Assert.True(launcher.Hosts[0].Killed);
        Assert.Equal(ChildPhase.Restarting, child.Phase);
        Assert.Contains("never came up", child.Words);

        // A host that has spoken once is on the beat's clock from then on.
        now = now.AddSeconds(3);
        child.Poll();
        var second = launcher.Hosts[1];
        second.Say(HostProtocol.Hello, JsonUtil.Serialize(new HostHello(1001, "encoder", false)));
        Assert.True(Until(() => child.SaidHello));
        Assert.False(child.HostHasLibVlc);
        now = now + HostProtocol.BeatTimeout + TimeSpan.FromSeconds(1);
        child.Poll();
        Assert.True(second.Killed);
        Assert.Contains("beat went silent", child.Words);
    }

    [Fact]
    public void AHostThatDiesOrFallsSilentComesBackWithBackoffAndTheSamePlan()
    {
        var launcher = new FakeLauncher();
        var now = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        using var child = new ChildProcess("encoder", launcher, _ => { }, () => now);
        child.Start("plan");
        var first = launcher.Hosts[0];
        first.Say(HostProtocol.Started);
        Assert.True(Until(() => child.Phase == ChildPhase.Running));

        first.Die(unchecked((int)0xC0000005));
        child.Poll();
        Assert.Equal(ChildPhase.Restarting, child.Phase);
        Assert.Equal(1, child.Restarts);
        Assert.Contains("access violation", child.Words);
        Assert.Contains("restart #1 in 2 s", child.Words);
        Assert.False(child.Failed);
        now = now.AddSeconds(1);
        child.Poll();
        Assert.Single(launcher.Hosts);                   // not yet
        now = now.AddSeconds(1);
        child.Poll();
        Assert.Equal(2, launcher.Hosts.Count);           // started again, the same plan
        Assert.Equal("START plan", launcher.Hosts[1].Sent(0));
        Assert.Equal(ChildPhase.Starting, child.Phase);
        Assert.Equal(1001, child.Pid);
        Assert.Contains("started again (#1", child.Words);

        // The second one falls silent: ended, and back after the next backoff step.
        var second = launcher.Hosts[1];
        second.Say(HostProtocol.Started);
        Assert.True(Until(() => child.Phase == ChildPhase.Running));
        now = now.AddSeconds(3);
        child.Poll();
        Assert.Equal(ChildPhase.Running, child.Phase);   // within the beat timeout
        now = now + HostProtocol.BeatTimeout + TimeSpan.FromSeconds(1);
        child.Poll();
        Assert.True(second.Killed);
        Assert.Equal(ChildPhase.Restarting, child.Phase);
        Assert.Contains("silent", child.Words);
        Assert.Contains("restart #2 in 4 s", child.Words);
        now = now.AddSeconds(4);
        child.Poll();
        Assert.Equal(3, launcher.Hosts.Count);

        // A quiet exit nobody asked for is a host that is gone, too.
        launcher.Hosts[2].Say(HostProtocol.Started);
        Assert.True(Until(() => child.Phase == ChildPhase.Running));
        launcher.Hosts[2].Die(0);
        child.Poll();
        Assert.Equal(ChildPhase.Restarting, child.Phase);
        Assert.Equal(3, child.Restarts);
    }

    [Fact]
    public void ACrashLoopStandsDownWithWordsAndAStartThatCannotHappenSaysSo()
    {
        var launcher = new FakeLauncher();
        var now = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        using var child = new ChildProcess("encoder", launcher, _ => { }, () => now);
        child.Start("plan");
        for (var i = 0; ; i++)
        {
            launcher.Hosts[^1].Die(unchecked((int)0xC0000005));
            child.Poll();
            if (child.Phase == ChildPhase.GaveUp) break;
            Assert.True(i < 10, "the loop never gave up");
            now = now.AddSeconds(31);   // past any backoff step
            child.Poll();
        }
        Assert.Equal(6, launcher.Hosts.Count);
        Assert.Equal(5, child.Restarts);
        Assert.True(child.Failed);
        Assert.Contains("failed 6 times in a short window", child.Words);
        Assert.Contains("access violation", child.Words);
        now = now.AddSeconds(60);
        child.Poll();
        Assert.Equal(6, launcher.Hosts.Count);           // stood down: nothing more is started
        Assert.Equal(ChildPhase.GaveUp, child.Phase);

        // START again is the operator's decision: a fresh policy, a fresh count.
        child.Start("plan");
        Assert.Equal(ChildPhase.Starting, child.Phase);
        Assert.Equal(7, launcher.Hosts.Count);
        Assert.Equal(0, child.Restarts);
        Assert.False(child.Failed);

        launcher.Refuse = true;
        using var refused = new ChildProcess("encoder", launcher, _ => { }, () => now);
        refused.Start("plan");
        Assert.Equal(ChildPhase.GaveUp, refused.Phase);
        Assert.Contains("could not be started", refused.Words);
        Assert.Contains("no exe to start", refused.LastError);
    }

    /// <summary>
    /// The real host in a real process — this same build started with --host encoder — fed a ring
    /// from here with the null plan: HELLO, STARTED, frames counted in its beats; killed from
    /// outside, it is back on the same ring after the backoff; STOP and QUIT end it cleanly.
    /// </summary>
    [Fact]
    public void TheEncoderHostRunsInItsOwnProcessCountsTheRingAndComesBackWhenKilled()
    {
        using var ring = SharedFrameRing.Create(SharedFrameRing.NameFor("hosttest"), 64, 36);
        var log = new List<string>();
        using var child = new ChildProcess(EncoderHost.Role, ProcessChildLauncher.Default, l => { lock (log) log.Add(l); });
        var plan = new EncoderPlan(EncoderPlan.Null, "", Array.Empty<string>(), ring.Address, 64, 36, 30);
        child.Start(JsonUtil.Serialize(plan));
        string Where() { lock (log) return $"{child.Phase}: {child.Words} / {child.LastError} / {string.Join(" | ", log)}"; }
        Assert.True(Until(() => child.Phase == ChildPhase.Running, 40000), Where());
        Assert.True(child.SaidHello);
        var pid = child.Pid;
        Assert.NotEqual(Environment.ProcessId, pid);

        // Every frame written is taken and counted — the next is written once the host has said so, so no
        // timing between writer and reader is assumed (the ring promises the newest frame, not every one).
        for (var i = 1; i <= 5; i++)
        {
            var slot = ring.BeginWrite();
            ring.EndWrite(slot, i);
            Assert.True(Counted(child, i), $"frame {i} not counted: {child.Frames} — {Where()}");
        }
        Assert.Equal("counting", child.HostState);

        // Killed from outside: the desk's tick notices, and after the backoff the host is back on the same ring.
        Process.GetProcessById(pid).Kill();
        Assert.True(Until(() =>
        {
            child.Poll();
            return child.Phase == ChildPhase.Restarting;
        }, 10000), Where());
        Assert.Equal(1, child.Restarts);
        Assert.True(Until(() =>
        {
            child.Poll();
            return child.Phase == ChildPhase.Running;
        }, 40000), Where());
        Assert.NotEqual(pid, child.Pid);
        for (var i = 6; i <= 8; i++)
        {
            var slot = ring.BeginWrite();
            ring.EndWrite(slot, i);
            Assert.True(Counted(child, i - 5), $"frame {i} not counted: {child.Frames} — {Where()}");   // the new host counts from zero
        }

        var second = child.Pid;
        child.Stop();
        Assert.Equal(ChildPhase.Stopped, child.Phase);
        Assert.True(Until(() => !Alive(second), 10000), "the host did not leave on QUIT");
        Assert.True(Until(() => child.HostsGone, 10000), "the desk's side did not see the host go");
    }

    /// <summary>
    /// A host that beats "starting" and never says STARTED is stuck in its bring-up: alive by the beat,
    /// hung by any other measure — ended after the start's patience and started again; one that said
    /// STARTED is on the beat's clock alone. And a host is gone only when its process is.
    /// </summary>
    [Fact]
    public void AHostStuckInItsBringUpIsEndedAfterTheStartTimeoutAndOneThatStartedIsNot()
    {
        var launcher = new FakeLauncher();
        var now = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        using var child = new ChildProcess("encoder", launcher, _ => { }, () => now);
        child.Start("plan");
        var host = launcher.Hosts[0];
        host.Say(HostProtocol.Hello, JsonUtil.Serialize(new HostHello(1000, "encoder", true)));
        Assert.True(Until(() => child.SaidHello));

        // Beating "starting" keeps it alive through the start's patience…
        now = now + HostProtocol.StartTimeout - TimeSpan.FromSeconds(1);
        host.Say(HostProtocol.Beat, JsonUtil.Serialize(new HostBeat(0, "starting")));
        Assert.True(Until(() => child.HostState == "starting"));
        child.Poll();
        Assert.Equal(ChildPhase.Starting, child.Phase);
        Assert.False(host.Killed);

        // …and not past it: a bring-up that never ends is a hung host, ended and started again with backoff.
        now = now.AddSeconds(2);
        host.Say(HostProtocol.Beat, JsonUtil.Serialize(new HostBeat(1, "starting")));
        Assert.True(Until(() => child.Frames == 1));
        child.Poll();
        Assert.True(host.Killed);
        Assert.Equal(ChildPhase.Restarting, child.Phase);
        Assert.Contains("did not start within 30 s", child.Words);
        Assert.Contains("restart #1 in 2 s", child.Words);
        Assert.False(child.Failed);

        // A host that said STARTED is on the beat's clock only: a minute of beats is a minute of running.
        now = now.AddSeconds(3);
        child.Poll();
        var second = launcher.Hosts[1];
        second.Say(HostProtocol.Started);
        Assert.True(Until(() => child.Phase == ChildPhase.Running));
        now = now + HostProtocol.StartTimeout + TimeSpan.FromSeconds(30);
        second.Say(HostProtocol.Beat, JsonUtil.Serialize(new HostBeat(9, "Playing")));
        Assert.True(Until(() => child.Frames == 9));
        child.Poll();
        Assert.Equal(ChildPhase.Running, child.Phase);
        Assert.False(second.Killed);

        // Gone is the process out, not the STOP sent: a host that lingers past QUIT is still there until it is not.
        second.LeavesOnQuit = false;
        child.Stop();
        Assert.True(Until(() => second.SentCount >= 3));
        Assert.Equal("QUIT", second.Sent(2));
        Assert.False(child.HostsGone);
        second.Die(0);
        Assert.True(Until(() => child.HostsGone));
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The stream service around the host: a rendered stream makes a ring and a plan for the
    /// encoder process, the engine draws into the ring, the status says LIVE and names the process;
    /// a dead host reads as restarting with the show untouched; a host without libVLC is held and
    /// said, not retried every second; the encoder's own failure stops the stream with the reason.
    /// </summary>
    [AvaloniaFact]
    public void TheStreamServiceRunsTheEncoderInItsOwnProcessAndSaysWhatBecomesOfIt()
    {
        var b = TestApp.Boot();
        var runsHere = StreamService.RunsHere;
        StreamService.RunsHere = true;
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            vm.State.Mode = ShowMode.Show;
            vm.ActivePattern.Kind = PatternKind.ColorBars;
            var launcher = new FakeLauncher();
            services.Stream.Launcher = launcher;
            vm.State.Stream.SourceScreenId = StreamConfig.OwnScreenId;
            vm.State.Stream.Destinations[0].Enabled = true;
            vm.State.Stream.Destinations[0].Url = "rtmp://example/live/key";
            vm.State.Stream.Active = true;
            vm.PollNow();
            services.RepublishNow();

            services.Stream.Poll();
            var host = Assert.Single(launcher.Hosts);
            var (word, rest) = HostProtocol.Parse(host.Sent(0));
            Assert.Equal(HostProtocol.Start, word);
            var plan = JsonUtil.Deserialize<EncoderPlan>(rest)!;
            Assert.Equal(EncoderPlan.Rendered, plan.Kind);
            Assert.Equal(StreamMrl.RenderedMrl, plan.Mrl);
            Assert.Equal(services.Stream.Ring!.Address, plan.Ring);
            Assert.Equal((vm.State.Stream.Width, vm.State.Stream.Height), (plan.Width, plan.Height));
            Assert.Contains(plan.Options, o => o.StartsWith(":sout=", StringComparison.Ordinal) && o.Contains("rtmp://example/live/key"));
            Assert.Contains("Starting the encoder", services.Stream.Status);
            Assert.NotNull(services.Stream.Renderer);

            // The engine's thread draws the stream's screen into the ring while the host comes up.
            Assert.True(Until(() => services.Stream.Ring!.LatestSeq > 0, 5000), "no frame reached the ring");

            // STARTED is the process running its plan, not the destination reached: the status says
            // "connecting" on libVLC's opening state and LIVE once it plays.
            host.Say(HostProtocol.Started);
            host.Say(HostProtocol.Beat, JsonUtil.Serialize(new HostBeat(0, "Opening")));
            Assert.True(Until(() => services.Stream.Encoder!.Phase == ChildPhase.Running && services.Stream.Encoder.HostState == "Opening"));
            services.Stream.Poll();
            Assert.StartsWith("Connecting to 1 destination", services.Stream.Status);
            host.Say(HostProtocol.Beat, JsonUtil.Serialize(new HostBeat(3, "Playing")));
            Assert.True(Until(() => services.Stream.Encoder!.Frames == 3));
            services.Stream.Poll();
            Assert.StartsWith("LIVE", services.Stream.Status);
            Assert.Contains("rendered", services.Stream.Status);
            Assert.Contains("encoder in its own process", services.Stream.Status);
            Assert.Equal("", vm.State.Stream.LastError);

            // A settings change while LIVE: the old encoder is told to go, and the new one waits until
            // its process is out — a stream key takes one publisher — then starts with the new plan.
            host.LeavesOnQuit = false;
            vm.State.Stream.VideoKbps += 500;
            var kbps = vm.State.Stream.VideoKbps;
            services.Stream.Poll();
            Assert.Contains("let go of the destination", services.Stream.Status);
            Assert.True(Until(() => host.SentCount >= 3));
            Assert.Equal("QUIT", host.Sent(2));
            Assert.Null(services.Stream.Ring);
            Assert.True(vm.State.Stream.Active);
            services.Stream.Poll();
            Assert.Single(launcher.Hosts);   // still waiting: the old process lingers
            host.Die(0);                     // out: the destination is free
            Assert.True(Until(() =>
            {
                services.Stream.Poll();
                return launcher.Hosts.Count == 2;
            }, 10000), services.Stream.Status);
            host = launcher.Hosts[1];
            var newPlan = JsonUtil.Deserialize<EncoderPlan>(HostProtocol.Parse(host.Sent(0)).Text)!;
            Assert.Contains(newPlan.Options, o => o.Contains($"vb={kbps}"));
            Assert.NotEqual(plan.Ring, newPlan.Ring);
            host.Say(HostProtocol.Started);
            host.Say(HostProtocol.Beat, JsonUtil.Serialize(new HostBeat(1, "Playing")));
            Assert.True(Until(() => services.Stream.Encoder!.Phase == ChildPhase.Running && services.Stream.Encoder.Frames == 1));
            services.Stream.Poll();
            Assert.StartsWith("LIVE", services.Stream.Status);

            // The host dies: restarting, the show untouched, the stream still on.
            host.Die(unchecked((int)0xC0000005));
            services.Stream.Poll();
            Assert.Contains("restarting", services.Stream.Status);
            Assert.Contains("the show is untouched", services.Stream.Status);
            Assert.True(vm.State.Stream.Active);
            Assert.Equal("", vm.State.Stream.LastError);
            Assert.Equal(2, launcher.Hosts.Count);
            Assert.True(Until(() =>
            {
                services.Stream.Poll();
                return launcher.Hosts.Count == 3;
            }, 10000), services.Stream.Status);   // the first backoff step is two seconds
            Assert.Contains("started again 1×", services.Stream.Status);

            // No libVLC on this machine: said and held, the switch left on, no relaunch every tick.
            launcher.Hosts[2].Say(HostProtocol.Error, $"{HostProtocol.ErrorLibVlc} Streaming needs libVLC — use the full build (or install VLC).");
            Assert.True(Until(() => services.Stream.Encoder?.Failed == true));
            services.Stream.Poll();
            Assert.Equal("Streaming needs libVLC — use the full build (or install VLC).", services.Stream.Status);
            Assert.True(vm.State.Stream.Active);
            Assert.Null(services.Stream.Encoder);
            services.Stream.Poll();
            services.Stream.Poll();
            Assert.Equal(3, launcher.Hosts.Count);

            // Off and on again is a fresh try (once the held host's process is out); the encoder's own failure stops the stream with the reason.
            vm.State.Stream.Active = false;
            services.Stream.Poll();
            Assert.Equal("Not streaming.", services.Stream.Status);
            vm.State.Stream.Active = true;
            Assert.True(Until(() =>
            {
                services.Stream.Poll();
                return launcher.Hosts.Count == 4;
            }, 10000), services.Stream.Status);
            launcher.Hosts[3].Say(HostProtocol.Error, $"{HostProtocol.ErrorEncoder} the encoder reported an error");
            Assert.True(Until(() => services.Stream.Encoder?.Failed == true));
            services.Stream.Poll();
            Assert.False(vm.State.Stream.Active);
            Assert.Equal("the encoder reported an error", vm.State.Stream.LastError);
            Assert.Contains("Stream error", services.Stream.Status);
            Assert.Null(services.Stream.Ring);
            Assert.Null(services.Stream.Renderer);
        }
        finally
        {
            StreamService.RunsHere = runsHere;
            b.Dispose();
        }
    }

    /// <summary>
    /// The render thread owns its surfaces and the ring under them: a Stop that runs out of patience
    /// while a frame is still drawing leaves both to the thread, which releases them when the frame
    /// ends — never pulled from under a running frame; stopped in time, they go at once.
    /// </summary>
    [AvaloniaFact]
    public void TheRenderThreadOwnsTheRingAndLetsItGoWhenALateFrameEnds()
    {
        var b = TestApp.Boot();
        try
        {
            b.Services.RepublishNow();
            var ring = SharedFrameRing.Create(SharedFrameRing.NameFor("late"), 64, 36);
            var frameStarted = new ManualResetEventSlim(false);
            var frameMayEnd = new ManualResetEventSlim(false);
            var renderer = new StreamRenderer(b.Services.Bus, StreamConfig.OwnScreenId, ring, 30)
            {
                StopTimeout = TimeSpan.FromMilliseconds(200),
                BeforeFrame = () =>
                {
                    frameStarted.Set();
                    frameMayEnd.Wait();
                },
            };
            renderer.Start();
            Assert.True(frameStarted.Wait(5000), "the render thread never drew");
            renderer.Stop();                      // the wait runs out: the frame is still drawing
            Assert.False(ring.IsClosed);          // so the ring is still the thread's
            frameMayEnd.Set();
            Assert.True(Until(() => ring.IsClosed, 5000), "the ring was not released when the frame ended");
            Assert.True(renderer.FramesRendered >= 1);
            renderer.Dispose();                   // nothing left to release twice

            // Stopped in time: released at once, and the ring is closed for the encoder's side too.
            var quick = SharedFrameRing.Create(SharedFrameRing.NameFor("quick"), 64, 36);
            using var other = SharedFrameRing.Open(quick.Address);
            using var prompt = new StreamRenderer(b.Services.Bus, StreamConfig.OwnScreenId, quick, 30);
            prompt.Start();
            Assert.True(Until(() => quick.LatestSeq > 0, 5000), "no frame reached the ring");
            prompt.Stop();
            Assert.True(quick.IsClosed);
            Assert.True(other.IsClosed);
            Assert.Equal(-1, other.WaitForFrame(0, 10));
        }
        finally
        {
            b.Dispose();
        }
    }
}
