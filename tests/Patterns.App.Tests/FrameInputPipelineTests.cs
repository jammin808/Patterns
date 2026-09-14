using Patterns.App.Rendering;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// One frame, one world: the pipeline captures the program and the preview once at the frame's
/// start, draws from that capture, and reports that capture's version — never what the bus
/// holds by the time the frame ends. A calibration frame draws no show and says so.
/// </summary>
public class FrameInputPipelineTests
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
    public void APublishLandingMidFrameIsNeitherDrawnNorReportedByThatFrame()
    {
        var state = Flat("#FFFFFF");
        var bus = new SnapshotBus(state);
        bus.Publish(state);
        var captured = bus.Current.Version;
        using var pipeline = new RenderPipeline(bus, Output());
        var info = new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);

        // The screen-id override runs inside the frame, after the capture and before the draw:
        // the show goes black on the bus while this frame is in flight.
        var published = 0;
        pipeline.ScreenIdOverride = () =>
        {
            if (published++ == 0)
            {
                state.Pattern.FlatField.Color = "#000000";
                bus.Publish(state);
            }
            return null;
        };
        pipeline.Render(surface.Canvas, 320, 180, 1.0);
        Assert.Equal(1, published);
        Assert.Equal(captured + 1, bus.Current.Version);

        // The frame drew the world it captured (white) …
        using var first = Pixels(surface, info);
        Assert.Equal(255, first.GetPixel(160, 90).Red);
        // … and reported that version, not the one that landed while it drew.
        Assert.NotNull(pipeline.Budget.FirstShown(captured));
        Assert.Null(pipeline.Budget.FirstShown(captured + 1));

        // The next frame is the new world's: black, and the new version shown.
        pipeline.Render(surface.Canvas, 320, 180, 1.0);
        using var second = Pixels(surface, info);
        Assert.Equal(0, second.GetPixel(160, 90).Red);
        Assert.NotNull(pipeline.Budget.FirstShown(captured + 1));
    }

    [Fact]
    public void APreviewPaneDrawsTheSandboxItCapturedEvenWhenTheSandboxClosesMidFrame()
    {
        var state = Flat("#FFFFFF");
        var bus = new SnapshotBus(state);
        bus.Publish(state);
        var sandbox = Flat("#FF0000");
        bus.PublishSandbox(sandbox);
        var preview = PipelineViewport.Preview with { ReferenceSize = new SKSizeI(320, 180) };
        using var pipeline = new RenderPipeline(bus, preview);
        var info = new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);

        var closed = 0;
        pipeline.ScreenIdOverride = () =>
        {
            if (closed++ == 0) bus.ClearSandbox();                        // the sandbox closes while the frame is in flight
            return null;
        };
        pipeline.Render(surface.Canvas, 320, 180, 1.0);
        using var drawn = Pixels(surface, info);
        Assert.Equal(255, drawn.GetPixel(160, 90).Red);                    // the sandbox's red, the capture's
        Assert.Equal(0, drawn.GetPixel(160, 90).Green);
        Assert.Null(bus.Sandbox);

        pipeline.Render(surface.Canvas, 320, 180, 1.0);
        using var after = Pixels(surface, info);
        Assert.Equal(255, after.GetPixel(160, 90).Green);                  // the next frame is the program's white
    }

    [Fact]
    public void ACalibrationFrameCountsAsAFrameAndNeverAsTheShowsVersionShown()
    {
        var state = Flat("#FFFFFF");
        var bus = new SnapshotBus(state);
        bus.Publish(state);
        var version = bus.Current.Version;
        using var pipeline = new RenderPipeline(bus, Output() with { OutputId = "s1", ScreenId = "s1" });
        var info = new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);

        CalibrationOverlay.Begin();
        try
        {
            pipeline.Render(surface.Canvas, 320, 180, 1.0);
            pipeline.Render(surface.Canvas, 320, 180, 1.0);
            using var dark = Pixels(surface, info);
            Assert.Equal(0, dark.GetPixel(160, 90).Red);                   // black until a pattern is shown
            Assert.Equal(2, pipeline.Budget.Frames);                       // frames, for the pacing
            Assert.Null(pipeline.Budget.FirstShown(version));              // and no show shown: a GO's clock never closes on the structured light
        }
        finally
        {
            CalibrationOverlay.End();
        }

        pipeline.Render(surface.Canvas, 320, 180, 1.0);
        using var back = Pixels(surface, info);
        Assert.Equal(255, back.GetPixel(160, 90).Red);
        Assert.NotNull(pipeline.Budget.FirstShown(version));
    }

    [Fact]
    public void TheCaptureReadsTheSandboxOnceForBothSides()
    {
        var state = Flat("#FFFFFF");
        var bus = new SnapshotBus(state);
        bus.Publish(state);
        var program = FrameInput.Capture(bus, previewSide: false, clock: 1.0);
        Assert.Same(bus.Current, program.Program);
        Assert.Null(program.Preview);
        Assert.Equal(-1, program.PreviewVersion);
        Assert.True(program.IsShow);

        bus.PublishSandbox(Flat("#00FF00"));
        var side = FrameInput.Capture(bus, previewSide: true, clock: 2.0, FrameKind.Show);
        Assert.Same(bus.Sandbox, side.Program);                            // the preview side draws the sandbox
        Assert.Same(bus.Sandbox, side.Preview);                            // and its tile the same sandbox
        Assert.Equal(bus.Sandbox!.Version, side.ProgramVersion);
        var output = FrameInput.Capture(bus, previewSide: false, clock: 2.0, FrameKind.Calibration);
        Assert.Same(bus.Current, output.Program);                          // an output draws the program
        Assert.Same(bus.Sandbox, output.Preview);
        Assert.False(output.IsShow);
    }
}
