using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The lower-thirds engine: easing, keys, the clock, the motions, the presets, the picture and its caches.</summary>
public class LowerThirdTests
{
    private static LowerThirdKeyframe Key(double u, double x = 0, double opacity = 1, double scale = 1, EaseKind ease = EaseKind.Linear)
        => new() { U = u, X = x, Opacity = opacity, Scale = scale, Ease = ease };

    [Fact]
    public void EveryEaseStartsAtZeroAndEndsAtOne()
    {
        foreach (var ease in Enum.GetValues<EaseKind>())
        {
            Assert.Equal(0, Easing.Apply(ease, 0), 6);
            Assert.Equal(1, Easing.Apply(ease, 1), 6);
            Assert.Equal(1, Easing.Apply(ease, 1.5), 6); // clamped
        }
        Assert.Equal(0.5, Easing.Apply(EaseKind.Linear, 0.5), 6);
        Assert.Equal(0.5, Easing.Apply(EaseKind.EaseInOut, 0.5), 6);
        Assert.True(Easing.Apply(EaseKind.EaseIn, 0.5) < 0.5);
        Assert.True(Easing.Apply(EaseKind.EaseOut, 0.5) > 0.5);
        Assert.True(Easing.Apply(EaseKind.Back, 0.8) > 1, "back overshoots before it settles");
        Assert.True(Easing.Apply(EaseKind.Elastic, 0.15) > 1, "elastic overshoots early on");
        for (var u = 0.0; u <= 1; u += 0.05) Assert.InRange(Easing.Apply(EaseKind.Bounce, u), 0, 1.0001);
    }

    [Fact]
    public void KeysBlendBetweenTheTwoAroundTheInstant()
    {
        Assert.Equal(ElementPose.Identity, LowerThirdKeyframes.Evaluate(Array.Empty<LowerThirdKeyframe>(), 0.5));

        var one = new[] { Key(0.5, x: 40, opacity: 0.5) };
        Assert.Equal(40, LowerThirdKeyframes.Evaluate(one, 0).X);
        Assert.Equal(40, LowerThirdKeyframes.Evaluate(one, 1).X);

        var two = new[] { Key(0, x: -100, opacity: 0), Key(1, x: 0, opacity: 1) };
        var mid = LowerThirdKeyframes.Evaluate(two, 0.5);
        Assert.Equal(-50, mid.X, 3);
        Assert.Equal(0.5f, mid.Opacity, 3);
        Assert.Equal(1f, mid.Scale, 3);

        // The later key's ease shapes the travel; before the first key and after the last, they hold.
        var eased = new[] { Key(0.2, x: -100), Key(0.8, x: 0, ease: EaseKind.EaseIn) };
        Assert.Equal(-100, LowerThirdKeyframes.Evaluate(eased, 0).X);
        Assert.Equal(0, LowerThirdKeyframes.Evaluate(eased, 1).X);
        Assert.True(LowerThirdKeyframes.Evaluate(eased, 0.5).X < -50, "ease-in is still near the start at the middle");

        // A third key in the middle is the one the blend goes through; list order does not matter.
        var three = new[] { Key(1, x: 0), Key(0, x: -100), Key(0.5, x: 20) };
        Assert.Equal(20, LowerThirdKeyframes.Evaluate(three, 0.5).X, 3);
        Assert.Equal(-40, LowerThirdKeyframes.Evaluate(three, 0.25).X, 3);
        Assert.Equal(10, LowerThirdKeyframes.Evaluate(three, 0.75).X, 3);
    }

