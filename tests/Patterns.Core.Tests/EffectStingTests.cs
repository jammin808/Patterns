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

    private static string Signature(SKBitmap bmp)
    {
        var sb = new System.Text.StringBuilder();
        for (var y = 0; y < bmp.Height; y += 9)
        {
            for (var x = 0; x < bmp.Width; x += 16) sb.Append(bmp.GetPixel(x, y).ToString());
        }
        return sb.ToString();
    }

    [Fact]
    public void EveryShapeIsNothingOutsideItsStingAndSettlesByTheEnd()
    {
        foreach (var preset in Enum.GetValues<PulsePreset>())
        {
            var sting = new EffectImpulse(preset, 2, 1.5);
            Assert.Equal(EffectSurge.Zero, EffectEnvelope.At(sting, 1.99));
            Assert.Equal(EffectSurge.Zero, EffectEnvelope.At(sting, 3.5));
            var strongest = 0f;
            for (var t = 2.0; t < 3.5; t += 0.01) strongest = Math.Max(strongest, EffectEnvelope.At(sting, t).Peak);
            Assert.True(strongest > 0.5, $"{preset} does something ({strongest:0.00})");
            var end = EffectEnvelope.At(sting, 3.495).Peak;
            Assert.True(end < 0.06, $"{preset} settles by the end ({end:0.000})");
            Assert.Equal(EffectScores.For(preset) is not null, EffectEnvelope.IsScored(preset));
        }
        Assert.Equal(new[] { PulsePreset.Explosion, PulsePreset.Rush, PulsePreset.Flash, PulsePreset.Bloom },
            Enum.GetValues<PulsePreset>().Where(p => !EffectEnvelope.IsScored(p)).ToArray());
        Assert.Equal(12, Enum.GetValues<PulsePreset>().Length);
    }

    [Fact]
    public void TheScoredShapesChangeTheSettingsInPhases()
    {
        var freeze = new EffectImpulse(PulsePreset.Freeze, 0, 1);
        var held = EffectEnvelope.At(freeze, 0.3);
        Assert.True(held.Slow > 0.95f && held.Burst == 0 && held.Hue < -0.2f, "held in slow motion, shifted cold");
        var release = EffectEnvelope.At(freeze, 0.66);
        Assert.True(release.Burst > 0.6f && release.Speed > 0.9f && release.Slow < 0.05f,
            $"the release: burst {release.Burst:0.00} speed {release.Speed:0.00} slow {release.Slow:0.00}");

        var vortex = new EffectImpulse(PulsePreset.Vortex, 0, 1);
        Assert.True(EffectEnvelope.At(vortex, 0.5).Swirl > 0.95f);
        Assert.True(EffectEnvelope.At(vortex, 0.5).Rotate > 0.6f);
        Assert.True(EffectEnvelope.At(vortex, 0.15).Swirl < EffectEnvelope.At(vortex, 0.5).Swirl, "spins up");
        Assert.True(EffectEnvelope.At(vortex, 0.7).Burst > 0.7f, "then it lets go");

        var strobe = new EffectImpulse(PulsePreset.Strobe, 0, 1);
        var hits = 0;
        var wasLit = false;
        for (var t = 0.0; t < 1; t += 0.001)
        {
            var lit = EffectEnvelope.At(strobe, t).Flash > 0.5f;
            if (lit && !wasLit) hits++;
            wasLit = lit;
        }
        Assert.Equal(EffectScores.StrobeHits, hits);
        Assert.True(EffectEnvelope.At(strobe, 0.51).Flash > 0.9f && EffectEnvelope.At(strobe, 0.6).Flash < 0.05f);
        Assert.NotEqual(EffectEnvelope.At(strobe, 0.02).Hue, EffectEnvelope.At(strobe, 0.145).Hue); // the colours flip between hits

        var gust = new EffectImpulse(PulsePreset.Gust, 0, 1);
        Assert.True(EffectEnvelope.At(gust, 0.1).Gust > 0.9f && EffectEnvelope.At(gust, 0.55).Gust < -0.9f, "one way, then back the other");

        var rainbow = new EffectImpulse(PulsePreset.Rainbow, 0, 1);
        var hue = 0f;
        for (var t = 0.0; t < 1; t += 0.02)
        {
            var h = EffectEnvelope.At(rainbow, t).Hue;
            Assert.True(h >= hue - 1e-4f, "the hue only ever turns forward");
            hue = h;
        }
        Assert.True(hue > 1.9f, "two full turns by the end");
        Assert.Equal(0f, EffectEnvelope.At(rainbow, 0.5).Flash);

        var nova = EffectEnvelope.At(new EffectImpulse(PulsePreset.Supernova, 0, 1), 0.2);
        Assert.True(nova.Lift > 0.95f && nova.Morph > 0.95f, "everything falls upward and the shape drifts");
        Assert.True(EffectEnvelope.At(new EffectImpulse(PulsePreset.Quake, 0, 1), 0.05).Shake > 0.95f);
        Assert.True(EffectEnvelope.At(new EffectImpulse(PulsePreset.Shockwave, 0, 1), 0.05).Zoom < -0.7f, "the fractal punches out");

        // Where the sting is rides along for the things that travel; it is never a strength.
        var mid = EffectEnvelope.At(new EffectImpulse(PulsePreset.Quake, 10, 2), 11);
        Assert.Equal(0.5f, mid.Progress, 3);
        Assert.Equal(1f, mid.Phase, 3);
        Assert.True(new EffectSurge(0, 0, 0, 0, 0) { Progress = 0.5f, Phase = 1f }.IsZero);
        Assert.True(new EffectSurge(0, 0, 0, 0, 0) { Hue = 1f }.IsZero); // a whole turn is the same colours
        Assert.Equal(0.5f, EffectColor.HueStrength(0.5f));
        Assert.Equal(0f, EffectColor.HueStrength(2f));
        Assert.Equal(1f, EffectEnvelope.Weights(PulsePreset.Vortex).Swirl);
        Assert.Equal(SKColors.Red, EffectColor.Turn(SKColors.Red, 1f));
        var turned = EffectColor.Turn(SKColors.Red, 1f / 3);
        Assert.True(turned.Green > 200 && turned.Red < 40, $"a third of a turn from red is green ({turned})");

        // The four pulses read exactly as they always did: no position fields on their surge.
        Assert.Equal(EffectEnvelope.Weights(PulsePreset.Bloom), EffectEnvelope.At(new EffectImpulse(PulsePreset.Bloom, 0, 1), EffectEnvelope.AttackShare));
    }

    [Fact]
    public void AVortexSpinsTheFieldAGustPushesItLiftTurnsGravityOverAndSlowMotionHoldsIt()
    {
        static double MeanX(ParticleSim sim)
        {
            double s = 0;
            for (var i = 0; i < sim.Count; i++) s += sim.PositionOf(i).X;
            return s / sim.Count;
        }

        static double MeanVy(ParticleSim sim)
        {
            double s = 0;
            for (var i = 0; i < sim.Count; i++) s += sim.VelocityOf(i).Vy;
            return s / sim.Count;
        }

        static double Spin(ParticleSim a, ParticleSim b, SKSizeI canvas)
        {
            double sum = 0;
            for (var i = 0; i < a.Count; i++)
            {
                var (x0, y0) = a.PositionOf(i);
                var (x1, y1) = b.PositionOf(i);
                var a0 = Math.Atan2(y0 - canvas.Height / 2.0, x0 - canvas.Width / 2.0);
                var a1 = Math.Atan2(y1 - canvas.Height / 2.0, x1 - canvas.Width / 2.0);
                var d = a1 - a0;
                while (d > Math.PI) d -= 2 * Math.PI;
                while (d < -Math.PI) d += 2 * Math.PI;
                sum += d;
            }
            return sum / a.Count;
        }

        static void Run(ParticleSim sim, int steps, in EffectSurge surge)
        {
            for (var i = 0; i < steps; i++) sim.StepFixed(ParticleSim.StepSeconds, surge);
        }

        var canvas = new SKSizeI(1280, 720);
        EffectImpulses.Clear();
        var none = EffectSurge.Zero;

        using var still = Sim("Starfield", canvas);
        using var spun = Sim("Starfield", canvas);
        still.Advance(1);
        spun.Advance(1);
        Run(still, 24, none);
        Run(spun, 24, new EffectSurge(0, 0, 0, 0, 0) { Swirl = 1f });
        Assert.True(Spin(still, spun, canvas) > 0.2, "a swirl turns the field round the centre");

        using var calm = Sim("Confetti", canvas);
        using var blown = Sim("Confetti", canvas);
        calm.Advance(1);
        blown.Advance(1);
        Run(calm, 120, none);
        Run(blown, 120, new EffectSurge(0, 0, 0, 0, 0) { Gust = 1f });
        Assert.True(MeanX(blown) - MeanX(calm) > 60, $"a gust pushes the field sideways ({MeanX(blown) - MeanX(calm):0} px)");

        using var falling = Sim("Confetti", canvas);
        using var lifted = Sim("Confetti", canvas);
        falling.Advance(1);
        lifted.Advance(1);
        Run(falling, 120, none);
        Run(lifted, 120, new EffectSurge(0, 0, 0, 0, 0) { Lift = 1f });
        // A falling field re-births at the top as fast as it leaves, so its mean height barely moves; its speed does.
        Assert.True(MeanVy(falling) - MeanVy(lifted) > 40, $"with gravity reversed the field slows and turns ({MeanVy(falling) - MeanVy(lifted):0} px/s)");

        using var moving = Sim("Snow", canvas);
        using var frozen = Sim("Snow", canvas);
        moving.Advance(1);
        frozen.Advance(1);
        var before = new (double X, double Y)[moving.Count];
        for (var i = 0; i < moving.Count; i++) before[i] = moving.PositionOf(i);
        Run(moving, 120, none);
        Run(frozen, 120, new EffectSurge(0, 0, 0, 0, 0) { Slow = 1f });
        double moved = 0, held = 0;
        for (var i = 0; i < moving.Count; i++)
        {
            var (mx, my) = moving.PositionOf(i);
            var (fx, fy) = frozen.PositionOf(i);
            moved += Math.Sqrt((mx - before[i].X) * (mx - before[i].X) + (my - before[i].Y) * (my - before[i].Y));
            held += Math.Sqrt((fx - before[i].X) * (fx - before[i].X) + (fy - before[i].Y) * (fy - before[i].Y));
        }
        Assert.True(held < moved * 0.2, $"slow motion all but stops the field ({held / moving.Count:0.0} px against {moved / moving.Count:0.0})");

        // The shake is a pure function of the sting's phase: the same on every sink, nothing without a shake.
        var quake = new EffectSurge(0, 0, 0, 0, 0) { Shake = 1f, Phase = 0.4f };
        Assert.Equal(ParticleSim.ShakeOffset(quake, 720), ParticleSim.ShakeOffset(quake, 720));
        Assert.NotEqual(ParticleSim.ShakeOffset(quake, 720), ParticleSim.ShakeOffset(quake with { Phase = 0.41f }, 720));
        Assert.Equal((0f, 0f), ParticleSim.ShakeOffset(new EffectSurge(0, 0, 0, 0, 0) { Phase = 0.4f }, 720));
    }

    [Fact]
    public void ARainbowTurnsTheColoursOnScreenAndAQuakeDrawsCleanOnEverySink()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.Particles;
            ParticlePresets.Apply("Confetti", s.Pattern.Particles);
            s.Pattern.Particles.Count = 600;
            s.Pattern.Particles.Seed = 3;
        });
        EffectImpulses.Clear();
        using var plain = RenderTestHarness.Render(state, 320, 180, time: 2.6, sinkKind: SinkKind.Ndi);
        EffectImpulses.Fire(PulsePreset.Rainbow, 2.0, 2.0);   // at 2.6 the colours are 0.6 of a turn round the wheel
        using var hued = RenderTestHarness.Render(state, 320, 180, time: 2.6, sinkKind: SinkKind.Ndi);
        Assert.NotEqual(Signature(plain), Signature(hued));
        Assert.NotEqual(new SKColor(0x14, 0x06, 0x06), hued.GetPixel(160, 90));

        EffectImpulses.Fire(PulsePreset.Quake, 2.55, 1.0);   // shaking hard
        foreach (var sink in new[] { SinkKind.Output, SinkKind.Ndi, SinkKind.Preview })
        {
            using var shaken = RenderTestHarness.Render(state, 320, 180, time: 2.6, sinkKind: sink);
            Assert.NotEqual(new SKColor(0x14, 0x06, 0x06), shaken.GetPixel(160, 90));
        }
        EffectImpulses.Clear();
    }

    [Fact]
    public void AShockwavePunchesTheFractalOutAVortexTurnsItAndAMorphMovesTheJuliaSet()
    {
        var o = new FractalOptions { Kind = FractalKind.Julia, Speed = 0 };
        var still = FractalView.Of(o, 4.0, AudioLevelFrame.Zero);
        var punched = FractalView.Of(o, 4.0, AudioLevelFrame.Zero, surge: new EffectSurge(0, 0, 0, -0.8f, 0));
        Assert.True(punched.Span > still.Span, "a negative zoom punches out");

        var turned = FractalView.Of(o, 4.0, AudioLevelFrame.Zero, surge: new EffectSurge(0, 0, 0, 0, 0) { Rotate = 1f });
        Assert.Equal(Math.PI / 2, turned.Angle, 6);
        var (x, y) = turned.ToPlane(100, 50, 100, 100);   // a point right of the centre lands below it after a quarter turn
        var (sx, sy) = still.ToPlane(100, 50, 100, 100);
        Assert.Equal(still.CenterX, x, 6);
        Assert.True(y > still.CenterY);
        Assert.True(sx > still.CenterX);
        Assert.Equal(still.CenterY, sy, 6);

        var morphed = FractalView.Of(o, 4.0, AudioLevelFrame.Zero, surge: new EffectSurge(0, 0, 0, 0, 0) { Morph = 1f });
        Assert.NotEqual(still.JuliaRe, morphed.JuliaRe);
        Assert.Equal(1.0, morphed.Warp);
        Assert.NotEqual(FractalMath.Warp(0.3, 0.2, 4.0), FractalMath.Warp(0.3, 0.2, 4.0, 1));
        var hued = FractalView.Of(o, 4.0, AudioLevelFrame.Zero, surge: new EffectSurge(0, 0, 0, 0, 0) { Hue = 0.5f });
        Assert.Equal(still.PaletteOffset + 0.5, hued.PaletteOffset, 6);

        // Every family's shader still compiles with the turn and the warp in it.
        foreach (var kind in Enum.GetValues<FractalKind>())
        {
            using var effect = SKRuntimeEffect.CreateShader(Patterns.FractalPattern.SourceFor(kind), out var errors);
            Assert.True(effect is not null, $"{kind}: {errors}");
        }

        // A turned, morphing fractal draws clean on the CPU path and differs from the still one.
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.Fractal;
            s.Pattern.Fractal.Kind = FractalKind.DomainWarp;
            s.Pattern.Fractal.Speed = 0;
        });
        EffectImpulses.Clear();
        using var before = RenderTestHarness.Render(state, 320, 180, time: 3.0, sinkKind: SinkKind.Ndi);
        EffectImpulses.Fire(PulsePreset.Vortex, 2.5, 1.0);   // at 3.0: the plane turns and the shape drifts
        using var during = RenderTestHarness.Render(state, 320, 180, time: 3.0, sinkKind: SinkKind.Ndi);
        Assert.NotEqual(Signature(before), Signature(during));
        Assert.NotEqual(new SKColor(0x14, 0x06, 0x06), during.GetPixel(160, 90));
        EffectImpulses.Clear();
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
