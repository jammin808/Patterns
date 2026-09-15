using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The edge bends' patch, black-level matching's regions and pedestal, and the blend grid's layout — pure.</summary>
public class WarpAndBlackLevelTests
{
    [Fact]
    public void TheBendsMakeAPatchWhoseEdgesPassThroughTheBowAtTheirMiddle()
    {
        var straight = WarpMesh.Cubics(1920, 1080, 0, 0, 0, 0);
        Assert.Equal(12, straight.Length);
        Assert.Equal(new SKPoint(0, 0), straight[0]);
        Assert.Equal(new SKPoint(1920, 0), straight[3]);
        Assert.Equal(new SKPoint(1920, 1080), straight[6]);
        Assert.Equal(new SKPoint(0, 1080), straight[9]);
        Assert.Equal(new SKPoint(960, 0), WarpMesh.EdgeMidpoint(straight, "top"));
        Assert.Equal(new SKPoint(0, 540), WarpMesh.EdgeMidpoint(straight, "left"));

        var bent = WarpMesh.Cubics(1920, 1080, top: 40, right: -30, bottom: 24, left: 16);
        var top = WarpMesh.EdgeMidpoint(bent, "top");
        Assert.Equal(960, top.X, 0.01);
        Assert.Equal(-40, top.Y, 0.01);                       // positive bows the top edge up, out of the picture
        var right = WarpMesh.EdgeMidpoint(bent, "right");
        Assert.Equal(1920 - 30, right.X, 0.01);               // negative pulls the edge in
        Assert.Equal(540, right.Y, 0.01);
        Assert.Equal(1080 + 24, WarpMesh.EdgeMidpoint(bent, "bottom").Y, 0.01);
        Assert.Equal(-16, WarpMesh.EdgeMidpoint(bent, "left").X, 0.01);
        // The corners never move: a bend is not a keystone.
        Assert.Equal(new SKPoint(0, 0), bent[0]);
        Assert.Equal(new SKPoint(1920, 1080), bent[6]);
        Assert.Equal(new[] { new SKPoint(0, 0), new SKPoint(1920, 0), new SKPoint(1920, 1080), new SKPoint(0, 1080) }, WarpMesh.TextureCorners(1920, 1080));

        var p = new ScreenPlacement();
        Assert.False(p.HasBend);
        Assert.False(WarpMesh.HasBend(p));
        p.WarpLeftBow = 5000;                                  // clamped, and now bent
        Assert.Equal(4096, p.WarpLeftBow);
        Assert.True(p.HasBend);
        Assert.Equal(-4096f * 4f / 3f, WarpMesh.ForPlacement(p, 100, 100)[10].X, 0.01);
    }

    [Fact]
    public void TheRegionsCountTheProjectorsThatReachThem()
    {
        Assert.Equal(1, BlackLevel.MaxCoverage(BlendWidths.None));
        Assert.Equal(2, BlackLevel.MaxCoverage(new BlendWidths(200, 0, 0, 0)));            // a row
        Assert.Equal(2, BlackLevel.MaxCoverage(new BlendWidths(0, 0, 0, 120)));            // a stack
        Assert.Equal(4, BlackLevel.MaxCoverage(new BlendWidths(0, 120, 200, 0)));          // a grid's corner

        Assert.Equal(new[] { (SKRectI.Create(0, 0, 1920, 1080), 1) }, BlackLevel.Cells(1920, 1080, BlendWidths.None));

        var row = BlackLevel.Cells(1920, 1080, new BlendWidths(200, 0, 0, 0));
        Assert.Equal(2, row.Count);
        Assert.Equal((SKRectI.Create(0, 0, 200, 1080), 2), row[0]);
        Assert.Equal((SKRectI.Create(200, 0, 1720, 1080), 1), row[1]);

        // A projector in a 2 × 2: a side band and a bottom band, and the corner where they meet counts four.
        var grid = BlackLevel.Cells(1920, 1080, new BlendWidths(0, 0, 200, 120));
        Assert.Equal(4, grid.Count);
        Assert.Contains((SKRectI.Create(0, 0, 1720, 960), 1), grid);
        Assert.Contains((SKRectI.Create(1720, 0, 200, 960), 2), grid);
        Assert.Contains((SKRectI.Create(0, 960, 1720, 120), 2), grid);
        Assert.Contains((SKRectI.Create(1720, 960, 200, 120), 4), grid);
        Assert.Equal(1920 * 1080, grid.Sum(c => c.Rect.Width * c.Rect.Height));                // the cells tile the picture

        // The middle projector of a row of three: two side bands and the picture between; nine cells for one with every edge.
        Assert.Equal(3, BlackLevel.Cells(1920, 1080, new BlendWidths(200, 0, 200, 0)).Count);
        Assert.Equal(9, BlackLevel.Cells(1920, 1080, new BlendWidths(200, 120, 200, 120)).Count);
        // Zones wider than the picture are clamped rather than crossed.
        Assert.All(BlackLevel.Cells(1000, 500, new BlendWidths(800, 0, 800, 0)), c => Assert.True(c.Rect.Width > 0));
    }