    [Fact]
    public void TheClockRunsInHoldAndOutByTheHoldOrByAHide()
    {
        var d = new LowerThirdDesign { InMs = 600, OutMs = 400, HoldMs = 0 };
        Assert.Equal(LowerThirdPhase.Before, LowerThirdClock.Evaluate(d, 10, null, 9.9).Phase);
        var t = LowerThirdClock.Evaluate(d, 10, null, 10.3);
        Assert.Equal(LowerThirdPhase.In, t.Phase);
        Assert.Equal(0.5, t.U, 6);
        Assert.Equal(LowerThirdPhase.Hold, LowerThirdClock.Evaluate(d, 10, null, 11).Phase);
        Assert.Equal(LowerThirdPhase.Hold, LowerThirdClock.Evaluate(d, 10, null, 1000).Phase);

        // A hide at 12: out for 400 ms, then gone.
        t = LowerThirdClock.Evaluate(d, 10, 12, 12.2);
        Assert.Equal(LowerThirdPhase.Out, t.Phase);
        Assert.Equal(0.5, t.U, 6);
        Assert.Equal(LowerThirdPhase.Gone, LowerThirdClock.Evaluate(d, 10, 12, 12.4).Phase);
        Assert.False(LowerThirdClock.Evaluate(d, 10, 12, 12.4).Visible);
        Assert.Equal(LowerThirdPhase.Hold, LowerThirdClock.Evaluate(d, 10, 12, 11.9).Phase); // not yet

        // A hold ends by itself: in 600 + hold 1000 → out starts at 11.6.
        d.HoldMs = 1000;
        t = LowerThirdClock.Evaluate(d, 10, null, 11.8);
        Assert.Equal(LowerThirdPhase.Out, t.Phase);
        Assert.Equal(0.5, t.U, 6);
        Assert.Equal(LowerThirdPhase.Gone, LowerThirdClock.Evaluate(d, 10, null, 12.1).Phase);
        // An earlier hide wins over the hold.
        Assert.Equal(LowerThirdPhase.Out, LowerThirdClock.Evaluate(d, 10, 11, 11.1).Phase);
        // A hide before the show (a stale one) counts for nothing.
        Assert.Equal(LowerThirdPhase.Hold, LowerThirdClock.Evaluate(d, 10, 5, 11.0).Phase);

        // No way in: at rest at once; no way out: gone the instant it is hidden.
        d.InMs = 0;
        d.OutMs = 0;
        d.HoldMs = 0;
        Assert.Equal(LowerThirdPhase.Hold, LowerThirdClock.Evaluate(d, 10, null, 10).Phase);
        Assert.Equal(LowerThirdPhase.Gone, LowerThirdClock.Evaluate(d, 10, 12, 12).Phase);

        // The config's instants ride the master clock, and IsLive reads them.
        var cfg = new LowerThirdsConfig();
        cfg.Designs.Add(d);
        Assert.False(LowerThirdClock.IsLive(cfg, ShowClock.UtcAt(5)));
        cfg.Show(d, ShowClock.UtcAt(4));
        Assert.True(cfg.IsShowing);
        Assert.True(LowerThirdClock.IsLive(cfg, ShowClock.UtcAt(5)));
        cfg.Hide(ShowClock.UtcAt(6));
        Assert.False(cfg.IsShowing);
        Assert.False(LowerThirdClock.IsLive(cfg, ShowClock.UtcAt(6.5)));
        cfg.Hide(ShowClock.UtcAt(7)); // a second hide changes nothing
        Assert.Equal(ShowClock.UtcAt(6), cfg.HiddenAtUtc);
        cfg.Show(d, ShowClock.UtcAt(8));
        Assert.Null(cfg.HiddenAtUtc);
        Assert.Same(d, cfg.Find(d.Id));
        Assert.Same(d, cfg.Find("1"));
        d.Name = "Guest";
        Assert.Same(d, cfg.Find("guest"));
        Assert.Null(cfg.Find("nobody"));
    }

