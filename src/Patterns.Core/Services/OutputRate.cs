using Patterns.Core.Model;

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
        // Asked for the display's own rate: pace to it only when the clock beats at a faster rate
        // — a different family, not the same rate read a hair high.
        return clockHz > displayHz && !SameFamily(clockHz, displayHz) ? displayHz : 0;
    }

    /// <summary>
    /// Two rates within this share of each other are one family: 59.94 and 60, 29.97 and 30,
    /// 23.976 and 24, a measured 60.4 and a display's 60. Outside it they are different cadences —
    /// 50 and 60, 60 and 75, 60 and 120 — whatever the percentage.
    /// </summary>
    public const double FamilyTolerance = 0.025;

    /// <summary>Whether two rates are effectively the same cadence (round 64); false when either is unknown.</summary>
    public static bool SameFamily(double a, double b)
        => a > 0 && b > 0 && Math.Abs(a - b) <= Math.Max(a, b) * FamilyTolerance;

    /// <summary>
    /// Whether an output needs more beats than the platform's render clock supplies (round 64).
    /// The clock beats at one display's refresh for every window: an output on a faster display
    /// gets the clock's beats and no more, and no pacing can make frames the compositor never
    /// asks for — the honest word is LIMITED, on the chip, the Machine page, STATE and the brief.
    /// <paramref name="presentFps"/> is the rate the sink presents at (0: its display's own), the
    /// display's refresh as reported (0 unknown), the clock's measured beat (≤ 0 not measured).
    /// </summary>
    public static RateLimit ClockLimit(int presentFps, int displayHz, double clockHz)
        => ClockLimit(presentFps, displayHz, clockHz, null);

    /// <summary>
    /// Round 79: as above, and why — the refresh rates of every display the outputs are on tell a clock that
    /// follows a slower display (the clock beats in the family of one of them: make the display that needs the
    /// higher rate the one the clock follows) from a clock below every display (the desk cannot keep up:
    /// lighten it). Null or empty rates: the cause is not known, and the words say only that it is limited.
    /// </summary>
    public static RateLimit ClockLimit(int presentFps, int displayHz, double clockHz, IReadOnlyCollection<int>? displayRates)
    {
        var needed = presentFps > 0 ? (displayHz > 0 ? Math.Min(presentFps, displayHz) : presentFps) : displayHz;
        var limited = clockHz > 0 && needed > 0 && clockHz < needed && !SameFamily(clockHz, needed);
        if (!limited) return new RateLimit(false, needed, clockHz, RateLimitCause.None, 0);
        var known = displayRates?.Where(r => r > 0).Distinct().ToList();
        if (known is not { Count: > 0 }) return new RateLimit(true, needed, clockHz, RateLimitCause.Unknown, 0);
        var led = known.FirstOrDefault(r => SameFamily(clockHz, r));
        if (led > 0) return new RateLimit(true, needed, clockHz, RateLimitCause.FollowsSlowerDisplay, led);
        return known.All(r => clockHz < r)
            ? new RateLimit(true, needed, clockHz, RateLimitCause.BelowEveryDisplay, known.Min())
            : new RateLimit(true, needed, clockHz, RateLimitCause.Unknown, 0);
    }

    // ---- round 80: the master rate follows the displays ----------------------------------------

    /// <summary>
    /// Round 80: the master rate the show runs at. The operator's setting stands, except that a show
    /// following its displays never asks a display for more frames than it refreshes: with the
    /// follow on and a display slower than the set rate behind an enabled screen, the rate in force
    /// is that display's — the slowest known — and the words name it. Unlimited (0) stays
    /// unlimited: every output already presents at its own display. A display in the family of the
    /// set rate (59 under 60) is not slower. A display not yet seen (0 Hz) does not count, and with
    /// none known the set rate stands. With the follow off, a slower display is still named, so the
    /// Super Check and the Screens page can say what the outputs on it are dropping.
    /// </summary>
    public static MasterRate Master(int setFps, bool follow, IEnumerable<(string Label, int Hz)> displays)
    {
        var set = Math.Max(0, setFps);
        var label = "";
        var hz = 0;
        if (set > 0)
        {
            foreach (var (l, h) in displays)
            {
                if (h <= 0 || h >= set || SameFamily(h, set)) continue;   // not slower than the show asks
                if (hz == 0 || h < hz)
                {
                    hz = h;
                    label = l;
                }
            }
        }
        return new MasterRate(set, follow, follow && hz > 0 ? hz : set, label, hz);
    }

    /// <summary>
    /// The rule over the show: the displays behind the screens that are here — enabled, not planned,
    /// not lost — as last seen (<see cref="ScreenPlacement.DisplayHz"/>), labelled by the operator's
    /// name or the screen id. The desk labels them better with the display's own name
    /// (Rig.MasterRate); the rate is the same.
    /// </summary>
    public static MasterRate Master(OutputConfig output)
        => Master(output.MasterFps, output.FollowDisplays,
            Leading(output).Select(p => (p.CustomLabel.Length > 0 ? p.CustomLabel : p.ScreenId, p.DisplayHz)));

    /// <summary>The screens whose displays may lead the master rate: enabled, not planned, not lost (a display that is gone leads nothing until it is back).</summary>
    public static IEnumerable<ScreenPlacement> Leading(OutputConfig output)
        => output.Placements.Where(p => p.Enabled && !p.Planned && p.LostAtUtc is null);

    /// <summary>The master rate in force — what every reader of the master rate reads (round 80); 0 unlimited.</summary>
    public static int EffectiveMaster(OutputConfig output) => Master(output).Effective;
}

