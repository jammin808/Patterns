using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>
/// Show Bus 1.3 §13, PeerLink — the pure part this desk's links are built on. A code's canonical form, K and the kid
/// (13.5.1); the transcript T, both proofs, the lid, the session credential S and an enrolled K (13.6, 13.5.7); 1.1's
/// legacy proof as 13.6.8 keys it; what an acceptor reads LINK with and what a connector reads the answer with (13.6.4,
/// 13.6.5, 13.7.1); the link's liveness (13.10.3) and 13.12.3's table of answers. Nothing here reads a clock, a socket or a
/// file, and nothing here keeps a key: a secret comes in as an argument and goes out only as a proof made with it.
/// <see cref="PeerLinkMachine"/> is 13.16's machine on top of it, <see cref="LinkClock"/> 13.11's clock and
/// <see cref="ReceiptBook"/> 13.12.6's record. Each number is its §14 row's, held to it by <c>ShowBusSpecTests</c>; the
/// numbers this desk kept before the core (the hello's beat and liveness, the redial, the refused key's hold, a request
/// id's length) are read where they already live, so each has one home.
/// </summary>
public static class PeerLink
{
    /// <summary>The PeerLink version this core speaks (§14 <c>peerlink.version</c>, 13.9.1).</summary>
    public const int Version = 1;

    /// <summary>T's first line, and the start of the code, kid and lid labels (§14 <c>peerlink.label</c>).</summary>
    public const string Label = "showbus-peerlink-1";

    /// <summary>The most words a verdict's <c>can</c> lists (§14 <c>peerlink.can.words</c>); past it the rest are not read.</summary>
    public const int CanWords = 8;

    /// <summary>A nonce's bytes (§14 <c>peerlink.nonce.bytes</c>): 32 lowercase hex.</summary>
    public const int NonceBytes = 16;

    /// <summary>The bytes of SHA-256 a kid keeps (§14 <c>peerlink.kid.bytes</c>): 16 lowercase hex.</summary>
    public const int KidBytes = 8;

    /// <summary>The bytes of SHA-256 a lid keeps (§14 <c>peerlink.lid.bytes</c>): 16 lowercase hex.</summary>
    public const int LidBytes = 8;

    /// <summary>A connection that has not finished LINK within this is closed (§14 <c>peerlink.handshake.ms</c>, 13.6.3).</summary>
    public static readonly TimeSpan Handshake = TimeSpan.FromSeconds(10);

    /// <summary>A link verified and up this long, never down, resets the redial count (§14 <c>peerlink.stable.ms</c>, 13.15.2).</summary>
    public static readonly TimeSpan Stable = TimeSpan.FromSeconds(10);

    /// <summary>The connector's last pings a pong may answer and still be a clock sample (§14 <c>peerlink.pings.matched</c>, 13.10.2).</summary>
    public const int PingsMatched = 8;

    /// <summary>The evaluator's own gap past which it takes itself for paused, never the link (§14 <c>link.selfgap.ms</c>, 13.10.4).</summary>
    public static readonly TimeSpan SelfGap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often a connector pings (§14 <c>ping.min.ms</c>–<c>ping.max.ms</c>, 13.10.1): strictly inside the band, so a ping due a beat
    /// after the last one left and sent a little late by its timer is still within <c>ping.max.ms</c>.
    /// </summary>
    public static readonly TimeSpan PingEvery = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// How often a link's state is evaluated, on a timer of its own that never waits behind a side call or a write (§14
    /// <c>link.check.max.ms</c>, 13.10.3): strictly inside the row, so an evaluator whose timer fires a little late still runs within it.
    /// </summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(750);

    /// <summary>How long one write to a link may take before the connection is cut (§14 <c>link.write.deadline.max.ms</c>, 13.14).</summary>
    public static readonly TimeSpan WriteDeadline = TimeSpan.FromSeconds(10);

    /// <summary>The side calls of one kind a link runs at once, on their lane of their own (§14 <c>link.sidecall.inflight</c>, 13.7.2).</summary>
    public const int SideCallsAtOnce = 1;

    /// <summary>The least time between two looks at the certificate a node shows after CERTIFICATE CHANGED, each one TLS greeting that sends nothing (§14 <c>peerlink.cert.probe.ms</c>, 13.8.3).</summary>
    public static readonly TimeSpan CertProbe = TimeSpan.FromSeconds(60);

