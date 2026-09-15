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
        // FPS off: the size and the shape still say what the sink is.
        Assert.Equal("Output 3 · 1080×1920 · 9:16 · LedWall",
            OverlayRenderer.InfoChipText("Output 3", new SKSizeI(1080, 1920), PatternKind.LedWall, false, 60, 60, 60, 60));
    }
}
