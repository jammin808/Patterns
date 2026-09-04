using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.Views.Sections;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>An effect pulse as a library item: fired like a stinger, owning nothing, over the wire and in a cue beside a look.</summary>
public class EffectStingAppTests
{
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

    [AvaloniaFact]
    public void APulseSurgesTheScreensAndOwnsNothing()
    {
        var b = TestApp.Boot();
        try
        {
            EffectImpulses.Clear();
            var vm = b.Vm;
            var item = new StingerItemConfig { Source = StingerSource.EffectPulse, PulsePreset = PulsePreset.Rush, PulseMs = 1200, Kind = StingerKind.Sting };
            vm.State.Stingers.Items.Add(item);
            Assert.Equal("Rush pulse", item.DisplayName);
            Assert.Equal("PULSE", item.KindLabel);
            Assert.True(item.IsPulse);
            Assert.False(item.IsSting); // no ending to choose
            b.Services.AirLabel = "Walk-in";
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Particles;
            var now = DateTime.UtcNow;

            var result = b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id);
            Assert.Equal(ActionStatus.Requested, result.Status);
            Assert.Contains("Pulse", result.Message);
            Assert.False(b.Services.Stingers.OwnsScreens);
            Assert.Equal("", b.Services.Stingers.StingOnAir);
            Assert.Equal("", vm.State.Stingers.PlayingName);
            Assert.Equal("Walk-in", b.Services.AirLabel);
            Assert.Equal(1.0, b.Services.Stingers.MusicGainAt(now.AddSeconds(2)));
            Assert.Equal(PatternKind.Particles, vm.State.Pattern.Kind);
            var fired = EffectImpulses.Current;
            Assert.Equal(PulsePreset.Rush, fired.Preset);
            Assert.Equal(1.2, fired.LengthSeconds, 3);
            Assert.InRange(Math.Abs(fired.StartSeconds - ShowClock.Seconds), 0, 2);
            var entry = b.Services.Journal.Tail(1).Single();
            Assert.Equal(("StingerFire", "Requested"), (entry.Kind, entry.Outcome));

            // STOP has nothing to stop; the pulse runs its course.
            Assert.Equal(ActionStatus.Done, b.Services.Actions.Execute(ShowActionKind.StingerStop, ActionOrigin.Desk).Status);
            Assert.Equal(PulsePreset.Rush, EffectImpulses.Current.Preset);

            // Over the wire it is STINGER n like any other, and STATE says what it is.
            EffectImpulses.Clear();
            var router = new CommandRouter(b.Services);
            Assert.Equal("OK", Pump(router.ExecuteAsync(ControlProtocol.Parse("STINGER 1"))));
            Assert.False(EffectImpulses.Current.IsNone);
            Assert.Contains("\"source\":\"pulse\"", router.StateJson());
            Assert.Contains("\"kind\":\"sting\"", router.StateJson());

            // A cue with a pulse and a look validates clean and fires both.
            vm.NewLookName = "Bars";
            vm.ActivePattern.Kind = PatternKind.ColorBars;
            vm.SaveLookCommand.Execute(null);
            var look = LookService.Find(vm.State, "Bars")!;
            vm.ActivePattern.Kind = PatternKind.Grid;
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "01.010", Name = "Hit" };
            cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.StingerFire, Target = item.Id });
            cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look.Id });
            stack.Cues.Add(cue);
            Dispatcher.UIThread.RunJobs();
            var report = CueValidator.Validate(vm.State, stack, b.Services.ValidationContext);
            Assert.Equal(0, report.BrokenCount);
            Assert.Empty(report.Warnings);
            Assert.Equal("Pulse 'Rush pulse'", CueSummary.DescribeAction(vm.State, cue.Actions[0]));
            EffectImpulses.Clear();
            var fire = b.Services.Actions.Execute(new ShowAction(ShowActionKind.CueFire, cue.Id), ActionOrigin.Desk);
            Assert.True(fire.Ok, fire.Message);
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
            Assert.Equal(PulsePreset.Rush, EffectImpulses.Current.Preset);

            // Deleting it is refused while the cue names it; the Audio page's chip adds one and the row renders.
            vm.RemoveStingerCommand.Execute(item);
            Assert.Contains(item, vm.State.Stingers.Items);
            vm.AddEffectPulseCommand.Execute(null);
            var added = vm.State.Stingers.Items.Last();
            Assert.True(added.IsPulse);
            Assert.Equal("Explosion pulse", added.DisplayName);
            Assert.Equal(StingerKind.Sting, added.Kind);
            var host = new Window { DataContext = vm, Width = 900, Height = 1600, Content = new ScrollViewer { Content = new AudioSection() } };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            using var frame = host.CaptureRenderedFrame();
            Assert.NotNull(frame);
            host.Close();

            // A file item's rules are untouched: a nameless file still reads by its file name.
            var file = new StingerItemConfig { Path = "C:/show/opening.mp4" };
            Assert.Equal("opening.mp4", file.DisplayName);
            Assert.True(file.IsFile);
            Assert.Equal("VOG", file.KindLabel);
        }
        finally
        {
            EffectImpulses.Clear();
            b.Dispose();
        }
    }
}
