namespace Patterns.Core.Services;

/// <summary>
/// The rate an output presents at, and the words for a size's shape — the tech info chip's truth
/// (round 63). The platform's render clock beats at one display's refresh for every window; a
/// screen whose own display refreshes slower was drawn at the clock's rate and showed fewer, so
/// the chip read "60 fps" on a 50 Hz display and the operator read it as Patterns pushing 60.
/// The output paces to its own display when the display is known and slower than the clock, or
/// than what was asked; a display as fast as the clock, or faster, is left unpaced as before —
/// pacing at the clock's own rate would drop a frame on every beat that lands a hair early.
/// </summary>
public static class OutputRate
{
    /// <summary>
    /// The rate to present at: 0 is unpaced (every beat of the clock). <paramref name="wanted"/>
    /// is the screen's own rate or the master's (0 = the display's); <paramref name="displayHz"/>
    /// the display's refresh as Windows reports it (0 unknown); <paramref name="clockHz"/> the
    /// measured beat of the platform's render clock (0 not yet measured).
    /// </summary>
    public static int Present(int wanted, int displayHz, double clockHz)
    {
        if (displayHz <= 0) return Math.Max(0, wanted);
        if (wanted > 0) return Math.Min(wanted, displayHz);
        // Asked for the display's own rate: pace to it only when the clock clearly beats faster.
        return clockHz > displayHz * 1.1 ? displayHz : 0;
    }
}

/// <summary>A pixel size's shape in the words a video engineer uses: 16:9, 16:10, 4:3, 21:9, 1:1, 9:16 — or the reduced pair, or "1.78:1" when neither reads.</summary>
public static class AspectWords
{
    private static readonly (double Ratio, string Words)[] Known =
    {
        (16.0 / 9, "16:9"), (16.0 / 10, "16:10"), (4.0 / 3, "4:3"), (21.0 / 9, "21:9"), (64.0 / 27, "21:9"),
        (1.0, "1:1"), (9.0 / 16, "9:16"), (10.0 / 16, "10:16"), (3.0 / 4, "3:4"), (32.0 / 9, "32:9"), (5.0 / 4, "5:4"), (3.0 / 2, "3:2"),
    };

    public static string Of(int width, int height)
    {
        if (width <= 0 || height <= 0) return "";
        var ratio = (double)width / height;
        foreach (var (known, words) in Known)
        {
            if (Math.Abs(ratio - known) / known < 0.01) return words;
        }
        var g = Gcd(width, height);
        var w = width / g;
        var h = height / g;
        return w <= 32 && h <= 32 ? $"{w}:{h}" : $"{ratio:0.00}:1";
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
