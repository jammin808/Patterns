using System.Text;
using System.Text.Json;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The twin's <c>WINDOWS</c> word (standby → main): the node id a standby speaks to Stagewatch under, and the held forms
/// (<c>stagewatch://&lt;node&gt;/&lt;window&gt;</c>) of the windows a Stagewatch granted that standby itself whose address reached it.
/// Ids only — never a base, a path or a pin: a grant belongs to the node it was granted to, and its address rides no twin line.
/// </summary>
public sealed record TwinWindowsHeld(string Node, IReadOnlyList<string> Held)
{
    /// <summary>The most held forms a word carries: a standby keeps no more grants than any desk (<see cref="StagewatchGrants.Max"/>).</summary>
    public const int Max = StagewatchGrants.Max;

    /// <summary>A standby's word from its own grants: the page grants that open on it, each once, in order — never one without its address.</summary>
    public static TwinWindowsHeld Of(string? node, IEnumerable<StagewatchGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var held = grants.Where(g => g.Opens).Select(g => g.Held).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(Max).ToList();
        return new TwinWindowsHeld(PeerLink.IsId(node) ? node! : "", held);
    }

    /// <summary>Whether the standby holds the window under this held form.</summary>
    public bool Holds(string held) => Held.Contains(held, StringComparer.Ordinal);

    /// <summary>The word's payload: <c>{"node":"a1b2c3d4","held":["stagewatch://swdesk01/lobby"]}</c>.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("node", Node);
            w.WriteStartArray("held");
            foreach (var h in Held) w.WriteStringValue(h);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Reads the word fail closed: an object whose <c>node</c> is a Show Bus id or "" (a standby whose id is not known) and whose
    /// <c>held</c> lists at most <see cref="Max"/> granted windows' held forms, none twice. Anything else — a path, an address, a
    /// form that is no grant's, too many — is null: the main then reads the standby as one that has not said.
    /// </summary>
    public static TwinWindowsHeld? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var node = root.TryGetProperty("node", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            if (node.Length > 0 && !PeerLink.IsId(node)) return null;
            if (!root.TryGetProperty("held", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > Max) return null;
            var held = new List<string>(list.GetArrayLength());
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) return null;
                var form = e.GetString() ?? "";
                if (!WindowAddresses.TryGrant(form, out var sw, out var window)) return null;
                var canonical = WindowAddresses.Grant(sw, window);
                if (!string.Equals(form, canonical, StringComparison.Ordinal) || held.Contains(canonical, StringComparer.Ordinal)) return null;
                held.Add(canonical);
            }
            return new TwinWindowsHeld(node, held);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// What the twin says of the granted windows a takeover would open: the row's value, its words, and whether a window on air shows or
/// would show black — "" value when there is nothing to say (no granted window on air, no standby on the link).
/// </summary>
public sealed record TwinWindowsReport(string Value, string Words, bool Lacking)
{
    public static TwinWindowsReport None { get; } = new("", "", false);
}

/// <summary>
/// A granted window the air shows: its held form, its window id, the name it is known by here, where it is on air, and whether a grant
/// of this desk's own opens it (its address reached this desk).
/// </summary>
public sealed record TwinWindowOnAir(string Held, string Window, string Name, IReadOnlyList<string> Places, bool Granted);

/// <summary>
/// The standby's side of its own report: its Show Bus id (the grant Stagewatch is asked for names it), its main's name, whether it took
/// the show (the windows are on its own air now, not a takeover away), whether its Stagewatch link takes an ask (NODES ASK WINDOW), and
/// how ready a granted window's page is on this desk — the browser's word, read once the window is on this desk's own air.
/// </summary>
public sealed record TwinStandbySide(string SelfId, string Main, bool TookOver, bool CanAsk, Func<StagewatchGrant, WindowReadiness> Readiness);

/// <summary>
/// Show Bus 1.3 §12 on the twin: which granted windows a takeover would show black. A grant belongs to the node it was
/// granted to, and a standby is a node of its own, so a window on the main's air opens on the standby only through a grant of its
/// own; the main reads the standby's <c>WINDOWS</c> word against its air, a standby its own grants against the air it mirrors, and
/// each says by name what Stagewatch is asked to grant. Pure: the twin hands in the air, the grants and the word.
/// </summary>
public static class TwinWindows
{
    /// <summary>The Super Check's row and the words' item.</summary>
    public const string Item = "Twin windows";

    /// <summary>The held forms of the granted windows a state puts on air — the programme's own picture and each screen's own — each once, in order.</summary>
    public static IReadOnlyList<string> HeldIn(ShowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var found = new List<string>();
        Add(state.Pattern);
        foreach (var a in state.Independent)
        {
            if (ContentTargets.UsesOwnPattern(state, a.ScreenId)) Add(a.Pattern);
        }
        return found;

        void Add(PatternConfig? p)
        {
            if (p is null) return;
            One(p.Kind == PatternKind.Media && p.Media.Source == MediaSource.Web ? p.Media.WebUrl : "");
            One(p.Layer1.WebUrl);
            One(p.Layer2.WebUrl);
        }

        void One(string? url)
        {
            if (string.IsNullOrEmpty(url)) return;
            var held = WindowAddresses.Held(url);
            if (WindowAddresses.IsGrant(held) && !found.Contains(held, StringComparer.Ordinal)) found.Add(held);
        }
    }