    /// <summary>§14 <c>log.repeat.summary.ms</c>: while a link's states repeat, one summary line this often in their place (13.19.4).</summary>
    public static readonly TimeSpan RepeatSummary = TimeSpan.FromSeconds(60);

    /// <summary>The most commands that wait for answers on one link (§14 <c>link.inflight.max</c>, 13.12.8).</summary>
    public const int InFlightMost = 32;

    /// <summary>A line acceptor that answers LINK nothing in this does not speak it (§14 <c>receipt.deadline.ms</c>, 13.6.8, 13.12.4).</summary>
    public static readonly TimeSpan ReceiptDeadline = TimeSpan.FromSeconds(3);

    /// <summary>A command written with no final answer in this is unconfirmed (§14 <c>receipt.final.ms</c>, 13.12.4); an answer at it still counts.</summary>
    public static readonly TimeSpan ReceiptFinal = TimeSpan.FromSeconds(30);

    /// <summary>The ids an acceptor's record keeps per connector (§14 <c>receipts.kept</c>, 13.12.6).</summary>
    public const int ReceiptsKept = 256;

    /// <summary>How long an acceptor's record keeps a receipt (§14 <c>receipts.kept.ms</c>, 13.12.6).</summary>
    public static readonly TimeSpan ReceiptsKeptFor = TimeSpan.FromMinutes(10);

    /// <summary>The most ids one RECEIPTS ask names, and an answer covers (§14 <c>receipts.ask.max</c>, 13.12.6).</summary>
    public const int ReceiptsAskMost = 32;

    /// <summary>The features a verdict's <c>can</c> may list (13.1), each a §14 <c>peerlink.feature</c> row, in its order.</summary>
    public static IReadOnlyList<string> Features { get; } = new[] { "final", "receipts", "stuck", "expire", "enrol" };

    /// <summary>
    /// 13.6.4's verdict classes, a closed set: the only words a refusing verdict's <c>class</c> is taken as, and so the only
    /// ones 13.16.3's <c>refusal</c> records from a verdict. A connector reads any other class as <c>malformed</c>.
    /// </summary>
    public static IReadOnlyList<string> VerdictClasses { get; } = new[] { "malformed", "version", "refused", "locked", "binding", "scope", "busy", "open" };

    /// <summary>The words a proof that does not verify is refused with, at either end, byte for byte (13.6.3).</summary>
    public const string ProofWords = "the proof did not verify: a wrong key, or another machine between us";

    /// <summary>13.5.1's words where a typed code is not of its shape.</summary>
    public const string CodeShapeWords = "PeerLink pairs a token of letters and digits — this one reaches only an older build";

    /// <summary>13.6.8's words for a token the legacy proof will not prove: it still holds a character 13.5.1 removes.</summary>
    public const string InvisibleWords = "this token holds an invisible character — retype it";

    private const string TlsBind = "tls:sha256:";

    // ---- 13.5 keys -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// 13.5.1: a code's canonical form — <c>-</c> and the separators 13.5.1 lists removed, <c>a</c>–<c>z</c> upper-cased and
    /// nothing else changed — or null when what is left is not <c>[A-Z0-9]{1,64}</c>: such a code is never proved by LINK.
    /// </summary>
    public static string? Canon(string? code)
    {
        if (code is null) return null;
        var kept = new StringBuilder(code.Length);
        foreach (var c in code)
        {
            if (CanonRemoves(c)) continue;
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') kept.Append(c);
            else if (c is >= 'a' and <= 'z') kept.Append((char)(c - ('a' - 'A')));
            else return null;
            if (kept.Length > 64) return null;
        }
        return kept.Length == 0 ? null : kept.ToString();
    }

    /// <summary>The characters 13.5.1's canonical form removes: <c>-</c>, U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F, U+3000 and U+FEFF.</summary>
    public static bool CanonRemoves(char c)
        => c is '-' or (>= '\u0009' and <= '\u000D') or '\u0020' or '\u0085' or '\u00A0' or '\u1680' or (>= '\u2000' and <= '\u200A')
            or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF';

    /// <summary>13.5.1: a code's K, SHA-256 of the code label, an LF and its canonical form.</summary>
    public static byte[] CodeKey(string canonical) => SHA256.HashData(Utf8(Label + " code\n" + canonical));

    /// <summary>13.5.1: a machine-made key's K, SHA-256 of the key — what Stagewatch already keeps for one.</summary>
    public static byte[] KeyKey(string key) => SHA256.HashData(Utf8(key));