/// <summary>
/// Round 80: the master rate as the show runs it — the operator's setting, whether it follows the
/// displays, the rate in force, and the slowest display below the setting (its label and Hz; ""
/// and 0 when none is).
/// </summary>
public sealed record MasterRate(int Set, bool Follows, int Effective, string SlowestLabel = "", int SlowestHz = 0)
{
    /// <summary>The rate in force is a display's, not the setting's.</summary>
    public bool Followed => Effective != Set;

    /// <summary>A display refreshes slower than the show asks and the follow is off: the outputs on it drop frames.</summary>
    public bool Overasks => !Followed && SlowestHz > 0;

    /// <summary>"60 fps", "50 fps — following Lobby (50 Hz); set 60", "60 fps — Lobby refreshes at 50 Hz and is not followed", "every display's own refresh".</summary>
    public string Words => Set <= 0 ? "every display's own refresh"
        : Followed ? $"{Effective} fps — following {SlowestLabel} ({SlowestHz} Hz); set {Set}"
        : Overasks ? $"{Set} fps — {SlowestLabel} refreshes at {SlowestHz} Hz and is not followed"
        : $"{Set} fps";
}

/// <summary>Round 79: why the render clock limits an output — which of the two very different fixes the words should name.</summary>
public enum RateLimitCause
{
    /// <summary>Not limited.</summary>
    None,
    /// <summary>Limited, and the displays' rates were not given — the cause is not known.</summary>
    Unknown,
    /// <summary>The clock beats at a slower display's rate: Windows drives the compositor from that display — make the one that needs the higher rate lead.</summary>
    FollowsSlowerDisplay,
    /// <summary>The clock is below every display's rate: the desk is not keeping up — a page, a decoder, the tiles; lighten it.</summary>
    BelowEveryDisplay,
}

/// <summary>An output against the render clock: whether the clock limits it, the rate it needs, the clock's measured beat (≤ 0: not measured — unknown, never assumed), and since round 79 why (with the display rate the cause names, 0 none).</summary>
public readonly record struct RateLimit(bool Limited, int NeededHz, double ClockHz, RateLimitCause Cause = RateLimitCause.Unknown, int CauseHz = 0)
{
    /// <summary>The words for a sink: "Output 2: 60 Hz needed, render clock 50.0 Hz — LIMITED BY RENDER CLOCK — the clock follows a 50 Hz display: make the display that needs 60 Hz the one it follows".</summary>
    public string Words(string sink)
        => Limited ? $"{sink}: {NeededHz} Hz needed, render clock {ClockHz:0.0} Hz — LIMITED BY RENDER CLOCK{CauseWords}" : "";

    /// <summary>Round 79: the cause and its fix, as a suffix; "" when not limited or not known.</summary>
    public string CauseWords => Cause switch
    {
        RateLimitCause.FollowsSlowerDisplay => $" — the clock follows a {CauseHz} Hz display: make the display that needs {NeededHz} Hz the one it follows",
        RateLimitCause.BelowEveryDisplay => $" — the clock is below every display's rate (the slowest is {CauseHz} Hz): the desk is not keeping up — lower the desk monitors' rate, close a page, lighten the show",
        _ => "",
    };
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
