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

    // ---- beyond two projectors ---------------------------------------------------------------

    [Fact]
    public void ARowOfThreeGivesTheMiddleOneAZoneOnEachSideAndTheEndsOne()
    {
        var a = R(0, 0, 1920, 1080);
        var b = R(1720, 0, 1920, 1080);
        var c = R(3440, 0, 1920, 1080);
        var all = new[] { a, b, c };
        Assert.Equal(new BlendWidths(0, 0, 200, 0), EdgeBlend.Derive(a, all.Where(r => r != a)));
        Assert.Equal(new BlendWidths(200, 0, 200, 0), EdgeBlend.Derive(b, all.Where(r => r != b)));
        Assert.Equal(new BlendWidths(200, 0, 0, 0), EdgeBlend.Derive(c, all.Where(r => r != c)));
        Assert.True(EdgeBlend.Derive(b, all.Where(r => r != b)).FitsIn(1920, 1080));

        // Unequal overlaps stay on their own joins: the middle projector fades each side by that side's overlap.
        var c2 = R(3520, 0, 1920, 1080);   // 120 px into b
        Assert.Equal(new BlendWidths(200, 0, 120, 0), EdgeBlend.Derive(b, new[] { a, c2 }));
        Assert.Equal(new BlendWidths(120, 0, 0, 0), EdgeBlend.Derive(c2, new[] { a, b }));

        // Four in a row: two middles, each with both sides.
        var d = R(5160, 0, 1920, 1080);
        var four = new[] { a, b, c, d };
        Assert.Equal(new BlendWidths(200, 0, 200, 0), EdgeBlend.Derive(c, four.Where(r => r != c)));
        Assert.Equal(new BlendWidths(200, 0, 0, 0), EdgeBlend.Derive(d, four.Where(r => r != d)));

        // A stack of three: the middle one fades its top and its bottom.
        var top = R(0, 0, 1920, 1080);
        var mid = R(0, 960, 1920, 1080);
        var low = R(0, 1920, 1920, 1080);
        Assert.Equal(new BlendWidths(0, 120, 0, 120), EdgeBlend.Derive(mid, new[] { top, low }));
        Assert.Equal(new BlendWidths(0, 120, 0, 0), EdgeBlend.Derive(low, new[] { top, mid }));
    }

    [Fact]
    public void AGridOfSixGivesEveryProjectorItsOwnSidesAndTheDiagonalNeighbourNoZoneOfItsOwn()
    {
        // 3 × 2, overlapping 200 px along and 120 px across; a portrait member in the row is its own case.
        var tl = R(0, 0, 1920, 1080);
        var tm = R(1720, 0, 1920, 1080);
        var tr = R(3440, 0, 1920, 1080);
        var bl = R(0, 960, 1920, 1080);
        var bm = R(1720, 960, 1920, 1080);
        var br = R(3440, 960, 1920, 1080);
        var all = new[] { tl, tm, tr, bl, bm, br };
        BlendWidths Of(SKRectI me) => EdgeBlend.Derive(me, all.Where(r => r != me));
        Assert.Equal(new BlendWidths(0, 0, 200, 120), Of(tl));
        Assert.Equal(new BlendWidths(200, 0, 200, 120), Of(tm));   // both sides and the bottom
        Assert.Equal(new BlendWidths(200, 0, 0, 120), Of(tr));
        Assert.Equal(new BlendWidths(0, 120, 200, 0), Of(bl));
        Assert.Equal(new BlendWidths(200, 120, 200, 0), Of(bm));   // both sides and the top
        Assert.Equal(new BlendWidths(200, 120, 0, 0), Of(br));
        // The diagonal neighbour's overlap (200 × 120) adds nothing the side and the bottom do not already carry.
        Assert.Equal("bottom", EdgeBlend.EdgeOf(tl, bm, out var diagonal));
        Assert.Equal(120, diagonal);
        Assert.Equal("", EdgeBlend.EdgeOf(tl, R(1720, 880, 1920, 1080), out _));   // a square corner: no zone of its own
        Assert.Equal("right", EdgeBlend.Facing("left"));
        Assert.Equal("bottom", EdgeBlend.Facing("top"));

        // The grouping is transitive: every projector joins one canvas the size of the union.
        var arranged = all.Select((r, i) => new ArrangedScreen("p" + i, r, Blend: true)).ToList();
        var groups = ScreenLayout.Groups(arranged);
        Assert.Single(groups);
        Assert.Equal(new SKRectI(0, 0, 5360, 2040), ScreenLayout.Union(groups[0]));

        // One blending projector in the middle joins the neighbours on both sides.
        var row = new[] { new ArrangedScreen("a", tl), new ArrangedScreen("b", tm, Blend: true), new ArrangedScreen("c", tr) };
        Assert.Single(ScreenLayout.Groups(row));
        Assert.Equal(3, ScreenLayout.Groups(row)[0].Count);
    }

    [Fact]
    public void ZonesThatMeetInTheMiddleDoNotFit()
    {
        Assert.True(new BlendWidths(200, 0, 200, 0).FitsIn(1920, 1080));
        Assert.False(new BlendWidths(1000, 0, 1000, 0).FitsIn(1920, 1080));
        Assert.False(new BlendWidths(0, 600, 0, 600).FitsIn(1920, 1080));
        Assert.Equal(7, new BlendWidths(1, 3, 5, 7).On("bottom"));
        Assert.Equal(1, new BlendWidths(1, 3, 5, 7).On("left"));
    }

    // ---- the audit -----------------------------------------------------------------------------

    private static (List<ArrangedScreen> Arranged, Dictionary<string, ScreenPlacement> Placements) RowOfThree()
    {
        var placements = new Dictionary<string, ScreenPlacement>
        {
            ["a"] = new() { ScreenId = "a", BlendAuto = true },
            ["b"] = new() { ScreenId = "b", BlendAuto = true },
            ["c"] = new() { ScreenId = "c", BlendAuto = true },
        };
        var arranged = new List<ArrangedScreen>
        {
            new("a", R(0, 0, 1920, 1080), Blend: true),
            new("b", R(1720, 0, 1920, 1080), Blend: true),
            new("c", R(3440, 0, 1920, 1080), Blend: true),
        };
        return (arranged, placements);
    }

    private static IReadOnlyList<BlendNote> Audit(string id, (List<ArrangedScreen> Arranged, Dictionary<string, ScreenPlacement> Placements) rig)
        => BlendAudit.For(id, rig.Arranged, i => rig.Placements.GetValueOrDefault(i), i => "Projector " + i.ToUpperInvariant());

    [Fact]
    public void TheAuditPassesARowWhereEveryJoinFadesBothWaysWithTheSameWidthAndCurve()
    {
        var rig = RowOfThree();
        var middle = Audit("b", rig);
        Assert.Equal(2, middle.Count);
        Assert.All(middle, n => Assert.False(n.Warning));
        Assert.Contains(middle, n => n.Text.StartsWith("Left join with Projector A: 200 px, both fade, SCurve"));
        Assert.Contains(middle, n => n.Text.StartsWith("Right join with Projector C: 200 px, both fade"));
        Assert.Single(Audit("a", rig));
        Assert.Single(Audit("c", rig));
        Assert.Empty(Audit("nobody", rig));
        Assert.All(BlendAudit.Summary(middle).Split('\n'), line => Assert.StartsWith("✓ ", line));
    }

    [Fact]
    public void TheAuditNamesTheNeighbourThatDoesNotFadeTheWidthThatDiffersAndTheCurveThatDiffers()
    {
        // C does not blend at all: B is told its right join is bright, C is told which box to tick.
        var rig = RowOfThree();
        rig.Placements["c"].BlendAuto = false;
        var b = Audit("b", rig);
        Assert.Contains(b, n => n.Warning && n.Text.Contains("Projector C does not fade its left edge"));
        var c = Audit("c", rig);
        Assert.Single(c);
        Assert.True(c[0].Warning);
        Assert.Contains("this screen does not fade its left edge — tick Automatic, or type 200 in Left px", c[0].Text);

        // C fades, but by a typed 120 px against B's 200: the light will not add up.
        rig.Placements["c"].BlendLeftPx = 120;
        Assert.Contains(Audit("b", rig), n => n.Warning && n.Text.Contains("fades 200 px and Projector C 120 px across the same right join"));
        Assert.Contains(Audit("c", rig), n => n.Warning && n.Text.Contains("fades 120 px and Projector B 200 px"));

        // The same width, another curve.
        rig.Placements["c"].BlendAuto = true;
        rig.Placements["c"].BlendCurve = BlendCurve.Linear;
        Assert.Contains(Audit("b", rig), n => n.Warning && n.Text.Contains("this screen's curve is SCurve and Projector C's is Linear"));
        rig.Placements["c"].BlendCurve = BlendCurve.SCurve;
        Assert.All(Audit("b", rig), n => Assert.False(n.Warning));

        // Neither side fades an overlap: said once, plainly, and the warning leads the summary.
        rig.Placements["a"].BlendAuto = false;
        rig.Placements["b"].BlendAuto = false;
        var a = Audit("a", rig);
        Assert.Contains(a, n => n.Warning && n.Text.Contains("neither fades the join"));
        var summary = BlendAudit.Summary(Audit("b", rig));
        Assert.StartsWith("⚠ ", summary);
    }

    [Fact]
    public void TheAuditFlagsZonesTooWideToLeaveAPicture()
    {
        var placements = new Dictionary<string, ScreenPlacement>
        {
            ["a"] = new() { ScreenId = "a", BlendAuto = true },
            ["b"] = new() { ScreenId = "b", BlendAuto = true },
            ["c"] = new() { ScreenId = "c", BlendAuto = true },
        };
        // B is only 1920 wide but A and C each reach 1000 px into it.
        var arranged = new List<ArrangedScreen>
        {
            new("a", R(0, 0, 1920, 1080), Blend: true),
            new("b", R(920, 0, 1920, 1080), Blend: true),
            new("c", R(1840, 0, 1920, 1080), Blend: true),
        };
        var notes = BlendAudit.For("b", arranged, i => placements.GetValueOrDefault(i), i => i);
        Assert.Contains(notes, n => n.Warning && n.Text.Contains("left and right zones (1000 + 1000 px) meet on a 1920 px wide projector"));
    }
}
