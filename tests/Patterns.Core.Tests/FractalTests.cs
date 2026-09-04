using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Patterns;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The fractal maths point by point, the view, the CPU raster, both render paths, and the scenes.</summary>
public class FractalMathTests
{
    [Fact]
    public void KnownPointsOfEveryFamily()
    {
        Assert.Equal(1, FractalMath.Sample(FractalKind.Mandelbrot, 0, 0, 0, 0, 64, 0));        // the origin never escapes
        Assert.True(FractalMath.Sample(FractalKind.Mandelbrot, 2, 2, 0, 0, 64, 0) < 0.05);    // far out: gone at once
        var edge = FractalMath.Sample(FractalKind.Mandelbrot, -0.75, 0.1, 0, 0, 64, 0);
        Assert.InRange(edge, 0.01, 0.999999);

        // A Julia set is symmetric through the origin.
        Assert.Equal(FractalMath.Sample(FractalKind.Julia, 0.3, 0.2, -0.72, 0.27, 80, 0),
            FractalMath.Sample(FractalKind.Julia, -0.3, -0.2, -0.72, 0.27, 80, 0));
        Assert.Equal(1, FractalMath.Sample(FractalKind.BurningShip, 0, 0, 0, 0, 64, 0));

        // Newton: each cube root of one is found from nearby, quickly.
        var one = FractalMath.Sample(FractalKind.Newton, 1.1, 0.05, 0, 0, 40, 0);
        Assert.InRange(one, 0, 1.0 / 3);
        var second = FractalMath.Sample(FractalKind.Newton, -0.5, 0.9, 0, 0, 40, 0);
        Assert.InRange(second, 1.0 / 3, 2.0 / 3);
        var third = FractalMath.Sample(FractalKind.Newton, -0.5, -0.9, 0, 0, 40, 0);
        Assert.InRange(third, 2.0 / 3, 1);
        Assert.True(one % (1.0 / 3) < 0.1, "a point next to a root converges in a few steps");

        // Domain warp: bounded, deterministic, not flat, and moving with time.
        var a = FractalMath.Sample(FractalKind.DomainWarp, 0.3, 0.7, 0, 0, 0, 1.0);
        Assert.InRange(a, 0, 1);
        Assert.Equal(a, FractalMath.Sample(FractalKind.DomainWarp, 0.3, 0.7, 0, 0, 0, 1.0));
        Assert.NotEqual(a, FractalMath.Sample(FractalKind.DomainWarp, 0.9, -0.4, 0, 0, 0, 1.0));
        Assert.NotEqual(a, FractalMath.Sample(FractalKind.DomainWarp, 0.3, 0.7, 0, 0, 0, 7.0));
        Assert.InRange(FractalMath.Hash(12, 34), 0, 1);
        Assert.InRange(FractalMath.Noise(1.5, 2.25), 0, 1);
    }

    [Fact]
    public void TheViewMapsPixelsToThePlaneAndTheSoundBreathesOnlyWhenListening()
    {
        var o = new FractalOptions { Zoom = 1, CenterX = -0.6, CenterY = 0, Speed = 0 };
        var still = FractalView.Of(o, 3.0, AudioLevelFrame.Zero);
        Assert.Equal((-0.6, 0.0), still.ToPlane(960, 540, 1920, 1080));
        var (_, top) = still.ToPlane(960, 0, 1920, 1080);
        Assert.Equal(-1.2, top, 6);
        Assert.Equal(FractalView.BaseSpan, still.Span, 6);
        Assert.Equal(96, still.Iterations);
        Assert.Equal(64, FractalView.Of(o, 3.0, AudioLevelFrame.Zero, iterationCap: 64).Iterations);

        o.Zoom = 2;
        Assert.Equal(FractalView.BaseSpan / 2, FractalView.Of(o, 3.0, AudioLevelFrame.Zero).Span, 6);

        var loud = new AudioLevelFrame(1, 1, 1, 1);
        Assert.Equal(FractalView.Of(o, 3.0, AudioLevelFrame.Zero), FractalView.Of(o, 3.0, loud)); // not listening: the sound changes nothing
        o.AudioSource = AudioSourceKind.Internal;
        o.AudioAmount = 1;
        var moved = FractalView.Of(o, 3.0, loud);
        Assert.True(moved.Span < FractalView.Of(o, 3.0, AudioLevelFrame.Zero).Span, "the level zooms in");
        Assert.True(moved.Brightness > 1);
        Assert.True(moved.PaletteOffset > 0);
    }

