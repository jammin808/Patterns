using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Patterns.Core.Services;

/// <summary>
/// Where a thing or a link of the Eye comes from (the Eye design's D4), the order of precedence when two sources say one fact:
/// this desk's own facts, a record of a PeerLink this desk holds or accepts, a node's own context about itself, a node's context
/// about a third node, and the hello alone. A told or relayed link is the publishing node's claim, never a fact about the other.
/// </summary>
public enum EyeOrigin
{
    Own,
    Link,
    Told,
    Relayed,
    Heard,
}

/// <summary>A cross-node end of a context's link (Show Bus 1.3 §6): the node it names, and its item ("" for the node itself).</summary>
public readonly record struct EyeEnd(string Node, string Item);

/// <summary>
/// The network part of the Eye (Show Bus 1.3 §6, §13.16; the Eye design's D2, D3, D5, D6 and D8), pure: one card per app id,
/// folded only on evidence of identity; a link drawn as an edge between two cards and never as a thing; a context's rows with a
/// cross-node end placed on what they name, or counted when nothing on the Eye is that node; the counting things of an app whose
/// link is not live shown as unknown; and the bounds every table of the network keeps.
/// </summary>
public static class EyeNetwork
{
    /// <summary>Links one app's context says of a third node (its peers rows), drawn at most this many in all.</summary>
    public const int MaxRelayed = 256;

    /// <summary>Links one app's context draws from its things to another app's (a cross-node row), at most this many in all.</summary>
    public const int MaxCrossApp = 256;

    /// <summary>Nodes known only from one context's peers rows, at most this many from one context (§14 <c>context.peers</c>).</summary>
    public const int MaxMentionsPerContext = 32;

    /// <summary>Nodes known only from contexts' peers rows, at most this many on the Eye.</summary>
    public const int MaxMentions = 128;

    /// <summary>
    /// The things of an app's context that end a chain across apps (an item with a cross-node row) taken ahead of its other things,
    /// after its reds and ambers, at most this many — so what an app's cluster draws holds its problems and its chain ends.
    /// </summary>
    public const int MaxForced = 8;

    /// <summary>§14 <c>context.link.end.chars</c>: the longest cross-node end a reader takes.</summary>
    public const int EndChars = 120;

    /// <summary>§14 <c>context.item.id.chars</c>: the longest item id, so the longest item an end names.</summary>
    public const int ItemChars = 60;

    /// <summary>The longest id this desk exports for one of its own things before it is shortened (<see cref="ShortId"/>).</summary>
    public const int ShortChars = 48;

    /// <summary>
    /// The card a thing of an app sits under: <c>app:&lt;id&gt;</c>, but another Patterns node's — its id <c>node:&lt;instance&gt;</c>,
    /// the card the beacon draws it by — which is its own card, so its hello, its beacon and its twin context are one.
    /// </summary>
    public static string CardOf(string app) => app.StartsWith("node:", StringComparison.Ordinal) ? app : "app:" + app;

    /// <summary>
    /// A link record that proves its peer's id (13.6): LINK ran to level 1 with a mutual proof and the verdict named the id. The only
    /// evidence that folds a connection into the app a hello names; an address never does.
    /// </summary>
    public static bool Proved(LinkRecord? record) => record is { Level: >= 1, Proof: "mutual", PeerId.Length: > 0 };

    /// <summary>
    /// The heard app a link is to, or null. By the link's proof first: a record LINK verified names the t:id, and a hello that says
    /// that id under that kind is the app, of any kind but this desk's own and wherever it was heard from (a hello saying the id under
    /// another kind is another claim, never folded onto the link). Without a proof, a Stagewatch's hello from the link's address at one
    /// of its doors (the doors the link dials are a Stagewatch's) — and of two there, the one the link's own context names; never an
    /// address alone where two answer.
    /// </summary>
    public static string? LinkedId(IReadOnlyList<ShowBusPeer> peers, StagewatchSight sight)
    {
        if (sight.Address.Length == 0) return null;
        if (Proved(sight.Link))
        {
            var t = sight.Link!.PeerT is { Length: > 0 } said ? said : "main";
            if (t != "patterns" && peers.Any(p => p.Kind == t && p.Id == sight.Link.PeerId)) return sight.Link.PeerId;
        }
        var heard = peers.Where(p => p.Kind == "main" && LinkedTo(sight, p)).Select(p => p.Id).Distinct(StringComparer.Ordinal).ToList();
        if (heard.Count == 1) return heard[0];
        return sight.Context?.Node.Id is { Length: > 0 } verified && heard.Contains(verified, StringComparer.Ordinal) ? verified : null;
    }

