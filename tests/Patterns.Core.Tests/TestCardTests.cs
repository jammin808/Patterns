using Patterns.Core.Model;
using Patterns.Core.Patterns;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 27: "Patterns needs some unique Patterns branded test cards (not an overlay, part of the
/// card). Pixel perfect, scientifically accurate, technically useful. The strongest of these
/// should be the default on first run."
///
/// What these pin is the part an operator cannot check by looking: that the numbers on the card
/// are the numbers the card claims, that they are the same on every machine, and that the
/// structure lands on whole pixels — because a card whose reading depends on which sink drew it
/// is worse than no card at all.
/// </summary>
public class TestCardTests
{
    private static ShowState Card(TestCardVariant variant = TestCardVariant.Rig)
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.TestCard;
        state.Pattern.TestCard.Variant = variant;
        state.Overlays.Badge.Enabled = false;
        return state;
    }

    private static bool Near(SKColor c, byte v, int tol = 2)
        => Math.Abs(c.Red - v) <= tol && Math.Abs(c.Green - v) <= tol && Math.Abs(c.Blue - v) <= tol;

    [Fact]
    public void ABrandNewInstallComesUpOnTheCard()
    {
        // The strongest card is what a first run shows: a grid answers one of a rig day's
        // questions, the card answers four — and names the screen while it does it.
        Assert.Equal(PatternKind.TestCard, new ShowState().Pattern.Kind);
        Assert.Equal(PatternKind.TestCard, SettingsStore.Fresh().Pattern.Kind);
        Assert.Equal(TestCardVariant.Rig, new ShowState().Pattern.TestCard.Variant);

        // And it is registered, because a kind with no renderer draws nothing at all — the engine
        // has no error card, it simply falls through.
        Assert.True(PatternRegistry.CreateAll().ContainsKey(PatternKind.TestCard));

        // Appended to the enum, never inserted: the tolerant reader falls back to the FIRST
        // member, so anything moved to the front silently becomes what every unknown value reads as.
        Assert.Equal(PatternKind.Grid, Enum.GetValues<PatternKind>()[0]);
        Assert.Equal(PatternKind.TestCard, Enum.GetValues<PatternKind>()[^1]);
    }

    [Fact]
    public void TheCardCarriesItsOwnMarkSoTheBadgeStaysOffIt()
    {
        var badge = new ShowState().Overlays.Badge;
        Assert.False(badge.ShowsOn(PatternKind.TestCard));   // two logos on a card is a mistake, not branding
        badge.OnMediaToo = true;
        Assert.False(badge.ShowsOn(PatternKind.TestCard));   // and asking for media does not bring it back
        Assert.True(badge.ShowsOn(PatternKind.Grid));

        // The mark on the card is the badge's mark, out of one place: the cyan cross of the icon
        // and the wordmark's magenta rule are both present in the middle of the picture.
        using var bmp = RenderTestHarness.Render(Card(), 1920, 1080);
        var cyan = 0;
        var magenta = 0;
        for (var y = 1080 / 4; y < 1080 * 3 / 4; y++)
        {
            for (var x = 1920 / 3; x < 1920 * 2 / 3; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Blue > 200 && p.Green > 150 && p.Red < 120) cyan++;
                if (p.Red > 190 && p.Blue > 130 && p.Green < 110) magenta++;
            }
        }
        Assert.True(cyan > 200, $"the icon's cross and the card's accent are there (cyan={cyan})");
        Assert.True(magenta > 100, $"the wordmark's rule is there (magenta={magenta})");
    }

    [Fact]
    public void TheStaircaseIsTheElevenStepsItSaysItIsAndNoStepIsThinnerThanItsNeighbour()
    {
        using var bmp = RenderTestHarness.Render(Card(TestCardVariant.Levels), 1920, 1080);

        byte[] expected = { 0, 26, 51, 77, 102, 128, 153, 179, 204, 230, 255 };
        var runs = FindStaircase(bmp, expected);
        Assert.True(runs is not null, "eleven steps, 0 to 255 in tens, somewhere on the card");

        // The run widths differ by at most one pixel: the boundaries are rounded to whole pixels,
        // so no step is a pixel thinner than the one beside it and reads as a different width on
        // a photograph sent to an LED supplier.
        var widths = runs!.Select(r => r.Width).ToList();
        Assert.True(widths.Max() - widths.Min() <= 1, $"steps {widths.Min()}–{widths.Max()} px wide");
    }

    [Fact]
    public void EveryClippingPatchIsDrawnSoAMissingOneIsTheProcessorAndNotTheCard()
    {
        using var bmp = RenderTestHarness.Render(Card(TestCardVariant.Levels), 1920, 1080);
        var seen = new HashSet<byte>();
        for (var y = 0; y < 1080; y++)
        {
            for (var x = 0; x < 1920; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Red == p.Green && p.Green == p.Blue) seen.Add(p.Red);
            }
        }
        // Just off black and just off white. If any of these is missing from the picture the desk
        // drew, an engineer reading the card would blame their processor for the card's own fault.
        foreach (byte v in new byte[] { 2, 4, 6, 8, 247, 249, 251, 253 })
        {
            Assert.Contains(v, seen);
        }
        // And the gamma solids, whose labels are the whole reading.
        foreach (byte v in new byte[] { 177, 186, 195 })
        {
            Assert.Contains(v, seen);
        }
    }

    [Fact]
    public void TheOneToOneFieldsAreExactlyHalfLitSoScalingIsWhatMakesThemMoire()
    {
        // Pinned through the real engine, on a real output, because that is the only place the
        // claim is made: at native size the fields are half lit, so the three read as one even
        // brightness — and any scaler in the chain breaks that, which is the whole test.
        using var bmp = RenderTestHarness.Render(Card(TestCardVariant.Pixel), 1200, 1200);

        // Each field is a square of alternating single pixels inside a bright border. Weigh the
        // three of them by their own share of lit pixels rather than by where they sit.
        var fields = HalfLitSquares(bmp);
        Assert.True(fields.Count >= 3, $"three one-to-one fields on the card (found {fields.Count})");
        foreach (var share in fields)
        {
            Assert.InRange(share, 0.44, 0.56);
        }
    }

    [Fact]
    public void ACardThatCannotMeasureSaysSoRatherThanLookingTheSame()
    {
        // A card drawn at half size — a monitor-wall tile, a preview pane, a canvas that is not the
        // output's own size — is drawing its one-pixel fields at the SINK's pixel, not the screen's,
        // so it is decorating rather than measuring. A measurement that cannot say when it has
        // stopped measuring is the one thing this desk does not ship, so the card marks itself.
        var honestState = Card(TestCardVariant.Pixel);
        using var honest = RenderTestHarness.Render(honestState, 1200, 1200);

        // A pattern canvas half the output's size: every canvas pixel lands on two device pixels,
        // so the "one-pixel" lines are two screen pixels wide and prove nothing about the chain.
        var scaledState = Card(TestCardVariant.Pixel);
        scaledState.Pattern.Canvas.FollowOutput = false;
        scaledState.Pattern.Canvas.Width = 600;
        scaledState.Pattern.Canvas.Height = 600;
        using var scaled = RenderTestHarness.Render(scaledState, 1200, 1200);

        Assert.False(Same(honest, scaled), "the card does not look identical when it cannot measure");
        Assert.True(WarnPixels(scaled) > WarnPixels(honest) + 200,
            $"the scaled card wears the warning ink (honest={WarnPixels(honest)} scaled={WarnPixels(scaled)})");
    }

    [Fact]
    public void TheCardHoldsOnEveryShapeAndSinkTheDeskHas()
    {
        // A fixed pixel layout falls off the side of the first LED strip it meets, and a card that
        // is blank on the stream is a card that lied to whoever was watching the stream.
        foreach (var (w, h) in new[] { (1920, 1080), (3840, 1080), (1080, 1920), (800, 600), (96, 54) })
        {
            foreach (var variant in Enum.GetValues<TestCardVariant>())
            {
                foreach (var sink in new[] { SinkKind.Output, SinkKind.Ndi, SinkKind.Thumbnail })
                {
                    using var bmp = RenderTestHarness.Render(Card(variant), w, h, sinkKind: sink);
                    Assert.False(Blank(bmp), $"{variant} on {sink} at {w}×{h} drew nothing");
                }
            }
        }
    }

    [Fact]
    public void TheCardIsStaticSoAWallOfThemCostsNothingToHold()
    {
        // A rig day leaves these up for hours on every screen. Nothing on the card moves, so the
        // engine draws it once per snapshot rather than once per vsync — which is the difference
        // between a laptop that stays cool all afternoon and one that does not.
        var snap = RenderTestHarness.Snap(Card());
        Assert.Equal(RedrawCadence.Static, PatternEngine.CadenceOf(snap, null, RenderTestHarness.FixedUtcNow));

        // Two draws of the same card are the same pixels: no clock, no frame counter, nothing that
        // would make a photograph of one screen disagree with a photograph of the next.
        using var a = RenderTestHarness.Render(Card(), 640, 360, time: 1.0);
        using var b = RenderTestHarness.Render(Card(), 640, 360, time: 97.5);
        Assert.True(Same(a, b), "the card does not move");
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>Orange — the ink the card wears where it cannot honestly claim a measurement.</summary>
    private static int WarnPixels(SKBitmap bmp)
    {
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Red > 200 && p.Green > 100 && p.Green < 180 && p.Blue < 90) n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Every square block of alternating single pixels on the card, as its share of lit pixels.
    /// Found by looking rather than by knowing the layout, so the test does not restate the
    /// renderer's own arithmetic back to it.
    /// </summary>
    private static List<double> HalfLitSquares(SKBitmap bmp)
    {
        var shares = new List<double>();
        var size = 40;
        for (var y = 0; y + size < bmp.Height; y += size)
        {
            for (var x = 0; x + size < bmp.Width; x += size)
            {
                long lit = 0, dark = 0, other = 0;
                for (var j = y; j < y + size; j++)
                {
                    for (var i = x; i < x + size; i++)
                    {
                        var p = bmp.GetPixel(i, j);
                        if (p.Red > 230 && p.Green > 230 && p.Blue > 230) lit++;
                        else if (p.Red < 20 && p.Green < 20 && p.Blue < 20) dark++;
                        else other++;
                    }
                }
                // A one-to-one field is black and white and nothing else.
                if (other == 0 && lit > 0 && dark > 0) shares.Add(lit / (double)(size * size));
            }
        }
        return shares;
    }

    private static PatternFrame Frame(ShowState state, int w, int h)
    {
        var snap = RenderTestHarness.Snap(state);
        return new PatternFrame
        {
            Snapshot = snap,
            Config = state.Pattern,
            Ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(w, h),
                ReferenceSize = new SKSizeI(w, h),
                Time = 1,
                Now = DateTime.Now,
                UtcNow = RenderTestHarness.FixedUtcNow,
                Sink = SinkKind.Output,
                SinkLabel = "TEST",
                DeviceScale = 1,
            },
            Sink = new SinkState(),
            Canvas = new SKSizeI(w, h),
            Palette = Palette.Resolve(snap),
            DeviceScale = 1,
        };
    }

    private static bool Blank(SKBitmap bmp)
    {
        var first = bmp.GetPixel(0, 0);
        for (var y = 0; y < bmp.Height; y += Math.Max(1, bmp.Height / 40))
        {
            for (var x = 0; x < bmp.Width; x += Math.Max(1, bmp.Width / 40))
            {
                if (bmp.GetPixel(x, y) != first) return false;
            }
        }
        return true;
    }

    private static bool Same(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The staircase's own row: the first row whose wide runs are exactly the eleven steps, in
    /// order. Narrow runs are skipped so the card's own frame and the labels' glyphs — which sit
    /// in the lower part of the band — cannot be mistaken for steps.
    /// </summary>
    private static List<(SKColor Colour, int Width)>? FindStaircase(SKBitmap bmp, byte[] expected)
    {
        for (var y = 0; y < bmp.Height; y++)
        {
            var wide = RunsOf(bmp, y).Where(r => r.Width >= 8).ToList();
            if (wide.Count != expected.Length) continue;
            var ok = true;
            for (var i = 0; i < expected.Length && ok; i++) ok = Near(wide[i].Colour, expected[i]);
            if (ok) return wide;
        }
        return null;
    }

    private static List<(SKColor Colour, int Width)> RunsOf(SKBitmap bmp, int y)
    {
        var runs = new List<(SKColor, int)>();
        var start = 0;
        for (var x = 1; x <= bmp.Width; x++)
        {
            if (x < bmp.Width && bmp.GetPixel(x, y) == bmp.GetPixel(start, y)) continue;
            runs.Add((bmp.GetPixel(start, y), x - start));
            start = x;
        }
        return runs;
    }
}
