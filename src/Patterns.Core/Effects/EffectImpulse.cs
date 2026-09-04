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

/// <summary>One fired effect pulse: its shape, when it started on the show clock, and how long it runs.</summary>
public readonly record struct EffectImpulse(PulsePreset Preset, double StartSeconds, double LengthSeconds)
{
    public static readonly EffectImpulse None = new(PulsePreset.Explosion, double.NegativeInfinity, 0);

    public bool IsNone => double.IsNegativeInfinity(StartSeconds) || LengthSeconds <= 0;

    public double EndSeconds => StartSeconds + LengthSeconds;
}

/// <summary>
/// The surge a pulse puts through the picture at one instant, each 0–1: <paramref name="Burst"/>
/// (fresh births at the emitter), <paramref name="Speed"/> (everything moves faster),
/// <paramref name="Glow"/> (bigger, brighter, additive), <paramref name="Zoom"/> (the fractal
/// dives in), <paramref name="Flash"/> (a white flash over the whole picture).
/// </summary>
public readonly record struct EffectSurge(float Burst, float Speed, float Glow, float Zoom, float Flash)
{
    public static readonly EffectSurge Zero = default;

    public bool IsZero => Burst <= 0 && Speed <= 0 && Glow <= 0 && Zoom <= 0 && Flash <= 0;

    public float Peak => Math.Max(Math.Max(Burst, Speed), Math.Max(Math.Max(Glow, Zoom), Flash));
}

/// <summary>
/// The pure envelope: a quick rise, then a settle back to nothing by the end of the pulse.
/// Every sink evaluates it from the same show time, so a span's halves and NDI see the same
/// surge on the same frame. Each preset weights the channels.
/// </summary>
public static class EffectEnvelope
{
    /// <summary>The rise takes this much of the pulse, never under 40 ms.</summary>
    public const double AttackShare = 0.08;

    public static EffectSurge At(in EffectImpulse impulse, double time)
    {
        if (impulse.IsNone) return EffectSurge.Zero;
        var u = (time - impulse.StartSeconds) / impulse.LengthSeconds;
        if (u < 0 || u >= 1) return EffectSurge.Zero;
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

    /// <summary>Which channels a preset drives, at the peak.</summary>
    public static EffectSurge Weights(PulsePreset preset) => preset switch
    {
        PulsePreset.Rush => new EffectSurge(0.3f, 1f, 0.4f, 0.8f, 0.2f),
        PulsePreset.Flash => new EffectSurge(0f, 0.2f, 1f, 0f, 1f),
        PulsePreset.Bloom => new EffectSurge(0.5f, 0.3f, 1f, 0.5f, 0.3f),
        _ => new EffectSurge(1f, 1f, 0.8f, 0.3f, 0.6f),   // Explosion, and any preset this build does not know
    };
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
