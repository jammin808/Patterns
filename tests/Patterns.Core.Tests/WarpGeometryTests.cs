using Patterns.Core.Model;
using Patterns.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The output's geometry built once: what it holds is what the maths make, and the cache keeps it until a number changes.</summary>
public class WarpGeometryTests
{
    private static WarpGeometrySpec Spec(string mesh = "", int columns = 3, int rows = 3)
        => WarpGeometrySpec.Plain(new SKSizeI(400, 200), columns, rows) with { Mesh = mesh };

    [Fact]
    public void TheGeometryHoldsWhatTheMathsMakeForItsNumbers()
    {
        var rest = string.Join(";", Enumerable.Repeat("0,0", 9));
        var mesh = WarpGrid.Moved(rest, 3, 3, 0, 80, 60);
        var spec = Spec(mesh) with
        {
            TopBow = 10, LeftBow = -4,
            Tlx = 8, Try = 6,
            Rotation = OutputRotation.Rot90, Physical = new SKSizeI(200, 400),
            Blend = new BlendWidths(0, 0, 100, 0), BlackPct = 8, BlendGamma = 1.0,
        };
        var geo = WarpGeometry.Build(spec);

        Assert.Equal(spec, geo.Spec);
        Assert.Equal(WarpGrid.Parse(mesh, 3, 3), geo.Offsets);
        Assert.Equal(WarpGrid.Nodes(3, 3, 400, 200, geo.Offsets, 10, 0, 0, -4), geo.Nodes);
        var patches = WarpGrid.Patches(geo.Nodes, 3, 3, 400, 200);
        Assert.Equal(4, geo.PatchCubics.Length);
        Assert.Equal(4, geo.PatchTextures.Length);
        for (var k = 0; k < patches.Count; k++)
        {
            Assert.Equal(patches[k].Cubics, geo.PatchCubics[k]);
            Assert.Equal(patches[k].Texture, geo.PatchTextures[k]);
        }
        Assert.Equal(WarpGrid.Lines(geo.Nodes, 3, 3), geo.Lines);
        Assert.Equal(WarpMesh.Cubics(400, 200, 10, 0, 0, -4), geo.BendCubics);
        Assert.Equal(WarpMesh.TextureCorners(400, 200), geo.BendTexture);

        // The pedestal: the picture left of the zone is covered once, the zone itself twice — the
        // deepest — so the one cell that lifts is the picture, at the level the maths give it.
        var cell = Assert.Single(geo.Pedestal);
        Assert.Equal(SKRectI.Create(0, 0, 300, 200), cell.Rect);
        Assert.Equal(BlackLevel.Level(8, 1, 2, 1.0), cell.Level);
        Assert.True(cell.Level > 0);

        Assert.Equal(WarpMath.QuadWarp(200, 400, new SKPoint(8, 0), new SKPoint(200, 6), new SKPoint(0, 400), new SKPoint(200, 400)), geo.Keystone);
        Assert.Equal(WarpGeometry.RotationOf(OutputRotation.Rot90, new SKSizeI(200, 400)), geo.Rotation);
        Assert.True(geo.Invertible);
        var p = new SKPoint(123, 45);
        var back = geo.MapToLocal(geo.MapToPhysical(p));
        Assert.InRange(back.X, p.X - 0.01, p.X + 0.01);
        Assert.InRange(back.Y, p.Y - 0.01, p.Y + 0.01);
    }

