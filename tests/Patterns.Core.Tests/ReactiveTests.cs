using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Patterns;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 21: the reactive scenes. The two things worth holding: the flash guard (a whole-screen
/// light change is the one effect with a documented harm, and three a second is the line), and the
/// agreement between the two drawing paths — outputs and the preview take the graphics card, NDI,
/// the stream and the thumbnails take the CPU twin, and the client's stream must show the wall's
/// picture.
/// </summary>
public class ReactiveTests
{
    // ---- the flash guard -----------------------------------------------------------------

    [Fact]
    public void ThreeFlashesASecondGetThroughAndTheFourthDoesNot()
    {
        var guard = new FlashGuard();
        // A pulse is a rise and a fall; four of them inside a second.
        var let = 0;
        for (var i = 0; i < 4; i++)
        {
            var at = i * 0.24;                       // ~4.2 a second
            if (guard.Limit(0.6f, at) > 0) let++;
            guard.Limit(0f, at + 0.1);               // the fall
        }
        Assert.Equal(2, let);                        // 0.00 and 0.48 land; 0.24 and 0.72 are too soon
        Assert.Equal(2, guard.Dropped);
        Assert.Equal(2, guard.Allowed);

        // Spaced out, every one gets through.
        var slow = new FlashGuard();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(slow.Limit(0.6f, i * 0.5) > 0);
            slow.Limit(0f, i * 0.5 + 0.2);
        }
        Assert.Equal(4, slow.Allowed);
        Assert.Equal(0, slow.Dropped);
    }

    [Fact]
    public void ADroppedFlashStaysDroppedForItsWholePulse()
    {
        // Letting it in halfway would be a shorter, sharper flash than the one refused.
        var guard = new FlashGuard();
        Assert.True(guard.Limit(0.6f, 0) > 0);
        guard.Limit(0f, 0.15);
        Assert.Equal(0f, guard.Limit(0.6f, 0.2));    // too soon
        Assert.Equal(0f, guard.Limit(0.7f, 0.24));   // still the same pulse
        Assert.Equal(0f, guard.Limit(0.5f, 0.28));
        guard.Limit(0f, 0.34);                       // the pulse ends
        Assert.True(guard.Limit(0.6f, 0.4) > 0);     // and the next one is far enough from the first
    }

    [Fact]
    public void TheFlashIsCappedAndTheClockMayGoBackwards()
    {
        var guard = new FlashGuard();
        Assert.Equal(FlashGuard.MaxLevel, guard.Limit(5f, 0));
        Assert.Equal(FlashGuard.MaxLevel, EffectFlash.MaxAlpha);   // the drawn cap is the guard's

        // A show clock that reset must not hold everything down until the old time comes round.
        guard.Limit(0f, 0.2);
        Assert.True(guard.Limit(0.6f, 5) > 0);      // a flash five seconds in
        guard.Limit(0f, 5.2);
        Assert.True(guard.Limit(0.6f, 0.1) > 0);    // the clock went back to the top: start again

        // Nonsense in, nothing out.
        Assert.Equal(0f, guard.Limit(float.NaN, 1));
        guard.Reset();
        Assert.Equal(0, guard.Allowed);
    }

    [Fact]
    public void TheSoundMovesTheBrightnessButCanNeverFlashWithIt()
    {
        // Whatever the sound does, the swing stays inside a bound no guidance would call a flash.
        for (var level = 0f; level <= 1f; level += 0.05f)
        {
            var b = FlashGuard.Brightness(1f, level, 1f);
            Assert.InRange(b, 1f - FlashGuard.MaxAudioBrightnessSwing - 0.001f, 1f + FlashGuard.MaxAudioBrightnessSwing + 0.001f);
        }
        // With the response off, the sound moves nothing.
        Assert.Equal(1f, FlashGuard.Brightness(1f, 1f, 0f), 5);
    }

    // ---- the scenes ----------------------------------------------------------------------

    private static readonly ReactiveScene[] AllScenes = Enum.GetValues<ReactiveScene>();

    [Fact]
    public void EveryScenePaintsAMovingPictureWithNoSoundAtAll()
    {
        // The premise: sound capture is Windows-only, so every scene must be finished in silence —
        // which is also every walk-in before the music starts.
        var o = new ReactiveOptions();
        foreach (var scene in AllScenes)
        {
            o.Scene = scene;
            var still = Frame(o, seconds: 0);
            var later = Frame(o, seconds: 3.5);
            Assert.True(Coverage(still) > 0.02, $"{scene} drew a flat frame in silence");
            Assert.True(Difference(still, later) > 0.01, $"{scene} did not move in silence");
        }
    }

    [Fact]
    public void TheSameClockAndOptionsDrawTheSameFrame()
    {
        var o = new ReactiveOptions { Scene = ReactiveScene.Vortex };
        Assert.Equal(0, Difference(Frame(o, 2.25), Frame(o, 2.25)), 6);
    }

    [Fact]
    public void TheCpuTwinAgreesWithTheShadersArithmetic()
    {
        // The two paths cannot share code — one is SkSL, one is C# — so they are held to the same
        // numbers instead: every scene, sampled across the frame, must stay inside the palette and
        // vary across it. The pixel-for-pixel comparison against a compiled shader lives in the
        // app tests, where a GPU surface exists.
        var view = ReactiveView.Of(new ReactiveOptions(), 1.75, new AudioLevelFrame(0.5f, 0.4f, 0.3f, 0.2f));
        foreach (var scene in AllScenes)
        {
            var seen = new HashSet<int>();
            for (var y = -8; y <= 8; y++)
            {
                for (var x = -8; x <= 8; x++)
                {
                    var v = ReactiveField.Sample(scene, x / 16.0, y / 16.0, in view);
                    Assert.InRange(v, 0, 1);
                    seen.Add((int)(v * 32));
                }
            }
            Assert.True(seen.Count > 3, $"{scene} is nearly flat across the frame");
        }
    }

    [Fact]
    public void TheWorkingSizeFollowsTheQualityAndTheLadder()
    {
        var canvas = new SKSizeI(1920, 1080);
        var fast = ReactiveRaster.SizeFor(ReactiveQuality.Fast, canvas);
        var fine = ReactiveRaster.SizeFor(ReactiveQuality.Fine, canvas);
        Assert.True(fast.Width < fine.Width);
        Assert.Equal(canvas.Height / (double)canvas.Width, fine.Height / (double)fine.Width, 2);

        // The ladder shrinks it further, and never below something drawable.
        var stepped = ReactiveRaster.SizeFor(ReactiveQuality.Fine, canvas, 0.25);
        Assert.True(stepped.Width < fine.Width);
        Assert.True(ReactiveRaster.SizeFor(ReactiveQuality.Fast, canvas, 0.01).Width >= 32);

        // A canvas smaller than the working width is never upsampled to draw.
        Assert.Equal(64, ReactiveRaster.SizeFor(ReactiveQuality.Fine, new SKSizeI(64, 36)).Width);
    }

    [Fact]
    public void ThePaletteWrapsSmoothlyAndTakesTheBrightness()
    {
        var colors = new[] { SKColors.Black, SKColors.Red, SKColors.White };
        Assert.Equal(ReactiveField.Map(0, colors, 1), ReactiveField.Map(1, colors, 1));   // the cycle closes
        var dim = ReactiveField.Map(0.5, colors, 0.5f);
        var lit = ReactiveField.Map(0.5, colors, 1f);
        Assert.True(dim.Red <= lit.Red && dim.Green <= lit.Green && dim.Blue <= lit.Blue);
        Assert.Equal(SKColors.Black, ReactiveField.Map(0.3, Array.Empty<SKColor>(), 1));  // nothing to paint with

        // A list longer than the shader's five slots is cut, so both paths read the same colours.
        Assert.Equal(ReactiveField.PaletteColors, ReactiveRaster.PaletteOf("#000,#111,#222,#333,#444,#555,#666").Length);
        Assert.Single(ReactiveRaster.PaletteOf("nonsense"));
    }

    [Fact]
    public void TheOptionsKeepTheirBoundsAndAnOlderShowOpensAtTheDefaults()
    {
        var o = new ReactiveOptions { Speed = 99, Depth = -3, Symmetry = 400, Rotation = 9, Brightness = 99, AudioAmount = 5 };
        Assert.Equal(3, o.Speed);
        Assert.Equal(0, o.Depth);
        Assert.Equal(16, o.Symmetry);
        Assert.Equal(1, o.Rotation);
        Assert.Equal(1.6, o.Brightness);
        Assert.Equal(1, o.AudioAmount);

        // A show file written before the scenes existed opens with them at their defaults.
        var older = JsonUtil.Deserialize<PatternConfig>("""{"Kind":"Grid"}""")!;
        Assert.Equal(ReactiveScene.Plasma, older.Reactive.Scene);
        Assert.True(older.Reactive.UseBrandColors);
        Assert.Equal(AudioSourceKind.None, older.Reactive.AudioSource);

        // And the round trip keeps a scene as written.
        var chosen = new PatternConfig { Kind = PatternKind.Reactive };
        chosen.Reactive.Scene = ReactiveScene.Vortex;
        chosen.Reactive.Symmetry = 9;
        var back = JsonUtil.Deserialize<PatternConfig>(JsonUtil.Serialize(chosen))!;
        Assert.Equal(PatternKind.Reactive, back.Kind);
        Assert.Equal(ReactiveScene.Vortex, back.Reactive.Scene);
        Assert.Equal(9, back.Reactive.Symmetry);
    }

    [Fact]
    public void AReactiveSceneRedrawsEveryFrameOnEverySink()
    {
        // Miss this and the scene is frozen on NDI, the stream, the thumbnails and every wall tile
        // while the main output moves.
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Reactive;
        var snap = new ShowSnapshot { State = state, Version = 1 };
        Assert.Equal(RedrawCadence.Continuous, PatternEngine.CadenceOf(snap, null, DateTime.UtcNow));
    }

    [Fact]
    public void TheMakersBadgeIsOnAReactiveSceneLikeEveryOtherPictureTheAppDraws()
    {
        // A reactive scene is Patterns' own generated picture, exactly like the particles and the
        // fractals beside it. Holding it back made the badge appear on every page but this one,
        // which an operator meets as a bug rather than as a policy.
        var badge = new BadgeOverlay();
        Assert.True(badge.Enabled);
        Assert.True(badge.ShowsOn(PatternKind.Grid));
        Assert.True(badge.ShowsOn(PatternKind.Particles));
        Assert.True(badge.ShowsOn(PatternKind.Fractal));
        Assert.True(badge.ShowsOn(PatternKind.Reactive));

        // What it still keeps off: someone else's content, and the monitoring picture.
        Assert.False(badge.ShowsOn(PatternKind.Media));
        Assert.False(badge.ShowsOn(PatternKind.Multiview));
        badge.OnMediaToo = true;
        Assert.True(badge.ShowsOn(PatternKind.Media));
        Assert.False(badge.ShowsOn(PatternKind.Multiview));  // never, asked for or not
    }

    [Fact]
    public void EverySceneHasAShaderSourceAndTheyAreNotTheSameOne()
    {
        var sources = new HashSet<string>();
        foreach (var scene in AllScenes)
        {
            var src = ReactivePattern.SourceFor(scene);
            Assert.Contains("half4 main(float2 frag)", src);
            Assert.Contains("uniform float2 res;", src);
            sources.Add(src);
        }
        Assert.Equal(AllScenes.Length, sources.Count);
        Assert.True(ReactivePattern.UsesShader(SinkKind.Output));
        Assert.True(ReactivePattern.UsesShader(SinkKind.Preview));
        Assert.True(ReactivePattern.UsesShader(SinkKind.Monitor));
        Assert.False(ReactivePattern.UsesShader(SinkKind.Ndi));
        Assert.False(ReactivePattern.UsesShader(SinkKind.Stream));
        Assert.False(ReactivePattern.UsesShader(SinkKind.Thumbnail));
    }

    [Fact]
    public void TheStingsSurgeReachesTheScene()
    {
        // The scenes answer the same impulse channels the particles and the fractals do, so an
        // existing sting drives them without anyone wiring anything up.
        var o = new ReactiveOptions();
        var quiet = ReactiveView.Of(o, 2, AudioLevelFrame.Zero);
        var surged = ReactiveView.Of(o, 2, AudioLevelFrame.Zero, new EffectSurge(0, 1f, 0, 0.5f, 0) { Rotate = 0.25f });
        Assert.True(surged.Time > quiet.Time, "a surge hurries the scene");
        Assert.True(surged.Depth > quiet.Depth, "a surge deepens the warp");
        Assert.NotEqual(quiet.Angle, surged.Angle);
    }

    [Fact]
    public void TheShaderAndTheCpuTwinDrawTheSamePicture()
    {
        // The scene contract's last clause, and the reason it exists: outputs draw the shader and
        // the client's stream draws the twin, so the two must be the same picture. They are held to
        // a mean-difference bound rather than to equality — the twin draws small and upscales, and
        // a shader works in half floats — but a scene whose maths drifted would blow straight past it.
        const double bound = 0.06;
        var o = new ReactiveOptions { UseBrandColors = false };
        var palette = ReactiveRaster.PaletteOf(o.ColorsCsv);
        using var sink = new SinkState();
        foreach (var scene in AllScenes)
        {
            o.Scene = scene;
            var view = ReactiveView.Of(o, 1.4, new AudioLevelFrame(0.5f, 0.45f, 0.3f, 0.2f));

            var info = new SKImageInfo(160, 90, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var drawn = ReactivePattern.TryDrawShader(
                surface.Canvas, sink, scene, palette, in view, info.Width, info.Height,
                SKRect.Create(0, 0, info.Width, info.Height), sink.Paints);
            Assert.True(drawn, $"the {scene} shader did not compile");
            surface.Canvas.Flush();
            using var shaded = new SKBitmap(info);
            Assert.True(surface.ReadPixels(info, shaded.GetPixels(), info.RowBytes, 0, 0));

            var twin = ReactiveRaster.Render(null, new SKSizeI(info.Width, info.Height), scene, palette, in view);
            using (twin)
            {
                var diff = Difference(shaded, twin.Bitmap);
                Assert.True(diff < bound, $"{scene}: the shader and the CPU twin differ by {diff:0.000}, past {bound}");
            }
        }
    }

    [Fact]
    public void ASceneWhoseShaderWillNotCompileFallsToTheCpuAndIsOnlyAskedOnce()
    {
        using var sink = new SinkState();
        sink.ReactiveUnavailable.Add(ReactiveScene.Tunnel);
        var view = ReactiveView.Of(new ReactiveOptions(), 0, AudioLevelFrame.Zero);
        using var surface = SKSurface.Create(new SKImageInfo(8, 8));
        Assert.False(ReactivePattern.TryDrawShader(
            surface.Canvas, sink, ReactiveScene.Tunnel, new[] { SKColors.White }, in view, 8, 8, SKRect.Create(0, 0, 8, 8), sink.Paints));
        Assert.Empty(sink.ReactiveEffects);   // never compiled again

        // And the twin draws that scene regardless, which is what the caller falls through to.
        using var twin = ReactiveRaster.Render(null, new SKSizeI(16, 9), ReactiveScene.Tunnel, new[] { SKColors.White, SKColors.Black }, in view);
        Assert.Equal(16 * 9, twin.Pixels.Length);
    }

    // ---- helpers -------------------------------------------------------------------------

    private static ReactiveSurface Frame(ReactiveOptions o, double seconds, AudioLevelFrame audio = default)
    {
        var view = ReactiveView.Of(o, seconds, audio);
        return ReactiveRaster.Render(null, new SKSizeI(48, 27), o.Scene, ReactiveRaster.PaletteOf(o.ColorsCsv), in view);
    }

    /// <summary>The share of pixels that are not the commonest colour — a flat frame scores near zero.</summary>
    private static double Coverage(ReactiveSurface s)
    {
        var counts = new Dictionary<int, int>();
        foreach (var p in s.Pixels) counts[p] = counts.GetValueOrDefault(p) + 1;
        return 1 - counts.Values.Max() / (double)s.Pixels.Length;
    }

    /// <summary>Mean absolute channel difference between two pictures of the same size, 0–1.</summary>
    private static double Difference(SKBitmap a, SKBitmap b)
    {
        double total = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                total += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
            }
        }
        return total / (a.Width * a.Height * 3 * 255.0);
    }

    /// <summary>Mean absolute channel difference, 0–1.</summary>
    private static double Difference(ReactiveSurface a, ReactiveSurface b)
    {
        double total = 0;
        for (var i = 0; i < a.Pixels.Length; i++)
        {
            var x = (uint)a.Pixels[i];
            var y = (uint)b.Pixels[i];
            total += Math.Abs((int)((x >> 16) & 0xFF) - (int)((y >> 16) & 0xFF))
                   + Math.Abs((int)((x >> 8) & 0xFF) - (int)((y >> 8) & 0xFF))
                   + Math.Abs((int)(x & 0xFF) - (int)(y & 0xFF));
        }
        return total / (a.Pixels.Length * 3 * 255.0);
    }
}