    [Fact]
    public void TheMotionsWriteKeysAndTheStaggerDelaysAnElement()
    {
        var d = new LowerThirdDesign { Width = 800, InMs = 600 };
        var e = new LowerThirdElement { W = 400, H = 80 };
        LowerThirdMotions.Apply(e, d, LowerThirdMotion.SlideLeft, LowerThirdMotion.Fade);
        Assert.Equal(2, e.In.Count);
        Assert.Equal(0, e.In[0].U);
        Assert.Equal(-(800 + 240), e.In[0].X);
        Assert.Equal(0, e.In[0].Opacity);
        Assert.Equal(1, e.In[1].U);
        Assert.Equal(0, e.In[1].X);
        Assert.Equal(EaseKind.EaseOut, e.In[1].Ease); // the ease sits on the key travelled to
        Assert.Equal(2, e.Out.Count);
        Assert.Equal(0, e.Out[0].U);
        Assert.Equal(1, e.Out[1].U);
        Assert.Equal(0, e.Out[1].Opacity);
        Assert.Equal(EaseKind.EaseIn, e.Out[1].Ease);

        LowerThirdMotions.Apply(e, d, LowerThirdMotion.None, LowerThirdMotion.Wipe);
        Assert.Empty(e.In);
        Assert.Equal(0, e.Out[1].Reveal);
        Assert.Equal(EaseKind.Linear, e.Out[1].Ease);
        var pop = LowerThirdMotions.Keys(LowerThirdMotion.Pop, true, 0);
        Assert.Equal(0.6, pop[0].Scale);
        Assert.Equal(EaseKind.Back, pop[1].Ease);
        Assert.Equal(-220, LowerThirdMotions.Keys(LowerThirdMotion.Drop, true, 220)[0].Y);

        // The stagger: an element delayed 300 of 600 ms has not started at a quarter and is halfway at three quarters.
        LowerThirdMotions.Apply(e, d, LowerThirdMotion.SlideLeft, LowerThirdMotion.Fade);
        e.DelayMs = 300;
        var early = LowerThirdClock.PoseOf(e, d, new LowerThirdTiming(LowerThirdPhase.In, 0.25));
        Assert.Equal(-1040, early.X, 3);
        var late = LowerThirdClock.PoseOf(e, d, new LowerThirdTiming(LowerThirdPhase.In, 0.75));
        Assert.InRange(late.X, -1040, -1);
        Assert.Equal(ElementPose.Identity, LowerThirdClock.PoseOf(e, d, new LowerThirdTiming(LowerThirdPhase.Hold, 1)));
        // No keys at all is a plain fade in and out.
        var plain = new LowerThirdElement();
        Assert.Equal(0.5f, LowerThirdClock.PoseOf(plain, d, new LowerThirdTiming(LowerThirdPhase.In, 0.5)).Opacity, 3);
        Assert.Equal(0.5f, LowerThirdClock.PoseOf(plain, d, new LowerThirdTiming(LowerThirdPhase.Out, 0.5)).Opacity, 3);
        Assert.Equal(0f, LowerThirdClock.PoseOf(plain, d, new LowerThirdTiming(LowerThirdPhase.Gone, 1)).Opacity);
    }

    [Fact]
    public void TenPresetsAreSoundAndADesignTravelsInTheShow()
    {
        Assert.Equal(10, LowerThirdPresets.Names.Count);
        var kinds = new HashSet<LowerThirdElementKind>();
        foreach (var name in LowerThirdPresets.Names)
        {
            var d = LowerThirdPresets.Create(name);
            Assert.Equal(name, d.Preset);
            Assert.NotEmpty(d.Elements);
            Assert.Equal(d.Elements.Count, d.Elements.Select(e => e.Id).Distinct().Count());
            foreach (var e in d.Elements)
            {
                kinds.Add(e.Kind);
                Assert.True(e.X >= 0 && e.Y >= 0 && e.X + e.W <= d.Width + 0.5 && e.Y + e.H <= d.Height + 0.5, $"{name}/{e.Name} sits inside the box");
                foreach (var keys in new[] { e.In, e.Out })
                {
                    for (var i = 1; i < keys.Count; i++) Assert.True(keys[i].U >= keys[i - 1].U, $"{name}/{e.Name} keys are in order");
                }
                Assert.True(e.DelayMs < d.InMs, $"{name}/{e.Name} still lands with the rest");
            }
            Assert.Contains(d.Elements, e => e.Kind == LowerThirdElementKind.Text);
        }
        Assert.Contains(LowerThirdElementKind.Particles, kinds);
        Assert.Contains(LowerThirdElementKind.Fractal, kinds);
        Assert.Contains(LowerThirdElementKind.Logo, kinds);
        Assert.Contains(LowerThirdElementKind.Image, kinds);
        Assert.Empty(LowerThirdPresets.Create("no such preset").Elements);

        // Through the show's JSON: every element, key and enum comes back; a clone gets fresh ids.
        var state = new ShowState();
        var neon = LowerThirdPresets.Create("Neon");
        state.LowerThirds.Designs.Add(neon);
        state.LowerThirds.Show(neon, ShowClock.UtcAt(3));
        var back = JsonUtil.Deserialize<ShowState>(JsonUtil.Serialize(state))!;
        var again = Assert.Single(back.LowerThirds.Designs);
        Assert.Equal(neon.Id, again.Id);
        Assert.Equal(neon.Elements.Count, again.Elements.Count);
        Assert.Equal(neon.Elements[0].In.Count, again.Elements[0].In.Count);
        Assert.Equal(neon.Elements[0].Chaser, again.Elements[0].Chaser);
        Assert.Equal(EaseKind.EaseOut, again.Elements[0].In[1].Ease);
        Assert.Equal(neon.Id, back.LowerThirds.ActiveId);
        Assert.Equal(ShowClock.UtcAt(3), back.LowerThirds.ShownAtUtc);
        Assert.Same(again, back.LowerThirds.Active);
        var copy = neon.Clone();
        Assert.NotEqual(neon.Id, copy.Id);
        Assert.Equal(neon.Elements.Count, copy.Elements.Count);
        Assert.DoesNotContain(copy.Elements, e => neon.Elements.Any(o => o.Id == e.Id));
        Assert.Equal(neon.Elements[0].GlowPx, copy.Elements[0].GlowPx);
    }

