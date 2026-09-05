using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Effects;

/// <summary>The white flash a pulse puts over the whole picture, drawn last by the patterns that surge.</summary>
public static class EffectFlash
{
    public const float MaxAlpha = 0.7f;

    public static void Draw(SKCanvas c, int w, int h, float flash, PaintCache paints)
    {
        if (flash <= 0.01f) return;
        var alpha = (byte)Math.Clamp(flash * MaxAlpha * 255, 0, 255);
        c.DrawRect(SKRect.Create(0, 0, w, h), paints.Fill(SKColors.White.WithAlpha(alpha)));
    }
}

/// <summary>Colour helpers for a surge's hue channel.</summary>
public static class EffectColor
{
    /// <summary>Turns a colour round the hue wheel; a whole turn is the same colour again.</summary>
    public static SKColor Turn(SKColor color, float turns)
    {
        if (HueStrength(turns) < 0.001f) return color;
        color.ToHsv(out var h, out var s, out var v);
        var hue = (h + turns * 360f) % 360f;
        if (hue < 0) hue += 360f;
        return SKColor.FromHsv(hue, s, v, color.Alpha);
    }

    /// <summary>How far a turn is from "no change": 0 at every whole turn, 0.5 at a half turn.</summary>
    public static float HueStrength(float turns) => Math.Abs(turns - MathF.Round(turns));
}

/// <summary>One fired effect pulse: its shape, when it started on the show clock, and how long it runs.</summary>
public readonly record struct EffectImpulse(PulsePreset Preset, double StartSeconds, double LengthSeconds)
{
    public static readonly EffectImpulse None = new(PulsePreset.Explosion, double.NegativeInfinity, 0);

    public bool IsNone => double.IsNegativeInfinity(StartSeconds) || LengthSeconds <= 0;

    public double EndSeconds => StartSeconds + LengthSeconds;
}

/// <summary>
/// The surge a sting puts through the picture at one instant. The five pulse channels:
/// <paramref name="Burst"/> (fresh births at the emitter), <paramref name="Speed"/> (everything
/// moves faster), <paramref name="Glow"/> (bigger, brighter, additive), <paramref name="Zoom"/>
/// (the fractal dives in; negative punches out), <paramref name="Flash"/> (a white flash over the
/// whole picture). The scored shapes drive more: <see cref="Hue"/> (a turn of the colours),
/// <see cref="Lift"/> (gravity reversed), <see cref="Swirl"/> (the field spins round the centre),
/// <see cref="Gust"/> (a side wind), <see cref="Scale"/> (bigger particles), <see cref="Ripple"/>
/// (a ring rolling out from the centre), <see cref="Shake"/> (the picture shakes),
/// <see cref="Slow"/> (slow motion), <see cref="Rotate"/> (the fractal plane turns) and
/// <see cref="Morph"/> (the fractal's shape drifts). <see cref="Progress"/> and
/// <see cref="Phase"/> say where in the sting this instant is, for the things that travel.
/// </summary>
public readonly record struct EffectSurge(float Burst, float Speed, float Glow, float Zoom, float Flash)
{
    public static readonly EffectSurge Zero = default;

    /// <summary>Turns round the hue wheel (particles) or the palette (fractals); 1 = the same colours again.</summary>
    public float Hue { get; init; }

    /// <summary>0–1: how much of gravity is reversed — 0.5 is weightless, 1 falls upward.</summary>
    public float Lift { get; init; }

    /// <summary>−1–1: the field spins round the centre and is drawn inward, a whirl.</summary>
    public float Swirl { get; init; }

    /// <summary>−1–1: a wind slams through sideways.</summary>
    public float Gust { get; init; }

    /// <summary>0–1: every particle is drawn bigger.</summary>
    public float Scale { get; init; }

    /// <summary>0–1: a ring of enlarged particles rolls out from the centre as the sting progresses.</summary>
    public float Ripple { get; init; }

    /// <summary>0–1: the whole picture shakes.</summary>
    public float Shake { get; init; }

    /// <summary>0–1: slow motion — 1 all but stops the field.</summary>
    public float Slow { get; init; }

    /// <summary>−1–1: the fractal plane turns, up to a quarter turn either way.</summary>
    public float Rotate { get; init; }

    /// <summary>0–1: the fractal's shape drifts — the Julia constant wanders, the warp deepens.</summary>
    public float Morph { get; init; }

    /// <summary>0–1: where in the sting this instant is (0 at the start). Not a strength.</summary>
    public float Progress { get; init; }

    /// <summary>Seconds since the sting started. Not a strength.</summary>
    public float Phase { get; init; }

    public bool IsZero => Peak <= 0;

    /// <summary>The strongest channel at this instant; the hue counts by its distance from a whole turn.</summary>
    public float Peak
    {
        get
        {
            var m = Math.Max(Math.Max(Burst, Speed), Math.Max(Math.Max(Glow, Math.Abs(Zoom)), Flash));
            m = Math.Max(m, Math.Max(EffectColor.HueStrength(Hue), Lift));
            m = Math.Max(m, Math.Max(Math.Abs(Swirl), Math.Abs(Gust)));
            m = Math.Max(m, Math.Max(Scale, Ripple));
            m = Math.Max(m, Math.Max(Shake, Slow));
            return Math.Max(m, Math.Max(Math.Abs(Rotate), Morph));
        }
    }

    /// <summary>Channel-by-channel blend; the position fields are blended too.</summary>
    public static EffectSurge Lerp(in EffectSurge a, in EffectSurge b, float k)
    {
        static float L(float x, float y, float k) => x + (y - x) * k;
        return new EffectSurge(L(a.Burst, b.Burst, k), L(a.Speed, b.Speed, k), L(a.Glow, b.Glow, k), L(a.Zoom, b.Zoom, k), L(a.Flash, b.Flash, k))
        {
            Hue = L(a.Hue, b.Hue, k),
            Lift = L(a.Lift, b.Lift, k),
            Swirl = L(a.Swirl, b.Swirl, k),
            Gust = L(a.Gust, b.Gust, k),
            Scale = L(a.Scale, b.Scale, k),
            Ripple = L(a.Ripple, b.Ripple, k),
            Shake = L(a.Shake, b.Shake, k),
            Slow = L(a.Slow, b.Slow, k),
            Rotate = L(a.Rotate, b.Rotate, k),
            Morph = L(a.Morph, b.Morph, k),
            Progress = L(a.Progress, b.Progress, k),
            Phase = L(a.Phase, b.Phase, k),
        };
    }

    /// <summary>Channel-by-channel maximum of the strengths (the position fields stay zero).</summary>
    public static EffectSurge Max(in EffectSurge a, in EffectSurge b)
    {
        static float M(float x, float y) => Math.Abs(y) > Math.Abs(x) ? y : x;
        return new EffectSurge(M(a.Burst, b.Burst), M(a.Speed, b.Speed), M(a.Glow, b.Glow), M(a.Zoom, b.Zoom), M(a.Flash, b.Flash))
        {
            Hue = M(a.Hue, b.Hue),
            Lift = M(a.Lift, b.Lift),
            Swirl = M(a.Swirl, b.Swirl),
            Gust = M(a.Gust, b.Gust),
            Scale = M(a.Scale, b.Scale),
            Ripple = M(a.Ripple, b.Ripple),
            Shake = M(a.Shake, b.Shake),
            Slow = M(a.Slow, b.Slow),
            Rotate = M(a.Rotate, b.Rotate),
            Morph = M(a.Morph, b.Morph),
        };
    }
}

