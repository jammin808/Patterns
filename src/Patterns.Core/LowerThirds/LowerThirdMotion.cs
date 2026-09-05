using Patterns.Core.Services;

namespace Patterns.Core.LowerThirds;

/// <summary>Where an element is at one instant, as offsets from its resting place.</summary>
public readonly record struct ElementPose(float X, float Y, float Opacity, float Scale, float Rotate, float Reveal)
{
    public static readonly ElementPose Identity = new(0, 0, 1, 1, 0, 1);

    public static ElementPose Of(LowerThirdKeyframe k)
        => new((float)k.X, (float)k.Y, (float)k.Opacity, (float)k.Scale, (float)k.Rotate, (float)k.Reveal);

    public static ElementPose Lerp(in ElementPose a, in ElementPose b, float k)
        => new(
            a.X + (b.X - a.X) * k,
            a.Y + (b.Y - a.Y) * k,
            a.Opacity + (b.Opacity - a.Opacity) * k,
            a.Scale + (b.Scale - a.Scale) * k,
            a.Rotate + (b.Rotate - a.Rotate) * k,
            a.Reveal + (b.Reveal - a.Reveal) * k);
}

/// <summary>The easing curves, 0 → 0 and 1 → 1; Back and Elastic overshoot on the way.</summary>
public static class Easing
{
    public static double Apply(EaseKind ease, double u)
    {
        u = Math.Clamp(u, 0, 1);
        switch (ease)
        {
            case EaseKind.EaseIn:
                return u * u;
            case EaseKind.EaseOut:
                return 1 - (1 - u) * (1 - u);
            case EaseKind.EaseInOut:
                return u * u * (3 - 2 * u);
            case EaseKind.Back:
            {
                const double c1 = 1.70158;
                const double c3 = c1 + 1;
                var v = u - 1;
                return 1 + c3 * v * v * v + c1 * v * v;
            }
            case EaseKind.Bounce:
            {
                const double n1 = 7.5625;
                const double d1 = 2.75;
                if (u < 1 / d1) return n1 * u * u;
                if (u < 2 / d1)
                {
                    u -= 1.5 / d1;
                    return n1 * u * u + 0.75;
                }
                if (u < 2.5 / d1)
                {
                    u -= 2.25 / d1;
                    return n1 * u * u + 0.9375;
                }
                u -= 2.625 / d1;
                return n1 * u * u + 0.984375;
            }
            case EaseKind.Elastic:
            {
                if (u <= 0) return 0;
                if (u >= 1) return 1;
                const double c4 = 2 * Math.PI / 3;
                return Math.Pow(2, -10 * u) * Math.Sin((u * 10 - 0.75) * c4) + 1;
            }
            default:
                return u;
        }
    }
}

/// <summary>Reads a key list at an instant: the two keys around it, blended with the later key's ease.</summary>
public static class LowerThirdKeyframes
{
    public static ElementPose Evaluate(IReadOnlyList<LowerThirdKeyframe> keys, double u)
    {
        if (keys.Count == 0) return ElementPose.Identity;
        LowerThirdKeyframe? prev = null;
        LowerThirdKeyframe? next = null;
        foreach (var k in keys)
        {
            if (k.U <= u)
            {
                if (prev is null || k.U >= prev.U) prev = k;
            }
            else if (next is null || k.U < next.U)
            {
                next = k;
            }
        }
        if (prev is null) return ElementPose.Of(next!);
        if (next is null) return ElementPose.Of(prev);
        var span = next.U - prev.U;
        var t = span <= 1e-9 ? 1 : (u - prev.U) / span;
        return ElementPose.Lerp(ElementPose.Of(prev), ElementPose.Of(next), (float)Easing.Apply(next.Ease, t));
    }
}

/// <summary>Where a design is in its life at one instant.</summary>
public readonly record struct LowerThirdTiming(LowerThirdPhase Phase, double U)
{
    public bool Visible => Phase is LowerThirdPhase.In or LowerThirdPhase.Hold or LowerThirdPhase.Out;
}

/// <summary>The design's clock: in, hold, out — by its own hold or by an explicit hide, whichever comes first.</summary>
public static class LowerThirdClock
{
    public static LowerThirdTiming Evaluate(LowerThirdDesign d, double shownAt, double? hiddenAt, double time)
    {
        var t = time - shownAt;
        if (t < 0) return new LowerThirdTiming(LowerThirdPhase.Before, 0);
        var inS = d.InMs / 1000.0;
        var outS = d.OutMs / 1000.0;

        double? outStart = null;
        if (hiddenAt is { } h && h >= shownAt) outStart = h;
        if (d.HoldMs > 0)
        {
            var auto = shownAt + inS + d.HoldMs / 1000.0;
            if (outStart is null || auto < outStart) outStart = auto;
        }
        if (outStart is { } os && time >= os)
        {
            var to = time - os;
            if (outS <= 0 || to >= outS) return new LowerThirdTiming(LowerThirdPhase.Gone, 1);
            return new LowerThirdTiming(LowerThirdPhase.Out, to / outS);
        }
        if (inS > 0 && t < inS) return new LowerThirdTiming(LowerThirdPhase.In, t / inS);
        return new LowerThirdTiming(LowerThirdPhase.Hold, 1);
    }

    /// <summary>The instants the config records, on the master clock.</summary>
    public static (double ShownAt, double? HiddenAt)? Instants(LowerThirdsConfig cfg)
    {
        if (cfg.ShownAtUtc is not { } shown) return null;
        double? hidden = cfg.HiddenAtUtc is { } h ? ShowClock.SecondsAt(h) : null;
        return (ShowClock.SecondsAt(shown), hidden);
    }