    private static (int R, int G, int B) Pixel(SKBitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        return (c.Red, c.Green, c.Blue);
    }

    [Fact]
    public void TheDesignDrawsInItsBoxOnEverySinkAndLeavesCleanly()
    {
        var plain = RenderTestHarness.State(s => s.Pattern.Kind = PatternKind.FlatField);
        var state = RenderTestHarness.State(s => s.Pattern.Kind = PatternKind.FlatField);
        var clean = LowerThirdPresets.Create("Clean");
        state.LowerThirds.Designs.Add(clean);
        state.LowerThirds.Show(clean, ShowClock.UtcAt(0.5));

        // The box: bottom-left, 60 px in, 960×200 at 1080 lines.
        var box = LowerThirdRenderer.BoxOf(clean, new SKSizeI(1920, 1080), out var scale);
        Assert.Equal(1f, scale, 3);
        Assert.Equal(SKRect.Create(60, 820, 960, 200), box);
        var half = LowerThirdRenderer.BoxOf(clean, new SKSizeI(960, 540), out scale);
        Assert.Equal(0.5f, scale, 3);
        Assert.Equal(SKRect.Create(30, 410, 480, 100), half);

        using var without = RenderTestHarness.Render(plain, 1920, 1080, time: 3.0);
        using var held = RenderTestHarness.Render(state, 1920, 1080, time: 3.0);
        var inside = Pixel(held, 540, 920);
        Assert.NotEqual(Pixel(without, 540, 920), inside);
        Assert.True(inside.R > 200 && inside.G > 200 && inside.B > 200, "the Clean panel is light");
        Assert.Equal(Pixel(without, 1800, 100), Pixel(held, 1800, 100)); // nothing outside the box

        // Halfway in, the panel has not arrived where it rests; before the show, nothing at all.
        using var arriving = RenderTestHarness.Render(state, 1920, 1080, time: 0.65);
        Assert.NotEqual(inside, Pixel(arriving, 540, 920));
        using var before = RenderTestHarness.Render(state, 1920, 1080, time: 0.2);
        Assert.Equal(Pixel(without, 540, 920), Pixel(before, 540, 920));

        // The same picture on the NDI sink and on a thumbnail; hidden and gone, the canvas is clean again.
        using var ndi = RenderTestHarness.Render(state, 1920, 1080, time: 3.0, sinkKind: SinkKind.Ndi);
        Assert.Equal(inside, Pixel(ndi, 540, 920));
        using var thumb = RenderTestHarness.Render(state, 480, 270, time: 3.0, sinkKind: SinkKind.Thumbnail);
        Assert.True(Pixel(thumb, 135, 230).R > 150, "the thumbnail carries it, scaled");
        state.LowerThirds.Hide(ShowClock.UtcAt(3.5));
        using var leaving = RenderTestHarness.Render(state, 1920, 1080, time: 3.6);
        Assert.NotEqual(Pixel(without, 540, 920), Pixel(leaving, 540, 920));
        using var gone = RenderTestHarness.Render(state, 1920, 1080, time: 4.5);
        Assert.Equal(Pixel(without, 540, 920), Pixel(gone, 540, 920));

        // The cadence: continuous while it is on, back to static once it has gone.
        var snap = RenderTestHarness.Snap(state);
        Assert.Equal(RedrawCadence.Continuous, PatternEngine.CadenceOf(snap, null, ShowClock.UtcAt(3.6)));
        Assert.Equal(RedrawCadence.Static, PatternEngine.CadenceOf(snap, null, ShowClock.UtcAt(4.5)));
    }