/// <summary>One key of a scored sting: the surge at a point of the sting (0 = start, 1 = end).</summary>
public readonly record struct EffectKey(float U, EffectSurge Surge);

/// <summary>
/// The scored shapes: a sting as a run of keys through the sting's length, blended smoothly key
/// to key, so the settings change in phases — a freeze that releases, a vortex that spins up and
/// lets go, a strobe of eight hits. Every score ends at nothing.
/// </summary>
public static class EffectScores
{
    private static readonly EffectKey[] Shockwave =
    {
        new(0f, EffectSurge.Zero),
        new(0.05f, new EffectSurge(0.6f, 0.3f, 0.5f, -0.8f, 0.9f) { Shake = 0.6f }),
        new(0.25f, new EffectSurge(0f, 0.6f, 0.4f, 0.4f, 0f) { Ripple = 1f, Scale = 0.3f, Hue = 0.08f }),
        new(0.6f, new EffectSurge(0f, 0.2f, 0.4f, 0.2f, 0f) { Ripple = 0.7f, Hue = 0.04f }),
        new(1f, EffectSurge.Zero),
    };

    private static readonly EffectKey[] Vortex =
    {
        new(0f, EffectSurge.Zero),
        new(0.15f, new EffectSurge(0f, 0f, 0.2f, 0.1f, 0f) { Swirl = 0.6f, Rotate = 0.2f }),
        new(0.5f, new EffectSurge(0f, 0.1f, 0.6f, 0.5f, 0f) { Swirl = 1f, Rotate = 0.7f, Hue = 0.3f, Scale = 0.2f, Morph = 0.5f }),
        new(0.7f, new EffectSurge(0.8f, 0.8f, 0.5f, 0.2f, 0.5f) { Swirl = 0.3f, Rotate = 0.4f, Hue = 0.15f }),
        new(1f, EffectSurge.Zero),
    };

