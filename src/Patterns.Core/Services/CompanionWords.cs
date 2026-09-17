namespace Patterns.Core.Services;

/// <summary>The Companion module as this build knows it: the version the integrations folder carries, so the desk can say when a deck runs an older one.</summary>
public static class CompanionModule
{
    /// <summary>Kept equal to integrations/companion-module-patterns/package.json by a test.</summary>
    public const string Version = "3.13.0";

    /// <summary>The port Companion's own TCP remote-control API listens on, as Companion 4 and 5 ship it.</summary>
    public const int ApiPort = 16759;

    /// <summary>The service type Companion 5 announces its satellite port under; hearing it is how the desk knows a Companion is on the network.</summary>
    public const string SatelliteServiceType = "_companion-satellite-tcp._tcp";
}

/// <summary>One deck (a Companion connection, or anything else that said HELLO) on the wire right now.</summary>
public sealed record WireDeck(string Name, string Module, string Address, DateTime SinceUtc)
{
    /// <summary>Round 66: the connection presented the show's pairing token (or the desk asks for none) — its mutating verbs run; false is a deck whose keys do nothing yet.</summary>
    public bool Paired { get; init; }

    /// <summary>Round 74: where the deck's navigator is, as the deck said it (NAV DECK &lt;words&gt;: "PLAN › Cues › 03.020"); "" until it says.</summary>
    public string Where { get; init; } = "";

    /// <summary>Round 74: the deck asked for the desk's actions as ACTION lines (RECORD ON) — Companion's recorder is open on a button.</summary>
    public bool Recording { get; init; }

    /// <summary>"FOH deck (module 3.0.0, 10.0.0.5) — navigator at PLAN › Cues" — with a word when the module is behind the one this build ships.</summary>
    public string Line
    {
        get
        {
            var module = Module.Length == 0 ? "no module — Generic TCP or a script" : $"module {Module}";
            var behind = Module.Length > 0 && CompanionWords.IsOlder(Module, CompanionModule.Version) ? $" — {CompanionModule.Version} is current, in the app's integrations folder" : "";
            var where = Where.Length > 0 ? $" — navigator at {Where}" : "";
            var recording = Recording ? " — recording" : "";
            return $"{Name} ({module}{behind}, {Address}){where}{recording}";
        }
    }
}

/// <summary>
/// The words between the desk and the decks: a HELLO taken apart into the deck's name and its
/// module version, the Remote page's lines about the desk on the network, the decks connected and
/// the Companions heard. Pure, so the page reads the same on every desk and in the tests.
/// </summary>
public static class CompanionWords
{
    /// <summary>"FOH deck module=3.0.0" → the name and the module; a HELLO with no token is a name alone.</summary>
    public static (string Name, string Module) ParseHello(string text)
    {
        var t = (text ?? "").Trim();
        var at = t.LastIndexOf(" module=", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return t.StartsWith("module=", StringComparison.OrdinalIgnoreCase) ? ("Companion", t[7..].Trim()) : (t, "");
        return (t[..at].Trim(), t[(at + 8)..].Trim());
    }

    /// <summary>Is version a behind b — "2.8.0" behind "3.0.0"; words that are not versions are never behind.</summary>
    public static bool IsOlder(string a, string b)
    {
        if (!Version.TryParse(Normalise(a), out var va) || !Version.TryParse(Normalise(b), out var vb)) return false;
        return va < vb;
    }

    private static string Normalise(string v)
    {
        var s = (v ?? "").Trim().TrimStart('v', 'V');
        var dash = s.IndexOf('-');
        if (dash > 0) s = s[..dash];
        return s.Contains('.') ? s : s + ".0";
    }

    /// <summary>The line about this process on the network.</summary>
    public static string AnnounceLine(bool wireOn, bool announce, string mdnsStatus, string instanceLabel)
    {
        if (!wireOn) return "Remote control is off — nothing is announced and no deck can connect.";
        if (!announce) return "Not announced on the network: a Companion needs this machine's address typed. Tick ANNOUNCE to be listed.";
        return mdnsStatus.Length > 0 ? mdnsStatus : $"Announced on the network as \"{instanceLabel}\" — pick it under Desk on the network in Companion's connection settings.";
    }

    /// <summary>The decks connected right now, one line.</summary>
    public static string DecksLine(IReadOnlyList<WireDeck> decks)
        => decks.Count == 0
            ? "No deck connected. In Companion: Connections → Add → Patterns, pick this desk, drag the presets."
            : $"Connected: {string.Join(" · ", decks.Select(d => d.Line))}.";

    /// <summary>The Companions heard announcing themselves, one line.</summary>
    public static string HeardLine(IReadOnlyList<MdnsPeer> companions)
        => companions.Count == 0
            ? "No Companion heard on the network yet (Companion 5 announces itself; older ones do not — add one by its address on the Interactive page, + COMPANION)."
            : $"Companion on the network: {string.Join(" · ", companions.Select(c => c.Line))} — the desk drives it through a Companion device on the Interactive page (port {CompanionModule.ApiPort}).";
}