    /// <summary>
    /// The granted windows the air shows, each with where (<see cref="StagewatchGrants.Places"/>) and the name a grant here knows it
    /// by — the window's id when this desk holds no grant of it.
    /// </summary>
    public static IReadOnlyList<TwinWindowOnAir> OnAir(ShowState air, IEnumerable<StagewatchGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(air);
        ArgumentNullException.ThrowIfNull(grants);
        var known = grants.ToList();
        var list = new List<TwinWindowOnAir>();
        foreach (var held in HeldIn(air))
        {
            if (!WindowAddresses.TryGrant(held, out _, out var window)) continue;
            var places = StagewatchGrants.Places(air, held);
            if (places.Count == 0) continue;
            var mine = known.Where(g => string.Equals(g.Held, held, StringComparison.Ordinal)).ToList();
            var name = mine.FirstOrDefault()?.Name;
            list.Add(new TwinWindowOnAir(held, window, string.IsNullOrEmpty(name) ? window : name, places, mine.Any(g => g.Opens)));
        }
        return list;
    }

    /// <summary>The windows on air a standby does not hold: every one while it has not said (<paramref name="held"/> null).</summary>
    public static IReadOnlyList<TwinWindowOnAir> Lacking(IReadOnlyList<TwinWindowOnAir> onAir, TwinWindowsHeld? held)
    {
        ArgumentNullException.ThrowIfNull(onAir);
        return held is null ? onAir : onAir.Where(w => !held.Holds(w.Held)).ToList();
    }

    /// <summary>The windows on air a standby lacks of its own: those no grant of its own opens.</summary>
    public static IReadOnlyList<TwinWindowOnAir> Lacking(IReadOnlyList<TwinWindowOnAir> onAir, IEnumerable<StagewatchGrant> own)
        => Lacking(onAir, TwinWindowsHeld.Of("", own));

    /// <summary>Where a window is on air, in words: "the programme", "Screen 2", "the programme and Screen 2".</summary>
    public static string PlaceWords(IReadOnlyList<string> places)
    {
        ArgumentNullException.ThrowIfNull(places);
        var words = places.Select(p => p == "PGM" ? "the programme" : "Screen " + p).ToList();
        return words.Count <= 1 ? string.Concat(words) : string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1];
    }

    /// <summary>
    /// The main's words for one standby: each window on air it lacks, where it is and the grant Stagewatch is asked for — named by
    /// the standby's node id when it said one, and said black here too when no grant of this desk's own opens it; or, while it has
    /// not said (an older standby, a link not yet told), that the windows on air may show black after a takeover. "" when it holds
    /// every one, or nothing granted is on air.
    /// </summary>
    public static string OnMain(string standby, TwinWindowsHeld? held, IReadOnlyList<TwinWindowOnAir> onAir)
    {
        ArgumentNullException.ThrowIfNull(onAir);
        var lacking = Lacking(onAir, held);
        if (lacking.Count == 0) return "";
        if (held is null)
            return $"{standby} has not said which Stagewatch windows it holds — {string.Join(", ", lacking.Select(w => w.Name))} on air here may show black after a takeover";
        var node = held.Node.Length > 0 ? held.Node : "<its Show Bus id, on its Nodes page>";
        return string.Join("; ", lacking.Select(w => w.Granted
            ? $"Stagewatch {w.Name} is on air ({PlaceWords(w.Places)}) and granted to this desk, not to its standby {standby} — a takeover shows black there: Stagewatch → window grant {w.Name} {node}"
            : $"Stagewatch {w.Name} is on air ({PlaceWords(w.Places)}) and black here too — no grant opens it on this desk or on its standby {standby}: Stagewatch → window grant {w.Name} {node}"));
    }

    /// <summary>
    /// A standby's own words: each window on the air it mirrors that no grant of its own opens — black here after a takeover, or black
    /// here now once it took the show — the grant Stagewatch is asked for, by this desk's node id, and this desk's own ask while its
    /// Stagewatch link takes one. "" when it holds every one.
    /// </summary>
    public static string OnStandby(IReadOnlyList<TwinWindowOnAir> lacking, TwinStandbySide side)
    {
        ArgumentNullException.ThrowIfNull(lacking);
        ArgumentNullException.ThrowIfNull(side);
        if (lacking.Count == 0) return "";
        var at = string.IsNullOrEmpty(side.Main) ? "the main" : side.Main;
        return string.Join("; ", lacking.Select(w => (side.TookOver
            ? $"Stagewatch {w.Name} is on air here ({PlaceWords(w.Places)}) and black — no grant of this desk's own opens it: Stagewatch → window grant {w.Name} {side.SelfId}"
            : $"Stagewatch {w.Name} is on air at {at} ({PlaceWords(w.Places)}) and not granted to this desk — a takeover shows black there: Stagewatch → window grant {w.Name} {side.SelfId}")
            + Ask(w.Name, side.CanAsk)));
    }