    /// <summary>Whether the link's address names this peer: an address its hellos came from, at one of its doors — never what a hello says of its own host.</summary>
    private static bool LinkedTo(StagewatchSight sight, ShowBusPeer p)
    {
        var colon = sight.Address.LastIndexOf(':');
        var host = colon > 0 ? sight.Address[..colon].Trim('[', ']') : sight.Address;
        var port = colon > 0 && int.TryParse(sight.Address[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return port == 0 ? p.HeardAt(host) : StagewatchAt.Speaks(p, host, port);
    }

    /// <summary>
    /// The heard PlayMap a node device this desk dialled folds into, by its place in <paramref name="apps"/>, or -1. At level 0 the
    /// device proves nothing, so it folds only when two observations agree: a hello from the device's address at its wire port, and
    /// the id the device's own context claims. An address alone (another node there, a forged hello) or an id alone (a context
    /// relayed from elsewhere) never makes one app of two; where they disagree the device is an app of its own and says so.
    /// </summary>
    public static int DeviceFolds(IReadOnlyList<ShowBusPeer> peers, IReadOnlyList<EyeShowApp> apps, ShowBusNodeSight n)
    {
        if (!n.Up || n.ContextId.Length == 0) return -1;
        var heard = peers.Where(p => p.Kind == "playmap" && p.Id == n.ContextId && p.HeardAt(n.Host)
            && (p.Ports.Wire == n.Port || (p.Ports.Wire == 0 && n.Port == ShowBusNodeSession.WirePort))).ToList();
        if (heard.Count != 1) return -1;
        for (var i = 0; i < apps.Count; i++)
            if (apps[i].Id == heard[0].Id && apps[i].KindWords == "PlayMap" && apps[i].DeviceId.Length == 0) return i;
        return -1;
    }

    /// <summary>
    /// One app per id: every hello, link, context and device a desk holds for one id are one app. Where the same id reaches the Eye
    /// twice, the one that holds a link leads — its record, its context and its device — and the hello's age, addresses and
    /// conflict are taken from the one heard; the order is the first sighting's.
    /// </summary>
    public static IReadOnlyList<EyeShowApp> Fold(IReadOnlyList<EyeShowApp> apps)
    {
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        var folded = new List<EyeShowApp>(apps.Count);
        foreach (var a in apps)
        {
            if (!at.TryGetValue(a.Id, out var i))
            {
                at[a.Id] = folded.Count;
                folded.Add(a);
                continue;
            }
            var (lead, other) = ShowBusView.HoldsLink(folded[i]) || !ShowBusView.HoldsLink(a) ? (folded[i], a) : (a, folded[i]);
            folded[i] = lead with
            {
                Heard = lead.Heard.Length > 0 ? lead.Heard : other.Heard,
                Addresses = lead.Addresses.Count > 0 ? lead.Addresses : other.Addresses,
                Conflict = lead.Conflict.Length > 0 ? lead.Conflict : other.Conflict,
                Needed = lead.Needed || other.Needed,
            };
        }
        return folded;
    }

    /// <summary>
    /// What a deck that said HELLO with an app's name is, in words — or "" for a deck that names no app. An accepted connection that
    /// did not run LINK proves nothing about who dialled, so it stays a deck and never folds into the app: its words state both
    /// observations without joining them — the name it said, and whose hello comes from the address it dialled from.
    /// </summary>
    public static string DeckWords(EyeDeck deck, IReadOnlyList<EyeShowApp> apps)
    {
        var said = deck.Name.Trim();
        if (!AppKinds.Any(k => ShowBusPeers.KindWords(k).Equals(said, StringComparison.OrdinalIgnoreCase))) return "";
        var host = Host(deck.Address);
        var there = apps.FirstOrDefault(a => a.KindWords.Equals(said, StringComparison.OrdinalIgnoreCase) && a.Addresses.Any(x => Host(x).Equals(host, StringComparison.OrdinalIgnoreCase)));
        return there is null
            ? $"says it is {said} — not proved (PeerLink not run)"
            : $"says it is {said} — from {host}, where {there.KindWords} {there.Name}'s hello comes from; not proved (PeerLink not run)";
    }

    /// <summary>The kinds of the other show apps (§8) a deck's HELLO may name itself by.</summary>
    private static readonly string[] AppKinds = { "main", "playmap", "messenger", "agent" };

    private static string Host(string address)
    {
        var a = address.Trim();
        if (a.StartsWith('[') && a.IndexOf(']') is var close and > 0) return a[1..close];
        return a.Count(c => c == ':') == 1 ? a[..a.IndexOf(':')] : a;
    }

    /// <summary>True for a link end that names another node (<c>@…</c>), well formed or not.</summary>
    public static bool IsEnd(string? text) => text is { Length: > 0 } && text[0] == '@';

    /// <summary>
    /// A cross-node end as §6 takes one: <c>@&lt;node id&gt;</c> or <c>@&lt;node id&gt;/&lt;item id&gt;</c>, the id 6 to 40 letters,
    /// digits, <c>-</c> or <c>_</c>, the item at most <see cref="ItemChars"/> characters with no control character, the whole at most
    /// <see cref="EndChars"/>. Anything else is refused: a reader never guesses which node a broken end meant.
    /// </summary>
    public static bool TryEnd(string? text, out EyeEnd end)
    {
        end = default;
        if (!IsEnd(text) || text!.Length > EndChars) return false;
        var body = text[1..];
        var slash = body.IndexOf('/');
        var node = slash < 0 ? body : body[..slash];
        var item = slash < 0 ? "" : body[(slash + 1)..];
        if (!IsId(node)) return false;
        if (slash >= 0 && (item.Length is 0 or > ItemChars || item.Any(char.IsControl))) return false;
        end = new EyeEnd(node, item);
        return true;
    }

    /// <summary>A node's id of §2's shape: 6 to 40 letters, digits, <c>-</c> or <c>_</c>. A peers row naming any other is no node's.</summary>
    public static bool IsId(string? id)
        => id is { Length: >= ShowBusPeers.IdMin and <= ShowBusPeers.IdMax } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// A link's kind as §6 words it — one of the eleven (<see cref="EyeEdgeKind"/>), letters only, any case. A number, a list or any
    /// other word is refused, never read as some kind: a reader draws no link whose kind it does not know.
    /// </summary>
    public static bool TryKind(string? word, out EyeEdgeKind kind)
    {
        kind = default;
        return word is { Length: > 0 and <= 16 } && word.All(char.IsAsciiLetter) && Enum.TryParse(word, true, out kind) && Enum.IsDefined(kind);
    }

    /// <summary>The end that names a node's item, or the node itself with no item.</summary>
    public static string End(string node, string item = "") => item.Length == 0 ? "@" + node : $"@{node}/{item}";

    /// <summary>
    /// A counting thing (a timer, a schedule, a playlist, a chase, a timecode, a clock: <see cref="ShowBusView.IsCounting"/>) from an
    /// app whose link is not live, as the Eye shows it: its count is not current, so its state is <c>? — reason</c>, and its light the
    /// worse of its own and amber — red when the show needs the app (PeerLink profile 13.10.6, 13.16.1). Any other thing is as it was.
    /// </summary>
    public static ShowContextItem Counting(ShowContextItem item, string reason, bool needed)
        => !ShowBusView.IsCounting(item.Kind) ? item : item with
        {
            State = "? — " + reason,
            Light = needed ? "red" : ShowContext.Word(ShowContext.Worst(new[] { ShowContext.LightOf(item.Light), CheckLight.Amber })),
        };

    /// <summary>
    /// The id this desk exports one of its own things by (its context's items, its rows, the <c>@&lt;desk&gt;/&lt;item&gt;</c> ends other
    /// apps write): the Eye's id where it is short and holds only letters, digits and <c>- _ : .</c>; else its kind's word and eight hex
    /// of the id's SHA-256 — stable across runs and machines, and never a path, an address or a page's pass.
    /// </summary>
    public static string ShortId(string eyeId)
    {
        if (eyeId.Length <= ShortChars && eyeId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.')) return eyeId;
        var colon = eyeId.IndexOf(':');
        var kind = colon > 0 && eyeId[..colon].All(char.IsAsciiLetterOrDigit) && colon <= 16 ? eyeId[..colon] : "thing";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(eyeId));
        return kind + ":" + Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    /// <summary>
    /// Words that carry what no export may: a Stagewatch key (<c>swk_</c>), an app key (<c>ptk_</c>), a window's address
    /// (<c>/w/</c>) or a file path. The canary the export is held to (Show Bus 1.3 §6 P8, §13.19).
    /// </summary>
    public static bool Secretish(string? words)
    {
        if (string.IsNullOrEmpty(words)) return false;
        if (words.Contains("swk_", StringComparison.Ordinal) || words.Contains("ptk_", StringComparison.Ordinal) || words.Contains("/w/", StringComparison.Ordinal)) return true;
        return words.Contains(":\\", StringComparison.Ordinal) || words.Contains("\\\\", StringComparison.Ordinal) || words.Contains("file:", StringComparison.OrdinalIgnoreCase);
    }
}
