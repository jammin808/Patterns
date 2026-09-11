using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 17: "add Patterns branding (icon / logo / colours) to a look, on by default, so a test
/// pattern looks like a branded test card; default middle / lower third." The badge is an
/// overlay like the clock — in every look, drawn by the engine on every sink — with the app's own
/// mark drawn by hand in the app's own colours, and a rule that keeps it to test patterns.
/// </summary>
public class BadgeTests
{
    private static readonly SKColor Cyan = new(0x3E, 0xC1, 0xF3);

    [Fact]
    public void TheBadgeIsOnByDefaultInTheLowerThirdAndTravelsWithLooksAndOldFiles()
    {
        var state = new ShowState();
        var badge = state.Overlays.Badge;
        Assert.True(badge.Enabled);
        Assert.Equal(Anchor9.BottomCenter, badge.Anchor);
        Assert.True(badge.OffsetYPct < 0, "lifted off the bottom edge into the lower third");
        Assert.True(badge.ShowLine);
        Assert.Equal(BadgeOverlay.DefaultLine, badge.Line);
        Assert.Equal("rig · playback · show control", badge.Line);   // the maker's own words, pinned
        Assert.False(badge.OnMediaToo);

        // The rule: test patterns yes, media and the multiview no — unless media is asked for.
        Assert.True(badge.ShowsOn(PatternKind.Grid));
        Assert.True(badge.ShowsOn(PatternKind.ColorBars));
        Assert.True(badge.ShowsOn(PatternKind.LedWall));
        Assert.True(badge.ShowsOn(PatternKind.Particles));
        Assert.True(badge.ShowsOn(PatternKind.Fractal));
        Assert.False(badge.ShowsOn(PatternKind.Media));
        Assert.False(badge.ShowsOn(PatternKind.Multiview));
        badge.OnMediaToo = true;
        Assert.True(badge.ShowsOn(PatternKind.Media));
        Assert.False(badge.ShowsOn(PatternKind.Multiview));
        badge.Enabled = false;
        Assert.False(badge.ShowsOn(PatternKind.Grid));

        // The limits hold, and a null line reads as none.
        badge.HeightPct = 200;
        Assert.Equal(40, badge.HeightPct);
        badge.Opacity = 0;
        Assert.Equal(0.05, badge.Opacity);
        badge.Line = null!;
        Assert.Equal("", badge.Line);

        // An older show file and an older look have no badge member: on, as the default says.
        Assert.True(JsonUtil.Deserialize<ShowState>("{}")!.Overlays.Badge.Enabled);
        var target = new ShowState();
        target.Overlays.Badge.Enabled = false;
        Assert.True(LookService.Apply("""{"Pattern":{"Kind":"Grid"},"Overlays":{"Clock":{"Enabled":true}}}""", target));
        Assert.True(target.Overlays.Badge.Enabled);
        Assert.True(target.Overlays.Clock.Enabled);

        // A look carries the badge as it was saved: off and reworded in the look, off and reworded on recall.
        var saved = new ShowState();
        saved.Overlays.Badge.Enabled = false;
        saved.Overlays.Badge.Line = "Hall 3 · stand B12";
        saved.Overlays.Badge.Anchor = Anchor9.TopRight;
        var json = LookService.Capture(saved);
        var recalled = new ShowState();
        Assert.True(LookService.Apply(json, recalled));
        Assert.False(recalled.Overlays.Badge.Enabled);
        Assert.Equal("Hall 3 · stand B12", recalled.Overlays.Badge.Line);
        Assert.Equal(Anchor9.TopRight, recalled.Overlays.Badge.Anchor);

        // And the show file keeps it.
        var back = JsonUtil.Deserialize<ShowState>(JsonUtil.Serialize(saved))!;
        Assert.False(back.Overlays.Badge.Enabled);
        Assert.Equal("Hall 3 · stand B12", back.Overlays.Badge.Line);
    }

    [Fact]
    public void TheBadgeDrawsOverATestPatternCentredInTheLowerThirdInTheAppsColours()
    {
        var state = Black();
        var (bmp, hits) = Render(state);
        using (bmp)
        {
            var box = LitBox(bmp);
            Assert.NotNull(box);
            var r = box!.Value;
            Assert.InRange(r.MidX, 940, 980);                 // centred
            Assert.InRange(r.MidY, 740, 900);                 // the middle of the lower third
            Assert.InRange(r.Height, 80, 115);                // 9 % of 1080, give or take the antialiased edge
            Assert.True(r.Width > r.Height * 3, $"a wide card: {r}");
            Assert.True(HasColourNear(bmp, r, Cyan), "the cyan of the icon's cross and the card's edge");
            Assert.True(HasColourNear(bmp, r, SKColors.White), "the white of the name");
            // The desk can take hold of it: the hit box is the card.
            var hit = Assert.Single(hits, h => h.Kind == HitKind.Badge);
            Assert.InRange(hit.Rect.Left, r.Left - 4, r.Left + 4);
            Assert.InRange(hit.Rect.Top, r.Top - 4, r.Top + 4);
            Assert.InRange(hit.Rect.Right, r.Right - 4, r.Right + 4);
            Assert.InRange(hit.Rect.Bottom, r.Bottom - 4, r.Bottom + 4);
        }

        // Off: a clean black field, and nothing to take hold of.
        state.Overlays.Badge.Enabled = false;
        var (off, offHits) = Render(state);
        using (off)
        {
            Assert.Null(LitBox(off));
            Assert.DoesNotContain(offHits, h => h.Kind == HitKind.Badge);
        }

        // Without the line: the icon and the name alone, a shorter card of the same height.
        state.Overlays.Badge.Enabled = true;
        state.Overlays.Badge.ShowLine = false;
        var (bare, _) = Render(state);
        using (bare)
        {
            var r = LitBox(bare)!.Value;
            Assert.InRange(r.Height, 80, 115);
            Assert.True(HasColourNear(bare, r, SKColors.White));
        }
    }

