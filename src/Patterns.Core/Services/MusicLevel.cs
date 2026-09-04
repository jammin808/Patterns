namespace Patterns.Core.Services;

/// <summary>
/// How loud background music sits right now — one rule for the file track and for break music.
/// <paramref name="duckPct"/> is <see cref="Model.StingerConfig.DuckPct"/>: a target <em>level</em>,
/// not a depth (0 % = silent).
/// </summary>
public static class MusicLevel
{
    /// <summary>0–1: what the music is multiplied by.</summary>
    public static double Factor(bool ducked, double duckPct, double fade01 = 1.0)
        => Math.Clamp(fade01, 0, 1) * (ducked ? Math.Clamp(duckPct, 0, 100) / 100.0 : 1.0);

    /// <summary>The whole percent a Spotify Connect device should be set to (0–100).</summary>
    public static int DevicePercent(double levelPct, bool ducked, double duckPct, double fade01 = 1.0)
        => (int)Math.Round(Math.Clamp(levelPct, 0, 100) * Factor(ducked, duckPct, fade01),
                           MidpointRounding.AwayFromZero);
}
