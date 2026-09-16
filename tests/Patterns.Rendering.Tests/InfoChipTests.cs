using Patterns.Core.Model;
using Patterns.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Round 63: the tech info chip says the sink's pixels and shape, and the frames it draws against the rate it presents at and its display.</summary>
public class InfoChipTests
{
    [Fact]
    public void TheChipNamesTheSizeTheShapeTheKindAndTheRatesItIsActuallyRunningAt()
    {
        Assert.Equal("Output 2 · 1920×1080 · 16:9 · Grid · 50.0 fps of 50 · 50 Hz display (60 asked)",
            OverlayRenderer.InfoChipText("Output 2", new SKSizeI(1920, 1080), PatternKind.Grid, showFps: true, measuredFps: 49.97, presentFps: 50, wantedFps: 60, displayHz: 50));
        // The display's own rate, paced to it: no "asked" — nothing was asked beyond the display.
        Assert.Equal("Output 1 · 3840×2160 · 16:9 · Media · 50.0 fps of 50 · 50 Hz display",
            OverlayRenderer.InfoChipText("Output 1", new SKSizeI(3840, 2160), PatternKind.Media, true, 50.02, 50, 0, 50));
        // A pane: no display, unpaced — the frames alone.
        Assert.Equal("Preview · 1280×720 · 16:9 · ColorBars · 59.9 fps",
            OverlayRenderer.InfoChipText("Preview", new SKSizeI(1280, 720), PatternKind.ColorBars, true, 59.94, 0, 0, 0));
        // A 60 Hz display under a 50 Hz render clock (round 64): the chip says the clock limits it — never a nominal 60.
        Assert.Equal("Output 2 · 1920×1080 · 16:9 · Grid · 49.9 fps · 60 Hz display · render clock 50.0 Hz — LIMITED BY RENDER CLOCK",
            OverlayRenderer.InfoChipText("Output 2", new SKSizeI(1920, 1080), PatternKind.Grid, true, 49.9, 0, 0, 60, clockHz: 50.0));
        // The same display under its own clock: no such words.
        Assert.Equal("Output 2 · 1920×1080 · 16:9 · Grid · 59.9 fps · 60 Hz display",
            OverlayRenderer.InfoChipText("Output 2", new SKSizeI(1920, 1080), PatternKind.Grid, true, 59.9, 0, 0, 60, clockHz: 60.1));
        // FPS off: the size and the shape still say what the sink is.
        Assert.Equal("Output 3 · 1080×1920 · 9:16 · LedWall",
            OverlayRenderer.InfoChipText("Output 3", new SKSizeI(1080, 1920), PatternKind.LedWall, false, 60, 60, 60, 60));
    }

    /// <summary>Round 73: what Patterns is trying to push down the link, and the master rate, beside what the display answers.</summary>
    [Fact]
    public void TheChipSaysWhatPatternsPushesAndTheMasterRateBesideWhatTheDisplayAnswers()
    {
        // A contract names the raster and the rate: the chip says the intent (4K at 50) beside the answer (the window's 1080 lines at 50).
        Assert.Equal("Output 2 · 1920×1080 · 16:9 · Grid · pushing 3840×2160 16:9 @ 50 · master 60 · 50.0 fps of 50 · 50 Hz display (60 asked)",
            OverlayRenderer.InfoChipText("Output 2", new SKSizeI(1920, 1080), PatternKind.Grid, true, 49.97, 50, 60, 50, pushPx: new SKSizeI(3840, 2160), pushHz: 50, masterFps: 60));
        // No contract: the output's own pixels at the rate the sink is asked for — the master's here.
        Assert.Equal("Output 1 · 1920×1080 · 16:9 · Media · pushing 1920×1080 16:9 @ 60 · master 60 · 59.9 fps of 60 · 60 Hz display",
            OverlayRenderer.InfoChipText("Output 1", new SKSizeI(1920, 1080), PatternKind.Media, true, 59.94, 60, 60, 60, pushPx: new SKSizeI(1920, 1080), pushHz: 0, masterFps: 60));
        // A screen's own rate over the master: the push says the screen's, the master stays named.
        Assert.Contains("pushing 1920×1080 16:9 @ 30 · master 60",
            OverlayRenderer.InfoChipText("Output 3", new SKSizeI(1920, 1080), PatternKind.Grid, true, 29.97, 30, 30, 60, pushPx: new SKSizeI(1920, 1080), masterFps: 60));
        // No master rate and nothing asked: the display's own, said so.
        Assert.Equal("Output 1 · 3840×2160 · 16:9 · Grid · pushing 3840×2160 16:9 @ the display's own · no master rate · 50.0 fps of 50 · 50 Hz display",
            OverlayRenderer.InfoChipText("Output 1", new SKSizeI(3840, 2160), PatternKind.Grid, true, 50.02, 50, 0, 50, pushPx: new SKSizeI(3840, 2160)));
        // A fractional contract rate reads as the engineer writes it; FPS off keeps the push — it is intent, not a measurement.
        Assert.Equal("Output 2 · 1920×1080 · 16:9 · LedWall · pushing 1920×1080 16:9 @ 59.94 · master 60",
            OverlayRenderer.InfoChipText("Output 2", new SKSizeI(1920, 1080), PatternKind.LedWall, false, 60, 60, 60, 60, pushPx: new SKSizeI(1920, 1080), pushHz: 59.94, masterFps: 60));
        // A pane pushes nothing down any link, and says nothing.
        Assert.Equal("Preview · 1280×720 · 16:9 · ColorBars · 59.9 fps",
            OverlayRenderer.InfoChipText("Preview", new SKSizeI(1280, 720), PatternKind.ColorBars, true, 59.94, 0, 0, 0, masterFps: 60));
    }
}