    [Fact]
    public void EveryElementKindDrawsAndAFailingOneSitsOut()
    {
        var state = RenderTestHarness.State(s => s.Pattern.Kind = PatternKind.FlatField);
        var d = new LowerThirdDesign { Name = "All kinds", Width = 1200, Height = 300, InMs = 500, OutMs = 500 };
        var text = new LowerThirdElement
        {
            Name = "Words", Kind = LowerThirdElementKind.Text, TextKind = LowerThirdTextKind.Custom,
            Text = "A very long line of words that will not fit in the box it was given and must shrink to fit",
            X = 20, Y = 20, W = 600, H = 80, GlowPx = 18, ShadowPx = 6, Uppercase = true, Align = LowerThirdAlign.Center, TextColor = "accent",
        };
        var bar = new LowerThirdElement
        {
            Name = "Bar", Kind = LowerThirdElementKind.Bar, X = 0, Y = 110, W = 1200, H = 90, Fill = LowerThirdFill.Gradient,
            FillColor = "primary", FillColor2 = "#20FFFFFF", Gradient = LowerThirdGradient.Diagonal, CornerPx = 20, BorderPx = 3,
            GlowPx = 24, ShadowPx = 16, Chaser = true, ChaserSpeed = 1, Opacity = 0.8,
        };
        var image = new LowerThirdElement { Name = "Photo", Kind = LowerThirdElementKind.Image, X = 640, Y = 10, W = 120, H = 90, Path = "/nowhere/photo.png", CornerPx = 12, BorderPx = 2 };
        var logo = new LowerThirdElement { Name = "Logo", Kind = LowerThirdElementKind.Logo, X = 780, Y = 10, W = 120, H = 90 };
        var media = new LowerThirdElement { Name = "Clip", Kind = LowerThirdElementKind.Media, X = 920, Y = 10, W = 120, H = 90, Path = "/nowhere/clip.mp4" };
        var particles = new LowerThirdElement { Name = "Sparks", Kind = LowerThirdElementKind.Particles, X = 0, Y = 210, W = 600, H = 90 };
        particles.Particles.Count = 200;
        particles.Particles.Glow = true;
        var fractal = new LowerThirdElement { Name = "Wave", Kind = LowerThirdElementKind.Fractal, X = 600, Y = 210, W = 600, H = 90, CornerPx = 10 };
        fractal.Fractal.Quality = FractalQuality.Fast;
        fractal.Fractal.Iterations = 24;
        foreach (var e in new[] { text, bar, image, logo, media, particles, fractal })
        {
            LowerThirdMotions.Apply(e, d, LowerThirdMotion.Wipe, LowerThirdMotion.Spin);
            d.Elements.Add(e);
        }
        state.LowerThirds.Designs.Add(d);
        state.LowerThirds.Show(d, ShowClock.UtcAt(1));

        var engine = new PatternEngine();
        using var sink = new SinkState();
        var snap = RenderTestHarness.Snap(state);
        var info = new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        foreach (var time in new[] { 1.25, 2.0, 2.5 })
        {
            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(1920, 1080), ReferenceSize = new SKSizeI(1920, 1080), Time = time,
                Now = new DateTime(2026, 8, 29, 12, 0, 0), UtcNow = RenderTestHarness.FixedUtcNow, Sink = SinkKind.Output, SinkIndex = 1, SinkLabel = "test",
            };
            engine.Render(surface.Canvas, snap, in ctx, sink);
        }
        surface.Canvas.Flush();
        using var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        // The bar row is coloured, nothing threw (no error card), and every element kept a cache.
        var box = LowerThirdRenderer.BoxOf(d, new SKSizeI(1920, 1080), out _);
        var onBar = bmp.GetPixel((int)(box.Left + 300), (int)(box.Top + 155));
        Assert.True(onBar.Red + onBar.Green + onBar.Blue > 60, "the bar row is lit");
        Assert.NotEqual(new SKColor(0x14, 0x06, 0x06), bmp.GetPixel(10, 10));
        Assert.Equal(7, sink.LowerThirds.Count);
        Assert.NotNull(sink.LowerThirds[particles.Id].Sim);
        Assert.NotNull(sink.LowerThirds[fractal.Id].Fractal);
        Assert.All(sink.LowerThirds.Values, c => Assert.Equal(-1, c.FailedVersion));

