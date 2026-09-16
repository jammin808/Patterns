using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The transition — or the video sting — the next TAKE alone arrives by (round 67.6). One shot: it is
/// set from a right-click on any TAKE, from TAKE NEXT on the wire or a Companion key, spent by the next
/// TAKE (the wall's or a tile's), and the show's own transition never moves. A CUT is a cut and leaves
/// it for the TAKE it was given to.
/// </summary>
public sealed record NextTransition(bool Cut, int FadeMs, TransitionKind? Kind, ReactiveScene? Scene, TransitionDirection? Direction, string StingId, string StingName)
{
    public bool IsSting => StingId.Length > 0;

    /// <summary>The operator's words: "WIPE LEFT 800 ms", "CUT", "STING Whoosh", "DIP".</summary>
    public string Words
    {
        get
        {
            if (IsSting) return $"STING {StingName}";
            if (Cut) return "CUT";
            var parts = new List<string>();
            if (Kind is { } k) parts.Add(k == TransitionKind.BrandStinger ? "BRAND STINGER" : k.ToString().ToUpperInvariant());
            if (Scene is { } s) parts.Add(s.ToString().ToUpperInvariant());
            if (Direction is { } d) parts.Add(d.ToString().ToUpperInvariant());
            if (FadeMs >= 0) parts.Add($"{FadeMs} ms");
            return parts.Count == 0 ? "the show's own" : string.Join(" ", parts);
        }
    }

    /// <summary>The words after TAKE NEXT that set this again: "wipe left 800", "cut", "STING Whoosh".</summary>
    public string WireWords
    {
        get
        {
            if (IsSting) return $"STING {StingName}";
            if (Cut) return "cut";
            var parts = new List<string>();
            if (Kind is { } k) parts.Add(k switch { TransitionKind.BrandStinger => "brand", TransitionKind.Reactive when Scene is not null => "", _ => k.ToString().ToLowerInvariant() });
            if (Scene is { } s) parts.Add(s.ToString().ToLowerInvariant());
            if (Direction is { } d) parts.Add(d.ToString().ToLowerInvariant());
            if (FadeMs >= 0) parts.Add(FadeMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return string.Join(" ", parts.Where(p => p.Length > 0));
        }
    }

    public string Wire => "TAKE NEXT " + WireWords;

    /// <summary>CLEAR, DEFAULT, NONE, OFF or nothing: the show's own transition again.</summary>
    public static bool IsClear(string? words) => (words ?? "").Trim().ToUpperInvariant() is "" or "CLEAR" or "DEFAULT" or "NONE" or "OFF";

    /// <summary>
    /// The operator's words to a next transition: CLEAR (and its kin) → null, the show's own; STING or
    /// STINGER and a name or number → the sting the lookup resolves (a VOG or an unknown name is a
    /// problem, never a guess); else the transition words a recall takes — "wipe left 800", "dip",
    /// "cut", "reactive vortex 1200", a bare "800" for the show's kind at that rate. False with the
    /// problem for words that mean nothing.
    /// </summary>
    public static bool TryParse(string? words, Func<string, (string Id, string Name)?> sting, out NextTransition? next, out string? problem)
    {
        next = null;
        problem = null;
        var w = (words ?? "").Trim();
        if (IsClear(w)) return true;
        var parts = w.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts[0].Equals("STING", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("STINGER", StringComparison.OrdinalIgnoreCase))
        {
            var name = string.Join(' ', parts.Skip(1));
            if (name.Length == 0)
            {
                problem = "STING needs the sting's name or number — TAKE NEXT STING Whoosh.";
                return false;
            }
            var found = sting(name);
            if (found is null)
            {
                problem = $"No video sting called '{name}' in the library (a VOG is not a sting).";
                return false;
            }
            next = new NextTransition(false, -1, null, null, null, found.Value.Id, found.Value.Name);
            return true;
        }
        if (!ActionSpec.TryParseTransition(w, out var cut, out var ms, out var kind, out var scene, out var way))
        {
            problem = $"'{w}' is not a transition — dissolve, dip, wipe, push, brand, reactive, cut, a fade in ms, STING <name>, or CLEAR.";
            return false;
        }
        next = new NextTransition(cut, ms, kind, scene, way, "", "");
        return true;
    }
}
