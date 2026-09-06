using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The standby cue's clip on a live desk: opened and held the moment the cue goes on standby,
/// retired when standby moves on, released — the same decoder, no reopen — when GO puts the look
/// on air, never held while it is already on the screens, and never at a live source's expense.
/// </summary>
public class PreRollAppTests
{
    private const string Clip = "/shows/vt.mp4";

    [AvaloniaFact]
    public void TheStandbyCuesClipIsHeldBeforeGoAndReleasedByIt()
    {
        var b = TestApp.Boot();
        var fakes = AudioFakes.Install(b);
        try
        {
            var (services, vm, _) = b;
            var state = services.State;
            var key = InputKeys.Video(Clip);

            // Two looks: one with the clip, one plain — captured from the state, which ends up plain.
            services.BulkEdit(() =>
            {
                state.Pattern.Kind = PatternKind.Media;
                state.Pattern.Media.Source = MediaSource.Video;
                state.Pattern.Media.VideoPath = Clip;
                state.Pattern.Media.Loop = false;
            });
            var vt = new LookConfig { Name = "VT in", Json = LookService.Capture(state) };
            services.BulkEdit(() => state.Pattern.Kind = PatternKind.Grid);
            var plain = new LookConfig { Name = "Plain", Json = LookService.Capture(state) };
            state.LooksAndCues.Looks.Add(vt);
            state.LooksAndCues.Looks.Add(plain);
            Dispatcher.UIThread.RunJobs();
            var liveOpens = fakes.Sources.Count(s => s.Wanted.Key == key);      // the clip was on air for a moment: one live open, now retired
            Assert.Equal(PreRoll.State.Missing, services.Video.PreRollStateOf(key));

            var stack = services.CueStack.Stack;
            var vtCue = new RunCueConfig { Number = "01.010", Name = "VT" };
            vtCue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = "VT in" });
            var plainCue = new RunCueConfig { Number = "01.020", Name = "Plain" };
            plainCue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = "Plain" });
            stack.Cues.Add(vtCue);
            stack.Cues.Add(plainCue);

            // Standby on the clip cue: the pool opens the clip at once and holds it on its first frame.
            services.CueStack.Standby(vtCue.Id);
            var held = fakes.Sources.Where(s => s.Wanted.Key == key && !s.Disposed && s.FadeMs < 0).ToList();
            var first = Assert.Single(held);
            Assert.Equal(1, first.Holds);
            Assert.True(first.IsHeld);
            Assert.False(first.Wanted.Loop);
            Assert.Equal(PreRoll.State.Ready, services.Video.PreRollStateOf(key));
            Assert.Contains(services.Video.MountStatuses, m => m.Key == key && m.Status == "pre-rolled");
            Assert.Equal("PRE-ROLLED", vm.Run.StandbyPreRollGood);
            Assert.Equal("", vm.Run.StandbyPreRollWait);
            Assert.Same(first, InputBus.For(key));                                  // mounted on the bus, ready for the renderers

            // Standby moves to a cue with no clip: the held mount retires like any other.
            services.CueStack.Standby(plainCue.Id);
            Assert.True(first.FadeMs >= 0, "the held clip left when the standby moved on");
            Assert.Equal(PreRoll.State.Missing, services.Video.PreRollStateOf(key));
            Assert.Equal("", vm.Run.StandbyPreRollGood);
            Assert.Equal("", vm.Run.StandbyPreRollWait);

            // Back on the clip cue: a retired source never comes back — a fresh one is opened and held.
            services.CueStack.Standby(vtCue.Id);
            var second = Assert.Single(fakes.Sources, s => s.Wanted.Key == key && !s.Disposed && s.FadeMs < 0);
            Assert.NotSame(first, second);
            Assert.True(second.IsHeld);
            Assert.Equal(liveOpens + 2, fakes.Sources.Count(s => s.Wanted.Key == key));

            // GO: the look lands, the held decoder is released — the same source, no reopen, playing with the look's sound.
            services.CueStack.SetArmed(true, ActionOrigin.Desk);
            var go = vm.Run.Go(ActionOrigin.Desk);
            Assert.True(go.Ok, go.Message);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Clip, state.Pattern.Media.VideoPath);
            Assert.Equal(1, second.Releases);
            Assert.False(second.IsHeld);
            Assert.False(second.Mute);
            Assert.Equal(PreRoll.State.OnAir, services.Video.PreRollStateOf(key));
            Assert.Equal(liveOpens + 2, fakes.Sources.Count(s => s.Wanted.Key == key));
            Assert.Contains(services.Video.MountStatuses, m => m.Key == key && m.Status == "playing");

            // Live wins: with the clip on air, a standby that wants it again holds nothing and says so.
            services.CueStack.Standby(vtCue.Id);
            vm.Run.Tick();
            Assert.Equal(1, second.Holds);
            Assert.Equal(PreRoll.State.OnAir, services.Video.PreRollStateOf(key));
            Assert.Equal("CLIP ON AIR", vm.Run.StandbyPreRollGood);
            services.CueStack.Standby(plainCue.Id);
            vm.Run.Tick();
            Assert.Equal("", vm.Run.StandbyPreRollGood);
            Assert.False(second.IsHeld);                                            // still on air, untouched
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void APreRollNeverTakesADecoderFromALiveSourceAndTheStripSaysSo()
    {
        var b = TestApp.Boot();
        var fakes = AudioFakes.Install(b);
        try
        {
            var services = b.Services;
            var wants = Enumerable.Range(1, VideoEngine_MaxMounts() + 1)
                .Select(i => new MediaLocator.WantedInput(InputKeys.Video($"/shows/clip{i}.mp4"), MediaLocator.WantedKind.VideoFile, $"/shows/clip{i}.mp4", false, false, 100))
                .ToList();
            services.Video.Reconcile(services.Bus.Current, null, DateTime.UtcNow, wants);
            Assert.Equal(VideoEngine_MaxMounts(), fakes.Sources.Count(s => s.IsHeld));
            Assert.Equal(1, services.Video.PreRollWaiting);
            var states = services.Video.PreRollStates(wants);
            Assert.Equal(VideoEngine_MaxMounts(), states.Count(s => s == PreRoll.State.Ready));
            Assert.Equal(PreRoll.State.Missing, states[^1]);
            Assert.Equal(("", "CLIP NOT OPEN"), PreRoll.Words(states));

            // The next reconcile with no pre-roll list retires them all; a live want is never touched by the cap they filled.
            services.Video.Reconcile(services.Bus.Current, null, DateTime.UtcNow);
            Assert.All(fakes.Sources, s => Assert.True(s.FadeMs >= 0));
            Assert.Equal(0, services.Video.PreRollWaiting);
        }
        finally
        {
            b.Dispose();
        }
    }

    private static int VideoEngine_MaxMounts() => Patterns.App.Services.VideoEngine.MaxMounts;
}
