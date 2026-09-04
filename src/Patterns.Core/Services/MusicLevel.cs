namespace Patterns.Core.Services;

/// <summary>
/// The one rule for how loud background music sits right now — the file track and break music both
/// read it, so "the music ducks under a VOG and fades under a stinger" is one rule with one test,
/// not one implementation per music source. A VOG ducks (a step to a level); a stinger fades (a
/// ramp to silence and back). The ramp is pure and time-based: computed from a start instant and
/// the clock, never accumulated per tick, so a missed poll, a long GC or a save cannot stall it or
/// push it past the ends. <paramref name="duckPct"/> is <see cref="Model.StingerConfig.DuckPct"/>:
/// a target <em>level</em>, not a depth (0 % = silent).
/// </summary>
public static class MusicLevel
{
    /// <summary>0–1: what the music is multiplied by.</summary>
    public static double Factor(bool ducked, double duckPct, double fade01 = 1.0)
        => Math.Clamp(fade01, 0, 1) * (ducked ? Duck(duckPct) : 1.0);

    /// <summary>A VOG's duck: the music holds at this share of its own volume, with no ramp.</summary>
    public static double Duck(double duckPct) => Math.Clamp(duckPct, 0, 100) / 100.0;

    /// <summary>0 at the start, 1 at the end, clamped. A zero-length fade is instant, never a divide by zero.</summary>
    public static double Progress(DateTime startUtc, DateTime nowUtc, int ms)
    {
        if (ms <= 0) return 1;
        var elapsed = (nowUtc - startUtc).TotalMilliseconds;
        return elapsed <= 0 ? 0 : elapsed >= ms ? 1 : elapsed / ms;
    }

    /// <summary>
    /// Linear ramp between two real gains, so reversing mid-fade never jumps. Both ends are real
    /// gains: the caller re-anchors <paramref name="from"/> on the level actually on air at every
    /// reversal, which is the difference between a clean transition and an audible click.
    /// </summary>
    public static double Gain(double from, double to, double progress)
        => from + (to - from) * Math.Clamp(progress, 0, 1);

    /// <summary>The whole percent a Spotify Connect device should be set to (0–100).</summary>
    public static int DevicePercent(double levelPct, bool ducked, double duckPct, double fade01 = 1.0)
        => DevicePercent(levelPct, Factor(ducked, duckPct, fade01));

    /// <summary>The same, at a gain already worked out (the duck and the fade together).</summary>
    public static int DevicePercent(double levelPct, double gain)
        => (int)Math.Round(Math.Clamp(levelPct, 0, 100) * Math.Clamp(gain, 0, 1),
                           MidpointRounding.AwayFromZero);
}