    [Fact]
    public void EveryFamilyRastersCleanWithAndWithoutSound()
    {
        var palette = new[] { SKColors.Black, SKColors.Blue, SKColors.White };
        FractalSurface? surface = null;
        foreach (var kind in Enum.GetValues<FractalKind>())
        {
            foreach (var audio in new[] { AudioLevelFrame.Zero, new AudioLevelFrame(0.8f, 0.6f, 0.5f, 0.9f) })
            {
                var o = new FractalOptions { Kind = kind, AudioSource = AudioSourceKind.Internal, Iterations = 48 };
                var view = FractalView.Of(o, 1.5, audio);
                surface = FractalRaster.Render(surface, new SKSizeI(64, 36), kind, palette, view);
                Assert.Equal(new SKSizeI(64, 36), surface.Size);
                var distinct = surface.Pixels.Distinct().Count();
                Assert.True(distinct >= 3, $"{kind} rastered {distinct} colours");
            }
        }
        var bigger = FractalRaster.Render(surface, new SKSizeI(80, 45), FractalKind.Julia, palette, FractalView.Of(new FractalOptions(), 0, AudioLevelFrame.Zero));
        Assert.NotSame(surface, bigger); // a size change allocates a fresh surface
        bigger.Dispose();

        Assert.Equal(new SKSizeI(240, 135), FractalRaster.SizeFor(FractalQuality.Balanced, new SKSizeI(1920, 1080)));
        Assert.Equal(new SKSizeI(160, 90), FractalRaster.SizeFor(FractalQuality.Fast, new SKSizeI(1920, 1080)));
        Assert.Equal(new SKSizeI(320, 180), FractalRaster.SizeFor(FractalQuality.Fine, new SKSizeI(1920, 1080)));
        Assert.Equal(new SKSizeI(100, 200), FractalRaster.SizeFor(FractalQuality.Fine, new SKSizeI(100, 200))); // never upscaled
    }

    [Fact]
    public void EveryFamilyRendersOnBothPathsWithoutTheErrorCard()
    {
        foreach (var kind in Enum.GetValues<FractalKind>())
        {
            foreach (var sink in new[] { SinkKind.Output, SinkKind.Ndi })
            {
                var state = RenderTestHarness.State(s =>
                {
                    s.Pattern.Kind = PatternKind.Fractal;
                    s.Pattern.Fractal.Kind = kind;
                    s.Pattern.Fractal.Iterations = 40;
                });
                using var bmp = RenderTestHarness.Render(state, 320, 180, time: 2.0, sinkKind: sink);
                Assert.NotEqual(new SKColor(0x14, 0x06, 0x06), bmp.GetPixel(160, 90));
                var colours = new HashSet<uint>();
                for (var y = 0; y < 180; y += 15)
                {
                    for (var x = 0; x < 320; x += 16) colours.Add((uint)bmp.GetPixel(x, y));
                }
                Assert.True(colours.Count >= 3, $"{kind} on {sink} drew {colours.Count} colours");
            }
        }
        Assert.True(FractalPattern.UsesShader(SinkKind.Output));
        Assert.True(FractalPattern.UsesShader(SinkKind.Preview));
        Assert.False(FractalPattern.UsesShader(SinkKind.Ndi));
        Assert.False(FractalPattern.UsesShader(SinkKind.Thumbnail));
        Assert.All(Enum.GetValues<FractalKind>(), k => Assert.Contains("half4 main(float2 px)", FractalPattern.SourceFor(k)));
    }

    [Fact]
    public void ScenesApplyAndLeaveTheSoundSettingsAlone()
    {
        Assert.True(FractalPresets.Names.Length >= 8);
        Assert.Equal(FractalPresets.Names.Length, FractalPresets.Names.Distinct().Count());
        var o = new FractalOptions { AudioSource = AudioSourceKind.External, AudioDevice = "Desk mic", AudioAmount = 0.9 };
        FractalPresets.Apply("Julia swirl", o);
        Assert.Equal(FractalKind.Julia, o.Kind);
        Assert.Equal("Julia swirl", o.Preset);
        Assert.Equal(AudioSourceKind.External, o.AudioSource);
        Assert.Equal("Desk mic", o.AudioDevice);
        Assert.Equal(0.9, o.AudioAmount);
        FractalPresets.Apply("no such scene", o);
        Assert.Equal(FractalPresets.Names[0], o.Preset);
        Assert.Equal(FractalKind.Mandelbrot, o.Kind);
        Assert.Equal("", new FractalOptions { AudioDevice = null! }.AudioDevice);
        Assert.Equal(8, new FractalOptions { Iterations = 1 }.Iterations);
    }
}

