using System.Globalization;
using System.Net;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The Stagewatch link's verbs as the wire spells them (G34; Show Bus 1.3 §13.16.6). The link is set and followed from the
/// wire, a deck and the menus as the Nodes page's own boxes set it — <c>STAGEWATCH LINK ON|OFF|AUTO|AGAIN|&lt;host[:port]&gt;|id:&lt;id&gt;</c>,
/// <c>STAGEWATCH FOLLOW ON|OFF</c>, <c>STAGEWATCH FLOOR LOWER</c>, <c>STAGEWATCH RELEASE</c> and the read <c>STAGEWATCH [STATUS]</c>.
/// TRUST and ACCEPT are a person's on the desk alone, beside the fingerprint: their words on a line carry nothing a press would and are
/// refused with <see cref="DeskOnlyWords"/>. The key never rides a line: <c>STAGEWATCH KEY …</c> carries nothing and is refused with
/// <see cref="KeyWords"/>. Any other word is no line of the wire. Pure.
/// </summary>
public static class StagewatchVerbs
{
    /// <summary>What the wire, a deck and the assistant hear for TRUST and ACCEPT.</summary>
    public const string DeskOnlyWords = "TRUST and ACCEPT are a person's choice on the desk, beside the fingerprint";

    /// <summary>What every surface but the Nodes page hears for the key.</summary>
    public const string KeyWords = "The Stagewatch key is typed on the Nodes page only — it never rides a line";

    /// <summary>The name an approved line from an approval desk runs under (<c>(Tcp, "approval desk")</c>).</summary>
    public const string ApprovalDesk = "approval desk";

    /// <summary>What an approved line that would change the link hears: the link is the operator's, never another app's approval.</summary>
    public const string ApprovalDeskWords = "The Stagewatch link is this desk's operator's to change — not an approval desk's";

    /// <summary>
    /// RELEASE's answer while nothing is held (Show Bus 1.3 §13.17): this desk follows no fact of a Stagewatch's for safety — a blackout or
    /// a hold — and a counting time is never held (13.10.6), so there is nothing a person could release.
    /// </summary>
    public const string NothingHeldWords = "Nothing of a Stagewatch's is held on this desk — it follows no blackout or hold from one, and a counting time is never held (Show Bus 1.3 §13.17)";

    /// <summary>FLOOR LOWER's answer while no floor is recorded (Show Bus 1.3 §13.9.3: floors come only from verified links).</summary>
    public const string NoFloorWords = "No floor is recorded for the Stagewatch link — a floor comes only from a link that proved itself with PeerLink (Show Bus 1.3 §13.9.3), and no Stagewatch has to this desk yet";

    /// <summary>
    /// The assistant's brief, under the linked Stagewatch: its verbs are the operator's (X-declines, G34) — a line that repoints a
    /// key-bearing link or the speaker's timer is a trust decision, as a certificate is — so the assistant names them in words and
    /// proposes none.
    /// </summary>
    public const string BriefWords = "the link's verbs are the operator's, never proposed: STAGEWATCH LINK ON|OFF|AUTO|AGAIN|<host:port>|id:<id> and STAGEWATCH FOLLOW ON|OFF on a deck, the wire or the Nodes card's menu; TRUST and ACCEPT only on the desk, beside the fingerprint";

    /// <summary>
    /// The words after STAGEWATCH as a line of the wire, or null when they are none (the wire answers it unknown, never a default):
    /// a bare or STATUS read, LINK with one value of <see cref="LinkValue"/> or AGAIN, FOLLOW ON|OFF, FLOOR LOWER, RELEASE; and TRUST,
    /// ACCEPT and KEY as their desk-only verbs with nothing after them carried, so a fingerprint, typed hex or a key on the line goes
    /// nowhere — not into the journal, a reply or a log.
    /// </summary>
    public static RemoteCommand? Parse(string arg)
    {
        var sub = (arg ?? "").Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var what = sub.Length > 0 ? sub[0].ToUpperInvariant() : "";
        var rest = sub.Length > 1 ? sub[1] : "";
        switch (what)
        {
            case "" or "STATUS":
                return rest.Length == 0 ? new RemoteCommand(RemoteCommandKind.StagewatchStatus, ShowAction.None) : null;
            case "LINK":
                if (rest.Equals("AGAIN", StringComparison.OrdinalIgnoreCase)) return Act(ShowActionKind.StagewatchLinkAgain);
                return LinkValue(rest) is { } value ? Act(ShowActionKind.StagewatchLinkSet, value) : null;
            case "FOLLOW":
                return rest.ToUpperInvariant() is "ON" or "OFF" ? Act(ShowActionKind.StagewatchFollow, rest.ToUpperInvariant()) : null;
            case "FLOOR":
                return rest.Equals("LOWER", StringComparison.OrdinalIgnoreCase) ? Act(ShowActionKind.StagewatchFloorLower) : null;
            case "RELEASE":
                return rest.Length == 0 ? Act(ShowActionKind.StagewatchRelease) : null;
            case "TRUST":
                return Act(ShowActionKind.StagewatchTrust);
            case "ACCEPT":
                return Act(ShowActionKind.StagewatchAccept);
            case "KEY":
                return Act(ShowActionKind.StagewatchKeyBind);
            default:
                return null;
        }

        static RemoteCommand Act(ShowActionKind kind, string value = "") => RemoteCommand.Of(new ShowAction(kind, "", value));
    }

