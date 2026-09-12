using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// What a publish costs the desk, and what it must never carry: a drag is one publish per move,
/// a published snapshot shares what did not move, the autosave writes off the UI thread and a
/// restart's own save still lands last, a thumbnail is the pattern alone, and a clock's redraw
/// timer wakes on the second.
/// </summary>
public class PublishHygieneTests
{
    [AvaloniaFact]
    public void ADragIsOnePublishPerMoveAndCopiesOnlyItsSection()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            vm.IsSandboxActive = false;
            Dispatcher.UIThread.RunJobs();
            var publishes = 0;
            services.SnapshotPublished += () => publishes++;

            var before = services.Bus.Current;
            vm.DragPlace(HitKind.Clock, 4, 6);              // an overlay: two properties, one movement of the hand
            Assert.Equal(1, publishes);
            Assert.Equal(4, services.Bus.Current.State.Overlays.Clock.OffsetXPct);
            Assert.Equal(6, services.Bus.Current.State.Overlays.Clock.OffsetYPct);
            var shared = SnapshotClone.SharedSections(before.State, services.Bus.Current.State);
            Assert.Contains(nameof(ShowState.Pattern), shared);
            Assert.Contains(nameof(ShowState.LooksAndCues), shared);
            Assert.DoesNotContain(nameof(ShowState.Overlays), shared);
            Assert.Same(before.Rig, services.Bus.Current.Rig);

            publishes = 0;
            vm.DragPlace(HitKind.Layer1, 10, 12);           // a layer's box: the pattern section alone
            Assert.Equal(1, publishes);
            Assert.Equal(10, services.Bus.Current.State.Pattern.Layer1.XPct);
            Assert.DoesNotContain(nameof(ShowState.Pattern), SnapshotClone.SharedSections(before.State, services.Bus.Current.State));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThePublishedShowIsImmutableAndThePreviewSharesWhatItDidNotEdit()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = (b.Services, b.Vm);
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.Grid;
            var air = services.Bus.Current;
            var preview = services.Bus.Sandbox!;
            Assert.Throws<InvalidOperationException>(() => air.State.Pattern.Kind = PatternKind.Focus);
            Assert.Throws<InvalidOperationException>(() => preview.State.Overlays.Clock.Enabled = !preview.State.Overlays.Clock.Enabled);

            vm.State.Pattern.Grid.CellSize = 33;             // the operator edits the preview
            Assert.Same(air.State, services.Bus.Current.State);          // the air did not move at all
            Assert.Same(preview.State.Overlays, services.Bus.Sandbox!.State.Overlays);
            Assert.Equal(33, services.Bus.Sandbox!.State.Pattern.Grid.CellSize);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheAutosaveWritesOffTheUiThreadAndARestartsSaveLandsLast()
    {
        var b = TestApp.Boot();
        try
        {
            var services = b.Services;
            services.State.Name = "Background";
            services.SaveInBackground();
            TestApp.Pump(services.PendingSaves.ContinueWith(_ => true));
            Assert.Contains("\"Background\"", File.ReadAllText(services.Store.SettingsPath));

            // Queued writes land in order, and the synchronous save waits for them: the file ends on the newest show.
            services.State.Name = "Queued";
            services.SaveInBackground();
            services.State.Name = "Latest";
            services.SaveNow();
            var saved = JsonUtil.Deserialize<ShowState>(File.ReadAllText(services.Store.SettingsPath))!;
            Assert.Equal("Latest", saved.Name);
        }
        finally
        {
            b.Dispose();
        }
    }

    [Fact]
    public void AThumbnailIsThePatternAlone()
    {
        var show = new ShowState();
        show.Overlays.Badge.Enabled = true;
        show.Overlays.Weather.Enabled = true;
        show.Overlays.Pip.Enabled = true;
        show.Overlays.Clock.Enabled = true;
        show.Countdown.Enabled = true;
        var design = new LowerThirdDesign { Name = "Name strap" };
        show.LowerThirds.Designs.Add(design);
        show.LowerThirds.Show(design, new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc));
        show.Brand.PrimaryColor = "#123456";
        var tile = new PatternConfig { Kind = PatternKind.Focus };

        var state = ThumbnailRenderer.ThumbnailState(show, tile);

        Assert.Equal(PatternKind.Focus, state.Pattern.Kind);
        Assert.Equal("#123456", state.Brand.PrimaryColor);            // the brand stays: it colours the pattern
        Assert.False(state.Overlays.Badge.Enabled);
        Assert.False(state.Overlays.Weather.Enabled);
        Assert.False(state.Overlays.Pip.Enabled);
        Assert.False(state.Overlays.Clock.Enabled);
        Assert.False(state.Countdown.Enabled);
        Assert.False(state.LowerThirds.IsShowing);
        Assert.True(show.LowerThirds.IsShowing);                       // the show itself is untouched
        Assert.True(show.Overlays.Badge.Enabled);
    }

    [Theory]
    [InlineData(0, 1005)]
    [InlineData(400, 605)]
    [InlineData(999, 6)]
    public void TheClockTimerWakesJustAfterTheSecondTurns(int millisecond, int expectedMs)
    {
        var now = new DateTime(2026, 9, 12, 10, 0, 0).AddMilliseconds(millisecond);
        Assert.Equal(expectedMs, (int)SkiaCanvasControl.UntilNextSecond(now).TotalMilliseconds);
    }
}