    [Fact]
    public void APlainOutputHasNoPedestalAndIdentityMatricesAndALatticeAtRest()
    {
        var geo = WarpGeometry.Build(Spec());
        Assert.False(geo.Spec.HasMesh);
        Assert.False(geo.Spec.HasBend);
        Assert.False(geo.Spec.HasWarp);
        Assert.Empty(geo.Pedestal);
        Assert.Equal(SKMatrix.Identity, geo.Keystone);
        Assert.Equal(SKMatrix.Identity, geo.Rotation);
        Assert.Equal(SKMatrix.Identity, geo.ToPhysical);
        Assert.Equal(SKMatrix.Identity, geo.ToLocal);
        Assert.Equal(new SKPoint(0, 0), geo.Nodes[0]);
        Assert.Equal(new SKPoint(400, 200), geo.Nodes[^1]);
        Assert.Equal(12, geo.Lines.Length);
        Assert.All(geo.Offsets, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ThePedestalIsOffWithoutAZoneOrWithoutABlack()
    {
        Assert.Empty(WarpGeometry.Build(Spec() with { BlackPct = 8 }).Pedestal);                                   // no zone: nothing overlaps
        Assert.Empty(WarpGeometry.Build(Spec() with { Blend = new BlendWidths(100, 0, 0, 0) }).Pedestal);          // a zone but no black
        var grid = WarpGeometry.Build(Spec() with { Blend = new BlendWidths(100, 0, 0, 50), BlackPct = 10 }).Pedestal;
        Assert.Equal(3, grid.Length);                                                                              // four cells; the corner where two zones meet is the deepest and lifts nothing
    }

    private static void Near(SKPoint expected, SKPoint actual)
    {
        Assert.InRange(actual.X, expected.X - 0.01, expected.X + 0.01);
        Assert.InRange(actual.Y, expected.Y - 0.01, expected.Y + 0.01);
    }

    [Fact]
    public void TheRotationsTurnTheCornersToTheirPlaces()
    {
        var window = new SKSizeI(1920, 1080);                                                     // portrait content, 1080 × 1920, turned onto it
        var r90 = WarpGeometry.RotationOf(OutputRotation.Rot90, window);
        Near(new SKPoint(1920, 0), r90.MapPoint(new SKPoint(0, 0)));                               // the content's top-left lands top-right
        Near(new SKPoint(1920, 1080), r90.MapPoint(new SKPoint(1080, 0)));                         // its top-right, bottom-right
        Near(new SKPoint(0, 0), r90.MapPoint(new SKPoint(0, 1920)));                               // its bottom-left, top-left
        var r180 = WarpGeometry.RotationOf(OutputRotation.Rot180, window);
        Near(new SKPoint(1920, 1080), r180.MapPoint(new SKPoint(0, 0)));
        var r270 = WarpGeometry.RotationOf(OutputRotation.Rot270, window);
        Near(new SKPoint(0, 1080), r270.MapPoint(new SKPoint(0, 0)));
        Assert.Equal(SKMatrix.Identity, WarpGeometry.RotationOf(OutputRotation.None, window));
    }

    [Fact]
    public void TheCacheBuildsOncePerGeometryAndNotForAnotherInstanceOfTheSameLine()
    {
        var cache = new WarpGeometryCache();
        var spec = Spec("0,0;10,-4;0,0;0,0;3.5,2;0,0;0,0;0,0;0,0");
        var a = cache.For(spec);
        Assert.Equal(1, cache.Builds);
        Assert.Same(a, cache.For(spec));
        Assert.Same(a, cache.For(spec with { Mesh = new string(spec.Mesh.ToCharArray()) }));          // the same line in another string: equal by value
        Assert.Equal(1, cache.Builds);

        var pulled = spec with { Mesh = WarpGrid.Moved(spec.Mesh, 3, 3, 4, 1, 1) };                   // the operator pulls a point
        var b = cache.For(pulled);
        Assert.NotSame(a, b);
        Assert.Equal(2, cache.Builds);
        Assert.Same(b, cache.For(pulled));

        var resized = pulled with { Effective = new SKSizeI(800, 400), Physical = new SKSizeI(800, 400) };  // the window changes size
        var c = cache.For(resized);
        Assert.Equal(3, cache.Builds);
        Assert.Equal(new SKPoint(800, 400), c.Nodes[^1]);
        Assert.Same(c, cache.Current);

        Assert.Equal(4, cache.For(resized with { BlackPct = 5, Blend = new BlendWidths(0, 0, 80, 0) }).Pedestal.Length + 3);  // a pedestal is geometry too
        Assert.Equal(4, cache.Builds);
        cache.Clear();
        Assert.Null(cache.Current);
        cache.For(resized);
        Assert.Equal(5, cache.Builds);
    }

    [Fact]
    public void TheSpecIsAValueAndItsFlagsReadItsNumbers()
    {
        var plain = WarpGeometrySpec.Plain(new SKSizeI(10, 10));
        Assert.Equal(plain, WarpGeometrySpec.Plain(new SKSizeI(10, 10)));
        Assert.NotEqual(plain, plain with { Rotation = OutputRotation.Rot180 });
        Assert.True((plain with { Bry = 1 }).HasWarp);
        Assert.True((plain with { BottomBow = -3 }).HasBend);
        Assert.True((plain with { Mesh = "1,1" }).HasMesh);
        Assert.False(plain.HasWarp || plain.HasBend || plain.HasMesh);
    }
}
