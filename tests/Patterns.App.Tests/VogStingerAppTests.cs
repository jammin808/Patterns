using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The VOG / stinger split against the live app: a VOG behaves exactly as stingers did, a
/// stinger fades the music and dissolves in, and when it lands the show goes where the stinger
/// says — or comes back. Every clip is a video (audio never plays off Windows); fades and holds
/// are driven by an injected clock, clip ends by a mounted source that reports Ended.
/// </summary>
public class VogStingerAppTests
{
    private static readonly DateTime T0 = new(2026, 9, 4, 20, 0, 0, DateTimeKind.Utc);

    private static string TempClip(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{name}");
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 1 });
        return path;
    }

    private sealed class EndedSource : IVideoFrameSource
    {
        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint) => false;
        public SKSizeI? FrameSize => null;
        public bool IsPlaying => false;
        public bool IsEnded => true;
        public double DurationSeconds => 3;
        public string StatusText => "ended";
    }

    /// <summary>The decoder reports the clip's natural end and the service polls at <paramref name="now"/>.</summary>
    private static void End(AppServices services, string clip, DateTime now)
    {
        InputBus.Mount(InputKeys.Video(clip), new EndedSource());
        services.Stingers.Poll(now);
        Dispatcher.UIThread.RunJobs();
    }

    private static LookConfig SaveLook(MainViewModel vm, string name, PatternKind kind)
    {
        vm.ActivePattern.Kind = kind;
        vm.NewLookName = name;
        vm.SaveLookCommand.Execute(null);
        return LookService.Find(vm.State, name)!;
    }

    private static StingerItemConfig Vog(MainViewModel vm, string clip, string name)
    {
        var item = new StingerItemConfig { Path = clip, Name = name };
        vm.State.Stingers.Items.Add(item);
        return item;
    }

    private static StingerItemConfig Sting(MainViewModel vm, string clip, string name, StingerAfter after, string target = "", bool musicReturns = true)
    {
        var item = new StingerItemConfig
        {
            Path = clip, Name = name, Kind = StingerKind.Sting, After = after, AfterTarget = target, MusicReturns = musicReturns,
        };
        vm.State.Stingers.Items.Add(item);
        return item;
    }

    /// <summary>Unsandboxed, program on Grid, the LIVE strip saying "Doors": the ground every test starts from.</summary>
    private static void Ground(TestApp.Booted b)
    {
        // The service's own timer must read the test's clock: with the wall clock it would judge a
        // clip fired "at T0" stuck (no decoder here) twelve seconds after T0 — which is every run
        // once the day has moved past it.
        b.Services.Stingers.NowUtc = () => T0;
        b.Vm.IsSandboxActive = false;
        b.Vm.ActivePattern.Kind = PatternKind.Grid;
        b.Vm.State.Stingers.FadeMs = 400;
        b.Services.AirLabel = "Doors";
        Dispatcher.UIThread.RunJobs();
    }

    private static T Pump<T>(Task<T> task, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("pumped task timed out");
        }
        return task.GetAwaiter().GetResult();
    }

    private static void Settle(Avalonia.Controls.Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    // ---- the verbs --------------------------------------------------------------------

    [AvaloniaFact]
    public void TheKindCheckedVerbsRefuseTheWrongOne()
    {
        var b = TestApp.Boot();
        var vogClip = TempClip("seats.mp4");
        var stingClip = TempClip("whoosh.mp4");
        try
        {
            Ground(b);
            Vog(b.Vm, vogClip, "Take your seats");
            Sting(b.Vm, stingClip, "Whoosh", StingerAfter.Return);

            var wrongVog = b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, "1", "sting");
            Assert.Equal(ActionStatus.Refused, wrongVog.Status);
            Assert.Contains("is a VOG, not a stinger", wrongVog.Message);
            var wrongSting = b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, "2", "vog");
            Assert.Equal(ActionStatus.Refused, wrongSting.Status);
            Assert.Contains("is a stinger, not a VOG", wrongSting.Message);
            Assert.False(b.Services.Stingers.ClipActive);

            Assert.Equal(ActionStatus.Requested, b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, "2").Status);
            b.Services.Stingers.Stop(T0);
            Assert.Equal(ActionStatus.Requested, b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, "2", "banana").Status); // a newer vocabulary never blocks a press
            b.Services.Stingers.Stop(T0);
            Assert.Equal(ActionStatus.Refused, b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, "9", "vog").Status);
        }
        finally
        {
            File.Delete(vogClip);
            File.Delete(stingClip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void RemoteFiresVogsAndStingsByKind()
    {
        var b = TestApp.Boot();
        var vogClip = TempClip("seats.mp4");
        var stingClip = TempClip("whoosh.mp4");
        try
        {
            Ground(b);
            Vog(b.Vm, vogClip, "Take your seats");
            Sting(b.Vm, stingClip, "Whoosh", StingerAfter.Return);
            var router = new CommandRouter(b.Services);

            Assert.Equal("OK", Pump(router.ExecuteAsync(ControlProtocol.Parse("VOG 1"))));
            Assert.Equal("VOG: Take your seats", b.Services.AirLabel);
            var wrong = Pump(router.ExecuteAsync(ControlProtocol.Parse("VOG 2")));
            Assert.StartsWith("ERR", wrong);
            Assert.Contains("not a VOG", wrong);
            Assert.Equal("OK", Pump(router.ExecuteAsync(ControlProtocol.Parse("STING 2"))));
            Assert.Equal("STING: Whoosh", b.Services.AirLabel);
            var wrongToo = Pump(router.ExecuteAsync(ControlProtocol.Parse("STING 1")));
            Assert.StartsWith("ERR", wrongToo);
            Assert.Contains("not a stinger", wrongToo);
            Assert.Equal("OK", Pump(router.ExecuteAsync(ControlProtocol.Parse("STINGER 2"))));
            Assert.Equal("OK", Pump(router.ExecuteAsync(ControlProtocol.Parse("STING STOP"))));
            Assert.False(b.Services.Stingers.ClipActive);
            Assert.Equal(PatternKind.Grid, b.Vm.State.Pattern.Kind);

            var json = router.StateJson();
            Assert.Contains("\"kind\":\"vog\"", json);
            Assert.Contains("\"kind\":\"sting\"", json);
        }
        finally
        {
            File.Delete(vogClip);
            File.Delete(stingClip);
            b.Dispose();
        }
    }

    // ---- the behaviour ----------------------------------------------------------------

    [AvaloniaFact]
    public void AVogBehavesExactlyAsStingersDidBefore()
    {
        var b = TestApp.Boot();
        var clip = TempClip("walk-in.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var placement = new ScreenPlacement { ScreenId = "s1", UseCustomPattern = true };
            vm.State.Output.Placements.Add(placement);
            Dispatcher.UIThread.RunJobs();
            var versionBefore = services.Bus.Current.Version;
            var item = Vog(vm, clip, "Walk-in");

            Assert.True(services.Stingers.Fire(item, T0));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Media, vm.State.Pattern.Kind);
            Assert.Equal(MediaSource.Video, vm.State.Pattern.Media.Source);
            Assert.Equal(clip, vm.State.Pattern.Media.VideoPath);
            Assert.False(vm.State.Pattern.Media.Loop);
            Assert.False(placement.UseCustomPattern);
            Assert.Equal("Walk-in", vm.State.Stingers.PlayingName);
            Assert.True(services.Stingers.ClipActive);
            Assert.True(services.Stingers.OwnsScreens);
            Assert.False(services.Stingers.Holding);
            Assert.Equal("VOG: Walk-in", services.AirLabel);
            Assert.Equal("Walk-in", services.Stingers.VogOnAir);
            Assert.Equal("", services.Stingers.StingOnAir);
            Assert.Equal(1.0, services.Stingers.MusicGainAt(T0.AddSeconds(1)));                    // a video VOG does not duck
            Assert.False(services.Stingers.MusicRamping(T0.AddMilliseconds(100)));
            Assert.NotEqual(services.Bus.Current.Version, services.Bus.Current.FadeOverrideVersion); // and forces no dissolve
            Assert.True(services.Bus.Current.Version > versionBefore);

            End(services, clip, T0.AddSeconds(5));
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.True(placement.UseCustomPattern);
            Assert.Equal("", vm.State.Stingers.PlayingName);
            Assert.False(services.Stingers.ClipActive);
            Assert.False(services.Stingers.OwnsScreens);
            Assert.Equal("Doors", services.AirLabel);
            Assert.Equal(1.0, services.Stingers.MusicGainAt(T0.AddSeconds(6)));
            Assert.Equal("Clip finished — previous content back.", services.Stingers.Status);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStingFadesTheMusicOutAndCrossfadesThePicture()
    {
        var b = TestApp.Boot();
        var clip = TempClip("whoosh.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var item = Sting(vm, clip, "Whoosh", StingerAfter.Return);
            var before = services.Bus.Current.Version;

            Assert.True(services.Stingers.Fire(item, T0));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1.0, services.Stingers.MusicGainAt(T0));
            Assert.Equal(0.5, services.Stingers.MusicGainAt(T0.AddMilliseconds(200)), 2);
            Assert.Equal(0.0, services.Stingers.MusicGainAt(T0.AddMilliseconds(400)));
            Assert.Equal(0.0, services.Stingers.MusicGainAt(T0.AddSeconds(3)));
            Assert.True(services.Stingers.MusicRamping(T0.AddMilliseconds(100)));
            Assert.False(services.Stingers.MusicRamping(T0.AddSeconds(1)));
            var snap = services.Bus.Current;
            Assert.Equal(400, snap.FadeOverrideMs);
            Assert.InRange(snap.FadeOverrideVersion, before + 1, snap.Version); // the sting's own publish carried the dissolve
            Assert.Equal("STING: Whoosh", services.AirLabel);
            Assert.Equal("Whoosh", services.Stingers.StingOnAir);
            Assert.Equal("", services.Stingers.VogOnAir);
            Assert.Equal("Whoosh", vm.State.Stingers.PlayingName);

            services.Stingers.Stop(T0.AddSeconds(4));
            Assert.Equal(1.0, services.Stingers.MusicGainAt(T0.AddSeconds(4.5)));
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);

            // 0 = a hard cut, and an instant fade.
            vm.State.Stingers.FadeMs = 0;
            before = services.Bus.Current.Version;
            Assert.True(services.Stingers.Fire(item, T0.AddSeconds(5)));
            Dispatcher.UIThread.RunJobs();
            snap = services.Bus.Current;
            Assert.InRange(snap.CutAtVersion, before + 1, snap.Version);
            Assert.Equal(0.0, services.Stingers.MusicGainAt(T0.AddSeconds(5)));
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStingWithReturnPutsTheShowBackAndTheMusicComesUp()
    {
        var b = TestApp.Boot();
        var clip = TempClip("whoosh.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            services.ValidationVideoOverride = () => true; // headless: no libVLC
            var item = Sting(vm, clip, "Whoosh", StingerAfter.Return);
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "1", Name = "Hit", Actions = { new CueActionConfig { Kind = CueActionKind.StingerFire, Target = item.Id } } };
            stack.Cues.Add(cue);
            vm.State.Blackout = true;
            Dispatcher.UIThread.RunJobs();

            var result = services.Actions.Execute(new ShowAction(ShowActionKind.CueFire, cue.Id), ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(result.Ok, result.Message);
            Assert.False(vm.State.Blackout);
            Assert.False(services.Bus.Current.State.Blackout);
            Assert.Equal(clip, services.Bus.Current.State.Pattern.Media.VideoPath);
            Assert.Equal("STING: Whoosh", services.AirLabel);

            var t1 = DateTime.UtcNow.AddSeconds(2); // the cue fired on the real clock
            End(services, clip, t1);
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.False(services.Stingers.OwnsScreens);
            Assert.False(vm.State.Blackout);
            Assert.Equal(1.0, services.Stingers.MusicGainAt(t1.AddMilliseconds(400)));
            Assert.DoesNotContain("STING", services.AirLabel);
            Assert.Equal("Sting done — previous content back.", services.Stingers.Status);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStingWithManualHoldsUntilTheOperatorTakesIt()
    {
        var b = TestApp.Boot();
        var clip = TempClip("whoosh.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var look = SaveLook(vm, "Awards", PatternKind.ColorBars);
            vm.ActivePattern.Kind = PatternKind.Grid;
            vm.State.Stingers.HoldSeconds = 0;
            var item = Sting(vm, clip, "Whoosh", StingerAfter.Manual);

            Assert.True(services.Stingers.Fire(item, T0));
            Dispatcher.UIThread.RunJobs();
            End(services, clip, T0.AddSeconds(3));
            Assert.True(services.Stingers.Holding);
            Assert.Equal("Whoosh", services.Stingers.HoldName);
            Assert.True(services.Stingers.OwnsScreens);
            Assert.True(services.Stingers.ClipActive);
            Assert.Equal(clip, services.Bus.Current.State.Pattern.Media.VideoPath); // the picture did not move
            Assert.Equal("STING HOLD: Whoosh", services.AirLabel);
            Assert.Contains("Holding", services.Stingers.Status);
            Assert.Equal(0.0, services.Stingers.MusicGainAt(T0.AddSeconds(3)));
            vm.PollNow();
            Assert.True(vm.StingerHolding);
            Assert.Contains("Whoosh", vm.StingerHoldText);
            Assert.True(vm.Run.IsStingHolding);
            Assert.Equal("STING HOLD: Whoosh", vm.Run.StingHoldText);
            services.Stingers.Poll(T0.AddSeconds(30));
            Assert.True(services.Stingers.Holding); // no limit: it holds until taken

            // Their take is the release. Their choice stands, no revert.
            Assert.True(services.Actions.Execute(ShowActionKind.ApplyLook, ActionOrigin.Desk, look.Id).Ok);
            services.Stingers.Poll(T0.AddSeconds(31));
            Assert.False(services.Stingers.Holding);
            Assert.False(services.Stingers.OwnsScreens);
            Assert.Contains("took over", services.Stingers.Status);
            Assert.Equal("Awards", services.AirLabel);
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
            Assert.Equal(1.0, services.Stingers.MusicGainAt(T0.AddSeconds(31.5)));
            services.Stingers.Poll(T0.AddSeconds(32));
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind); // no zombie revert
            vm.PollNow();
            Assert.False(vm.StingerHolding);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AHeldStingIsPutBackByStopAndByTheHoldLimit()
    {
        var b = TestApp.Boot();
        var clip = TempClip("whoosh.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var item = Sting(vm, clip, "Whoosh", StingerAfter.Manual);

            // (a) STOP puts a held frame back: the hold kept the saved content.
            Assert.True(services.Stingers.Fire(item, T0));
            End(services, clip, T0.AddSeconds(3));
            Assert.True(services.Stingers.Holding);
            Assert.Equal(ActionStatus.Done, services.Actions.Execute(ShowActionKind.StingerStop, ActionOrigin.Desk).Status);
            Assert.False(services.Stingers.Holding);
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.Equal("Doors", services.AirLabel);
            Assert.Equal("Ready.", services.Stingers.Status);

            // (b) A hold limit gives the show back by itself.
            InputBus.Clear();
            vm.State.Stingers.HoldSeconds = 5;
            var t = T0.AddMinutes(1);
            Assert.True(services.Stingers.Fire(item, t));
            End(services, clip, t.AddSeconds(1));
            Assert.True(services.Stingers.Holding);
            services.Stingers.Poll(t.AddSeconds(3));
            Assert.True(services.Stingers.Holding);
            services.Stingers.Poll(t.AddSeconds(7));
            Assert.False(services.Stingers.Holding);
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.Contains("timed out", services.Stingers.Status);
            Assert.Equal(1.0, services.Stingers.MusicGainAt(t.AddSeconds(8)));
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStingWithNextGoesTheCallersNextCueThroughTheGate()
    {
        var b = TestApp.Boot();
        var clip = TempClip("whoosh.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var look = SaveLook(vm, "A", PatternKind.ColorBars);
            vm.ActivePattern.Kind = PatternKind.Grid;
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "01.010", Name = "A", Actions = { new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look.Id } } };
            stack.Cues.Add(cue);
            Dispatcher.UIThread.RunJobs();
            var svc = services.CueStack;
            svc.SetArmed(true, ActionOrigin.Desk);
            svc.Standby(cue.Id);
            var item = Sting(vm, clip, "Whoosh", StingerAfter.Next);

            Assert.True(services.Stingers.Fire(item, T0));
            End(services, clip, T0.AddSeconds(3));
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);   // the next cue, not the old content
            Assert.Equal("01.010 A", services.AirLabel);
            Assert.False(services.Stingers.OwnsScreens);
            Assert.Equal("", vm.State.Stingers.PlayingName);
            var row = svc.History.First();
            Assert.Equal(CueOutcome.Done, row.Outcome);
            Assert.Equal(ActionOrigin.Stinger.Label, row.Origin);
            Assert.Equal("Sting done — the show moved on.", services.Stingers.Status);
            Assert.Equal(1.0, services.Stingers.MusicGainAt(T0.AddSeconds(4)));
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStingWithNextOnAnUnarmedStackPutsTheShowBack()
    {
        var b = TestApp.Boot();
        var clip = TempClip("whoosh.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var look = SaveLook(vm, "A", PatternKind.ColorBars);
            vm.ActivePattern.Kind = PatternKind.Grid;
            var stack = CueStacks.Caller(vm.State);
            stack.Cues.Add(new RunCueConfig { Number = "01.010", Name = "A", Actions = { new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look.Id } } });
            Dispatcher.UIThread.RunJobs();
            var item = Sting(vm, clip, "Whoosh", StingerAfter.Next);

            Assert.True(services.Stingers.Fire(item, T0));
            End(services, clip, T0.AddSeconds(3));
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);       // fell back to Return
            Assert.False(services.Stingers.Holding);
            Assert.False(services.Stingers.OwnsScreens);
            Assert.Contains("could not", services.Stingers.Status);
            Assert.DoesNotContain(services.CueStack.History, r => r.Outcome is CueOutcome.Done or CueOutcome.Requested); // nothing fired
            Assert.Contains(services.Journal.Tail(6), e => e.Kind == "StingerStop" && e.Outcome == "Failed" && e.Message.Contains("not armed"));
            Assert.Equal("Doors", services.AirLabel);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStingNeverConfirmsACueOnTheCallersBehalf()
    {
        var b = TestApp.Boot();
        var clip = TempClip("whoosh.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var look = SaveLook(vm, "A", PatternKind.ColorBars);
            vm.ActivePattern.Kind = PatternKind.Grid;
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "01.010", Name = "A", RequireConfirm = true, Actions = { new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look.Id } } };
            stack.Cues.Add(cue);
            Dispatcher.UIThread.RunJobs();
            var svc = services.CueStack;
            svc.SetArmed(true, ActionOrigin.Desk);
            svc.Standby(cue.Id);
            var item = Sting(vm, clip, "Whoosh", StingerAfter.Next);

            Assert.True(services.Stingers.Fire(item, T0));
            End(services, clip, T0.AddSeconds(3));
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.Null(svc.Runtime.ConfirmPendingCueId);                 // no confirm window was opened
            Assert.Contains("could not", services.Stingers.Status);
            Assert.Contains(services.Journal.Tail(6), e => e.Kind == "StingerStop" && e.Outcome == "Failed" && e.Message.Contains("confirm"));
            Assert.Equal(cue.Id, svc.Runtime.StandbyCueId);              // still waiting for the caller's GO
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStingWithNextCanAdvanceANamedList()
    {
        var b = TestApp.Boot();
        var clip = TempClip("whoosh.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var look = SaveLook(vm, "B", PatternKind.Focus);
            vm.ActivePattern.Kind = PatternKind.Grid;
            var clicker = CueStacks.Clicker(vm.State);
            clicker.Cues.Add(new RunCueConfig { Number = "02.010", Name = "B", Actions = { new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look.Id } } });
            Dispatcher.UIThread.RunJobs();
            var item = Sting(vm, clip, "Whoosh", StingerAfter.Next, clicker.Id);

            Assert.True(services.Stingers.Fire(item, T0));
            End(services, clip, T0.AddSeconds(3));
            Assert.Equal(PatternKind.Focus, vm.State.Pattern.Kind);
            Assert.DoesNotContain("STING", services.AirLabel);
            Assert.False(services.Stingers.OwnsScreens);
            Assert.Contains(services.Journal.Tail(8), e => e.Kind == "ListGo" && e.Origin == ActionOrigin.Stinger.Label);
            Assert.Equal(0, services.Cues.For(clicker).CurrentIndex);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStingWhoseAfterPolicyCannotRunPutsTheShowBack()
    {
        var b = TestApp.Boot();
        var clips = new[] { TempClip("a.mp4"), TempClip("b.mp4"), TempClip("c.mp4") };
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var cases = new (string Clip, StingerAfter After, string Target)[]
            {
                (clips[0], StingerAfter.Custom, "not-a-real-id"),
                (clips[1], StingerAfter.Next, "no-such-list"),
                (clips[2], StingerAfter.Custom, ""),
            };
            var t = T0;
            foreach (var (clip, after, target) in cases)
            {
                InputBus.Clear();
                var item = Sting(vm, clip, $"Hit {after}", after, target);
                Assert.True(services.Stingers.Fire(item, t));
                Assert.Equal(PatternKind.Media, vm.State.Pattern.Kind);
                End(services, clip, t.AddSeconds(3));
                Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
                Assert.False(services.Stingers.Holding);
                Assert.False(services.Stingers.OwnsScreens);
                Assert.Contains("could not", services.Stingers.Status);
                Assert.Equal("Doors", services.AirLabel);
                var journal = services.Journal.Tail(3);
                Assert.Contains(journal, e => e.Kind == "StingerStop" && e.Outcome == "Failed" && e.Target == item.DisplayName);
                Assert.Equal(1.0, services.Stingers.MusicGainAt(t.AddSeconds(4)));
                t = t.AddMinutes(1);
            }
        }
        finally
        {
            InputBus.Clear();
            foreach (var c in clips) File.Delete(c);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStingWithCustomAppliesTheNamedLookOrCue()
    {
        var b = TestApp.Boot();
        var clipA = TempClip("a.mp4");
        var clipB = TempClip("b.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var look = SaveLook(vm, "Awards", PatternKind.ColorBars);
            var other = SaveLook(vm, "Sponsor", PatternKind.Focus);
            vm.ActivePattern.Kind = PatternKind.Grid;
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "01.010", Name = "Sponsor", Actions = { new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = other.Id } } };
            stack.Cues.Add(cue);
            Dispatcher.UIThread.RunJobs();

            var toLook = Sting(vm, clipA, "To awards", StingerAfter.Custom, look.Id);
            Assert.True(services.Stingers.Fire(toLook, T0));
            End(services, clipA, T0.AddSeconds(3));
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
            Assert.Equal("Awards", services.AirLabel);
            Assert.False(services.Stingers.OwnsScreens);

            InputBus.Clear();
            var toCue = Sting(vm, clipB, "To sponsor", StingerAfter.Custom, cue.Id);
            Assert.True(services.Stingers.Fire(toCue, T0.AddMinutes(1)));
            End(services, clipB, T0.AddMinutes(1).AddSeconds(3));
            Assert.Equal(PatternKind.Focus, vm.State.Pattern.Kind);
            Assert.Equal("01.010 Sponsor", services.AirLabel);
            Assert.False(services.Stingers.OwnsScreens);
            Assert.Contains(services.Journal.Tail(6), e => e.Kind == "CueFire" && e.Origin == ActionOrigin.Stinger.Label);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clipA);
            File.Delete(clipB);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void MusicThatDoesNotComeBackStopsTheTrack()
    {
        var b = TestApp.Boot();
        var clip = TempClip("whoosh.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            vm.State.AudioPlayer.Playing = true;
            var item = Sting(vm, clip, "Whoosh", StingerAfter.Return, musicReturns: false);

            Assert.True(services.Stingers.Fire(item, T0));
            Assert.Equal(0.0, services.Stingers.MusicGainAt(T0.AddSeconds(1)));
            End(services, clip, T0.AddSeconds(3));
            Assert.False(vm.State.AudioPlayer.Playing);                       // the track stopped for good
            Assert.Equal(1.0, services.Stingers.MusicGainAt(T0.AddSeconds(3))); // the next ▶ Play comes up at full
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void StopAllReleasesAHeldStingAndNeverRunsItsAfter()
    {
        var b = TestApp.Boot();
        var clipA = TempClip("a.mp4");
        var clipB = TempClip("b.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var look = SaveLook(vm, "A", PatternKind.ColorBars);
            vm.ActivePattern.Kind = PatternKind.Grid;
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "01.010", Name = "A", Actions = { new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look.Id } } };
            stack.Cues.Add(cue);
            Dispatcher.UIThread.RunJobs();
            services.CueStack.SetArmed(true, ActionOrigin.Desk);
            services.CueStack.Standby(cue.Id);
            services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            vm.State.Stream.Active = true;
            vm.State.Tone.Enabled = true;
            vm.State.AudioPlayer.Playing = true;

            // Mid-clip, a stinger that would GO the next cue: STOP ALL means stop.
            var next = Sting(vm, clipA, "Next", StingerAfter.Next);
            Assert.True(services.Stingers.Fire(next, T0));
            services.Actions.Execute(ShowActionKind.BlackoutOn, ActionOrigin.Desk);
            services.Actions.Execute(ShowActionKind.StopAll, ActionOrigin.Desk);
            Assert.False(services.Stingers.OwnsScreens);
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.DoesNotContain(services.CueStack.History, r => r.Outcome is CueOutcome.Done or CueOutcome.Requested);
            Assert.False(vm.State.AudioPlayer.Playing);
            Assert.False(vm.State.Tone.Enabled);
            Assert.True(vm.State.Stream.Active);
            Assert.True(services.Outputs.IsLive);
            Assert.True(vm.State.Blackout);
            End(services, clipA, T0.AddSeconds(5));                        // a late end of the stopped clip changes nothing
            Assert.DoesNotContain(services.CueStack.History, r => r.Outcome is CueOutcome.Done or CueOutcome.Requested);
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);

            // A held stinger: Esc twice on the Run surface puts it back the same way.
            InputBus.Clear();
            services.Actions.Execute(ShowActionKind.BlackoutOff, ActionOrigin.Desk);
            var hold = Sting(vm, clipB, "Hold", StingerAfter.Manual);
            Assert.True(services.Stingers.Fire(hold, T0.AddMinutes(1)));
            End(services, clipB, T0.AddMinutes(1).AddSeconds(3));
            Assert.True(services.Stingers.Holding);
            vm.IsRunLayout = true;
            vm.Run.EscapePressed();
            Assert.True(services.Stingers.Holding);
            Assert.Contains("Esc again", vm.StatusMessage);
            vm.Run.EscapePressed();
            Assert.False(services.Stingers.Holding);
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clipA);
            File.Delete(clipB);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStuckStingNeverMovesTheShowOn()
    {
        var b = TestApp.Boot();
        var clip = TempClip("whoosh.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var look = SaveLook(vm, "A", PatternKind.ColorBars);
            vm.ActivePattern.Kind = PatternKind.Grid;
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "01.010", Name = "A", Actions = { new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look.Id } } };
            stack.Cues.Add(cue);
            Dispatcher.UIThread.RunJobs();
            services.CueStack.SetArmed(true, ActionOrigin.Desk);
            services.CueStack.Standby(cue.Id);
            var item = Sting(vm, clip, "Whoosh", StingerAfter.Next);

            Assert.True(services.Stingers.Fire(item, T0));   // nothing ever decodes it
            services.Stingers.Poll(T0.AddSeconds(5));
            Assert.True(services.Stingers.ClipActive);
            services.Stingers.Poll(T0.AddSeconds(13));
            Assert.False(services.Stingers.OwnsScreens);
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.Contains("could not play", services.Stingers.Status);
            Assert.Empty(services.CueStack.History);
            Assert.Equal(1.0, services.Stingers.MusicGainAt(T0.AddSeconds(14)));
            Assert.Equal("Doors", services.AirLabel);
        }
        finally
        {
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ASecondClipReplacesAHeldStingAndTheChainRevertsToTheOriginal()
    {
        var b = TestApp.Boot();
        var clipA = TempClip("a.mp4");
        var clipB = TempClip("b.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var hold = Sting(vm, clipA, "Hold", StingerAfter.Manual);
            var vog = Vog(vm, clipB, "Winner");

            Assert.True(services.Stingers.Fire(hold, T0));
            End(services, clipA, T0.AddSeconds(3));
            Assert.True(services.Stingers.Holding);

            Assert.True(services.Stingers.Fire(vog, T0.AddSeconds(10)));
            Assert.False(services.Stingers.Holding);
            Assert.Equal(clipB, vm.State.Pattern.Media.VideoPath);
            Assert.Equal("VOG: Winner", services.AirLabel);
            Assert.Equal(1.0, services.Stingers.MusicGainAt(T0.AddSeconds(11)));   // a VOG brings the music back

            InputBus.Clear();
            End(services, clipB, T0.AddSeconds(15));
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);   // the original, not the held clip
            Assert.Equal("", vm.State.Pattern.Media.VideoPath);
            Assert.False(services.Stingers.OwnsScreens);
            Assert.Equal("Doors", services.AirLabel);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clipA);
            File.Delete(clipB);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AWatchdogRestartDuringAStingComesBackToTheShowNotTheClip()
    {
        var b = TestApp.Boot();
        var clip = TempClip("whoosh.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "01.010", Name = "A" };
            stack.Cues.Add(cue);
            services.CueStack.Standby(cue.Id); // a place to keep: the sidecar exists headless
            var item = Sting(vm, clip, "Whoosh", StingerAfter.Manual);

            Assert.True(services.Stingers.Fire(item, T0));
            Dispatcher.UIThread.RunJobs();
            var during = services.Recovery.Read();
            Assert.NotNull(during);
            Assert.False(string.IsNullOrEmpty(during!.AirLook));
            var look = JsonUtil.Deserialize<LookData>(during.AirLook!)!;
            Assert.Equal(PatternKind.Grid, look.Pattern.Kind);            // the show, never the clip

            End(services, clip, T0.AddSeconds(3));
            Assert.True(services.Stingers.Holding);
            var held = services.Recovery.Read();
            Assert.Equal(PatternKind.Grid, JsonUtil.Deserialize<LookData>(held!.AirLook!)!.Pattern.Kind);

            services.Stingers.Stop(T0.AddSeconds(5));
            Assert.Null(services.Recovery.Read()?.AirLook);              // unsandboxed, the settings file is the air again
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AShowFileNeverSavesWhileAClipOrAHoldOwnsTheScreens()
    {
        var b = TestApp.Boot();
        var clip = TempClip("whoosh.mp4");
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            services.SaveNow();
            string SavedVideoPath() => JsonUtil.Deserialize<ShowState>(File.ReadAllText(services.Store.SettingsPath))!.Pattern.Media.VideoPath;
            PatternKind SavedKind() => JsonUtil.Deserialize<ShowState>(File.ReadAllText(services.Store.SettingsPath))!.Pattern.Kind;
            Assert.Equal(PatternKind.Grid, SavedKind());
            var item = Sting(vm, clip, "Whoosh", StingerAfter.Manual);

            Assert.True(services.Stingers.Fire(item, T0));
            Assert.Equal(clip, vm.State.Pattern.Media.VideoPath);         // unsandboxed, the live state is the clip
            services.SaveNow();
            Assert.NotEqual(clip, SavedVideoPath());
            Assert.Equal(PatternKind.Grid, SavedKind());

            End(services, clip, T0.AddSeconds(3));
            Assert.True(services.Stingers.Holding);
            services.SaveNow();
            Assert.NotEqual(clip, SavedVideoPath());

            services.Stingers.Stop(T0.AddSeconds(5));
            services.SaveNow();
            Assert.Equal("", SavedVideoPath());
            Assert.Equal(PatternKind.Grid, SavedKind());
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheShowPanelAndTheAudioPageRenderBothKinds()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            Ground(b);
            var look = SaveLook(vm, "Awards", PatternKind.ColorBars);
            vm.ActivePattern.Kind = PatternKind.Grid;
            vm.State.Stingers.Items.Add(new StingerItemConfig { Path = "C:/show/seats.wav", Name = "Take your seats" });
            var sting = new StingerItemConfig { Path = "C:/show/whoosh.mp4", Name = "Whoosh", Kind = StingerKind.Sting, After = StingerAfter.Custom, AfterTarget = look.Id };
            vm.State.Stingers.Items.Add(sting);
            vm.PollNow();
            Assert.Single(vm.VogChips);
            Assert.Single(vm.StingChips);
            Assert.Contains(vm.AfterLookOrCueChoices, p => p.Id == look.Id);
            Assert.Equal("", vm.AfterListChoices[0].Id);   // the caller's list

            foreach (var page in new[] { "Panel", "Audio" })
            {
                vm.SelectPage(Shell.IndexOf(page));
                Settle(b.Window);
                using var frame = b.Window.CaptureRenderedFrame();
                Assert.NotNull(frame);
            }

            // A re-kind regroups the chips; a renamed look keeps the picker in step.
            sting.Kind = StingerKind.Vog;
            vm.PollNow();
            Assert.Equal(2, vm.VogChips.Count);
            Assert.Empty(vm.StingChips);
            look.Name = "Awards 2";
            vm.PollNow();
            Assert.Contains(vm.AfterLookOrCueChoices, p => p.Id == look.Id && p.Label.Contains("Awards 2"));
        }
        finally
        {
            b.Dispose();
        }
    }
}
