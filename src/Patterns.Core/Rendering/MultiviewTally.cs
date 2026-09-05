using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>One badge on a multiview tile: the word, its colour, and whether it is a filled chip or an outline.</summary>
public readonly record struct TileBadge(string Text, SKColor Color, bool Filled);

/// <summary>
/// What a multiview tile says about its target beyond the picture: whether it is in program
/// (live to the audience) or in the preview (what the next TAKE brings), whether it is held,
/// locked or on its own picture, and which output, screen or canvas it is. Pure, from the
/// snapshot alone, so a screen's own multiview, an NDI send of it and the remote /multiview
/// all read the same words.
/// </summary>
public static class MultiviewTally
{
    public static readonly SKColor Program = new(0xE0, 0x34, 0x2E);   // red: live to the audience
    public static readonly SKColor Preview = new(0x2E, 0xE6, 0x8A);   // green: the preview, and what the next TAKE changes
    public static readonly SKColor Held = new(0xFF, 0xC2, 0x4D);      // amber: held, locked, or on its own picture
    public static readonly SKColor Off = new(0x6A, 0x73, 0x82);       // grey: switched off, outputs closed
    public static readonly SKColor Frozen = new(0x35, 0xE0, 0xD0);    // cyan: the outputs hold their frame
    public static readonly SKColor Black = new(0x9A, 0xA3, 0xB3);     // a blackout

    /// <summary>EDIT SAFE is open: there is a preview the tiles can speak of.</summary>
    public static bool HasPreview(ShowSnapshot snap) => snap.PreviewSource?.Invoke() is not null;

    /// <summary>The next CUT / TAKE changes this target (everything is armed unless the wall un-armed it).</summary>
    public static bool IsArmed(ShowSnapshot snap, string targetId) => !snap.UnarmedTargets.Contains(targetId);

    /// <summary>
    /// Live to the audience: the outputs open, no blackout, and the target switched on — the
    /// program tile whenever the outputs are open (the audience's picture is the program).
    /// </summary>
    public static bool IsOnAir(ShowSnapshot snap, MultiviewTileConfig tile)
    {
        if (snap.State.Blackout || !snap.OutputsLive) return false;
        return tile.Source switch
        {
            MultiviewSource.Program => true,
            MultiviewSource.Screen => ContentTargets.IsTargetEnabled(snap.State, tile.ScreenId),
            _ => false,
        };
    }

    /// <summary>The badges for a tile, in the order they are drawn, left to right; empty for a live input or the clock.</summary>
    public static List<TileBadge> Badges(ShowSnapshot snap, MultiviewTileConfig tile)
    {
        var list = new List<TileBadge>(4);
        var state = snap.State;
        switch (tile.Source)
        {
            case MultiviewSource.Program:
                AddAirState(list, snap, on: IsOnAir(snap, tile), enabled: true);
                break;

            case MultiviewSource.Screen:
            {
                var id = tile.ScreenId;
                if (!ContentTargets.IsInRig(state, id))
                {
                    list.Add(new TileBadge(id.Length == 0 ? "NO TARGET" : "NOT IN RIG", Off, false));
                    break;
                }
                AddAirState(list, snap, on: IsOnAir(snap, tile), enabled: ContentTargets.IsTargetEnabled(state, id));
                var locked = ScreenRoles.IsLocked(state, id);
                var mirror = MirrorOf(state, id);
                if (mirror.Length > 0) list.Add(new TileBadge("REP " + Short(snap, mirror), Held, false));
                else if (locked) list.Add(new TileBadge("LOCKED", Held, true));
                else if (ContentTargets.UsesOwnPattern(state, id)) list.Add(new TileBadge("OWN", Held, false));
                // With EDIT SAFE open the tile says whether the next TAKE reaches it. A lock or a
                // repeater already says the take leaves it alone.
                if (HasPreview(snap) && mirror.Length == 0 && !locked)
                {
                    list.Add(IsArmed(snap, id) ? new TileBadge("NEXT", Preview, true) : new TileBadge("HELD", Held, false));
                }
                break;
            }

            case MultiviewSource.Preview:
                list.Add(HasPreview(snap) ? new TileBadge("PVW", Preview, true) : new TileBadge("NO PREVIEW", Off, false));
                break;
        }
        return list;
    }

    private static void AddAirState(List<TileBadge> list, ShowSnapshot snap, bool on, bool enabled)
    {
        if (on)
        {
            list.Add(new TileBadge("PGM", Program, true));
            if (snap.Frozen) list.Add(new TileBadge("FROZEN", Frozen, true));
            return;
        }
        if (!enabled)
        {
            list.Add(new TileBadge("OFF", Off, false));
            return;
        }
        if (!snap.OutputsLive)
        {
            list.Add(new TileBadge("OUTPUTS OFF", Off, false));
            return;
        }
        if (snap.State.Blackout) list.Add(new TileBadge("BLACK", Black, false));
    }

    /// <summary>The tile's caption: the target's name (or the tile's own label), and what kind of thing it is with its size.</summary>
    public static (string Name, string Kind) Caption(ShowSnapshot snap, MultiviewTileConfig tile)
        => (Name(snap, tile), Kind(snap, tile));

