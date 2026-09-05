using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The wall's dead strips as pure maths: what is dropped, where raster pixels land on the surface, how an output is cut, what a canvas's seams are.</summary>
public class GapMapTests
{
    [Fact]
    public void BuildSortsMergesAndDropsWhatIsNotAGap()
    {
        var map = GapMap.Build(new SKSizeI(1152, 384), new[]
        {
            (GapAxis.Vertical, 768, 200),
            (GapAxis.Vertical, 384, 100),
            (GapAxis.Vertical, 384, 200),      // the same position twice: the widest wins
            (GapAxis.Vertical, 0, 50),         // at the raster's left edge: no gap
            (GapAxis.Vertical, 1152, 50),      // at its right edge: no gap
            (GapAxis.Horizontal, 200, 0),      // no width: no gap
            (GapAxis.Horizontal, 192, 40),
        });

        Assert.Equal(new[] { new WallStrip(384, 200, 0), new WallStrip(768, 200, 200) }, map.Vertical);
        Assert.Equal(new[] { new WallStrip(192, 40, 0) }, map.Horizontal);
        Assert.Equal(new SKSizeI(1152, 384), map.Raster);
        Assert.Equal(new SKSizeI(1552, 424), map.Virtual);
        Assert.Equal(3, map.Count);
        Assert.False(map.IsEmpty);
        Assert.Equal(584, map.Vertical[0].VirtualEnd);
        Assert.Equal(968, map.Vertical[1].VirtualStart);

        Assert.True(GapMap.Empty.IsEmpty);
        Assert.Equal(SKSizeI.Empty, GapMap.Empty.Virtual);
        var none = GapMap.Build(new SKSizeI(100, 100), Array.Empty<(GapAxis, int, int)>());
        Assert.True(none.IsEmpty);
        Assert.Equal(new SKSizeI(100, 100), none.Virtual);
        Assert.StartsWith("No gaps", none.Summary);
        Assert.Contains("2 vertical · 1 horizontal", map.Summary);
        Assert.Contains("1552×424", map.Summary);
        Assert.Contains("1152×384", map.Summary);
    }

    [Fact]
    public void RasterPixelsMovePastTheStripsBeforeThem()
    {
        // Three pillars of 100 px packed in a 300 px raster, 50 px of air between them.
        var map = GapMap.Build(new SKSizeI(300, 100), new[] { (GapAxis.Vertical, 100, 50), (GapAxis.Vertical, 200, 50) });

        Assert.Equal(0, map.VirtualX(0));
        Assert.Equal(99, map.VirtualX(99));
        Assert.Equal(150, map.VirtualX(100));
        Assert.Equal(249, map.VirtualX(199));
        Assert.Equal(300, map.VirtualX(200));
        Assert.Equal(399, map.VirtualX(299));
        Assert.Equal(7, map.VirtualY(7));
        Assert.Equal(new SKPointI(300, 0), map.VirtualOrigin(new SKPointI(200, 0)));

        Assert.Equal(new SKRectI(0, 0, 400, 100), map.VirtualRect(new SKRectI(0, 0, 300, 100)));
        Assert.Equal(new SKRectI(150, 0, 250, 100), map.VirtualRect(new SKRectI(100, 0, 200, 100)));   // the middle pillar: no strip inside it
        Assert.Equal(new SKRectI(0, 0, 250, 100), map.VirtualRect(new SKRectI(0, 0, 200, 100)));       // the first two: the strip between them inside

        Assert.Equal(new[] { new SKRectI(100, 0, 150, 100), new SKRectI(250, 0, 300, 100) },
            map.StripsIn(new SKRectI(0, 0, 400, 100)).ToList());
        // A region of the surface sees only the strips that cross it, in its own coordinates.
        Assert.Equal(new[] { new SKRectI(0, 0, 30, 100) }, map.StripsIn(new SKRectI(120, 0, 220, 100)).ToList());
        Assert.Empty(map.StripsIn(new SKRectI(150, 0, 250, 100)));
    }