    /// <summary>13.5.1: a key's kid, the lowercase hex of the first <see cref="KidBytes"/> bytes of SHA-256(kid label ‖ K). Not a secret.</summary>
    public static string Kid(byte[] k)
    {
        var label = Utf8(Label + " kid\n");
        var both = new byte[label.Length + k.Length];
        label.CopyTo(both, 0);
        k.CopyTo(both, label.Length);
        return Convert.ToHexStringLower(SHA256.HashData(both))[..(KidBytes * 2)];
    }

    // ---- 13.6 LINK -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// 13.6.1: the transcript T — its ten lines in UTF-8 joined by LF, no final LF; <paramref name="server"/> and
    /// <paramref name="client"/> are <c>t:id</c>. Null when a field breaks its shape, so no LF can enter it.
    /// </summary>
    public static string? Transcript(string carrier, long offer, long speaks, string server, string client, string kid, string snonce, string cnonce, string bind)
    {
        if (carrier is not ("line" or "ws") || offer < 0 || speaks < 0 || !IsNode(server) || !IsNode(client) || !IsKid(kid)
            || !IsHex(snonce, NonceBytes * 2) || !IsHex(cnonce, NonceBytes * 2) || !IsBind(bind)) return null;
        return string.Join('\n',
            Label,
            "carrier=" + carrier,
            "offer=" + offer.ToString(CultureInfo.InvariantCulture),
            "speaks=" + speaks.ToString(CultureInfo.InvariantCulture),
            "server=" + server,
            "client=" + client,
            "kid=" + kid,
            "snonce=" + snonce,
            "cnonce=" + cnonce,
            "bind=" + bind);
    }

    /// <summary>13.6.2: the connector's proof, lowercase hex of HMAC-SHA256(K, "client\n" ‖ T).</summary>
    public static string ClientProof(byte[] k, string t) => Mac(k, "client\n" + t);

    /// <summary>13.6.2: the acceptor's proof, lowercase hex of HMAC-SHA256(K, "server\n" ‖ T).</summary>
    public static string ServerProof(byte[] k, string t) => Mac(k, "server\n" + t);

    /// <summary>13.6.2: the link's id, the lowercase hex of the first <see cref="LidBytes"/> bytes of SHA-256(lid label ‖ T). Not a secret.</summary>
    public static string Lid(string t) => Convert.ToHexStringLower(SHA256.HashData(Utf8(Label + " lid\n" + t)))[..(LidBytes * 2)];

    /// <summary>13.6.2: on <c>ws</c>, the session credential S, lowercase hex of HMAC-SHA256(K, "session\n" ‖ T) — a secret that rides side calls alone (13.7).</summary>
    public static string Session(byte[] k, string t) => Mac(k, "session\n" + t);

    /// <summary>13.5.7: an enrolled key, K_link = HMAC-SHA256(K_code, "enrol\n" ‖ T); never sent, named by its <see cref="Kid"/>.</summary>
    public static byte[] EnrolKey(byte[] codeK, string t) => HMACSHA256.HashData(codeK, Utf8("enrol\n" + t));

    /// <summary>13.5.7: only a code proved over TLS enrols; a code proved over plain never does.</summary>
    public static bool MayEnrol(string kid, string bind) => kid == "code" && bind.StartsWith(TlsBind, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="given"/> is the proof <paramref name="expected"/>: compared in constant time once it is lowercase
    /// hex of the expected length (13.6.3). A proof of another shape is a wrong proof.
    /// </summary>
    public static bool Verify(string expected, string? given)
        => given is not null && IsHex(given, expected.Length) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(given));

    /// <summary>The version an acceptor of this core speaks to a connector offering <paramref name="offer"/>: this one, or null below it (a <c>version</c> refusal).</summary>
    public static int? Speaks(long offer) => offer >= Version ? Version : null;

    // ---- 13.6.8 level 0 ------------------------------------------------------------------------------------------------

    /// <summary>13.6.8: the legacy proof answers a NONCE of exactly 32 lowercase hex, and no other.</summary>
    public static bool IsProvableNonce(string? nonce) => IsHex(nonce, 32);