    /// <summary>
    /// LINK's one value in its written form, or null when it is none: ON, OFF and AUTO in capitals; a heard Stagewatch's id as
    /// <c>id:&lt;id&gt;</c> (<see cref="StageFollow.ParseId"/>; a bare word is a host); an address as <c>host:port</c> (an IPv6 host
    /// bracketed, <c>[fe80::1]:9924</c>), port 9924 when none is typed. A word of these that is also a host's name is the word: such a
    /// host is typed with its port.
    /// </summary>
    public static string? LinkValue(string word)
    {
        var w = (word ?? "").Trim();
        var upper = w.ToUpperInvariant();
        if (upper is "ON" or "OFF" or "AUTO") return upper;
        if (StageFollow.ParseId(w) is { } id) return StageFollow.IdPrefix + id;
        if (w.StartsWith(StageFollow.IdPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        return StageFollow.ParseAddress(w) is { } a ? AddressWords(a.Host, a.Port) : null;
    }

    /// <summary>An address as the link and the wire write it: <c>host:port</c>, an IPv6 host bracketed.</summary>
    public static string AddressWords(string host, int port)
        => (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + host + "]" : host)
           + ":" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Why ACCEPT with <paramref name="typed"/> is refused, or null when it is taken (Show Bus 1.3 §13.8.3): the waiting certificate's
    /// first hex — at least 8 of them, spaces dropped, either case, never more than the 64 there are — read as the core reads it
    /// (<see cref="PeerLinkMachine.AcceptCertificate"/>) and as Stagewatch's own desk does, so the two apps' "type 8 hex" take the same
    /// input. A typing check that a person compared the certificate the Stagewatch shows now with the one this desk shows, and that
    /// refuses a press made for one that has changed again since; the trust is the person's.
    /// </summary>
    public static string? AcceptRefusal(string typed, string pending, string name)
    {
        if (!TlsFingerprint.IsValid(pending)) return "No changed certificate is waiting.";
        var hex = new string((typed ?? "").Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray());
        var shown = pending[TlsFingerprint.Prefix.Length..];
        if (hex.Length < 8 || !PeerLink.IsHex(hex, hex.Length))
            return $"Type at least 8 hex of the certificate Stagewatch {name} shows now — compare it on its own screen first; nothing is accepted yet.";
        if (hex.Length > shown.Length || !shown.StartsWith(hex, StringComparison.Ordinal))
            return $"The waiting certificate begins {TlsFingerprint.Short(pending)} — not what was typed; compare it with Stagewatch {name}'s own screen. Nothing is accepted.";
        return null;
    }
}

/// <summary>
/// G34: where the Stagewatch key may go — <see cref="Model.StagewatchLinkConfig.KeyFor"/>. The key is typed on the desk and goes only
/// to the Stagewatch it is bound to: <c>node:&lt;id&gt;</c> (that Stagewatch, at whatever address its hello names) or
/// <c>addr:&lt;host&gt;:&lt;port&gt;</c> (that address, and a heard Stagewatch reached by typing it). Unbound (<c>""</c>), the key
/// follows the target the desk's own settings name, and binds to it at the first 2xx a key-shaped key earns. A link a line set — the
/// wire, a deck, anything but a person on this desk — first turns an unbound key to <see cref="None"/>, so the key never goes to a
/// target a line chose; <see cref="None"/> sends it nowhere until a person binds it. An unknown word sends it nowhere. Pure.
/// </summary>
public static class StagewatchKeyFor
{
    /// <summary>Bound to nothing: the key stays on this desk until a person binds it on the Nodes page.</summary>
    public const string None = "none";

    /// <summary>The binding of a Stagewatch known by its show network id.</summary>
    public static string Node(string id) => "node:" + id;

    /// <summary>The binding of a typed address, as the link's pins name it: the host in lower case, then the port.</summary>
    public static string Address(string host, int port) => "addr:" + host.ToLowerInvariant() + ":" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether the key goes to the target the link dials: <paramref name="pinKey"/> its name (<c>node:</c> or <c>addr:</c>) and
    /// <paramref name="typedKey"/> the <c>addr:</c> of the address typed to reach it ("" when it was found or named by id).
    /// </summary>
    public static bool Goes(string keyFor, string pinKey, string typedKey = "")
    {
        if (keyFor.Length == 0) return true;
        if (keyFor.StartsWith("node:", StringComparison.Ordinal)) return string.Equals(keyFor, pinKey, StringComparison.Ordinal);
        if (keyFor.StartsWith("addr:", StringComparison.Ordinal))
            return string.Equals(keyFor, pinKey, StringComparison.OrdinalIgnoreCase) || (typedKey.Length > 0 && string.Equals(keyFor, typedKey, StringComparison.OrdinalIgnoreCase));
        return false;
    }

    /// <summary>The binding once a line set the link: an unbound key goes nowhere a line chose; a bound one stays bound.</summary>
    public static string AfterLine(string keyFor) => keyFor.Length == 0 ? None : keyFor;

    /// <summary>A binding as the desk and STATE say it: "Stagewatch a1b2c3d4", "10.0.0.6:9924", "nothing", or the settings' target.</summary>
    public static string Words(string keyFor) => keyFor switch
    {
        "" => "the Stagewatch this desk's own settings name",
        None => "nothing — bind it on the Nodes page",
        _ when keyFor.StartsWith("node:", StringComparison.Ordinal) => "Stagewatch " + keyFor[5..],
        _ when keyFor.StartsWith("addr:", StringComparison.Ordinal) => keyFor[5..],
        _ => "nothing",
    };
}
