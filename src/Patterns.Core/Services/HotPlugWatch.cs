using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>A display as the hot-plug watch sees it: its id (which embeds its geometry), its name, its size, where it sits, its rate when known.</summary>
public sealed record DisplayFact(string Id, string Label, int Width, int Height, int X, int Y, int Hz = 0)
{
    /// <summary>What survives a re-index: the name and the size.</summary>
    public string Key => HotPlugWatch.KeyOf(Label, Width, Height);

    public string Origin => HotPlugWatch.OriginOf(X, Y);

    public string Words => $"{(Label.Length > 0 ? Label : "Display")} {Width}×{Height}{(Hz > 0 ? $" @ {Hz} Hz" : "")}";
}

/// <summary>What one topology change asks of the rig, in the order to apply it.</summary>
public sealed record HotPlugPlan(
    IReadOnlyList<(ScreenPlacement Placement, DisplayFact Display)> Renamed,
    IReadOnlyList<ScreenPlacement> Lost,
    IReadOnlyList<(ScreenPlacement Placement, DisplayFact Display)> Returned,
    IReadOnlyList<DisplayFact> Strangers)
{
    public bool IsEmpty => Renamed.Count == 0 && Lost.Count == 0 && Returned.Count == 0 && Strangers.Count == 0;
}

/// <summary>Whether a display that is not the one a screen lost can stand in for it.</summary>
public sealed record SubstituteVerdict(bool Fits, (int Width, int Height, int Hz)? Mode, string Words)
{
    /// <summary>It can be a substitute: as it is, or once the mode is forced.</summary>
    public bool Offered => Fits || Mode is not null;
}

/// <summary>
/// The pure part of output hot-plug. Display ids embed the index and the geometry, so a display
/// unplugged on the left re-identifies every display to its right: the watch tells a screen that
/// merely re-indexed (renamed) from one that is gone (lost), a lost screen's own display coming
/// back (returned) from a display the rig has never met (a stranger), and whether a stranger can
/// stand in for a lost screen. What a screen remembers of its display — its name, its size, where
/// it sat, its rate — lives on the placement, so the memory survives a restart and travels with
/// the show file.
/// </summary>
public static class HotPlugWatch
{
    public const string LostIdPrefix = ScreenPlacement.PlannedIdPrefix + "lost-";

    public static string KeyOf(string label, int width, int height) => $"{label.Trim()}|{width}x{height}";

    public static string OriginOf(int x, int y) => $"{x},{y}";

    /// <summary>A planned id no display can ever carry: the screen waits under it for its display.</summary>
    public static string LostId() => LostIdPrefix + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The name half of a key.</summary>
    public static string LabelOf(string key)
    {
        var i = key.LastIndexOf('|');
        return i < 0 ? key : key[..i];
    }

    /// <summary>The size half of a key, or null when it has none.</summary>
    public static (int Width, int Height)? SizeOf(string key)
    {
        var i = key.LastIndexOf('|');
        var size = i < 0 ? key : key[(i + 1)..];
        var x = size.IndexOf('x');
        if (x <= 0) return null;
        return int.TryParse(size[..x], out var w) && int.TryParse(size[(x + 1)..], out var h) && w > 0 && h > 0 ? (w, h) : null;
    }

    /// <summary>A screen that lost its display and waits for it: planned, and remembering a display.</summary>
    public static bool IsLost(ScreenPlacement p) => p.IsPlannedDisplay && p.LostAtUtc is not null;

    public static IReadOnlyList<ScreenPlacement> LostScreens(ShowState state) => state.Output.Placements.Where(IsLost).ToList();

    /// <summary>
    /// The decisions for one topology change. Renames first, so a display that re-indexed is never
    /// read as gone; then what is gone; then lost screens whose display is back; then displays the
    /// rig has never met. A display keeps its identity by name and size, and — after a mode change,
    /// or a like-for-like swap — by name and place.
    /// </summary>
    public static HotPlugPlan Decide(IReadOnlyList<ScreenPlacement> placements, IReadOnlyList<DisplayFact> now)
    {
        var byId = now.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var unsettled = new List<ScreenPlacement>();
        foreach (var p in placements.Where(p => !p.Planned && !p.IsVirtual))
        {
            // Still there: the same id, and — ids embed only the index and the geometry — the same display
            // behind it, when the screen remembers one. Another display under a coinciding id is not it.
            if (byId.TryGetValue(p.ScreenId, out var d) && (p.DisplayKey.Length == 0 || string.Equals(d.Key, p.DisplayKey, StringComparison.Ordinal))) claimed.Add(d.Id);
            else unsettled.Add(p);
        }
        var renamed = new List<(ScreenPlacement, DisplayFact)>();
        var lost = new List<ScreenPlacement>();
        var returned = new List<(ScreenPlacement, DisplayFact)>();

        // Displays that re-indexed: the placement's id is gone (or another display sits under it), a display with its name and size (or its name and place) is free.
        foreach (var p in unsettled)
        {
            var match = Match(p, now.Where(d => !claimed.Contains(d.Id)).ToList(), byPlaceToo: true);
            if (match is null)
            {
                lost.Add(p);
                continue;
            }
            claimed.Add(match.Id);
            renamed.Add((p, match));
        }

        // Lost screens whose display is back: the same name and size.
        foreach (var p in placements.Where(IsLost))
        {
            var match = Match(p, now.Where(d => !claimed.Contains(d.Id)).ToList(), byPlaceToo: false);
            if (match is null) continue;
            claimed.Add(match.Id);
            returned.Add((p, match));
        }

        // A display nobody claimed and no screen names is new to the rig; one that sits under a screen's
        // old id but is not that screen's display (the coinciding id above) is new to it as well.
        var named = new HashSet<string>(placements.Where(p => !lost.Contains(p)).Select(p => p.ScreenId), StringComparer.Ordinal);
        var strangers = now.Where(d => !claimed.Contains(d.Id) && !named.Contains(d.Id)).ToList();
        return new HotPlugPlan(renamed, lost, returned, strangers);
    }