    /// <summary>
    /// 13.6.8: the legacy proof's key — the token as §7's 1.1 desks compare it (<see cref="PairingToken.Normal"/>: <c>-</c>,
    /// space, tab, CR and LF removed, each other UTF-16 unit upper-cased alone by its simple mapping) — or null when nothing is
    /// left, or what is left still holds a character 13.5.1's canonical form removes: 1.1 desks differ on those, so such a
    /// token is not proved, and the words say retype it (<see cref="InvisibleWords"/>).
    /// </summary>
    public static string? LegacyKey(string? token)
    {
        var key = PairingToken.Normal(token);
        return key.Length == 0 || key.Any(CanonRemoves) ? null : key;
    }

    /// <summary>
    /// 13.6.8: §7's AUTH-PROOF, lowercase hex of HMAC-SHA256(the legacy key, nonce ‖ the HELLO name); null for a nonce that is
    /// not 32 lowercase hex, or a token 13.6.8 does not prove.
    /// </summary>
    public static string? LegacyProof(string? token, string? nonce, string name)
        => IsProvableNonce(nonce) && LegacyKey(token) is { } key ? Convert.ToHexStringLower(HMACSHA256.HashData(Utf8(key), Utf8(nonce + name))) : null;

    // ---- what an acceptor reads ----------------------------------------------------------------------------------------

    /// <summary>
    /// 13.6.4 and 13.6.5 as an acceptor reads them: one LINK line, or the WebSocket's query and its <c>link</c> message,
    /// as its ask, its proof, or the class it is refused with — <c>malformed</c>, <c>version</c>, or <c>refused</c> for a
    /// proof that is not 64 lowercase hex (a malformed proof is a wrong proof).
    /// </summary>
    public static LinkParse ReadLinkLine(string line)
    {
        var w = line.Split(' ');
        var at = 0;
        if (w[0].StartsWith('@'))
        {
            if (!WireReceipt.IsId(w[0][1..])) return LinkParse.Malformed;
            at = 1;
        }
        if (w.Length <= at || w[at] != "LINK") return LinkParse.Malformed;
        if (w.Length > at + 1 && w[at + 1] == "PROOF")
        {
            if (w.Length != at + 3) return LinkParse.Malformed;
            return IsHex(w[at + 2], 64) ? new LinkParse(null, w[at + 2], null) : new LinkParse(null, null, "refused");
        }
        if (w.Length != at + 6 || !IsCount(w[at + 1]) || !IsKind(w[at + 2]) || !IsId(w[at + 3]) || !IsHex(w[at + 4], NonceBytes * 2) || !IsKid(w[at + 5]))
            return LinkParse.Malformed;
        var offer = long.Parse(w[at + 1], NumberStyles.None, CultureInfo.InvariantCulture);
        return Speaks(offer) is null
            ? new LinkParse(null, null, "version")
            : new LinkParse(new LinkAsk(offer, w[at + 2], w[at + 3], w[at + 4], w[at + 5]), null, null);
    }

