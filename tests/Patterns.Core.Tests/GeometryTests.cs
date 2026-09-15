using Patterns.Core.Geometry;
using Patterns.Core.Media;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The core's own geometry and colour — what the rig, the decks, the stream plan and the gap map
/// are argued in without a drawing library — keep the semantics of the canvas types they
/// replaced, and convert to them at the edge by a copy of four numbers.
/// </summary>
public class GeometryTests
{
    [Fact]
    public void ARectangleIsItsEdgesAndItsSizeTheDifferences()
    {
        var r = RasterRect.Create(10, 20, 300, 100);
        Assert.Equal((10, 20, 310, 120), (r.Left, r.Top, r.Right, r.Bottom));
        Assert.Equal(new RasterSize(300, 100), r.Size);
        Assert.Equal(new RasterPoint(10, 20), r.Location);
        Assert.True(r.HasArea);
        Assert.False(r.IsEmpty);
        Assert.True(r.Contains(10, 20));
        Assert.False(r.Contains(310, 120));                                                  // the right and bottom edges are outside, as on the canvas
        Assert.Equal(RasterRect.Create(15, 25, 300, 100), r.Offset(5, 5));
        Assert.Equal(RasterRect.Of(new RasterSize(4, 3)), RasterRect.Create(0, 0, 4, 3));
    }

    [Fact]
    public void EmptyIsTheAllZeroRectangleAsTheCanvasReadIt()
    {
        Assert.True(RasterRect.Empty.IsEmpty);
        Assert.False(RasterRect.Empty.HasArea);
        Assert.False(new RasterRect(5, 5, 5, 5).IsEmpty);                                   // degenerate but placed: not "no region"
        Assert.False(new RasterRect(5, 5, 5, 5).HasArea);
        Assert.True(RasterSize.Empty.IsEmpty);
        Assert.False(new RasterSize(0, 10).IsEmpty);
        Assert.False(new RasterSize(0, 10).HasArea);
        Assert.Equal(12L, new RasterSize(4, 3).Area);
        Assert.Equal(new RasterSize(3, 4), new RasterSize(4, 3).Transposed);
    }

    [Fact]
    public void UnionIsThePlainBoundingBoxAndIntersectionIsEmptyWhenApart()
    {
        var a = RasterRect.Create(0, 0, 100, 100);
        var b = RasterRect.Create(50, 50, 100, 100);
        var apart = RasterRect.Create(200, 0, 10, 10);
        Assert.Equal(new RasterRect(0, 0, 150, 150), RasterRect.Union(a, b));
        Assert.Equal(new RasterRect(50, 50, 100, 100), RasterRect.Intersect(a, b));
        Assert.True(a.IntersectsWith(b));
        Assert.False(a.IntersectsWith(apart));
        Assert.Equal(RasterRect.Empty, RasterRect.Intersect(a, apart));
        Assert.Equal(RasterRect.Union(a, b), RasterRect.Union(b, a));
        Assert.True(RasterRect.Union(a, b).Contains(b));
        // The canvas union it replaced pulled the box to an empty rectangle at the origin; so does this one.
        Assert.Equal(new RasterRect(0, 0, 150, 150), RasterRect.Union(RasterRect.Empty, b));
    }

    [Theory]
    [InlineData("#FF8800", 255, 136, 0, 255)]
    [InlineData("ff8800", 255, 136, 0, 255)]
    [InlineData("#F80", 255, 136, 0, 255)]
    [InlineData("#80FF8800", 255, 136, 0, 128)]
    public void AColourParsesFromTheShowsHexWords(string hex, int r, int g, int b, int a)
    {
        Assert.True(Rgba.TryParse(hex, out var c));
        Assert.Equal((r, g, b, a), ((int)c.R, (int)c.G, (int)c.B, (int)c.A));
        Assert.Equal(c, Rgba.Parse(c.ToHex(), Rgba.Black));                                  // round trip through its own words
    }

    [Fact]
    public void AWordThatIsNotAColourFallsBack()
    {
        Assert.False(Rgba.TryParse("", out _));
        Assert.False(Rgba.TryParse("#12", out _));
        Assert.False(Rgba.TryParse("#GGGGGG", out _));
        Assert.Equal(Rgba.White, Rgba.Parse("nope", Rgba.White));
        Assert.Equal(new[] { Rgba.White }, Rgba.ParseList("", Rgba.White));
        Assert.Equal(new[] { Rgba.White }, Rgba.ParseList("nope, still nope", Rgba.White));
        Assert.Equal(2, Rgba.ParseList("#F00; #0F0 nope", Rgba.White).Length);
        Assert.Equal(0xFF00FF00u, Rgba.Parse("#00FF00", Rgba.Black).Argb);
    }

    [Fact]
    public void TheEdgeConvertsByCopyingTheNumbers()
    {
        var r = RasterRect.Create(3, 4, 50, 60);
        Assert.Equal(r, r.ToSk().ToRaster());
        Assert.Equal(SKRectI.Create(3, 4, 50, 60), r.ToSk());
        Assert.Equal(new SKRect(3, 4, 53, 64), r.ToSkRect());
        Assert.Equal(new RasterSize(7, 8), new SKSizeI(7, 8).ToRaster());
        Assert.Equal(new SKPointI(1, 2), new RasterPoint(1, 2).ToSk());
        var c = new Rgba(1, 2, 3, 4);
        Assert.Equal(new SKColor(1, 2, 3, 4), c.ToSk());
        Assert.Equal(c, c.ToSk().ToRgba());
        Assert.True(ColorUtil.TryParse("#010203", out var sk));
        Assert.Equal(new SKColor(1, 2, 3), sk);
        Assert.Equal(new[] { SKColors.Red, SKColors.Lime }, ColorUtil.ParseList("#FF0000 #00FF00", SKColors.Black));
    }

    [Fact]
    public void TheMediaMemoryViewReadsWhatTheRenderSideRegisters()
    {
        var was = MediaMemory.Source;
        try
        {
            MediaMemory.Source = null;
            var nothing = MediaMemory.Read(8192);
            Assert.Equal(0, nothing.Pictures + nothing.Pools + nothing.FramesRetiring);     // a process that never draws holds nothing, and says so
            MediaMemory.Source = () => new MediaMemory.MediaBytes(10, 1, 20, 2, 3);
            var some = MediaMemory.Read(8192);
            Assert.Equal((10L, 1L, 20L, 2L, 3L), (some.Pictures, some.PicturesRetiring, some.Pools, some.PoolsRetiring, some.FramesRetiring));
        }
        finally
        {
            MediaMemory.Source = was;
        }
        Assert.True(RenderingModule.Registered);                                             // the suite draws, so the module registered once
    }

    [Fact]
    public void ABuildWithoutAPictureCodecSaysSoInsteadOfGuessing()
    {
        var was = Pictures.Shrinker;
        try
        {
            Pictures.Shrinker = null;
            var result = AssistantAttachments.Read("shot.png", new byte[] { 1, 2, 3 });
            Assert.Null(result.Attachment);
            Assert.Contains("no picture codec", result.Refusal);
        }
        finally
        {
            Pictures.Shrinker = was;
        }
    }
}