    [Fact]
    public void TheBadgeMovesWithItsAnchorAndNudgeAndGrowsWithItsHeight()
    {
        var state = Black();
        state.Overlays.Badge.Anchor = Anchor9.TopLeft;
        state.Overlays.Badge.OffsetYPct = 0;
        state.Overlays.Badge.HeightPct = 20;
        var (bmp, _) = Render(state);
        using (bmp)
        {
            var r = LitBox(bmp)!.Value;
            Assert.InRange(r.Left, 26, 40);                   // the 3 % margin
            Assert.InRange(r.Top, 26, 40);
            Assert.InRange(r.Height, 200, 225);               // 20 % of 1080
        }

        state.Overlays.Badge.OffsetXPct = 25;                 // a drag on the PREVIEW pane: a quarter of the canvas to the right
        var (moved, _) = Render(state);
        using (moved)
        {
            var r = LitBox(moved)!.Value;
            Assert.InRange(r.Left, 26 + 480 - 4, 40 + 480 + 4);
        }
    }

    [Fact]
    public void TheBadgeIsOnEveryPictureTheAppDrawsItself()
    {
        // A reactive scene is Patterns' own generated picture, like the particles and the fractals
        // beside it — it was the one exception, and an operator meets that as "the logo is missing
        // on this page and nowhere else".
        foreach (var kind in new[]
                 {
                     PatternKind.Grid, PatternKind.ColorBars, PatternKind.Ramp, PatternKind.FlatField,
                     PatternKind.Motion, PatternKind.ColorCycle, PatternKind.Particles,
                     PatternKind.Fractal, PatternKind.Reactive,
                 })
        {
            var state = Black();
            state.Pattern.Kind = kind;
            var (_, hits) = Render(state);
            Assert.Contains(hits, h => h.Kind == HitKind.Badge);
        }
    }

    [Fact]
    public void TheBadgeKeepsOffMediaAndTheMultiviewUnlessAskedOntoMedia()
    {
        var state = Black();
        state.Pattern.Kind = PatternKind.Media;
        var (_, hits) = Render(state);
        Assert.DoesNotContain(hits, h => h.Kind == HitKind.Badge);

        state.Overlays.Badge.OnMediaToo = true;
        var (_, asked) = Render(state);
        Assert.Contains(asked, h => h.Kind == HitKind.Badge);

        state.Pattern.Kind = PatternKind.Multiview;
        var (_, multiview) = Render(state);
        Assert.DoesNotContain(multiview, h => h.Kind == HitKind.Badge);
    }

    private static ShowState Black()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.FlatField;
        state.Pattern.FlatField.Color = "#000000";
        state.Pattern.FlatField.ShowLabel = false;
        state.Pattern.Canvas.FollowOutput = true;
        return state;
    }

    private static (SKBitmap Bmp, List<HitRect> Hits) Render(ShowState state, int w = 1920, int h = 1080)
    {
        var snap = new ShowSnapshot { State = state, Version = 1 };
        using var sink = new SinkState();
        var engine = new PatternEngine();
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(w, h),
            ReferenceSize = new SKSizeI(w, h),
            Time = 0,
            Now = DateTime.Now,
            UtcNow = DateTime.UtcNow,
            Sink = SinkKind.Output,
            SinkLabel = "out",
            DeviceScale = 1f,
        };
        engine.Render(surface.Canvas, snap, in ctx, sink);
        surface.Canvas.Flush();
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return (bmp, sink.Hits.ToList());
    }

    /// <summary>The box around every pixel that is not black, or null on a clean field.</summary>
    private static SKRect? LitBox(SKBitmap bmp)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Red + p.Green + p.Blue < 24) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        return right < 0 ? null : new SKRect(left, top, right + 1, bottom + 1);
    }

    private static bool HasColourNear(SKBitmap bmp, SKRect r, SKColor wanted)
    {
        for (var y = (int)r.Top; y < (int)r.Bottom; y++)
        {
            for (var x = (int)r.Left; x < (int)r.Right; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (Math.Abs(p.Red - wanted.Red) + Math.Abs(p.Green - wanted.Green) + Math.Abs(p.Blue - wanted.Blue) < 90) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The maker's line changed in the source, and a settings file already carries the words it
    /// was written with — so without this the change would be real everywhere except where an
    /// operator looks. A line still exactly the old one follows; anything typed over it is theirs.
    /// </summary>
    [Fact]
    public void TheMakersLineFollowsTheAppUnlessSomebodyTypedOverIt()
    {
        var untouched = new ShowState();
        untouched.Overlays.Badge.Line = BadgeOverlay.LegacyLine;
        SettingsStore.Migrate(untouched);
        Assert.Equal(BadgeOverlay.DefaultLine, untouched.Overlays.Badge.Line);

        var theirs = new ShowState();
        theirs.Overlays.Badge.Line = "The Barbican · Silk Street EC2Y 8DS";
        SettingsStore.Migrate(theirs);
        Assert.Equal("The Barbican · Silk Street EC2Y 8DS", theirs.Overlays.Badge.Line);

        var off = new ShowState();
        off.Overlays.Badge.Line = "";
        SettingsStore.Migrate(off);
        Assert.Equal("", off.Overlays.Badge.Line);                   // cleared on purpose stays cleared

        // Idempotent, like every other step in the pass.
        SettingsStore.Migrate(untouched);
        Assert.Equal(BadgeOverlay.DefaultLine, untouched.Overlays.Badge.Line);
    }
}