    /// <summary>The desk's own way to the same grant: NODES ASK WINDOW puts the ask on Stagewatch's desk, and needs the link up to.</summary>
    private static string Ask(string name, bool canAsk)
        => canAsk ? $", or ask from this desk: NODES ASK WINDOW {name}" : $" (NODES ASK WINDOW {name} asks it from this desk once its Stagewatch link is up)";

    /// <summary>The row's value: "lobby not held by Backup", "2 not held by this desk", "Backup has not said".</summary>
    public static string Value(string who, IReadOnlyList<TwinWindowOnAir> lacking, bool said)
    {
        ArgumentNullException.ThrowIfNull(lacking);
        if (!said) return $"{who} has not said";
        return lacking.Count == 1 ? $"{lacking[0].Name} not held by {who}" : $"{lacking.Count} not held by {who}";
    }

    /// <summary>
    /// The main's report over its standbys (each by name, with its last <c>WINDOWS</c> word, null before one): amber naming each
    /// window on air a standby lacks, or that a standby has not said; green when every standby holds every one; nothing to say with
    /// no granted window on air or no standby on the link.
    /// </summary>
    public static TwinWindowsReport ForMain(IReadOnlyList<(string Name, TwinWindowsHeld? Held)> standbys, IReadOnlyList<TwinWindowOnAir> onAir)
    {
        ArgumentNullException.ThrowIfNull(standbys);
        ArgumentNullException.ThrowIfNull(onAir);
        if (onAir.Count == 0 || standbys.Count == 0) return TwinWindowsReport.None;
        var words = new List<string>();
        var value = "";
        foreach (var (name, held) in standbys)
        {
            var said = OnMain(name, held, onAir);
            if (said.Length == 0) continue;
            words.Add(said);
            if (value.Length == 0) value = Value(name, Lacking(onAir, held), held is not null);
        }
        if (words.Count == 0)
        {
            var names = string.Join(", ", standbys.Select(s => s.Name));
            return new TwinWindowsReport($"{onAir.Count} on air · held by {names}",
                $"each granted window on air here ({string.Join(", ", onAir.Select(w => w.Name))}) is granted to {names} too: a takeover opens it", false);
        }
        if (words.Count > 1) value += $" · and {words.Count - 1} more standby";
        return new TwinWindowsReport(value, string.Join("; ", words), true);
    }

    /// <summary>
    /// A standby's own report: amber naming each window on the air it mirrors that no grant of its own opens; once it took the show,
    /// amber too for each window its own grant opens whose page is not ready here (black now), green only when every one is ready —
    /// observed, never claimed from the grant alone; before then green when its own grants hold every one. Nothing to say with no
    /// granted window on that air.
    /// </summary>
    public static TwinWindowsReport ForStandby(IReadOnlyList<TwinWindowOnAir> onAir, IEnumerable<StagewatchGrant> own, TwinStandbySide side)
    {
        ArgumentNullException.ThrowIfNull(onAir);
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(side);
        if (onAir.Count == 0) return TwinWindowsReport.None;
        var mine = own.ToList();
        var lacking = Lacking(onAir, mine);
        if (lacking.Count > 0) return new TwinWindowsReport(Value("this desk", lacking, said: true), OnStandby(lacking, side), true);
        var names = string.Join(", ", onAir.Select(w => w.Name));
        if (!side.TookOver)
            return new TwinWindowsReport($"{onAir.Count} on air · held here",
                $"each granted window on air at {(string.IsNullOrEmpty(side.Main) ? "the main" : side.Main)} ({names}) is granted to this desk too, its address here: a takeover has its own grant to open it", false);
        var notReady = onAir
            .Select(w => (Window: w, Ready: side.Readiness(mine.First(g => g.Opens && string.Equals(g.Held, w.Held, StringComparison.Ordinal)))))
            .Where(x => !x.Ready.Ready)
            .ToList();
        if (notReady.Count == 0)
            return new TwinWindowsReport($"{onAir.Count} on air · open here", $"each granted window on air here ({names}) opens from this desk's own grant and its page is ready", false);
        return new TwinWindowsReport(notReady.Count == 1 ? $"{notReady[0].Window.Name} not ready here" : $"{notReady.Count} not ready here",
            string.Join("; ", notReady.Select(x => $"Stagewatch {x.Window.Name} is on air here ({PlaceWords(x.Window.Places)}) from this desk's own grant and black until its page is ready: {x.Ready.Why}")), true);
    }
}
