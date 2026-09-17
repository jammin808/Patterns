using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.Core.Menus;

/// <summary>
/// What choosing an entry does — the one fact the desk needs to run it and the wire needs to
/// describe it. The rule of the round: a menu changes the preview, the cue stack, or where the
/// desk is looking; the few entries that change the air are marked Live and worn in red, and
/// they exist only where the same surface already had a one-click live verb (a look's button,
/// a lower-third chip, a tile's own LOCK / ARM / OUT).
/// </summary>
public enum MenuScope
{
    /// <summary>A show action that lands in the preview and nowhere else: EDIT SAFE opens first; CUT or TAKE puts it up.</summary>
    Preview,
    /// <summary>A show action, or a tile's own switch, that changes the desk or the air now — always said so.</summary>
    Live,
    /// <summary>An edit of the cue stack — a cue's look, its transition, its overlays, its lower third, its auto-follow — never the picture.</summary>
    Stack,
    /// <summary>A page of the rail, with the item selected there.</summary>
    Go,
    /// <summary>A question to the assistant with the facts already in it.</summary>
    Ask,
    /// <summary>Round 73: MIDI learn — arms the desk for a wire line; the next control moved on a surface is bound to it (saved with the show). Nothing changes on air.</summary>
    Learn,
}

/// <summary>
/// The colour a group or an entry wears — the desk's own language: amber is the preview (what
/// the next TAKE puts up), red is the air, the rig's violet is a tile's own switches, the plan's
/// blue is the cue stack, mint is the assistant, the neutral grey is a page of the rail.
/// </summary>
public enum MenuTone
{
    Preview,
    Live,
    Tile,
    Stack,
    Go,
    Ask,
    Plain,
    Warn,
}

/// <summary>The tones as hex — the rail's hues and the Companion palette's, never a third table.</summary>
public static class MenuTones
{
    public static string Hex(MenuTone tone) => tone switch
    {
        MenuTone.Preview => CompanionPalette.Hex("amber"),   // #FFC24D — the preview, the edited, the waiting
        MenuTone.Live => "#FF5C7A",                          // the PROGRAM dot: the air
        MenuTone.Tile => "#B18CFF",                          // SETUP's violet: the rig's own switches
        MenuTone.Stack => "#6E9BFF",                         // PLAN's blue: the cue stack
        MenuTone.Go => "#C0CBDB",                            // the neutral: a page of the rail
        MenuTone.Ask => "#7CF5C8",                           // the assistant's mint
        MenuTone.Warn => CompanionPalette.Hex("orange"),     // #FF8A00 — off the look, late
        _ => "#E6EAF2",                                      // the tile title's white
    };

    /// <summary>The word a group's heading carries beside its colour, so a colour-blind operator reads the same thing.</summary>
    public static string Word(MenuScope scope) => scope switch
    {
        MenuScope.Preview => "PREVIEW",
        MenuScope.Live => "LIVE",
        MenuScope.Stack => "CUE STACK",
        MenuScope.Go => "GO TO",
        MenuScope.Ask => "ASK",
        MenuScope.Learn => "MIDI",
        _ => "",
    };
}

/// <summary>A page of the rail and the item to select on it (a screen id, a cue id, a look id, a design id, "layer1"…).</summary>
public sealed record MenuRoute(string Page, string Item = "");

/// <summary>
/// One line of a menu. What it does is one of five things (<see cref="Scope"/>); what it says is
/// three: the text, a detail line under it, and — when it cannot be chosen now — why
/// (<see cref="Because"/>, which also disables it). The wire line is the same thing said to
/// Companion or a tablet, and it is shown dimmed beside the text so an operator building a deck
/// reads the verb off the desk. An entry with children is a drawer: the choices open beside it.
/// </summary>
public sealed record MenuEntry(string Id, string Text, MenuScope Scope, MenuTone Tone)
{
    /// <summary>One line under the text: what choosing it does, in the desk's words.</summary>
    public string Detail { get; init; } = "";

    /// <summary>The wire line that does the same (a Companion key, OSC through its map); empty when the wire has no word for it.</summary>
    public string Wire { get; init; } = "";

    /// <summary>Why it cannot be chosen now — shown in place of the detail, and the entry is disabled. Empty means it can.</summary>
    public string Because { get; init; } = "";

    /// <summary>A tick: the current choice, or a switch that is on.</summary>
    public bool IsOn { get; init; }

    /// <summary>Preview and Live: the show action the desk runs (through the action layer, journaled like a key).</summary>
    public ShowAction? Action { get; init; }

    /// <summary>An edit the desk knows by key (a cue's, the preview's, a tile's own switch) when no show action says it.</summary>
    public string Edit { get; init; } = "";

    /// <summary>Go: the page and the item.</summary>
    public MenuRoute? Route { get; init; }

    /// <summary>Ask: the question, with the facts in it.</summary>
    public string Question { get; init; } = "";

    /// <summary>The drawer: the choices this entry opens (looks, presets, kinds, timings…).</summary>
    public IReadOnlyList<MenuEntry> Children { get; init; } = Array.Empty<MenuEntry>();

    public bool IsEnabled => Because.Length == 0;

    public bool HasChildren => Children.Count > 0;

    public bool HasWire => Wire.Length > 0;
}

/// <summary>A heading with its entries — PREVIEW, LIVE, CUE STACK, GO TO, ASK — and a note that says the rule of the group in one line.</summary>
public sealed record MenuGroup(string Heading, MenuTone Tone, IReadOnlyList<MenuEntry> Entries)
{
    /// <summary>The group's rule in one line: "the audience sees nothing until CUT or TAKE".</summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// A right-click menu, built for one thing on the desk — a tile, a cue, a look, a chip, a layer,
/// an overlay, the pane — from facts alone, so the same menu can be built for the wire and read
/// back by a test. The header names the thing and its state; the groups say what can be done to
/// it and, when something cannot, why.
/// </summary>
public sealed record DeskMenu(string Kind, string Subject, string Title, string Subtitle, MenuTone Tone, IReadOnlyList<MenuGroup> Groups)
{
    /// <summary>Every entry, depth first — the drawers' choices after the entries that open them.</summary>
    public IEnumerable<MenuEntry> Flatten()
    {
        foreach (var group in Groups)
        {
            foreach (var entry in group.Entries)
            {
                yield return entry;
                foreach (var child in entry.Children) yield return child;
            }
        }
    }

    public MenuEntry? Find(string id)
    {
        foreach (var entry in Flatten())
        {
            if (entry.Id == id) return entry;
        }
        return null;
    }

    public int EntryCount => Flatten().Count();
}