    /// <summary>
    /// 13.6.5: a <c>link=</c> socket's query and its connector's <c>link</c> message, whose <c>client</c> must be the query's
    /// <c>t</c> and <c>node</c>. The ask carries the proof.
    /// </summary>
    public static LinkParse ReadLinkMessage(string query, JsonElement message)
    {
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !q.TryAdd(pair[..eq], pair[(eq + 1)..])) return LinkParse.Malformed;
        }
        if (!q.TryGetValue("node", out var node) || !q.TryGetValue("t", out var t) || !q.TryGetValue("link", out var link)
            || !IsId(node) || !IsKind(t) || !IsCount(link)) return LinkParse.Malformed;
        if (message.ValueKind != JsonValueKind.Object || Text(message, "type") != "link"
            || !message.TryGetProperty("client", out var client) || client.ValueKind != JsonValueKind.Object
            || Text(client, "t") != t || Text(client, "id") != node
            || Text(message, "cnonce") is not { } cnonce || !IsHex(cnonce, NonceBytes * 2)
            || Text(message, "kid") is not { } kid || !IsKid(kid)
            || Text(message, "proof") is not { } proof) return LinkParse.Malformed;
        if (!IsHex(proof, 64)) return new LinkParse(null, null, "refused");
        var offer = long.Parse(link, NumberStyles.None, CultureInfo.InvariantCulture);
        return Speaks(offer) is null
            ? new LinkParse(null, null, "version")
            : new LinkParse(new LinkAsk(offer, t, node, cnonce, kid), proof, null);
    }

    /// <summary>13.7.1: <c>Authorization: ShowBus lid=&lt;16 hex&gt;, s=&lt;64 hex&gt;</c>, exactly; null for any other header.</summary>
    public static (string Lid, string S)? ReadSessionHeader(string? authorization)
    {
        const string head = "ShowBus lid=";
        const string mid = ", s=";
        var lidChars = LidBytes * 2;
        if (authorization is null || authorization.Length != head.Length + lidChars + mid.Length + 64
            || !authorization.StartsWith(head, StringComparison.Ordinal)
            || string.CompareOrdinal(authorization, head.Length + lidChars, mid, 0, mid.Length) != 0) return null;
        var lid = authorization.Substring(head.Length, lidChars);
        var s = authorization[(head.Length + lidChars + mid.Length)..];
        return IsHex(lid, lidChars) && IsHex(s, 64) ? (lid, s) : null;
    }

    /// <summary>
    /// 13.7.1: a side call's session, checked against the S of the live, verified link its lid names
    /// (<paramref name="sessionOf"/> answers null for a lid with no such link). Another header, a lid with no live link or a
    /// wrong S is 401 with <c>relink</c>. A right S that came in clear from another machine
    /// (<paramref name="inClearFromAnother"/>: no TLS, and a remote address that is not loopback) is refused the same way and
    /// its link ended (<see cref="SessionCheck.Ends"/>), for anyone on the path read it; a wrong one in clear ends nothing — a
    /// lid is no secret, so no one ends a link by naming it.
    /// </summary>
    public static SessionCheck CheckSession(string? authorization, Func<string, string?> sessionOf, bool inClearFromAnother = false)
    {
        if (ReadSessionHeader(authorization) is not { } a) return new SessionCheck(401, true, "this call carries no session of a live link — link again", null);
        if (sessionOf(a.Lid) is not { } s || !Verify(s, a.S)) return new SessionCheck(401, true, "the link ended", null);
        return inClearFromAnother
            ? new SessionCheck(401, true, "a session credential came in clear from another machine — its link is ended; link again", a.Lid)
            : new SessionCheck(200, false, "", null);
    }

    // ---- what a connector reads ----------------------------------------------------------------------------------------

    /// <summary>13.6.4: the acceptor's answer to LINK, <c>OK [@id] LINK &lt;speaks&gt; &lt;t&gt; &lt;id&gt; &lt;snonce&gt; &lt;bind&gt;</c>, exactly; null for anything else.</summary>
    public static LinkOffer? ReadOfferLine(string line)
    {
        var w = line.Split(' ');
        if (w[0] != "OK") return null;
        var at = 1;
        if (w.Length > 1 && w[1].StartsWith('@'))
        {
            if (!WireReceipt.IsId(w[1][1..])) return null;
            at = 2;
        }
        if (w.Length != at + 6 || w[at] != "LINK" || !IsCount(w[at + 1]) || !IsKind(w[at + 2]) || !IsId(w[at + 3]) || !IsHex(w[at + 4], NonceBytes * 2) || !IsBind(w[at + 5]))
            return null;
        return new LinkOffer(long.Parse(w[at + 1], NumberStyles.None, CultureInfo.InvariantCulture), w[at + 2], w[at + 3], w[at + 4], w[at + 5]);
    }

    /// <summary>13.6.5: the acceptor's first message on a <c>link=</c> socket, <c>{"type":"link","v":1,"speaks","server":{"t","id"},"snonce","bind"}</c>; null for anything else.</summary>
    public static LinkOffer? ReadOfferMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || Text(message, "type") != "link"
            || !message.TryGetProperty("speaks", out var speaks) || speaks.ValueKind != JsonValueKind.Number || !speaks.TryGetInt64(out var n) || n < 0
            || !message.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.Object
            || Text(server, "t") is not { } t || !IsKind(t) || Text(server, "id") is not { } id || !IsId(id)
            || Text(message, "snonce") is not { } snonce || !IsHex(snonce, NonceBytes * 2)
            || Text(message, "bind") is not { } bind || !IsBind(bind)) return null;
        return new LinkOffer(n, t, id, snonce, bind);
    }

    // ---- 13.10 liveness ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 13.10.3: live while the silence is within <c>link.stale.ms</c>, stale within <c>link.drop.ms</c>, then down — the
    /// hello's own rule (<see cref="ShowBusLiveness"/>), so 6000 ms is live, 6001 stale, 12000 stale and 12001 down.
    /// </summary>
    public static LinkState Liveness(TimeSpan silence) => ShowBusLiveness.StateOf(silence) switch
    {
        ShowBusState.Live => LinkState.Live,
        ShowBusState.Stale => LinkState.Stale,
        _ => LinkState.Down,
    };

    /// <summary>13.15.1's j: uniform in [0, 1), drawn from the platform's CSPRNG. A test injects its own.</summary>
    public static double Jitter() => RandomNumberGenerator.GetInt32(1 << 30) / (double)(1 << 30);

    // ---- 13.12 ids and answers -----------------------------------------------------------------------------------------

    /// <summary>13.12.1: a request id, <c>&lt;prefix&gt;&lt;n&gt;-&lt;inst&gt;</c> — 1–4 letters, a counter from 1, the process's inst — within <see cref="WireReceipt.MaxId"/>.</summary>
    public static string RequestId(string prefix, long n, string inst)
    {
        if (prefix.Length is < 1 or > 4 || !prefix.All(char.IsAsciiLetter)) throw new ArgumentException("a request id's prefix is 1-4 letters", nameof(prefix));
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1L);
        var id = prefix + n.ToString(CultureInfo.InvariantCulture) + "-" + inst;
        return WireReceipt.IsId(id) ? id : throw new ArgumentException("a request id is at most " + WireReceipt.MaxId.ToString(CultureInfo.InvariantCulture) + " letters, digits, '-' and '_'", nameof(inst));
    }

    private const string Repeated = "answered from its record — not run again";
    private const string SecondGo = "asks for a second GO — a person's";
    private const string NoStage = "took it — this node does not say whether it ran";

    /// <summary>
    /// 13.12.3 for a line answer: <c>OK @id …</c> or <c>ERR @id …</c>, with <c>final</c> after the id on a follow-up, read as
    /// its id and its reading. Null for a line that answers nothing; an OK or ERR that echoes no id is an acceptor older
    /// than 1.1, unconfirmed.
    /// </summary>
    public static (string? Id, LinkReading Reading)? ReadLine(string line)
    {
        bool ok;
        string rest;
        if (line == "OK" || line.StartsWith("OK ", StringComparison.Ordinal))
        {
            ok = true;
            rest = line.Length > 3 ? line[3..] : "";
        }
        else if (line == "ERR" || line.StartsWith("ERR ", StringComparison.Ordinal))
        {
            ok = false;
            rest = line.Length > 4 ? line[4..] : "";
        }
        else
        {
            return null;
        }
        if (!rest.StartsWith('@')) return (null, new LinkReading(LinkOutcome.Unconfirmed, "sent — this node gives no receipts; look at its state"));
        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        var id = space < 0 ? rest[1..] : rest[1..space];
        rest = space < 0 ? "" : rest[(space + 1)..];
        var final = rest == "final" || rest.StartsWith("final ", StringComparison.Ordinal);
        if (final) rest = rest.Length > 6 ? rest[6..] : "";
        if (!ok) return (id, new LinkReading(LinkOutcome.Refused, rest, Final: final));
        if (rest.StartsWith('{') && ObjectOf(rest) is { } json) return (id, FromReceipt(json, final, LinkOutcome.Applied));
        return (id, new LinkReading(LinkOutcome.Applied, rest, Final: final));
    }

    /// <summary>13.12.3 for a JSON receipt: a <c>ws</c> receipt, an HTTP body that names a stage, a RECEIPTS answer's entry.</summary>
    public static LinkReading ReadReceipt(JsonElement receipt)
        => receipt.ValueKind == JsonValueKind.Object
            ? FromReceipt(receipt, Flag(receipt, "final"), LinkOutcome.Accepted)
            : new LinkReading(LinkOutcome.Unconfirmed, "an answer this node cannot read — it may have run");

    /// <summary>
    /// 13.12.3 for an HTTP answer — <paramref name="status"/> null when no response came after the request was written,
    /// <paramref name="retryAfterSeconds"/> a 429's <c>Retry-After</c>.
    /// </summary>
    public static LinkReading ReadHttp(int? status, string? body, int retryAfterSeconds = 0)
    {
        if (status is not { } code) return new LinkReading(LinkOutcome.Unconfirmed, "no answer — it may have run");
        if (code == 408 || code >= 500) return new LinkReading(LinkOutcome.Unconfirmed, code.ToString(CultureInfo.InvariantCulture) + " — it may have run");
        var json = body is null ? null : ObjectOf(body);
        var words = json is { } j ? BodyWords(j) : "";
        if (code == 401)
            return json is { } r && Flag(r, "relink") ? new LinkReading(LinkOutcome.Refused, words, Relink: true) : new LinkReading(LinkOutcome.Refused, words, Hold: true);
        if (code == 429)
            return new LinkReading(LinkOutcome.Refused, "rate limited — again in " + retryAfterSeconds.ToString(CultureInfo.InvariantCulture) + " s", RetryAfter: retryAfterSeconds);
        if (code is < 200 or >= 300) return new LinkReading(LinkOutcome.Refused, words.Length > 0 ? words : code.ToString(CultureInfo.InvariantCulture));
        if (json is not { } o) return new LinkReading(LinkOutcome.Accepted, NoStage);
        if (o.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.False) return new LinkReading(LinkOutcome.Refused, words, Repeat: Flag(o, "repeat"));
        if (o.TryGetProperty("stage", out _)) return ReadReceipt(o);
        if (Text(o, "msg") is { } msg && (msg == "OK" || msg.StartsWith("OK ", StringComparison.Ordinal))) return new LinkReading(LinkOutcome.Applied, words);
        return new LinkReading(LinkOutcome.Accepted, NoStage);
    }

    /// <summary>
    /// 13.12.3's rows with no answer: a command never written is not-sent (<paramref name="why"/>); one written is unconfirmed
    /// once its link dropped, or once more than <see cref="ReceiptFinal"/> passed with no final answer — one at the limit still
    /// counts, so it waits (null).
    /// </summary>
    public static LinkReading? ReadUnanswered(bool written, TimeSpan silence, bool dropped, string node, string why)
    {
        if (!written) return new LinkReading(LinkOutcome.NotSent, why);
        if (dropped) return new LinkReading(LinkOutcome.Unconfirmed, "the link dropped before " + node + " answered");
        return silence > ReceiptFinal ? new LinkReading(LinkOutcome.Unconfirmed, NoFinalWords) : null;
    }

    /// <summary>13.12.4's words for a command left with no final answer.</summary>
    internal static readonly string NoFinalWords = "no answer in " + ReceiptFinal.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s — it may have run";

    /// <summary>The outcome's word as 13.12.2 names it.</summary>
    public static string Word(this LinkOutcome outcome) => outcome switch
    {
        LinkOutcome.NotSent => "not-sent",
        LinkOutcome.Refused => "refused",
        LinkOutcome.Accepted => "accepted",
        LinkOutcome.Applied => "applied",
        _ => "unconfirmed",
    };

    /// <summary>A receipt's fields by 13.12.3's rows: refused on <c>"ok":false</c>, else by its stage, Patterns' outcome, or neither.</summary>
    private static LinkReading FromReceipt(JsonElement o, bool final, LinkOutcome stageless)
    {
        var repeat = Flag(o, "repeat");
        long? late = o.TryGetProperty("lateMs", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt64(out var lm) ? lm : null;
        LinkReading Read(LinkOutcome outcome, string words) => new(outcome, repeat ? Joined(words, Repeated) : words, Final: final, Repeat: repeat, LateMs: late);
        if (o.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) return Read(LinkOutcome.Refused, BodyWords(o));
        var stage = Text(o, "stage");
        var outcome = Text(o, "outcome");
        var effect = Text(o, "effect") ?? "";
        return (stage, outcome) switch
        {
            ("accepted", _) => Read(LinkOutcome.Accepted, effect),
            ("applied", _) => Read(LinkOutcome.Applied, effect),
            (_, "Requested") => Read(LinkOutcome.Accepted, PatternsWords(o)),
            (_, "Confirm") => Read(LinkOutcome.Accepted, Joined(PatternsWords(o), SecondGo)),
            (_, "Done") => Read(LinkOutcome.Applied, effect.Length > 0 ? effect : PatternsWords(o)),
            (null, null) => Read(stageless, stageless == LinkOutcome.Accepted ? NoStage : effect.Length > 0 ? effect : PatternsWords(o)),
            _ => Read(LinkOutcome.Accepted, outcome ?? stage ?? ""),
        };
    }

    /// <summary>A Patterns answer's words: its effect, its confirm, its words or its message.</summary>
    private static string PatternsWords(JsonElement o) => Text(o, "effect") ?? Text(o, "confirm") ?? Text(o, "words") ?? Text(o, "msg") ?? "";

    /// <summary>A body's words: its <c>msg</c> after its OK or ERR and any <c>@id</c>, else its <c>error</c>.</summary>
    private static string BodyWords(JsonElement o)
    {
        if (Text(o, "msg") is not { } msg) return Text(o, "error") ?? "";
        var rest = msg.StartsWith("OK", StringComparison.Ordinal) ? msg[2..] : msg.StartsWith("ERR", StringComparison.Ordinal) ? msg[3..] : msg;
        rest = rest.TrimStart(' ');
        if (rest.StartsWith('@')) rest = rest.IndexOf(' ', StringComparison.Ordinal) is var sp and > 0 ? rest[(sp + 1)..] : "";
        return rest;
    }

    private static string Joined(string a, string b) => a.Length == 0 ? b : a + " — " + b;

    // ---- shapes --------------------------------------------------------------------------------------------------------

    /// <summary>A node kind, §2's <c>t</c>: 1–16 of <c>[a-z0-9_-]</c>.</summary>
    public static bool IsKind(string? t) => t is { Length: >= 1 and <= 16 } && t.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    /// <summary>A node id, §2's: 6–40 of <c>[A-Za-z0-9_-]</c>.</summary>
    public static bool IsId(string? id) => id is { Length: >= 6 and <= 40 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>A kid: 16 lowercase hex, or the word <c>code</c>.</summary>
    public static bool IsKid(string? kid) => kid == "code" || IsHex(kid, KidBytes * 2);

    /// <summary>Exactly <paramref name="length"/> lowercase hex digits.</summary>
    public static bool IsHex(string? s, int length) => s is not null && s.Length == length && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A transcript's <c>bind</c>: <c>tls:sha256:</c> and 64 lowercase hex, or <c>plain</c>.</summary>
    public static bool IsBind(string? bind) => bind == "plain" || (bind is not null && bind.StartsWith(TlsBind, StringComparison.Ordinal) && IsHex(bind[TlsBind.Length..], 64));

    /// <summary>A certificate's fingerprint as T's <c>bind</c> names it.</summary>
    public static string Bind(string? fingerprint) => fingerprint is null ? "plain" : TlsBind + fingerprint;

    private static bool IsNode(string node) => node.IndexOf(':', StringComparison.Ordinal) is var i and > 0 && IsKind(node[..i]) && IsId(node[(i + 1)..]);

    private static bool IsCount(string s) => s.Length is >= 1 and <= 9 && s.All(char.IsAsciiDigit);

    // ---- bytes and JSON ------------------------------------------------------------------------------------------------

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string Mac(byte[] k, string data) => Convert.ToHexStringLower(HMACSHA256.HashData(k, Utf8(data)));

    /// <summary>A string field of a JSON object, or null.</summary>
    public static string? Text(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>A JSON object read whole, or null for anything else.</summary>
    internal static JsonElement? ObjectOf(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A connector's LINK as an acceptor reads it: the version it offers, who it says it is, its nonce and the kid it proves.</summary>
public sealed record LinkAsk(long Offer, string T, string Id, string CNonce, string Kid);

/// <summary>
/// The acceptor's answer to LINK (13.6.4's <c>OK LINK</c>, 13.6.5's first <c>link</c> message): the version it speaks, who it
/// says it is, its nonce and the certificate it says the connection shows. Nothing here is a secret.
/// </summary>
public sealed record LinkOffer(long Speaks, string T, string Id, string SNonce, string Bind);

/// <summary>What an acceptor read: the ask, the proof, or the class it refuses with.</summary>
public readonly record struct LinkParse(LinkAsk? Ask, string? Proof, string? Refused)
{
    public static LinkParse Malformed => new(null, null, "malformed");
}

/// <summary>What an acceptor answers a side call by its session (13.7.1): 200, or 401 with <c>relink</c>; <see cref="Ends"/> names the lid of a link it ends.</summary>
public readonly record struct SessionCheck(int Status, bool Relink, string Words, string? Ends);

/// <summary>13.12.2's outcomes. One only moves forward.</summary>
public enum LinkOutcome
{
    NotSent,
    Refused,
    Accepted,
    Applied,
    Unconfirmed,
}

/// <summary>
/// What a connector read from one answer (13.12.3): its outcome and words, whether it was the final follow-up or a repeat
/// answered from the acceptor's record, a 401 that asks for a relink or holds the key, a 429's wait and a late run's <c>lateMs</c>.
/// </summary>
public readonly record struct LinkReading(LinkOutcome Outcome, string Words, bool Final = false, bool Repeat = false, bool Relink = false,
    bool Hold = false, int RetryAfter = 0, long? LateMs = null);
