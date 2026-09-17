namespace Patterns.Core.Services;

/// <summary>A rail of the desk — SHOW, PLAN, BUILD, SETUP, ADMIN — as the wire and a deck see it.</summary>
public sealed record DeskRail(string Id, string Label, string Hue, string Hint);

/// <summary>A page of the desk: its header, the rail it sits on, its neon hue.</summary>
public sealed record DeskPage(string Header, string Rail, string Hue);

/// <summary>
/// Round 74: where the desk is — the page and its rail, whether the Run surface is up, the
/// settings column beside the page (what it shows, for which selection), the page BACK would
/// return to, and the selection's words — for STATE's nav row, the wire's NAV, the Eye's desk
/// node and a deck that follows the desk.
/// </summary>
public sealed record NavFacts(string Page, string Rail, string Hue, bool Run)
{
    public bool SettingsOpen { get; init; }
    public string SettingsKey { get; init; } = "";
    public string SettingsTitle { get; init; } = "";
    public string SettingsIdentity { get; init; } = "";
    /// <summary>The page BACK returns to; "" when there is none.</summary>
    public string Back { get; init; } = "";
    /// <summary>The thing selected on the page, in words ("cue 03.020 Walk-in", "screen 2 · Left", "design Neon"); "" with none.</summary>
    public string Selection { get; init; } = "";

    /// <summary>"On the Cues page · settings column: SELECTED CUE · 03.020 Walk-in · back: Looks".</summary>
    public string Words
    {
        get
        {
            var parts = new List<string> { Run ? "On the Run surface" : $"On the {Page} page" };
            if (Selection.Length > 0) parts.Add(Selection);
            if (SettingsOpen) parts.Add($"settings column: {SettingsTitle}");
            if (Back.Length > 0) parts.Add($"back: {Back}");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// The desk's one table of rails and pages, pure (round 74). The shell builds its tabs from
/// it, the wire answers NAV from it, and a deck's navigator lays its rails and pages out from
/// the same rows — so a page added here is on the rail, on the wire and on the deck at once.
/// The order is the order the shell shows them; a node shows only the pages its kind is for
/// (<see cref="NodeKinds.Pages"/>).
/// </summary>
public static class DeskPages
{
    public static readonly IReadOnlyList<DeskRail> Rails = new[]
    {
        new DeskRail("Show", "SHOW", "#2EE68A", "Show time: the panel beside the switcher, and the Run surface for the caller"),
        new DeskRail("Plan", "PLAN", "#6E9BFF", "Before the show: the cue stack, looks, and the install's clock — programmes, adverts, announcements"),
        new DeskRail("Build", "BUILD", "#3EC1F3", "Making content: patterns, media, overlays, countdown, particles, fractals, branding, layers, the library, the assistant"),
        new DeskRail("Setup", "SETUP", "#B18CFF", "At the rig: screens, the monitor walls, audio, NDI, streaming, remote control"),
        new DeskRail("Admin", "ADMIN", "#B8E356", "The machine: performance, GPU, the watchdog — and Help"),
    };

    public static readonly IReadOnlyList<DeskPage> All = new[]
    {
        new DeskPage("Panel", "Show", "#2EE68A"),
        new DeskPage("Run", "Show", "#2EE68A"),
        new DeskPage("Eye", "Show", "#F2D26B"),
        new DeskPage("Cues", "Plan", "#6E9BFF"),
        new DeskPage("Looks", "Plan", "#6E9BFF"),
        new DeskPage("Install", "Plan", "#9AB4FF"),
        new DeskPage("Pattern", "Build", "#3EC1F3"),
        new DeskPage("Media", "Build", "#FF6EC7"),
        new DeskPage("Overlays", "Build", "#FFC24D"),
        new DeskPage("Lower thirds", "Build", "#FFC24D"),
        new DeskPage("Countdown", "Build", "#FFC24D"),
        new DeskPage("Particles", "Build", "#FFC24D"),
        new DeskPage("Fractals", "Build", "#FFC24D"),
        new DeskPage("Reactive", "Build", "#7CF5C8"),
        new DeskPage("Branding", "Build", "#FFC24D"),
        new DeskPage("Layers", "Build", "#E39BFF"),
        new DeskPage("Library", "Build", "#C0CBDB"),
        new DeskPage("Assistant", "Build", "#7CF5C8"),
        new DeskPage("Screens", "Setup", "#B18CFF"),
        new DeskPage("Multiview", "Setup", "#5FD0FF"),
        new DeskPage("Audio", "Setup", "#FF9E58"),
        new DeskPage("NDI", "Setup", "#8FA5FF"),
        new DeskPage("Stream", "Setup", "#FF5C7A"),
        new DeskPage("Remote", "Setup", "#35E0D0"),
        new DeskPage("Interactive", "Setup", "#7CF5C8"),
        new DeskPage("Arcade", "Setup", "#E0FF5F"),
        new DeskPage("Nodes", "Setup", "#5FD0FF"),
        new DeskPage("Machine", "Admin", "#B8E356"),
        new DeskPage("Help", "Admin", "#C0CBDB"),
    };

    /// <summary>The page with this header, case-blind, or null.</summary>
    public static DeskPage? Find(string? header)
    {
        var h = (header ?? "").Trim();
        foreach (var p in All)
        {
            if (string.Equals(p.Header, h, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    /// <summary>The rail with this label or id ("PLAN", "plan"), or null.</summary>
    public static DeskRail? FindRail(string? word)
    {
        var w = (word ?? "").Trim();
        foreach (var r in Rails)
        {
            if (string.Equals(r.Label, w, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Id, w, StringComparison.OrdinalIgnoreCase)) return r;
        }
        return null;
    }

    /// <summary>The pages of a rail, in order.</summary>
    public static IReadOnlyList<DeskPage> Of(string railId) => All.Where(p => string.Equals(p.Rail, railId, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The words after NAV taken apart: the longest page header or rail label at the front,
    /// case-blind, and the rest as the item ("Lower thirds Neon" → the Lower thirds page, "Neon";
    /// "plan" → the PLAN rail; "cues 03.020" → the Cues page, "03.020"). Null for a stranger.
    /// </summary>
    public static (string Page, string Item)? Resolve(string? words)
    {
        var text = (words ?? "").Trim();
        if (text.Length == 0) return null;
        DeskPage? best = null;
        foreach (var p in All)
        {
            if (!StartsWithWord(text, p.Header)) continue;
            if (best is null || p.Header.Length > best.Header.Length) best = p;
        }
        if (best is not null) return (best.Header, text.Length > best.Header.Length ? text[best.Header.Length..].Trim() : "");
        foreach (var r in Rails)
        {
            if (StartsWithWord(text, r.Label)) return (r.Label, text.Length > r.Label.Length ? text[r.Label.Length..].Trim() : "");
        }
        return null;
    }

    private static bool StartsWithWord(string text, string word)
        => text.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (text.Length == word.Length || text[word.Length] == ' ');
}
