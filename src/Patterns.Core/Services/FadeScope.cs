namespace Patterns.Core.Services;

/// <summary>Where a fade to black (or up) lands.</summary>
public enum FadeScopeKind
{
    /// <summary>The whole rig — the blackout with a fade, as before.</summary>
    All,
    /// <summary>The target the desk has focused (the wall tile clicked); the program tile focused means the rig.</summary>
    Focused,
    /// <summary>The wall tiles ticked.</summary>
    Ticked,
    /// <summary>The ticked tiles that are joined canvases (groups).</summary>
    Groups,
    /// <summary>A screen by its wall number (SCREEN 2) — the target it renders through when it joined a canvas.</summary>
    Screen,
    /// <summary>A joined canvas by its wall letter (GROUP A).</summary>
    Group,
    /// <summary>A content target by id (a canvas key such as a+b, or a screen id).</summary>
    Target,
}

/// <summary>
/// The scope of a fade as the desk, the wire, a cue and Companion write it: nothing (the rig),
/// FOCUSED, TICKED, GROUPS, SCREEN n, GROUP A, or a target id. Pure words in, a scope out; the
/// App resolves the scope against the rig it has. One parser, so the Show panel's picker, a cue
/// sheet's column and a Stream Deck key cannot disagree about what "group A" means.
/// </summary>
public readonly record struct FadeScope(FadeScopeKind Kind, string Arg)
{
    public static readonly FadeScope Everything = new(FadeScopeKind.All, "");
    public static readonly FadeScope Focused = new(FadeScopeKind.Focused, "");
    public static readonly FadeScope Ticked = new(FadeScopeKind.Ticked, "");
    public static readonly FadeScope Groups = new(FadeScopeKind.Groups, "");

    /// <summary>
    /// "" / ALL → the rig; FOCUSED; TICKED (SELECTED); GROUPS (CANVASES); SCREEN n; GROUP A
    /// (CANVAS A); ID x (a screen id or a canvas key — what a cue written by the desk carries);
    /// a bare canvas key (a+b) is an id too. Null for words that mean nothing (SCREEN with no
    /// number, three words, a bare word such as "slowly"), so a typo never fades the wrong thing.
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
            case "CANVASES":
                return parts.Length == 1 ? Groups : null;
            case "SCREEN":
                return parts.Length == 2 && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
                    ? new FadeScope(FadeScopeKind.Screen, n.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    : null;
            case "GROUP":
            case "CANVAS":
                return parts.Length == 2 && parts[1].Length == 1 && char.IsLetter(parts[1][0])
                    ? new FadeScope(FadeScopeKind.Group, parts[1].ToUpperInvariant())
                    : null;
            case "ID":
            case "TARGET":
                return parts.Length == 2 ? new FadeScope(FadeScopeKind.Target, parts[1]) : null;
            default:
                return parts.Length == 1 && parts[0].Contains('+') ? new FadeScope(FadeScopeKind.Target, parts[0]) : null;
        }
    }

    /// <summary>The words back, as the wire writes them ("" for the rig; a canvas key bare, any other id after ID).</summary>
    public string Words => Kind switch
    {
        FadeScopeKind.All => "",
        FadeScopeKind.Focused => "FOCUSED",
        FadeScopeKind.Ticked => "TICKED",
        FadeScopeKind.Groups => "GROUPS",
        FadeScopeKind.Screen => $"SCREEN {Arg}",
        FadeScopeKind.Group => $"GROUP {Arg}",
        _ => Arg.Contains('+') ? Arg : $"ID {Arg}",
    };

    /// <summary>For a sentence: "every screen", "the focused screen", "the ticked screens", "the ticked groups", "screen 2", "group A", the id.</summary>
    public string Label => Kind switch
    {
        FadeScopeKind.All => "every screen",
        FadeScopeKind.Focused => "the focused screen",
        FadeScopeKind.Ticked => "the ticked screens",
        FadeScopeKind.Groups => "the ticked groups",
        FadeScopeKind.Screen => $"screen {Arg}",
        FadeScopeKind.Group => $"group {Arg}",
        _ => Arg,
    };

    public bool IsEverything => Kind == FadeScopeKind.All;
}
