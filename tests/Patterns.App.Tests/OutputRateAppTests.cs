using Avalonia;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 63: an output on a display slower than the render clock paces to its display, so the
/// chip's frames are the frames the room gets — the display's refresh travels with the viewport
/// and the pipeline measures the clock it is offered.
/// </summary>
public class OutputRateAppTests
{
    private static ScreenInfo Info(string id, int x, int hz)
        => new(id, id, new PixelRect(x, 0, 1920, 1080), 1.0, false, 0, Hz: hz);

    [Fact]
    public void TheDisplaysRefreshTravelsWithTheViewport()
    {
        var screens = new List<ScreenInfo> { Info("a", 0, 50), Info("b", 2200, 0) };
        var placements = new[] { new ScreenPlacement { ScreenId = "a", X = 0 }, new ScreenPlacement { ScreenId = "b", X = 2200 } };
        var viewports = OutputWindowManager.BuildViewports(placements, screens);
        Assert.Equal(50, viewports.Single(v => v.Screen.Id == "a").Viewport.DisplayHz);
        Assert.Equal(0, viewports.Single(v => v.Screen.Id == "b").Viewport.DisplayHz);
        Assert.Equal(0, viewports.Single(v => v.Screen.Id == "a").Viewport.TargetFps);   // asked for the display's own rate, as before
    }

    [Fact]
    public void AnOutputOnASlowerDisplayPacesToItOnceItHasHeardTheClock()
    {
        var bus = new SnapshotBus(new ShowState());
        var fifty = new PipelineViewport(SinkKind.Output, new SKSizeI(1920, 1080), default, "a", 1, "Output 1") { DisplayHz = 50 };
        using var pipeline = new RenderPipeline(bus, fifty);
        Assert.Equal(0, pipeline.PresentFps);                // no beat heard yet: every beat
        for (var i = 0; i <= 120; i++) pipeline.NoteVsync(i / 60.0);   // two seconds of a 60 Hz clock
        Assert.InRange(pipeline.ClockHz, 59, 61);
        Assert.Equal(50, pipeline.PresentFps);               // the display's 50, not the clock's 60

        // The same display under a 50 Hz clock: unpaced — the clock is the display's own.
        using var matched = new RenderPipeline(bus, fifty);
        for (var i = 0; i <= 100; i++) matched.NoteVsync(i / 50.0);
        Assert.Equal(0, matched.PresentFps);

        // A 60 Hz display under a 50 Hz clock (round 64): the budget's reading says the clock limits
        // it once the sink has drawn a frame — the words the Machine page, STATE and the brief carry.
        var sixty = new PipelineViewport(SinkKind.Output, new SKSizeI(1920, 1080), default, "c", 3, "Output 3") { DisplayHz = 60 };
        using var starved = new RenderPipeline(bus, sixty);
        for (var i = 0; i <= 100; i++) starved.NoteVsync(i / 50.0);
        using (var surface = SKSurface.Create(new SKImageInfo(64, 36, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            starved.Render(surface.Canvas, 64, 36, 1);
        }
        var reading = FrameBudgets.Readings(ShowClock.Seconds).Single(r => r.Kind == SinkKind.Output && r.SinkIndex == 3);
        Assert.Equal(60, reading.DisplayHz);
        Assert.InRange(reading.ClockHz, 49, 51);
        Assert.True(reading.ClockLimit.Limited);
        Assert.StartsWith("Output 3: 60 Hz needed, render clock 50.0 Hz", Assert.Single(FrameBudgets.ClockLimited(FrameBudgets.Readings(ShowClock.Seconds))));

        // Asked for 60 on a 50 Hz display: 50 at once, before any beat is heard.
        using var asked = new RenderPipeline(bus, fifty with { TargetFps = 60 });
        Assert.Equal(50, asked.PresentFps);
        // Asked for 30: 30.
        using var slow = new RenderPipeline(bus, fifty with { TargetFps = 30 });
        Assert.Equal(30, slow.PresentFps);
    }
}
