using Patterns.Core.Model;
using Patterns.Core.Patterns;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

public class PatternRenderTests
{
    [Fact]
    public void FullHeightBarsHaveExactColors()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.ColorBars;
            s.Pattern.Bars.Variant = BarsVariant.Bars100;
        });
        using var bmp = RenderTestHarness.Render(state, 700, 100);

        var expected = new[]
        {
            new SKColor(255, 255, 255), new SKColor(255, 255, 0), new SKColor(0, 255, 255),
            new SKColor(0, 255, 0), new SKColor(255, 0, 255), new SKColor(255, 0, 0), new SKColor(0, 0, 255),
        };
        for (var i = 0; i < 7; i++)
        {
            var x = i * 100 + 50;
            Assert.Equal(expected[i], bmp.GetPixel(x, 50));
        }
    }

    [Fact]
    public void SmpteBandOneUses40PercentFlanksAnd75PercentWhite()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.ColorBars;
            s.Pattern.Bars.Variant = BarsVariant.Smpte;
            s.Pattern.Bars.FullRange = false;
        });
        using var bmp = RenderTestHarness.Render(state, 1920, 1080);

        Assert.Equal(new SKColor(104, 104, 104), bmp.GetPixel(60, 100));    // left 40% grey flank
        Assert.Equal(new SKColor(180, 180, 180), bmp.GetPixel(300, 100));   // first 75% bar
        Assert.Equal(new SKColor(104, 104, 104), bmp.GetPixel(1860, 100));  // right flank
        Assert.Equal(new SKColor(16, 16, 16), bmp.GetPixel(500, 1050));     // bottom black region
    }

    [Fact]
    public void SmpteFullRangeStretchesWhite()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.ColorBars;
            s.Pattern.Bars.Variant = BarsVariant.Smpte;
            s.Pattern.Bars.FullRange = true;
        });
        using var bmp = RenderTestHarness.Render(state, 1920, 1080);

        // 100% white block in the bottom band: columns d + 1.5..3.5 bars.
        var d = 1920 / 8f;
        var bar = (1920 - 2 * d) / 7f;
        var x = (int)(d + 2.5f * bar);
        Assert.Equal(new SKColor(255, 255, 255), bmp.GetPixel(x, 1050));
    }

    [Fact]
    public void CheckerboardAlternatesExactly()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.Checkerboard;
            s.Pattern.Checker.CellSize = 8;
        });
        using var bmp = RenderTestHarness.Render(state, 64, 64);

        Assert.Equal(SKColors.White, bmp.GetPixel(3, 3));
        Assert.Equal(SKColors.Black, bmp.GetPixel(11, 3));
        Assert.Equal(SKColors.Black, bmp.GetPixel(3, 11));
        Assert.Equal(SKColors.White, bmp.GetPixel(11, 11));
        // Boundary pixel: last pixel of first cell is white, first of second is black.
        Assert.Equal(SKColors.White, bmp.GetPixel(7, 0));
        Assert.Equal(SKColors.Black, bmp.GetPixel(8, 0));
    }

    [Fact]
    public void GridCenterLinesLandOnCenterPixels()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.Grid;
            s.Pattern.Grid.CellSize = 50;
            s.Pattern.Grid.LineWidth = 1;
            s.Pattern.Grid.ShowCenterCross = false;
            s.Pattern.Grid.ShowBorder = false;
            s.Pattern.Grid.ShowLabel = false;
        });
        using var bmp = RenderTestHarness.Render(state, 200, 200);

        Assert.Equal(SKColors.White, bmp.GetPixel(100, 10));  // vertical center line
        Assert.Equal(SKColors.White, bmp.GetPixel(150, 10));  // +1 cell
        Assert.Equal(SKColors.White, bmp.GetPixel(50, 10));   // −1 cell
        Assert.Equal(SKColors.White, bmp.GetPixel(10, 100));  // horizontal center line
        Assert.Equal(SKColors.Black, bmp.GetPixel(101, 10));  // adjacent column untouched
        Assert.Equal(SKColors.Black, bmp.GetPixel(125, 10));  // mid-cell
    }

    [Fact]
    public void FlatFieldLevelScales()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.FlatField;
            s.Pattern.FlatField.Color = "#FFFFFF";
            s.Pattern.FlatField.LevelPct = 50;
            s.Pattern.FlatField.ShowLabel = false;
        });
        using var bmp = RenderTestHarness.Render(state, 32, 32);
        Assert.Equal(new SKColor(128, 128, 128), bmp.GetPixel(16, 16));
    }

    [Fact]
    public void RampStepsHitBlackAndWhite()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.Ramp;
            s.Pattern.Ramp.Variant = RampVariant.Steps;
            s.Pattern.Ramp.Steps = 16;
            s.Pattern.Ramp.ShowMarkers = false;
        });
        using var bmp = RenderTestHarness.Render(state, 1600, 100);
        Assert.Equal(SKColors.Black, bmp.GetPixel(10, 50));
        Assert.Equal(SKColors.White, bmp.GetPixel(1590, 50));
        Assert.Equal(new SKColor(17, 17, 17), bmp.GetPixel(150, 50)); // step 2: round(1*255/15)=17
    }

    [Fact]
    public void LedWallDrawsTileBordersOnExactColumns()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.LedWall;
            s.Pattern.LedWall.TileWidth = 100;
            s.Pattern.LedWall.TileHeight = 100;
            s.Pattern.LedWall.DefineByCanvas = false;
            s.Pattern.LedWall.Columns = 4;
            s.Pattern.LedWall.Rows = 2;
            s.Pattern.LedWall.AlternateTint = false;
            s.Pattern.LedWall.ShowCenterCross = false;
            s.Pattern.LedWall.ShowInfo = false;
        });
        using var bmp = RenderTestHarness.Render(state, 400, 200);

        foreach (var x in new[] { 0, 100, 200, 300, 399 })
        {
            Assert.Equal(SKColors.White, bmp.GetPixel(x, 60));
        }
        Assert.Equal(SKColors.White, bmp.GetPixel(60, 100)); // row border
        Assert.Equal(SKColors.Black, bmp.GetPixel(99, 60));  // inside tile, next to border
    }

    [Fact]
    public void BlendGrayCheckIsFlatMidGray()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.ProjectionBlend;
            s.Pattern.Blend.Projectors = 2;
            s.Pattern.Blend.NativeWidth = 400;
            s.Pattern.Blend.NativeHeight = 240;
            s.Pattern.Blend.OverlapPx = 100;
            s.Pattern.Blend.GrayCheck = true;
            s.Pattern.Blend.ShowInfo = false;
        });
        // canvas 700×240 rendered 1:1
        using var bmp = RenderTestHarness.Render(state, 700, 240);
        Assert.Equal(new SKColor(128, 128, 128), bmp.GetPixel(350, 120));
        Assert.Equal(new SKColor(128, 128, 128), bmp.GetPixel(20, 120));
    }

    [Fact]
    public void BlendGridMarksItsZonesAlongAndAcross()
    {
        // 2 × 2 of 400×240 with 100 px along and 60 px across: canvas 700×420; the zone edges are marked
        // in the accent colour both ways, the corner where the zones cross included, and the rest is black.
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.ProjectionBlend;
            s.Pattern.Blend.Projectors = 2;
            s.Pattern.Blend.Rows = 2;
            s.Pattern.Blend.NativeWidth = 400;
            s.Pattern.Blend.NativeHeight = 240;
            s.Pattern.Blend.OverlapPx = 100;
            s.Pattern.Blend.OverlapAcrossPx = 60;
            s.Pattern.Blend.ShowGrids = false;
            s.Pattern.Blend.ShowRamps = false;
            s.Pattern.Blend.HueCode = false;
            s.Pattern.Blend.ShowInfo = false;
            s.Pattern.Blend.ShowMarkers = true;
        });
        using var bmp = RenderTestHarness.Render(state, 700, 420);
        static bool Lit(SKColor c) => c.Red + c.Green + c.Blue > 120;
        Assert.True(Lit(bmp.GetPixel(300, 40)), "left edge of the along zone");
        Assert.True(Lit(bmp.GetPixel(399, 40)), "right edge of the along zone");
        Assert.True(Lit(bmp.GetPixel(40, 180)), "top edge of the across zone");
        Assert.True(Lit(bmp.GetPixel(40, 239)), "bottom edge of the across zone");
        Assert.True(Lit(bmp.GetPixel(300, 200)), "the along zone's edge runs through the across zone");
        Assert.False(Lit(bmp.GetPixel(150, 120)), "inside P1, away from its frame");
        Assert.False(Lit(bmp.GetPixel(550, 330)), "inside P4, away from its frame");

        // The grey check is flat through both zones and their corner.
        state.Pattern.Blend.GrayCheck = true;
        using var grey = RenderTestHarness.Render(state, 700, 420);
        Assert.Equal(new SKColor(128, 128, 128), grey.GetPixel(350, 210));
        Assert.Equal(new SKColor(128, 128, 128), grey.GetPixel(350, 100));
        Assert.Equal(new SKColor(128, 128, 128), grey.GetPixel(100, 210));
    }

    [Fact]
    public void BlackoutBeatsEverything()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.ColorBars;
            s.Blackout = true;
            s.Overlays.Clock.Enabled = true;
        });
        using var bmp = RenderTestHarness.Render(state, 100, 100);
        for (var x = 0; x < 100; x += 7)
        {
            for (var y = 0; y < 100; y += 7)
            {
                Assert.Equal(SKColors.Black, bmp.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void SpanViewportsStitchSeamlessly()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.LedWall;
            s.Pattern.LedWall.TileWidth = 64;
            s.Pattern.LedWall.TileHeight = 64;
            s.Pattern.LedWall.DefineByCanvas = true;
            s.Pattern.LedWall.CanvasWidth = 200;
            s.Pattern.LedWall.CanvasHeight = 100;
        });
        var snap = RenderTestHarness.Snap(state);

        using var full = RenderTestHarness.Render(snap, 200, 100, reference: new SKSizeI(200, 100));
        using var left = RenderTestHarness.Render(snap, 100, 100, reference: new SKSizeI(200, 100), origin: new SKPointI(0, 0));
        using var right = RenderTestHarness.Render(snap, 100, 100, reference: new SKSizeI(200, 100), origin: new SKPointI(100, 0));

        for (var y = 0; y < 100; y += 3)
        {
            for (var x = 0; x < 100; x += 3)
            {
                Assert.Equal(full.GetPixel(x, y), left.GetPixel(x, y));
                Assert.Equal(full.GetPixel(x + 100, y), right.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void EveryPatternKindRendersWithoutFaulting()
    {
        foreach (var kind in Enum.GetValues<PatternKind>())
        {
            var state = RenderTestHarness.State(s =>
            {
                s.Pattern.Kind = kind;
                s.Overlays.Clock.Enabled = true;
                s.Overlays.Message.Enabled = true;
                s.Countdown.Enabled = true;
                s.Countdown.TargetTime = "23:00";
            });
            using var bmp = RenderTestHarness.Render(state, 640, 360, frame: 3);

            // The engine contains renderer faults by drawing an error card — the test
            // asserts the renderer really ran clean.
            var engineFault = bmp.GetPixel(320, 180) == new SKColor(0x14, 0x06, 0x06);
            Assert.False(engineFault, $"{kind} drew the error card");
        }
    }

    [Fact]
    public void BrandedGridUsesBrandBackground()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.Grid;
            s.Pattern.Grid.ShowLabel = false;
            s.Brand.ApplyToPatterns = true;
            s.Brand.BackgroundColor = "#102030";
        });
        using var bmp = RenderTestHarness.Render(state, 200, 200);
        Assert.Equal(new SKColor(0x10, 0x20, 0x30), bmp.GetPixel(120, 30));
    }
}