    /// <summary>The words on the tile: its own label when it has one, else the target's name, the feed's nickname, or the source.</summary>
    public static string Name(ShowSnapshot snap, MultiviewTileConfig tile)
    {
        if (tile.Label.Length > 0) return tile.Label;
        switch (tile.Source)
        {
            case MultiviewSource.Program:
                return "PROGRAM";
            case MultiviewSource.Screen:
                return tile.ScreenId.Length == 0 ? "—" : snap.Rig.LabelFor(snap.State, tile.ScreenId);
            case MultiviewSource.NdiFeed:
            {
                var name = tile.Input.Length > 0 ? tile.Input : MediaLocator.FindActiveNdiSource(snap.State);
                return name.Length > 0 ? snap.State.InputLabel("ndi:" + name, name) : "NDI FEED";
            }
            case MultiviewSource.Capture:
                return tile.Input.Length > 0 ? snap.State.InputLabel("cap:" + tile.Input, tile.Input) : "CAPTURE";
            case MultiviewSource.Pip:
                return "PIP";
            case MultiviewSource.Preview:
                return "PREVIEW";
            default:
                return "CLOCK";
        }
    }

    /// <summary>
    /// The second line under a tile: which output, screen or canvas it is and its pixels
    /// ("SCREEN 2 · 1920×1080", "CANVAS A · 3840×1080 · 2 SCREENS", "NDI SEND 4 · 1920×1080"),
    /// which targets the program is on, which the next TAKE changes, or the input's kind.
    /// </summary>
    public static string Kind(ShowSnapshot snap, MultiviewTileConfig tile)
    {
        var state = snap.State;
        switch (tile.Source)
        {
            case MultiviewSource.Program:
                return ProgramTargets(snap);
            case MultiviewSource.Preview:
                return PreviewTargets(snap);
            case MultiviewSource.Screen:
            {
                var id = tile.ScreenId;
                if (id.Length == 0) return "PICK A SCREEN OR CANVAS";
                if (!ContentTargets.IsInRig(state, id)) return "NOT IN THIS RIG";
                var size = snap.Rig.SizeOf(id);
                if (ContentTargets.IsCanvasKey(id))
                {
                    var letter = snap.Rig.LetterOf(id);
                    var members = snap.Rig.MembersOf(id).Count;
                    var head = letter.Length > 0 ? $"CANVAS {letter}" : "CANVAS";
                    return members > 0 ? $"{head} · {size.Width}×{size.Height} · {members} SCREENS" : $"{head} · {size.Width}×{size.Height}";
                }
                var p = state.Output.Placements.FirstOrDefault(x => x.ScreenId == id);
                var kind = p is { IsVirtual: true }
                    ? (p.VirtualKind == "NDI" ? "NDI SEND" : "STREAM")
                    : p is { Planned: true } ? "PLANNED SCREEN" : "SCREEN";
                var n = snap.Rig.NumberOf(id);
                var role = p is not null ? ScreenRoles.Badge(p.Role) : "";
                var words = $"{kind}{(n > 0 ? " " + n : "")} · {size.Width}×{size.Height}";
                return role.Length > 0 ? $"{words} · {role}" : words;
            }
            case MultiviewSource.NdiFeed:
                return "NDI FEED";
            case MultiviewSource.Capture:
                return "CAPTURE";
            case MultiviewSource.Pip:
                return "PIP INPUT";
            default:
                return "";
        }
    }

    /// <summary>
    /// The targets that follow the program — the PROGRAM tile's second line, "ON 1 · 2 · A": which
    /// screens and canvases the audience's picture is on. A target on its own picture or a
    /// repeater is not on the program.
    /// </summary>
    public static string ProgramTargets(ShowSnapshot snap)
    {
        var words = new List<string>();
        foreach (var t in snap.Rig.Targets)
        {
            if (ContentTargets.UsesOwnPattern(snap.State, t)) continue;
            if (MirrorOf(snap.State, t).Length > 0) continue;
            words.Add(Short(snap, t));
        }
        if (words.Count > 0) return "ON " + string.Join(" · ", words);
        return snap.Rig.Targets.Count == 0 ? "NO SCREENS IN THE RIG" : "NO SCREEN FOLLOWS IT";
    }

    /// <summary>
    /// The targets the next TAKE changes — the PREVIEW tile's second line, "NEXT TAKE → 1 · A":
    /// every armed target that is not locked; why there is none otherwise.
    /// </summary>
    public static string PreviewTargets(ShowSnapshot snap)
    {
        if (!HasPreview(snap)) return "EDIT SAFE OFF";
        var words = new List<string>();
        foreach (var t in snap.Rig.Targets)
        {
            if (!IsArmed(snap, t) || ScreenRoles.IsLocked(snap.State, t) || MirrorOf(snap.State, t).Length > 0) continue;
            words.Add(Short(snap, t));
        }
        if (words.Count > 0) return "NEXT TAKE → " + string.Join(" · ", words);
        return snap.Rig.Targets.Count == 0 ? "NO SCREENS IN THE RIG" : "NEXT TAKE → NOTHING (ALL HELD)";
    }

    /// <summary>A target's short name on the wall: a canvas's letter, a screen's number; the id when it has neither.</summary>
    public static string Short(ShowSnapshot snap, string targetId)
    {
        if (ContentTargets.IsCanvasKey(targetId))
        {
            var letter = snap.Rig.LetterOf(targetId);
            return letter.Length > 0 ? letter : "canvas";
        }
        var n = snap.Rig.NumberOf(targetId);
        return n > 0 ? n.ToString() : targetId;
    }

    private static string MirrorOf(ShowState state, string targetId)
    {
        if (ContentTargets.IsCanvasKey(targetId)) return "";
        foreach (var p in state.Output.Placements)
        {
            if (p.ScreenId == targetId) return p.MirrorOf;
        }
        return "";
    }
}
