using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Particles;
using Patterns.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The pulse envelope, the channel, and what a surge does to particles and fractals. One class: the channel is a static.</summary>
public class EffectStingTests : IDisposable
{
    public void Dispose() => EffectImpulses.Clear();

    [Fact]
    public void TheEnvelopeRisesFastSettlesToNothingAndEachPresetHasItsShape()
    {
        var pulse = new EffectImpulse(PulsePreset.Explosion, 10, 1);
        Assert.False(pulse.IsNone);
        Assert.Equal(11, pulse.EndSeconds);
        Assert.Equal(EffectSurge.Zero, EffectEnvelope.At(pulse, 9.99));
        Assert.Equal(EffectSurge.Zero, EffectEnvelope.At(pulse, 11));
        Assert.Equal(EffectSurge.Zero, EffectEnvelope.At(EffectImpulse.None, 10.5));

        var peak = EffectEnvelope.At(pulse, 10 + EffectEnvelope.AttackShare);
        Assert.Equal(EffectEnvelope.Weights(PulsePreset.Explosion), peak);
        Assert.Equal(1, peak.Burst);
        Assert.True(EffectEnvelope.At(pulse, 10.02).Burst < peak.Burst, "still rising");
        var last = peak.Peak;
        for (var t = 10.1; t < 11; t += 0.1)
        {
            var now = EffectEnvelope.At(pulse, t).Peak;
            Assert.True(now <= last + 1e-6, $"the surge never grows on the way down ({t:0.0})");
            last = now;
        }
        Assert.True(EffectEnvelope.At(pulse, 10.98).Peak < 0.01, "settled by the end");
        Assert.True(EffectEnvelope.At(pulse, 10.5).Peak < 0.5, "most of the surge is over halfway through");

        var flash = EffectEnvelope.Weights(PulsePreset.Flash);
        Assert.Equal((0f, 1f, 1f), (flash.Burst, flash.Flash, flash.Glow));
        Assert.Equal(1f, EffectEnvelope.Weights(PulsePreset.Rush).Speed);
        Assert.Equal(1f, EffectEnvelope.Weights(PulsePreset.Bloom).Glow);
        Assert.Equal(EffectEnvelope.Weights(PulsePreset.Explosion), EffectEnvelope.Weights((PulsePreset)99)); // a newer build's preset surges as an explosion
        Assert.True(EffectSurge.Zero.IsZero);
        Assert.False(peak.IsZero);

        // A very short pulse still has a rise of at least forty milliseconds.
        var blink = new EffectImpulse(PulsePreset.Flash, 0, 0.1);
        Assert.True(EffectEnvelope.At(blink, 0.02).Flash < EffectEnvelope.At(blink, 0.04).Flash);
    }

    [Fact]
    public void TheChannelHoldsTheLastPulseAndReadsByShowTime()
    {
        EffectImpulses.Clear();
        Assert.True(EffectImpulses.Current.IsNone);
        Assert.Equal(EffectSurge.Zero, EffectImpulses.SurgeAt(5));
        EffectImpulses.Fire(PulsePreset.Rush, 5, 0.9);
        Assert.Equal((PulsePreset.Rush, 5.0, 0.9), (EffectImpulses.Current.Preset, EffectImpulses.Current.StartSeconds, EffectImpulses.Current.LengthSeconds));
        Assert.True(EffectImpulses.SurgeAt(5.1).Speed > 0.5);
        Assert.Equal(EffectSurge.Zero, EffectImpulses.SurgeAt(6));
        EffectImpulses.Fire(PulsePreset.Flash, 7, 0.001);
        Assert.Equal(0.05, EffectImpulses.Current.LengthSeconds); // never shorter than a frame or two
        EffectImpulses.Clear();
        Assert.True(EffectImpulses.Current.IsNone);
    }

    private static ParticleSim Sim(string preset, SKSizeI canvas)
    {
        var sim = new ParticleSim();
        var o = new ParticleOptions();
        ParticlePresets.Apply(preset, o);
        o.Seed = 11;
        sim.Configure(o, RenderTestHarness.Snap(new ShowState()), canvas);
        return sim;
    }

    [Fact]
    public void TwoSinksStepThroughAPulseIdentically()
    {
        EffectImpulses.Fire(PulsePreset.Explosion, 1.0, 0.9);
        using var a = Sim("Confetti", new SKSizeI(1280, 720));
        using var b = Sim("Confetti", new SKSizeI(1280, 720));
        a.Advance(0.5);
        a.Advance(1.4);
        a.Advance(3.0);
        b.Advance(3.0);           // one sink woke late and caught up through the same steps
        for (var i = 0; i < a.Count; i += 5) Assert.Equal(a.PositionOf(i), b.PositionOf(i));
    }