/// <summary>The sound side: a window of samples into bands, the follower, and the channel's staleness.</summary>
public class SpectrumTests
{
    private static float[] Sine(double hz, int rate, double amplitude, int n = 4096)
    {
        var s = new float[n];
        for (var i = 0; i < n; i++) s[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        return s;
    }

    [Fact]
    public void ASineLandsInItsBandAndSilenceIsNothing()
    {
        var mid = Spectrum.Analyse(Sine(440, 48000, 0.5), 48000);
        Assert.True(mid.Mid > mid.Low && mid.Mid > mid.High, $"{mid}");
        Assert.True(mid.Mid > 0.3, $"{mid}");
        Assert.InRange(mid.Level, 0.7, 1.0);

        var low = Spectrum.Analyse(Sine(60, 48000, 0.5), 48000);
        Assert.True(low.Low > low.Mid && low.Low > low.High, $"{low}");

        var high = Spectrum.Analyse(Sine(5000, 48000, 0.5), 48000);
        Assert.True(high.High > high.Mid && high.High > high.Low, $"{high}");

        Assert.Equal(AudioLevelFrame.Zero, Spectrum.Analyse(new float[2048], 48000));
        Assert.True(Spectrum.Analyse(new float[2048], 48000).IsSilent);
        Assert.Equal(AudioLevelFrame.Zero, Spectrum.Analyse(ReadOnlySpan<float>.Empty, 48000));
        Assert.True(Spectrum.Analyse(Sine(440, 48000, 1.0), 48000).Mid <= 1);
        var short1 = Spectrum.Analyse(Sine(440, 44100, 0.5, 300), 44100); // shorter than a window: padded
        Assert.True(short1.Level > 0);
    }

    [Fact]
    public void TheFollowerAttacksFastAndReleasesSlowly()
    {
        var f = new LevelSmoother();
        var up = f.Follow(new AudioLevelFrame(1, 1, 1, 1), 0.03);
        Assert.InRange(up.Level, 0.6, 0.7);
        f.Follow(new AudioLevelFrame(1, 1, 1, 1), 1);
        var down = f.Follow(AudioLevelFrame.Zero, 0.03);
        Assert.InRange(down.Level, 0.85, 0.95);
        var gone = f.Follow(AudioLevelFrame.Zero, 1.5);
        Assert.True(gone.Level < 0.01);
        Assert.Equal(gone, f.Current);
    }

    [Fact]
    public void LevelsGoStaleAfterASecond()
    {
        var t = new DateTime(2026, 9, 4, 22, 0, 0, DateTimeKind.Utc);
        var frame = new AudioLevelFrame(0.5f, 0.2f, 0.3f, 0.4f);
        AudioLevels.Publish(frame, t);
        Assert.Equal(frame, AudioLevels.Read(t.AddMilliseconds(500)));
        Assert.Equal(AudioLevelFrame.Zero, AudioLevels.Read(t.AddSeconds(1.5)));
        Assert.Equal(AudioLevelFrame.Zero, AudioLevels.Read(t.AddSeconds(-2))); // a clock that went backwards
        Assert.Equal(t, AudioLevels.LastUtc);
        AudioLevels.Clear();
        Assert.Equal(AudioLevelFrame.Zero, AudioLevels.Read(t));
    }

    [Fact]
    public void TheFftIsAnFftAndTheEveryKindRenderCoversTheFractal()
    {
        var re = new double[8];
        var im = new double[8];
        re[1] = 1; // an impulse at 1 → flat magnitude 1 across the bins
        Spectrum.Fft(re, im);
        for (var k = 0; k < 8; k++) Assert.Equal(1, Math.Sqrt(re[k] * re[k] + im[k] * im[k]), 6);
        Assert.Contains(PatternKind.Fractal, Enum.GetValues<PatternKind>());
    }
}