        // The hold shows the same again; leaving spins out; a removed element's cache is swept on the next version.
        state.LowerThirds.Hide(ShowClock.UtcAt(3));
        var later = RenderTestHarness.Snap(state, version: 2);
        d.Elements.Remove(logo);
        var swept = RenderTestHarness.Snap(state, version: 3);
        foreach (var (s, time) in new[] { (later, 3.25), (swept, 3.3) })
        {
            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(1920, 1080), ReferenceSize = new SKSizeI(1920, 1080), Time = time,
                Now = new DateTime(2026, 8, 29, 12, 0, 0), UtcNow = RenderTestHarness.FixedUtcNow, Sink = SinkKind.Output, SinkIndex = 1, SinkLabel = "test",
            };
            engine.Render(surface.Canvas, s, in ctx, sink);
        }
        Assert.Equal(6, sink.LowerThirds.Count);
        Assert.False(sink.LowerThirds.ContainsKey(logo.Id));

        // The text of every kind, and the colour words.
        var stamp = new LowerThirdDesign { PersonName = "Ada", PersonRole = "Engineer", Company = "", DateText = "", TimeText = "" };
        var now = new DateTime(2026, 8, 29, 14, 5, 0);
        Assert.Equal("Ada", LowerThirdRenderer.TextOf(new LowerThirdElement { TextKind = LowerThirdTextKind.Name }, stamp, now, "Acme"));
        Assert.Equal("Engineer", LowerThirdRenderer.TextOf(new LowerThirdElement { TextKind = LowerThirdTextKind.Role }, stamp, now, "Acme"));
        Assert.Equal("Acme", LowerThirdRenderer.TextOf(new LowerThirdElement { TextKind = LowerThirdTextKind.Company }, stamp, now, "Acme"));
        Assert.Equal("14:05", LowerThirdRenderer.TextOf(new LowerThirdElement { TextKind = LowerThirdTextKind.Time }, stamp, now, ""));
        Assert.Equal(now.ToString("ddd d MMM yyyy") + " · 14:05", LowerThirdRenderer.TextOf(new LowerThirdElement { TextKind = LowerThirdTextKind.DateAndTime }, stamp, now, ""));
        stamp.TimeText = "Doors 19:00";
        Assert.Equal("Doors 19:00", LowerThirdRenderer.TextOf(new LowerThirdElement { TextKind = LowerThirdTextKind.Time }, stamp, now, ""));
        Assert.Equal("hello", LowerThirdRenderer.TextOf(new LowerThirdElement { Text = "hello" }, stamp, now, ""));
    }

    [Fact]
    public void AMediaElementsClipIsWantedWhileTheDesignIsOn()
    {
        var state = new ShowState();
        var d = new LowerThirdDesign();
        var clip = new LowerThirdElement { Kind = LowerThirdElementKind.Media, Path = "/shows/broll.mp4", MediaMute = false, MediaVolumePct = 40 };
        var still = new LowerThirdElement { Kind = LowerThirdElementKind.Media, Path = "/shows/badge.png" };
        d.Elements.Add(clip);
        d.Elements.Add(still);
        state.LowerThirds.Designs.Add(d);
        Assert.Empty(MediaLocator.FindWantedInputs(RenderTestHarness.Snap(state)));

        state.LowerThirds.Show(d, ShowClock.UtcAt(1));
        var wanted = Assert.Single(MediaLocator.FindWantedInputs(RenderTestHarness.Snap(state)));
        Assert.Equal("vid:/shows/broll.mp4", wanted.Key);
        Assert.True(wanted.Loop);
        Assert.False(wanted.Mute);
        Assert.Equal(40, wanted.VolumePct);

        clip.Enabled = false;
        Assert.Empty(MediaLocator.FindWantedInputs(RenderTestHarness.Snap(state)));
        clip.Enabled = true;
        state.LowerThirds.Hide(ShowClock.UtcAt(2));
        Assert.Empty(MediaLocator.FindWantedInputs(RenderTestHarness.Snap(state)));
    }
}