    [Fact]
    public void AnExplosionBirthsAtTheEmitterAndTheFieldSettlesAfterwards()
    {
        var canvas = new SKSizeI(1920, 1080);
        static double NearCentre(ParticleSim sim, SKSizeI canvas)
        {
            var near = 0;
            for (var i = 0; i < sim.Count; i++)
            {
                var (x, y) = sim.PositionOf(i);
                var dx = x - canvas.Width / 2.0;
                var dy = y - canvas.Height / 2.0;
                if (Math.Sqrt(dx * dx + dy * dy) < canvas.Height * 0.12) near++;
            }
            return near / (double)sim.Count;
        }

        static double Fresh(ParticleSim sim)
        {
            var young = 0;
            for (var i = 0; i < sim.Count; i++)
            {
                if (sim.AgeOf(i) < 0.16f) young++;
            }
            return young / (double)sim.Count;
        }

        EffectImpulses.Clear();
        using var quiet = Sim("Starfield", canvas);
        quiet.Advance(2.15);
        var quietShare = NearCentre(quiet, canvas);
        var quietFresh = Fresh(quiet);

        EffectImpulses.Fire(PulsePreset.Explosion, 2.0, 0.9);
        using var burst = Sim("Starfield", canvas);
        burst.Advance(2.15);
        var burstShare = NearCentre(burst, canvas);
        var burstFresh = Fresh(burst);
        Assert.True(burstFresh > Math.Max(0.15, quietFresh * 2), $"born in the last 160 ms: {burstFresh:0.000} with the burst, {quietFresh:0.000} without");
        Assert.True(burstShare > quietShare, $"near the centre: {burstShare:0.000} with the burst, {quietShare:0.000} without");

        // Long after the pulse the field is a starfield again: the surge is zero and nothing keeps re-birthing.
        burst.Advance(8);
        Assert.Equal(EffectSurge.Zero, EffectImpulses.SurgeAt(8));
        var settled = NearCentre(burst, canvas);
        Assert.InRange(settled, quietShare * 0.4, quietShare * 2.5);

        // A field that never saw a pulse is untouched by the channel.
        EffectImpulses.Clear();
        using var c1 = Sim("Snow", new SKSizeI(800, 450));
        using var c2 = Sim("Snow", new SKSizeI(800, 450));
        c1.Advance(2);
        var steps = (long)(2 / ParticleSim.StepSeconds); // the sim's own quantisation of two seconds
        for (var i = 0; i < steps; i++) c2.StepFixed(ParticleSim.StepSeconds);
        for (var i = 0; i < c1.Count; i += 7) Assert.Equal(c1.PositionOf(i), c2.PositionOf(i));
    }

    [Fact]
    public void AFractalDivesInBrightensAndFlashesUnderAPulseThenComesBack()
    {
        var o = new FractalOptions { Kind = FractalKind.Julia, Speed = 0 };
        var still = FractalView.Of(o, 4.0, AudioLevelFrame.Zero);
        var surged = FractalView.Of(o, 4.0, AudioLevelFrame.Zero, surge: EffectEnvelope.Weights(PulsePreset.Rush));
        Assert.True(surged.Span < still.Span, "a rush dives in");
        Assert.True(surged.Brightness > still.Brightness);
        Assert.True(surged.PaletteOffset > still.PaletteOffset);
        Assert.Equal(still, FractalView.Of(o, 4.0, AudioLevelFrame.Zero, surge: EffectSurge.Zero));

        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.Fractal;
            s.Pattern.Fractal.Kind = FractalKind.Mandelbrot;
            s.Pattern.Fractal.Iterations = 40;
            s.Pattern.Fractal.Speed = 0; // a still picture, so only the pulse can change it between frames
        });
        EffectImpulses.Clear();
        using var before = RenderTestHarness.Render(state, 320, 180, time: 3.0, sinkKind: SinkKind.Ndi);
        EffectImpulses.Fire(PulsePreset.Flash, 2.95, 0.5);
        using var during = RenderTestHarness.Render(state, 320, 180, time: 3.0, sinkKind: SinkKind.Ndi);
        using var after = RenderTestHarness.Render(state, 320, 180, time: 3.6, sinkKind: SinkKind.Ndi);
        Assert.True(Mean(during) > Mean(before) + 20, $"the flash brightens the frame ({Mean(before):0} → {Mean(during):0})");
        Assert.InRange(Mean(after), Mean(before) - 2, Mean(before) + 2);
        Assert.NotEqual(new SKColor(0x14, 0x06, 0x06), during.GetPixel(160, 90));
    }

    private static double Mean(SKBitmap bmp)
    {
        double sum = 0;
        var n = 0;
        for (var y = 0; y < bmp.Height; y += 9)
        {
            for (var x = 0; x < bmp.Width; x += 16)
            {
                var p = bmp.GetPixel(x, y);
                sum += (p.Red + p.Green + p.Blue) / 3.0;
                n++;
            }
        }
        return sum / n;
    }

    [Fact]
    public void TheFlashDrawsWhiteOverThePictureInProportion()
    {
        using var surface = SKSurface.Create(new SKImageInfo(40, 20));
        using var sink = new SinkState();
        surface.Canvas.Clear(SKColors.Black);
        EffectFlash.Draw(surface.Canvas, 40, 20, 0, sink.Paints);
        using var black = surface.Snapshot();
        using var none = SKBitmap.FromImage(black);
        Assert.Equal(SKColors.Black, none.GetPixel(20, 10));
        EffectFlash.Draw(surface.Canvas, 40, 20, 1, sink.Paints);
        using var lit = surface.Snapshot();
        using var white = SKBitmap.FromImage(lit);
        Assert.InRange(white.GetPixel(20, 10).Red, 170, 190); // 70 % of the way to white
    }
}
