using System.Globalization;

namespace Patterns.Core.LowerThirds;

/// <summary>
/// Round 73: a hold as the wire, a cue and the page write it — a number of seconds (decimals
/// allowed, "8", "7.5"), or STAY / 0 for "until hidden". One parser, so LT n FOR s, LT n HOLD s,
/// a cue's value and the validator agree on what the words mean.
/// </summary>
public static class LowerThirdHolds
{
    public const int StayMs = -1;

    /// <summary>The words as a run's hold: milliseconds, or <see cref="StayMs"/> (until hidden); false for words that are neither.</summary>
    public static bool TryParse(string? words, out int runHoldMs)
    {
        runHoldMs = 0;
        var w = (words ?? "").Trim();
        if (w.Length == 0) return false;
        if (w.Equals("STAY", StringComparison.OrdinalIgnoreCase) || w.Equals("HOLD", StringComparison.OrdinalIgnoreCase) || w.Equals("MANUAL", StringComparison.OrdinalIgnoreCase))
        {
            runHoldMs = StayMs;
            return true;
        }
        if (w.EndsWith("s", StringComparison.OrdinalIgnoreCase)) w = w[..^1].Trim();
        if (!double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || double.IsNaN(seconds) || seconds < 0 || seconds > 600) return false;
        runHoldMs = seconds <= 0 ? StayMs : (int)Math.Round(seconds * 1000);
        return true;
    }

    /// <summary>"8 s", "7.5 s" or "until hidden".</summary>
    public static string Words(int holdMs)
        => holdMs > 0 ? (holdMs % 1000 == 0 ? $"{holdMs / 1000} s" : $"{holdMs / 1000.0:0.#} s") : "until hidden";
}