    private static readonly EffectKey[] Supernova =
    {
        new(0f, EffectSurge.Zero),
        new(0.04f, new EffectSurge(1f, 1f, 1f, 0.8f, 1f) { Scale = 0.8f, Shake = 0.8f }),
        new(0.2f, new EffectSurge(0f, 0.5f, 0.8f, 0.5f, 0f) { Lift = 1f, Hue = 0.5f, Rotate = 0.3f, Morph = 1f, Scale = 0.3f }),
        new(0.5f, new EffectSurge(0f, 0.2f, 0.5f, 0.4f, 0f) { Lift = 0.6f, Hue = 1f, Rotate = 0.2f, Morph = 0.5f }),
        new(0.8f, new EffectSurge(0f, 0f, 0.2f, 0.1f, 0f) { Lift = 0.2f, Hue = 1f }),
        new(1f, new EffectSurge(0f, 0f, 0f, 0f, 0f) { Hue = 1f }),
    };

    private static readonly EffectKey[] Freeze =
    {
        new(0f, EffectSurge.Zero),
        new(0.08f, new EffectSurge(0f, 0f, 0.3f, -0.2f, 0f) { Slow = 1f, Hue = -0.3f }),
        new(0.6f, new EffectSurge(0f, 0f, 0.4f, -0.2f, 0f) { Slow = 1f, Hue = -0.4f }),
        new(0.66f, new EffectSurge(0.7f, 1f, 0.6f, 0.6f, 0.6f) { Shake = 0.4f, Hue = -0.1f }),
        new(1f, EffectSurge.Zero),
    };

    private static readonly EffectKey[] Gust =
    {
        new(0f, EffectSurge.Zero),
        new(0.1f, new EffectSurge(0f, 0.5f, 0.1f, 0f, 0f) { Gust = 1f, Rotate = -0.3f }),
        new(0.4f, new EffectSurge(0f, 0.3f, 0.2f, 0f, 0f) { Gust = 0.4f, Ripple = 0.3f, Rotate = -0.1f }),
        new(0.55f, new EffectSurge(0f, 0.5f, 0.1f, 0f, 0f) { Gust = -1f, Rotate = 0.3f }),
        new(0.85f, new EffectSurge(0f, 0.2f, 0f, 0f, 0f) { Gust = -0.3f, Rotate = 0.1f }),
        new(1f, EffectSurge.Zero),
    };

    private static readonly EffectKey[] Rainbow =
    {
        new(0f, EffectSurge.Zero),
        new(0.1f, new EffectSurge(0f, 0f, 0.6f, 0f, 0f) { Hue = 0.2f, Scale = 0.2f }),
        new(0.5f, new EffectSurge(0f, 0.1f, 0.8f, 0.1f, 0f) { Hue = 1f, Scale = 0.3f, Morph = 0.6f }),
        new(0.9f, new EffectSurge(0f, 0f, 0.4f, 0f, 0f) { Hue = 1.8f, Scale = 0.1f }),
        new(1f, new EffectSurge(0f, 0f, 0f, 0f, 0f) { Hue = 2f }),
    };

    private static readonly EffectKey[] Quake =
    {
        new(0f, EffectSurge.Zero),
        new(0.05f, new EffectSurge(0.4f, 0.2f, 0.2f, 0.1f, 0.3f) { Shake = 1f }),
        new(0.3f, new EffectSurge(0f, 0.4f, 0.3f, 0f, 0f) { Shake = 0.8f, Ripple = 0.5f, Rotate = 0.1f }),
        new(0.6f, new EffectSurge(0f, 0.2f, 0.2f, 0f, 0f) { Shake = 0.4f, Ripple = 0.3f, Rotate = -0.1f }),
        new(1f, EffectSurge.Zero),
    };

    public const int StrobeHits = 8;

    private static readonly EffectKey[] Strobe = BuildStrobe();

    private static EffectKey[] BuildStrobe()
    {
        var keys = new List<EffectKey> { new(0f, EffectSurge.Zero) };
        var previous = 0f;
        for (var k = 0; k < StrobeHits; k++)
        {
            var u0 = k / (float)StrobeHits;
            var hue = k % 2 == 0 ? 0.5f : 0f;
            keys.Add(new EffectKey(u0 + 0.002f, new EffectSurge(0f, 0f, 0.2f, 0f, 0f) { Hue = previous }));
            keys.Add(new EffectKey(u0 + 0.006f, new EffectSurge(0f, 0.2f, 0.8f, 0f, 1f) { Hue = hue }));
            keys.Add(new EffectKey(u0 + 0.045f, new EffectSurge(0f, 0.2f, 0.8f, 0f, 0.9f) { Hue = hue }));
            keys.Add(new EffectKey(u0 + 0.08f, new EffectSurge(0f, 0f, 0.2f, 0f, 0f) { Hue = hue }));
            previous = hue;
        }
        keys.Add(new EffectKey(1f, EffectSurge.Zero));
        return keys.ToArray();
    }