    /// <summary>Whether any of the active design is on screen at this instant (the cadence hook and the tally).</summary>
    public static bool IsLive(LowerThirdsConfig cfg, DateTime utcNow)
    {
        var design = cfg.Active;
        if (design is null || Instants(cfg) is not { } at) return false;
        return Evaluate(design, at.ShownAt, at.HiddenAt, ShowClock.SecondsAt(utcNow)).Visible;
    }

    /// <summary>The pose of one element at this timing, its stagger applied.</summary>
    public static ElementPose PoseOf(LowerThirdElement e, LowerThirdDesign d, in LowerThirdTiming timing)
    {
        switch (timing.Phase)
        {
            case LowerThirdPhase.In:
            {
                var u = timing.U;
                if (e.DelayMs > 0 && d.InMs > 0)
                {
                    var delay = Math.Min(e.DelayMs, d.InMs - 1) / (double)d.InMs;
                    u = Math.Clamp((u - delay) / (1 - delay), 0, 1);
                }
                return e.In.Count == 0
                    ? ElementPose.Identity with { Opacity = (float)Easing.Apply(EaseKind.EaseInOut, u) }
                    : LowerThirdKeyframes.Evaluate(e.In, u);
            }
            case LowerThirdPhase.Hold:
                return e.In.Count == 0 ? ElementPose.Identity : LowerThirdKeyframes.Evaluate(e.In, 1);
            case LowerThirdPhase.Out:
                return e.Out.Count == 0
                    ? ElementPose.Identity with { Opacity = (float)(1 - Easing.Apply(EaseKind.EaseInOut, timing.U)) }
                    : LowerThirdKeyframes.Evaluate(e.Out, timing.U);
            default:
                return ElementPose.Identity with { Opacity = 0 };
        }
    }
}

/// <summary>The ready-made ways in and out, written as keys an operator can then edit.</summary>
public static class LowerThirdMotions
{
    /// <summary>The distance a motion travels when the caller does not say: across the design, or a short lift.</summary>
    public static double DefaultDistance(LowerThirdMotion motion, LowerThirdDesign d) => motion switch
    {
        LowerThirdMotion.SlideLeft or LowerThirdMotion.SlideRight => d.Width + 240,
        LowerThirdMotion.SlideUp or LowerThirdMotion.SlideDown => 90,
        LowerThirdMotion.Drop or LowerThirdMotion.Rise => 220,
        _ => 0,
    };

    /// <summary>Replaces an element's in (or out) keys with a motion's.</summary>
    public static void Apply(LowerThirdElement e, LowerThirdMotion motion, bool isIn, double distance)
    {
        var keys = isIn ? e.In : e.Out;
        keys.Clear();
        foreach (var k in Keys(motion, isIn, distance)) keys.Add(k);
    }

    public static void Apply(LowerThirdElement e, LowerThirdDesign d, LowerThirdMotion motionIn, LowerThirdMotion motionOut)
    {
        Apply(e, motionIn, true, DefaultDistance(motionIn, d));
        Apply(e, motionOut, false, DefaultDistance(motionOut, d));
    }

    /// <summary>The keys of a motion: the far key at the phase's start (in) or end (out), the rest key at the other end.</summary>
    public static List<LowerThirdKeyframe> Keys(LowerThirdMotion motion, bool isIn, double distance)
    {
        var list = new List<LowerThirdKeyframe>(2);
        if (motion == LowerThirdMotion.None) return list;
        var away = new LowerThirdKeyframe();
        var ease = EaseKind.EaseInOut;
        switch (motion)
        {
            case LowerThirdMotion.Fade:
                away.Opacity = 0;
                break;
            case LowerThirdMotion.SlideLeft:
                away.X = -distance;
                away.Opacity = 0;
                ease = EaseKind.EaseOut;
                break;
            case LowerThirdMotion.SlideRight:
                away.X = distance;
                away.Opacity = 0;
                ease = EaseKind.EaseOut;
                break;
            case LowerThirdMotion.SlideUp:
                away.Y = distance;
                away.Opacity = 0;
                ease = EaseKind.EaseOut;
                break;
            case LowerThirdMotion.SlideDown:
                away.Y = -distance;
                away.Opacity = 0;
                ease = EaseKind.EaseOut;
                break;
            case LowerThirdMotion.Pop:
                away.Scale = 0.6;
                away.Opacity = 0;
                ease = EaseKind.Back;
                break;
            case LowerThirdMotion.Wipe:
                away.Reveal = 0;
                ease = EaseKind.EaseInOut;
                break;
            case LowerThirdMotion.Drop:
                away.Y = -distance;
                away.Opacity = 0;
                ease = EaseKind.Bounce;
                break;
            case LowerThirdMotion.Rise:
                away.Y = distance;
                away.Opacity = 0;
                ease = EaseKind.EaseOut;
                break;
            case LowerThirdMotion.Spin:
                away.Rotate = -90;
                away.Scale = 0.5;
                away.Opacity = 0;
                ease = EaseKind.Back;
                break;
        }
        var rest = new LowerThirdKeyframe();
        if (isIn)
        {
            // Travelling to rest: the rest key carries the ease.
            away.U = 0;
            away.Ease = EaseKind.Linear;
            rest.U = 1;
            rest.Ease = ease;
            list.Add(away);
            list.Add(rest);
        }
        else
        {
            // Travelling away: an ease-in leaves cleanly; a wipe runs straight.
            rest.U = 0;
            rest.Ease = EaseKind.Linear;
            away.U = 1;
            away.Ease = motion == LowerThirdMotion.Wipe ? EaseKind.Linear : EaseKind.EaseIn;
            list.Add(rest);
            list.Add(away);
        }
        return list;
    }
}