    [Fact]
    public void ARegionIsCutIntoRunsOfRealPixelsAtEveryStripInsideIt()
    {
        var map = GapMap.Build(new SKSizeI(300, 100), new[]
        {
            (GapAxis.Vertical, 100, 50), (GapAxis.Vertical, 200, 50), (GapAxis.Horizontal, 50, 20),
        });

        var slices = map.Slices(new SKRectI(0, 0, 300, 100));
        Assert.Equal(6, slices.Count);
        Assert.Equal(new WallSlice(new SKRectI(0, 0, 100, 50), new SKRectI(0, 0, 100, 50)), slices[0]);
        Assert.Equal(new WallSlice(new SKRectI(100, 0, 200, 50), new SKRectI(150, 0, 250, 50)), slices[1]);
        Assert.Equal(new WallSlice(new SKRectI(200, 0, 300, 50), new SKRectI(300, 0, 400, 50)), slices[2]);
        Assert.Equal(new WallSlice(new SKRectI(0, 50, 100, 100), new SKRectI(0, 70, 100, 120)), slices[3]);
        Assert.Equal(new WallSlice(new SKRectI(200, 50, 300, 100), new SKRectI(300, 70, 400, 120)), slices[5]);

        // A member whose only strips are at its edges is one run, moved past them.
        var one = Assert.Single(map.Slices(new SKRectI(100, 0, 200, 50)));
        Assert.Equal(new SKRectI(100, 0, 200, 50), one.Raster);
        Assert.Equal(new SKRectI(150, 0, 250, 50), one.Virtual);
        Assert.Empty(map.Slices(SKRectI.Empty));
        Assert.Single(GapMap.Empty.Slices(new SKRectI(0, 0, 300, 100)));
    }

    [Fact]
    public void ACanvasSeamsItsMembersAndKeepsTheirOwnStrips()
    {
        var plain = Array.Empty<WallGap>();
        var withOwn = new[] { new WallGap { Axis = GapAxis.Vertical, At = 960, Size = 30 } };
        // A 2 × 2 wall of 1920 × 1080 displays, 40 px of bezel between columns and 24 px between rows,
        // and the top-right display a wall controller's pair with its own bezel in the middle.
        var map = GapMap.ForCanvas(new SKSizeI(3840, 2160), new[]
        {
            (new SKRectI(0, 0, 1920, 1080), (IEnumerable<WallGap>)plain),
            (new SKRectI(1920, 0, 3840, 1080), withOwn),
            (new SKRectI(0, 1080, 1920, 2160), plain),
            (new SKRectI(1920, 1080, 3840, 2160), plain),
        }, 40, 24);

        Assert.Equal(new[] { new WallStrip(1920, 40, 0), new WallStrip(2880, 30, 40) }, map.Vertical);
        Assert.Equal(new[] { new WallStrip(1080, 24, 0) }, map.Horizontal);
        Assert.Equal(new SKSizeI(3910, 2184), map.Virtual);
        Assert.Equal(new SKPointI(1960, 1104), map.VirtualOrigin(new SKPointI(1920, 1080)));

        var none = GapMap.ForCanvas(new SKSizeI(3840, 1080), new[]
        {
            (new SKRectI(0, 0, 1920, 1080), (IEnumerable<WallGap>)plain),
            (new SKRectI(1920, 0, 3840, 1080), plain),
        }, 0, 0);
        Assert.True(none.IsEmpty);

        var screen = GapMap.ForScreen(new SKSizeI(600, 200), new[]
        {
            new WallGap { Axis = GapAxis.Vertical, At = 200, Size = 100 },
            new WallGap { Axis = GapAxis.Vertical, At = 400, Size = 100 },
        });
        Assert.Equal(new SKSizeI(800, 200), screen.Virtual);
        Assert.Equal(3, screen.Slices(new SKRectI(0, 0, 600, 200)).Count);
    }
}
