using Patterns.Core.Model;
using Patterns.Core.Patterns;
using Patterns.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Edge-blend geometry and the weight the mask uses — pure maths.</summary>
public class EdgeBlendTests
{
    private static SKRectI R(int x, int y, int w, int h) => SKRectI.Create(x, y, w, h);

    [Fact]
    public void ASideBySidePairSharesOneZone()
    {
        var a = R(0, 0, 1920, 1080);
        var b = R(1720, 0, 1920, 1080);
        Assert.Equal(new BlendWidths(0, 0, 200, 0), EdgeBlend.Derive(a, new[] { b }));
        Assert.Equal(new BlendWidths(200, 0, 0, 0), EdgeBlend.Derive(b, new[] { a }));
    }

    [Fact]
    public void AStackSharesATopAndABottom()
    {
        var upper = R(0, 0, 1920, 1080);
        var lower = R(0, 960, 1920, 1080);
        Assert.Equal(new BlendWidths(0, 0, 0, 120), EdgeBlend.Derive(upper, new[] { lower }));
        Assert.Equal(new BlendWidths(0, 120, 0, 0), EdgeBlend.Derive(lower, new[] { upper }));
    }

    [Fact]
    public void AGridOfFourGetsASideAndAnEdgeEachAndNoCornerOfItsOwn()
    {
        var tl = R(0, 0, 1920, 1080);
        var tr = R(1720, 0, 1920, 1080);
        var bl = R(0, 880, 1920, 1080);
        var br = R(1720, 880, 1920, 1080);
        var all = new[] { tl, tr, bl, br };
        Assert.Equal(new BlendWidths(0, 0, 200, 200), EdgeBlend.Derive(tl, all.Where(r => r != tl)));
        Assert.Equal(new BlendWidths(200, 0, 0, 200), EdgeBlend.Derive(tr, all.Where(r => r != tr)));
        Assert.Equal(new BlendWidths(0, 200, 200, 0), EdgeBlend.Derive(bl, all.Where(r => r != bl)));
        Assert.Equal(new BlendWidths(200, 200, 0, 0), EdgeBlend.Derive(br, all.Where(r => r != br)));
    }

    [Fact]
    public void FlushOrSeparatedNeighboursBlendNothing()
    {
        var a = R(0, 0, 1920, 1080);
        Assert.Equal(BlendWidths.None, EdgeBlend.Derive(a, new[] { R(1920, 0, 1920, 1080) }));
        Assert.Equal(BlendWidths.None, EdgeBlend.Derive(a, new[] { R(2200, 0, 1920, 1080) }));
        Assert.Equal(BlendWidths.None, EdgeBlend.Derive(a, Array.Empty<SKRectI>()));
        Assert.False(BlendWidths.None.Any);
    }

    [Fact]
    public void TheWiderOfTwoNeighboursOnOneEdgeWins()
    {
        var a = R(0, 0, 1920, 1080);
        var derived = EdgeBlend.Derive(a, new[] { R(1800, 0, 1920, 500), R(1720, 580, 1920, 500) });
        Assert.Equal(new BlendWidths(0, 0, 200, 0), derived);
    }

    [Fact]
    public void ResolveTakesTheOverlapsWhenAutomaticAndTheTypedWidthsOtherwise()
    {
        var derived = new BlendWidths(0, 0, 200, 0);
        var manual = new ScreenPlacement { BlendLeftPx = 64, BlendTopPx = 0, BlendRightPx = 0, BlendBottomPx = 32 };
        Assert.Equal(new BlendWidths(64, 0, 0, 32), EdgeBlend.Resolve(manual, derived));
        Assert.False(manual.BlendAuto);
        Assert.True(manual.HasBlend);

        var auto = new ScreenPlacement { BlendAuto = true, BlendLeftPx = 64 };
        Assert.Equal(derived, EdgeBlend.Resolve(auto, derived));

        var none = new ScreenPlacement();
        Assert.False(none.HasBlend);
        Assert.Equal(BlendCurve.SCurve, none.BlendCurve);
        Assert.Equal(1.0, none.BlendGamma);
    }

    [Fact]
    public void TheWeightsOfTheTwoSidesAddUpToOneAndGammaBendsThem()
    {
        foreach (var curve in new[] { BlendCurve.Linear, BlendCurve.Cosine, BlendCurve.SCurve })
        {
            for (var t = 0.0; t <= 1.0001; t += 0.125)
            {
                Assert.Equal(1.0, BlendMath.Weight(curve, t, 1.0) + BlendMath.Weight(curve, 1 - t, 1.0), 6);
            }
        }
        Assert.Equal(0.0, BlendMath.Weight(BlendCurve.SCurve, 0, 2.2));
        Assert.Equal(1.0, BlendMath.Weight(BlendCurve.SCurve, 1, 2.2));
        Assert.Equal(Math.Pow(0.5, 1 / 2.2), BlendMath.Weight(BlendCurve.Linear, 0.5, 2.2), 6);
        Assert.True(BlendMath.Weight(BlendCurve.Linear, 0.5, 2.2) > BlendMath.Weight(BlendCurve.Linear, 0.5, 1.0));
    }

    [Fact]
    public void OverlappingScreensJoinACanvasOnlyWhenOneOfThemBlends()
    {
        var a = new ArrangedScreen("a", R(0, 0, 1920, 1080));
        var b = new ArrangedScreen("b", R(1720, 0, 1920, 1080));
        Assert.Equal(2, ScreenLayout.Groups(new[] { a, b }).Count);       // an overlap by mistake stays two screens
        Assert.False(ScreenLayout.Connected(a, b));

        var bBlend = b with { Blend = true };
        Assert.True(ScreenLayout.Connected(a, bBlend));
        var groups = ScreenLayout.Groups(new[] { a, bBlend });
        Assert.Single(groups);
        Assert.Equal(new SKRectI(0, 0, 3640, 1080), ScreenLayout.Union(groups[0]));

        // A blending projector still needs real shared area: a gap is a gap.
        var far = new ArrangedScreen("c", R(4000, 0, 1920, 1080), Blend: true);
        Assert.False(ScreenLayout.Connected(a, far));
        // And touching connects as it always has, flag or no flag.
        Assert.True(ScreenLayout.Connected(a, new ArrangedScreen("d", R(1920, 0, 1920, 1080))));
    }
}