    /// <summary>The score of a shape, or null for the four pulses (and any shape this build does not know).</summary>
    public static IReadOnlyList<EffectKey>? For(PulsePreset preset) => preset switch
    {
        PulsePreset.Shockwave => Shockwave,
        PulsePreset.Vortex => Vortex,
        PulsePreset.Strobe => Strobe,
        PulsePreset.Supernova => Supernova,
        PulsePreset.Freeze => Freeze,
        PulsePreset.Gust => Gust,
        PulsePreset.Rainbow => Rainbow,
        PulsePreset.Quake => Quake,
        _ => null,
    };

    /// <summary>The surge at <paramref name="u"/> (0–1) of a score: the two keys around it, blended with an ease.</summary>
    public static EffectSurge Evaluate(IReadOnlyList<EffectKey> keys, double u)
    {
        if (keys.Count == 0 || u < 0 || u >= 1) return EffectSurge.Zero;
        if (u <= keys[0].U) return keys[0].Surge;
        for (var i = 0; i + 1 < keys.Count; i++)
        {
            var a = keys[i];
            var b = keys[i + 1];
            if (u < b.U)
            {
                var span = b.U - a.U;
                var k = span <= 0 ? 1f : (float)((u - a.U) / span);
                var eased = k * k * (3 - 2 * k);
                return EffectSurge.Lerp(a.Surge, b.Surge, eased);
            }
        }
        return keys[^1].Surge;
    }
}

/// <summary>
/// The pure envelope: a pulse is a quick rise, then a settle back to nothing by the end; a scored
/// shape runs its keys. Every sink evaluates it from the same show time, so a span's halves and
/// NDI see the same surge on the same frame.
/// </summary>
public static class EffectEnvelope
{
    /// <summary>The rise takes this much of a pulse, never under 40 ms.</summary>
    public const double AttackShare = 0.08;

    public static EffectSurge At(in EffectImpulse impulse, double time)
    {
        if (impulse.IsNone) return EffectSurge.Zero;
        var u = (time - impulse.StartSeconds) / impulse.LengthSeconds;
        if (u < 0 || u >= 1) return EffectSurge.Zero;

        if (EffectScores.For(impulse.Preset) is { } score)
        {
            return EffectScores.Evaluate(score, u) with { Progress = (float)u, Phase = (float)(time - impulse.StartSeconds) };
        }

        var attack = Math.Max(AttackShare, Math.Min(0.5, 0.04 / impulse.LengthSeconds));
        double env;
        if (u < attack)
        {
            env = u / attack;
        }
        else
        {
            var x = (u - attack) / (1 - attack);   // 0 at the peak, 1 at the end
            env = (1 - x) * (1 - x);                // a fast drop that eases into the settle
        }
        var w = Weights(impulse.Preset);
        return new EffectSurge((float)(w.Burst * env), (float)(w.Speed * env), (float)(w.Glow * env), (float)(w.Zoom * env), (float)(w.Flash * env));
    }

    /// <summary>Which channels a shape drives, at their strongest.</summary>
    public static EffectSurge Weights(PulsePreset preset)
    {
        if (EffectScores.For(preset) is { } score)
        {
            var max = EffectSurge.Zero;
            foreach (var key in score) max = EffectSurge.Max(max, key.Surge);
            return max;
        }
        return preset switch
        {
            PulsePreset.Rush => new EffectSurge(0.3f, 1f, 0.4f, 0.8f, 0.2f),
            PulsePreset.Flash => new EffectSurge(0f, 0.2f, 1f, 0f, 1f),
            PulsePreset.Bloom => new EffectSurge(0.5f, 0.3f, 1f, 0.5f, 0.3f),
            _ => new EffectSurge(1f, 1f, 0.8f, 0.3f, 0.6f),   // Explosion, and any preset this build does not know
        };
    }

    /// <summary>True for the shapes that run a score rather than a rise and a settle.</summary>
    public static bool IsScored(PulsePreset preset) => EffectScores.For(preset) is not null;
}

/// <summary>
/// The pulse channel every renderer reads: the last pulse fired, stamped with the show clock.
/// One at a time — a new press restarts the surge from its own rise, which is what a re-fire
/// should look like — and read on every frame by every sink.
/// </summary>
public static class EffectImpulses
{
    private static readonly object Gate = new();
    private static EffectImpulse _current = EffectImpulse.None;

    public static void Fire(PulsePreset preset, double startSeconds, double lengthSeconds)
    {
        lock (Gate) _current = new EffectImpulse(preset, startSeconds, Math.Max(0.05, lengthSeconds));
    }

    public static EffectImpulse Current
    {
        get
        {
            lock (Gate) return _current;
        }
    }

    public static EffectSurge SurgeAt(double time) => EffectEnvelope.At(Current, time);

    public static void Clear()
    {
        lock (Gate) _current = EffectImpulse.None;
    }
}
