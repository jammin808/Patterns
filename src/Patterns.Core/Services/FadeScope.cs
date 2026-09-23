using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>Where a fade to black (or up), a CUT or a TAKE lands.</summary>
public enum FadeScopeKind
{
    /// <summary>The whole rig — the blackout with a fade, as before; every armed screen for a take.</summary>
    All,
    /// <summary>The target the desk has focused (the wall tile clicked, or picked as the editing target); the program tile focused means the rig.</summary>
    Focused,
    /// <summary>The wall tiles ticked.</summary>
    Ticked,
    /// <summary>Round 81: every screen in the groups of the ticked tiles — the groups being what a screen is for (main, confidence, info), never a joined canvas.</summary>
    Groups,
    /// <summary>A screen by its wall number (SCREEN 2) — the target it renders through when it joined a canvas.</summary>
    Screen,
    /// <summary>Round 81: a group by its kind — GROUP MAIN, GROUP CONFIDENCE, GROUP INFO — every screen of that kind.</summary>
    Group,
    /// <summary>A content target by id (a canvas key such as a+b, or a screen id).</summary>
    Target,
    /// <summary>Round 81: a joined canvas by its wall letter (CANVAS A) — one target, not a group.</summary>
    Canvas,
    /// <summary>Round 81: the ticked tiles that are joined canvases (CANVASES).</summary>
    Canvases,
}

/// <summary>
/// The scope of a fade, a CUT or a TAKE as the desk, the wire, a cue and Companion write it: nothing
/// (the rig), FOCUSED, TICKED, GROUPS, GROUP MAIN / CONFIDENCE / INFO, SCREEN n, CANVAS A, CANVASES,
/// or a target id. Pure words in, a scope out; the App resolves the scope against the rig it has.
/// One parser, so the Show panel's picker, a cue sheet's column and a Stream Deck key cannot
/// disagree about what "group" means — since round 81 it means what a screen is for, and a joined
/// canvas is a canvas.
/// </summary>
public readonly record struct FadeScope(FadeScopeKind Kind, string Arg)
{
    public static readonly FadeScope Everything = new(FadeScopeKind.All, "");
    public static readonly FadeScope Focused = new(FadeScopeKind.Focused, "");
    public static readonly FadeScope Ticked = new(FadeScopeKind.Ticked, "");
    public static readonly FadeScope Groups = new(FadeScopeKind.Groups, "");
    public static readonly FadeScope Canvases = new(FadeScopeKind.Canvases, "");

    /// <summary>A group by its kind: GROUP MAIN / CONFIDENCE / INFO — the role's wire word as the argument.</summary>
    public static FadeScope GroupOf(ScreenRole role) => new(FadeScopeKind.Group, ScreenRoles.Word(role));

    /// <summary>
    /// "" / ALL → the rig; FOCUSED; TICKED (SELECTED); GROUPS (the ticked tiles' groups by kind);
    /// GROUP MAIN / CONFIDENCE (CONF) / INFO (a group by kind — a repeater is no group a take can
    /// reach, so GROUP REPEATER is nothing); SCREEN n; CANVAS A (a joined canvas by letter — GROUP A
    /// is read the same way for the cue sheets written before round 81, and written back as CANVAS);
    /// CANVASES (the ticked canvases); ID x (a screen id or a canvas key — what a cue written by the
    /// desk carries); a bare canvas key (a+b) is an id too. Null for words that mean nothing (SCREEN
    /// with no number, three words, a bare word such as "slowly"), so a typo never fades the wrong thing.
    /// </summary>
    public static FadeScope? Parse(string? words)
    {
        var w = (words ?? "").Trim();
        if (w.Length == 0) return Everything;
        var parts = w.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var head = parts[0].ToUpperInvariant();
        switch (head)
        {
            case "ALL":
            case "EVERYTHING":
            case "RIG":
                return parts.Length == 1 ? Everything : null;
            case "FOCUSED":
            case "FOCUS":
            case "CURRENT":
                return parts.Length == 1 ? Focused : null;
            case "TICKED":
            case "SELECTED":
            case "TICKS":
                return parts.Length == 1 ? Ticked : null;
            case "GROUPS":
                return parts.Length == 1 ? Groups : null;
            case "CANVASES":
                return parts.Length == 1 ? Canvases : null;
            case "SCREEN":
                return parts.Length == 2 && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
                    ? new FadeScope(FadeScopeKind.Screen, n.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    : null;
            case "GROUP":
                if (parts.Length != 2) return null;
                if (ScreenRoles.Parse(parts[1]) is { } role) return ScreenRoles.IsTakeKind(ScreenRoles.Word(role)) ? GroupOf(role) : null;
                return Letter(parts[1]);
            case "CANVAS":
                return parts.Length == 2 ? Letter(parts[1]) : null;
            case "ID":
            case "TARGET":
                return parts.Length == 2 ? new FadeScope(FadeScopeKind.Target, parts[1]) : null;
            default:
                return parts.Length == 1 && parts[0].Contains('+') ? new FadeScope(FadeScopeKind.Target, parts[0]) : null;
        }
    }

    private static FadeScope? Letter(string word)
        => word.Length == 1 && char.IsLetter(word[0]) ? new FadeScope(FadeScopeKind.Canvas, word.ToUpperInvariant()) : null;

    /// <summary>The words back, as the wire writes them ("" for the rig; a canvas key bare, any other id after ID; a group by its kind's word).</summary>
    public string Words => Kind switch
    {
        FadeScopeKind.All => "",
        FadeScopeKind.Focused => "FOCUSED",
        FadeScopeKind.Ticked => "TICKED",
        FadeScopeKind.Groups => "GROUPS",
        FadeScopeKind.Canvases => "CANVASES",
        FadeScopeKind.Screen => $"SCREEN {Arg}",
        FadeScopeKind.Group => $"GROUP {Arg.ToUpperInvariant()}",
        FadeScopeKind.Canvas => $"CANVAS {Arg}",
        _ => Arg.Contains('+') ? Arg : $"ID {Arg}",
    };

    /// <summary>For a sentence: "every screen", "the focused screen", "the ticked screens", "the ticked groups", "the confidence screens", "screen 2", "canvas A", "the ticked canvases", the id.</summary>
    public string Label => Kind switch
    {
        FadeScopeKind.All => "every screen",
        FadeScopeKind.Focused => "the focused screen",
        FadeScopeKind.Ticked => "the ticked screens",
        FadeScopeKind.Groups => "the ticked groups",
        FadeScopeKind.Canvases => "the ticked canvases",
        FadeScopeKind.Screen => $"screen {Arg}",
        FadeScopeKind.Group => $"the {Arg} screens",
        FadeScopeKind.Canvas => $"canvas {Arg}",
        _ => Arg,
    };

    public bool IsEverything => Kind == FadeScopeKind.All;
}
