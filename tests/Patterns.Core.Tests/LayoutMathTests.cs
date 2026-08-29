using Patterns.Core.Model;
using Patterns.Core.Patterns;
using Patterns.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

public class LayoutMathTests
{
    [Fact]
    public void LedByGridDerivesCanvas()
    {
        var layout = CanvasResolver.Led(new LedWallOptions
        {
            TileWidth = 128, TileHeight = 128, DefineByCanvas = false, Columns = 10, Rows = 6,
        });
        Assert.Equal(new SKSizeI(1280, 768), layout.Canvas);
        Assert.Equal(60, layout.TileCount);
    }

    [Fact]
    public void LedByCanvasDerivesPartialGrid()
    {
        var layout = CanvasResolver.Led(new LedWallOptions
        {
            TileWidth = 104, TileHeight = 104, DefineByCanvas = true, CanvasWidth = 1000, CanvasHeight = 500,
        });
        Assert.Equal(10, layout.Columns);   // ceil(1000/104)
        Assert.Equal(5, layout.Rows);       // ceil(500/104)
        Assert.Equal(new SKSizeI(1000, 500), layout.Canvas); // canvas stays exact — edge tiles partial
    }

    [Fact]
    public void BlendCanvasSubtractsOverlaps()
    {
        var layout = CanvasResolver.Blend(new BlendOptions
        {
            Projectors = 3, NativeWidth = 1920, NativeHeight = 1200, OverlapPx = 400,
        });
        Assert.Equal(new SKSizeI(3 * 1920 - 2 * 400, 1200), layout.Canvas);
        Assert.Equal(0, layout.OriginOf(0));
        Assert.Equal(1520, layout.OriginOf(1));
        Assert.Equal(3040, layout.OriginOf(2));
    }

    [Fact]
    public void BlendVerticalUsesHeightAxis()
    {
        var layout = CanvasResolver.Blend(new BlendOptions
        {
            Projectors = 2, NativeWidth = 1920, NativeHeight = 1200, OverlapPx = 200,
            Orientation = BlendOrientation.Vertical,
        });
        Assert.Equal(new SKSizeI(1920, 2200), layout.Canvas);
    }

    [Fact]
    public void BlendOverlapClampsBelowNative()
    {
        var layout = CanvasResolver.Blend(new BlendOptions
        {
            Projectors = 2, NativeWidth = 800, NativeHeight = 600, OverlapPx = 4096,
        });
        Assert.True(layout.Overlap < 800);
        Assert.True(layout.Canvas.Width > 800);
    }

    [Fact]
    public void VideoWallHonoursPortrait()
    {
        var o = new VideoWallOptions { ElementWidth = 1920, ElementHeight = 1080, Columns = 3, Rows = 2, Portrait = true };
        Assert.Equal(new SKSizeI(3 * 1080, 2 * 1920), CanvasResolver.VideoWall(o));
    }

    [Fact]
    public void ResolveFollowsOutputByDefault()
    {
        var p = new PatternConfig();
        Assert.Equal(new SKSizeI(2560, 1440), CanvasResolver.Resolve(p, new SKSizeI(2560, 1440)));

        p.Canvas.FollowOutput = false;
        p.Canvas.Width = 1280;
        p.Canvas.Height = 720;
        Assert.Equal(new SKSizeI(1280, 720), CanvasResolver.Resolve(p, new SKSizeI(2560, 1440)));
    }

    [Fact]
    public void MapToReferenceFitsAndCenters()
    {
        var (offset, scale) = CanvasResolver.MapToReference(new SKSizeI(1920, 1080), new SKSizeI(1920, 1080), CanvasScaleMode.Fit);
        Assert.Equal(1f, scale);
        Assert.Equal(new SKPoint(0, 0), offset);

        (offset, scale) = CanvasResolver.MapToReference(new SKSizeI(960, 540), new SKSizeI(1920, 1080), CanvasScaleMode.Fit);
        Assert.Equal(2f, scale);
        Assert.Equal(new SKPoint(0, 0), offset);

        (offset, scale) = CanvasResolver.MapToReference(new SKSizeI(960, 540), new SKSizeI(1920, 1080), CanvasScaleMode.OneToOne);
        Assert.Equal(1f, scale);
        Assert.Equal(new SKPoint(480, 270), offset);

        // Mixed aspect letterboxes.
        (offset, scale) = CanvasResolver.MapToReference(new SKSizeI(1000, 1000), new SKSizeI(2000, 1000), CanvasScaleMode.Fit);
        Assert.Equal(1f, scale);
        Assert.Equal(new SKPoint(500, 0), offset);
    }

    [Theory]
    [InlineData(TileNumbering.RowCol, 0, 0, "1-1")]
    [InlineData(TileNumbering.RowCol, 2, 1, "2-3")]      // col 2, row 1 → "row-col" 1-based
    [InlineData(TileNumbering.Linear, 0, 1, "5")]        // 4 columns: row 1 col 0 → 5
    [InlineData(TileNumbering.Serpentine, 0, 0, "1")]
    [InlineData(TileNumbering.Serpentine, 0, 2, "3")]    // first column runs top-down
    [InlineData(TileNumbering.Serpentine, 1, 2, "4")]    // second column runs bottom-up
    [InlineData(TileNumbering.Serpentine, 1, 0, "6")]
    public void TileLabels(TileNumbering numbering, int col, int row, string expected)
    {
        var layout = new LedLayout(4, 3, 100, 100, new SKSizeI(400, 300));
        Assert.Equal(expected, LedWallPattern.TileLabel(numbering, layout, col, row));
    }

    [Theory]
    [InlineData(BlendCurve.Linear)]
    [InlineData(BlendCurve.Cosine)]
    [InlineData(BlendCurve.SCurve)]
    [InlineData(BlendCurve.Gamma22)]
    public void BlendCurvesAreMonotoneAndBounded(BlendCurve curve)
    {
        Assert.Equal(0, BlendMath.Curve(curve, 0), 6);
        Assert.Equal(1, BlendMath.Curve(curve, 1), 6);
        var prev = -0.0001;
        for (var t = 0.0; t <= 1.0001; t += 0.01)
        {
            var v = BlendMath.Curve(curve, t);
            Assert.InRange(v, 0, 1);
            Assert.True(v >= prev, $"{curve} not monotone at t={t}");
            prev = v;
        }
    }
}