    private static DisplayFact? Match(ScreenPlacement p, IReadOnlyList<DisplayFact> free, bool byPlaceToo)
    {
        if (p.DisplayKey.Length == 0) return null;
        var sameKey = free.Where(d => string.Equals(d.Key, p.DisplayKey, StringComparison.Ordinal)).ToList();
        if (sameKey.Count > 0) return Nearest(sameKey, p.DisplayOrigin);
        if (!byPlaceToo) return null;
        var label = LabelOf(p.DisplayKey);
        if (label.Length == 0 || p.DisplayOrigin.Length == 0) return null;
        return free.FirstOrDefault(d => string.Equals(d.Label.Trim(), label, StringComparison.Ordinal) && d.Origin == p.DisplayOrigin);
    }

    /// <summary>Among displays with the right name and size, the one nearest to where the screen's display sat.</summary>
    private static DisplayFact Nearest(IReadOnlyList<DisplayFact> candidates, string origin)
    {
        if (candidates.Count == 1 || origin.Length == 0) return candidates[0];
        var parts = origin.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var ox) || !int.TryParse(parts[1], out var oy)) return candidates[0];
        return candidates.OrderBy(d => Math.Abs(d.X - ox) + Math.Abs(d.Y - oy)).First();
    }

    /// <summary>
    /// Can this display stand in for the lost one? As it is when the size matches and the rates
    /// do (or one is unknown); once its mode is forced when it offers the lost size at the lost
    /// rate; not at all when it does neither — another aspect, or a size it cannot reach — and the
    /// words say which.
    /// </summary>
    public static SubstituteVerdict Assess(ScreenPlacement lost, DisplayFact stranger, IReadOnlyList<(int Width, int Height, int Hz)> offered)
    {
        var w = lost.PlannedWidth;
        var h = lost.PlannedHeight;
        var hz = lost.DisplayHz;
        var sameSize = stranger.Width == w && stranger.Height == h;
        var rateOk = hz <= 0 || stranger.Hz <= 0 || stranger.Hz == hz;
        var want = $"{w}×{h}{(hz > 0 ? $" @ {hz} Hz" : "")}";
        if (sameSize && rateOk)
        {
            return new SubstituteVerdict(true, null, $"{stranger.Words} matches {LostName(lost)} ({want}) — USE AS SUBSTITUTE puts everything programmed for it on this display.");
        }
        var mode = offered.FirstOrDefault(m => m.Width == w && m.Height == h && (hz <= 0 || m.Hz == hz));
        if (mode != default)
        {
            return new SubstituteVerdict(false, mode, $"{stranger.Words} can be forced to {want} — USE AS SUBSTITUTE switches its mode, then puts everything programmed for {LostName(lost)} on it.");
        }
        var sameAspect = Math.Abs((double)stranger.Width / Math.Max(1, stranger.Height) - (double)w / Math.Max(1, h)) < 0.01;
        var why = !sameSize && !sameAspect ? $"another aspect ({stranger.Width}×{stranger.Height} against {w}×{h})"
            : !sameSize ? $"another resolution ({stranger.Width}×{stranger.Height}) and no {w}×{h} mode offered"
            : $"another rate ({stranger.Hz} Hz against {hz} Hz) and no {want} mode offered";
        return new SubstituteVerdict(false, null, $"{stranger.Words} cannot stand in for {LostName(lost)}: {why}. It can be ITS OWN SCREEN.");
    }

    public static string LostName(ScreenPlacement p)
        => p.CustomLabel.Length > 0 ? p.CustomLabel : p.DisplayKey.Length > 0 ? LabelOf(p.DisplayKey) : "the screen";

    /// <summary>"'Stage left' (DELL U2415 1920×1080) unplugged at 19:41:58 — plug it back in and it comes back on."</summary>
    public static string LostWords(ScreenPlacement p, DateTime? nowUtc = null)
    {
        var name = LostName(p);
        var was = p.DisplayKey.Length > 0 ? $" ({LabelOf(p.DisplayKey)} {p.PlannedWidth}×{p.PlannedHeight}{(p.DisplayHz > 0 ? $" @ {p.DisplayHz} Hz" : "")})" : "";
        var since = p.LostAtUtc is { } at ? $" unplugged at {at.ToLocalTime():HH:mm:ss}" : " missing";
        return $"'{name}'{was}{since} — plug it back in and it comes back on; another display connected can stand in for it.";
    }

    /// <summary>The health line's clause: "SCREEN MISSING: 'Stage left' unplugged at 19:41:58", or "" when every screen has its display.</summary>
    public static string HealthWords(IReadOnlyList<ScreenPlacement> lost)
    {
        if (lost.Count == 0) return "";
        var names = string.Join(", ", lost.Select(p => $"'{LostName(p)}'" + (p.LostAtUtc is { } at ? $" unplugged at {at.ToLocalTime():HH:mm:ss}" : "")));
        return (lost.Count == 1 ? "SCREEN MISSING: " : $"{lost.Count} SCREENS MISSING: ") + names;
    }
}
