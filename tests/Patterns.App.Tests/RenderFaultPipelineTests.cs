using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Rendering;
using Patterns.Core.Model;
using Patterns.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A frame whose draw throws: the fault is counted on the sink, the last good world is drawn
/// again in its place, the frame never claims the new version was shown, and the log gets one
/// note per while rather than one per frame. And the canvas that hosts a pipeline keeps asking
/// for frames across a detach and a reattach.
/// </summary>
public class RenderFaultPipelineTests
{
    private static ShowState Flat(string color)
    {
        var state = new ShowState();
        state.Pattern.Canvas.FollowOutput = true;
        state.Pattern.Kind = PatternKind.FlatField;
        state.Pattern.FlatField.Color = color;
        state.Pattern.FlatField.ShowLabel = false;
        state.Pattern.FlatField.ShowBorder = false;
        state.Overlays.Clock.Enabled = false;
        state.Overlays.Info.Enabled = false;
        state.Overlays.Badge.Enabled = false;
        state.Countdown.Enabled = false;
        state.Transition.Enabled = false;
        return state;
    }

    private static PipelineViewport Output() => new(SinkKind.Output, SKSizeI.Empty, default, null, 1, "test");

    private static SKBitmap Pixels(SKSurface surface, SKImageInfo info)
    {
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    [Fact]
    public void AFaultedFrameDrawsTheLastGoodWorldAndNeverClaimsTheNewOneShown()
    {
        var state = Flat("#FFFFFF");
        var bus = new SnapshotBus(state);
        bus.Publish(state);
        var good = bus.Current.Version;
        using var pipeline = new RenderPipeline(bus, Output());
        var info = new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        pipeline.Render(surface.Canvas, 320, 180, 1.0);                                      // the good frame: white
        Assert.Equal(good, pipeline.Budget.LastGoodVersion);

        // The show goes black, and the next frame's draw throws once, inside the frame.
        state.Pattern.FlatField.Color = "#000000";
        bus.Publish(state);
        var next = bus.Current.Version;
        var calls = 0;
        pipeline.ScreenIdOverride = () => calls++ == 0 ? throw new InvalidOperationException("a source that would not draw") : null;
        pipeline.Render(surface.Canvas, 320, 180, 1.0);

        using var shown = Pixels(surface, info);
        Assert.Equal(255, shown.GetPixel(160, 90).Red);                                        // the last good world, white, not a half-drawn black
        Assert.Equal(1, pipeline.Budget.Faults);
        Assert.Equal(1, pipeline.Budget.ConsecutiveFaults);
        Assert.Contains("a source that would not draw", pipeline.Budget.LastFault);
        Assert.NotNull(pipeline.Budget.LastFaultUtc);
        Assert.Equal(good, pipeline.Budget.LastGoodVersion);
        Assert.Null(pipeline.Budget.FirstShown(next));                                         // the new version was not shown by that frame
        Assert.Equal(2, pipeline.Budget.Frames);                                               // the frame counts for the pacing
        var reading = pipeline.Budget.Read(ShowClock.Seconds);
        Assert.Equal(1, reading.Faults);
        Assert.Contains("1 fault", reading.Words);
        Assert.Contains("FAULT", Glance.SinkWords(reading));

        // The frame after draws whole: black, the run of faults over, the version shown.
        pipeline.Render(surface.Canvas, 320, 180, 1.0);
        using var after = Pixels(surface, info);
        Assert.Equal(0, after.GetPixel(160, 90).Red);
        Assert.Equal(0, pipeline.Budget.ConsecutiveFaults);
        Assert.Equal(next, pipeline.Budget.LastGoodVersion);
        Assert.NotNull(pipeline.Budget.FirstShown(next));
        Assert.Equal(1, pipeline.Budget.Faults);
        Assert.DoesNotContain("FAULT", Glance.SinkWords(pipeline.Budget.Read(ShowClock.Seconds)));
    }

    [Fact]
    public void AFrameThatFaultsWithNoGoodWorldBehindItCountsAndTheNextOneRecovers()
    {
        var state = Flat("#FFFFFF");
        var bus = new SnapshotBus(state);
        bus.Publish(state);
        using var pipeline = new RenderPipeline(bus, Output());
        var info = new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var calls = 0;
        pipeline.ScreenIdOverride = () => calls++ < 3 ? throw new InvalidOperationException("still broken") : null;
        pipeline.Render(surface.Canvas, 320, 180, 1.0);
        pipeline.Render(surface.Canvas, 320, 180, 1.0);
        pipeline.Render(surface.Canvas, 320, 180, 1.0);
        Assert.Equal(3, pipeline.Budget.Faults);
        Assert.Equal(3, pipeline.Budget.ConsecutiveFaults);                                     // the red line: a sink drawing nothing whole
        Assert.Equal(-1, pipeline.Budget.LastGoodVersion);
        Assert.Equal(1, pipeline.FaultNotesLogged);                                             // one note in the log for three frames, not three
        pipeline.Render(surface.Canvas, 320, 180, 1.0);
        Assert.Equal(0, pipeline.Budget.ConsecutiveFaults);
        Assert.Equal(bus.Current.Version, pipeline.Budget.LastGoodVersion);
        using var drawn = Pixels(surface, info);
        Assert.Equal(255, drawn.GetPixel(160, 90).Red);
    }

    [Fact]
    public void TheFaultsReachTheDescribeLineAndTheSuperCheck()
    {
        var budget = new FrameBudget(SinkKind.Output, 2, "Wall");
        var clock = ShowClock.Seconds;
        budget.Record(4, "", clock);
        budget.RecordFault("InvalidOperationException: a source that would not draw", DateTime.UtcNow, clock);
        budget.Record(4, "", clock);
        budget.RecordFault("InvalidOperationException: a source that would not draw", DateTime.UtcNow, clock);
        var reading = budget.Read(clock);
        Assert.Equal(2, reading.Faults);
        Assert.Equal(2, reading.ConsecutiveFaults);
        Assert.Contains("2 faults", reading.Words);
        var line = FrameBudgets.Describe(new[] { reading });
        Assert.Contains("2 render faults in the last minute", line);
        Assert.Contains("last good frame", line);

        var facts = new CheckFacts { RenderWorstMs = 4, RenderFaults = 2, RenderConsecutiveFaults = 2, RenderLastFault = "Output 2 (Wall): InvalidOperationException: a source that would not draw" };
        var amber = Assert.Single(SuperCheck.Run(facts).Rows, r => r.Item == "Render faults");
        Assert.Equal(CheckLight.Amber, amber.Light);
        Assert.Contains("2 in the last minute", amber.Value);
        Assert.Contains("2 in a row", amber.Value);
        var stuck = new CheckFacts { RenderWorstMs = 4, RenderFaults = 9, RenderConsecutiveFaults = FrameBudget.FaultRun, RenderLastFault = facts.RenderLastFault };
        var red = Assert.Single(SuperCheck.Run(stuck).Rows, r => r.Item == "Render faults");
        Assert.Equal(CheckLight.Red, red.Light);
        Assert.Contains("nothing whole", red.Note);
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts { RenderWorstMs = 4 }).Rows, r => r.Item == "Render faults");
    }

    [AvaloniaFact]
    public void TheCanvasKeepsAskingForFramesAcrossADetachAndAReattach()
    {
        var state = Flat("#FFFFFF");
        state.Pattern.Kind = PatternKind.Particles;                                             // animated: a continuous cadence
        var bus = new SnapshotBus(state);
        bus.Publish(state);
        using var pipeline = new RenderPipeline(bus, PipelineViewport.Preview);
        var control = new SkiaCanvasControl { Pipeline = pipeline, Width = 200, Height = 100 };
        var panel = new Panel();
        panel.Children.Add(control);
        var window = new Window { Content = panel, Width = 300, Height = 200 };
        window.Show();
        try
        {
            Pump(6);
            var before = pipeline.Frames;
            Assert.True(before > 0, "the canvas drew while attached");
            Assert.True(control.FrameRequested || pipeline.Cadence == RedrawCadence.Continuous);

            panel.Children.Remove(control);
            Pump(3);
            Assert.False(control.FrameRequested);                                               // detached: nothing outstanding, the pacer forgotten
            Assert.Equal(-1, control.PacerSlot);
            var detached = pipeline.Frames;

            panel.Children.Add(control);
            Pump(6);
            Assert.True(pipeline.Frames > detached, $"frames resumed after the reattach: {detached} → {pipeline.Frames}");
        }
        finally
        {
            window.Close();
        }
    }

    private static void Pump(int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