    [Fact]
    public void ThePedestalLiftsEachRegionToTheDeepestOverlapsFloor()
    {
        // A row: the picture between the zones gets one black's worth; the overlap nothing.
        Assert.Equal(0.05, BlackLevel.Signal(5, 1, 2, 1.0), 6);
        Assert.Equal(0, BlackLevel.Signal(5, 2, 2, 1.0));
        // A 2 × 2: the single region three blacks, a two-way band one black each, the corner nothing — 1 + 3 = 2 + 2×1 = 4.
        Assert.Equal(0.15, BlackLevel.Signal(5, 1, 4, 1.0), 6);
        Assert.Equal(0.05, BlackLevel.Signal(5, 2, 4, 1.0), 6);
        Assert.Equal(0, BlackLevel.Signal(5, 4, 4, 1.0));
        // Through the gamma: the light is what must come out even, so the signal is its root.
        Assert.Equal(Math.Pow(0.05, 1 / 2.2), BlackLevel.Signal(5, 1, 2, 2.2), 6);
        // Off, or nothing to lift, is nothing.
        Assert.Equal(0, BlackLevel.Signal(0, 1, 4, 1.0));
        Assert.Equal(0, BlackLevel.Signal(5, 1, 1, 1.0));
        Assert.Equal(13, BlackLevel.Level(5, 1, 2, 1.0));
        Assert.Equal(0, BlackLevel.Level(5, 2, 2, 1.0));
        Assert.Equal(255, BlackLevel.Level(100, 1, 4, 1.0));                                     // clamped
    }

    [Fact]
    public void TheBlendGridLaysProjectorsOutWithTheirOverlaps()
    {
        var hd = new SKSizeI(1920, 1080);
        var four = new[] { hd, hd, hd, hd };
        var grid = BlendGridLayout.Positions(four, 2, 2, 200);
        Assert.Equal(new[] { new SKPointI(0, 0), new SKPointI(1720, 0), new SKPointI(0, 880), new SKPointI(1720, 880) }, grid);
        Assert.Equal("2 × 2, 200 px overlaps: the canvas is 3640 × 1960.", BlendGridLayout.Describe(four, grid, 2, 2, 200));

        // …and the derived zones of the top-left projector: a right side and a bottom, no corner of its own.
        var rects = grid.Select(p => SKRectI.Create(p.X, p.Y, hd.Width, hd.Height)).ToList();
        var topLeft = EdgeBlend.Derive(rects[0].ToRaster(), rects.Skip(1).Select(r => r.ToRaster()));
        Assert.Equal(new BlendWidths(0, 0, 200, 200), topLeft);
        Assert.Equal(4, BlackLevel.MaxCoverage(topLeft));

        var row = BlendGridLayout.Positions(new[] { hd, hd, hd }, 3, 1, 192);
        Assert.Equal(new[] { new SKPointI(0, 0), new SKPointI(1728, 0), new SKPointI(3456, 0) }, row);
        var mixed = BlendGridLayout.Positions(new[] { hd, new SKSizeI(1280, 720) }, 2, 1, 100);
        Assert.Equal(new SKPointI(1820, 0), mixed[1]);
        Assert.Equal(3, BlendGridLayout.Positions(new[] { hd, hd, hd }, 2, 2, 100).Count);      // a short list leaves the last cell empty
        Assert.Empty(BlendGridLayout.Positions(Array.Empty<SKSizeI>(), 2, 2, 100));
    }
}
