using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>
/// One credential the line wire's acceptor proves LINK against (Show Bus 1.3 §13.5): its kid, its K, how a verdict says it is
/// verified (<c>code</c> or <c>key</c>), its scope on 13.5.3's ladder and, for a key, the one connector <c>t:id</c> it speaks for
/// (13.5.2; null for a code, which several connectors prove). K is a secret: no log line, exception or STATE row prints it.
/// </summary>
public sealed record LinkCredential(string Kid, byte[] K, string Verified, string Scope, string? Bound)
{
    /// <summary>What a log line or an exception may say of a credential: its kid, how it verifies, its scope and binding — never K.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Kid = ").Append(Kid).Append(", Verified = ").Append(Verified).Append(", Scope = ").Append(Scope).Append(", Bound = ").Append(Bound);
        return true;
    }
}

/// <summary>
/// What the acceptor made of one <c>LINK PROOF</c> (13.6.3, 13.6.4): linked by a credential in force, with the acceptor's proof and
/// the link's id; or refused with 13.6.4's class and words. <see cref="CountKid"/> names the kid of a credential in force whose
/// proof did not verify — the one refusal the wrong-proof table counts (13.5.6); an unknown kid, a locked pair and a binding
/// refusal never name one. <see cref="EnrolK"/> is the link key both ends derive when a code proved over TLS enrols (13.5.7), null
/// otherwise: a secret, kept by the acceptor and never sent.
/// </summary>
public sealed record LinkDecision(bool Ok, string? Class, string Words, int RetrySeconds, LinkCredential? Credential, string? Proof, string? Lid, string? CountKid, byte[]? EnrolK = null)
{
    public static LinkDecision Refused(string cls, string words, int retrySeconds = 0, string? countKid = null)
        => new(false, cls, words, retrySeconds, null, null, null, countKid);

    /// <summary>What a log line or an exception may say of a decision: never the acceptor's proof (13.19), never the enrolled key.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Ok = ").Append(Ok ? "True" : "False").Append(", Class = ").Append(Class).Append(", Words = ").Append(Words)
            .Append(", RetrySeconds = ").Append(RetrySeconds.ToString(CultureInfo.InvariantCulture))
            .Append(", Credential = ").Append(Credential?.Kid).Append(", Lid = ").Append(Lid).Append(", CountKid = ").Append(CountKid);
        return true;
    }
}

/// <summary>
/// What a connection that finished LINK stands on (13.6.6): the connector's <c>t:id</c> the proof covered, the kid it proved, how
/// it is verified, its scope, the link's id (not a secret) and whether the connection is inside TLS — and, for the acceptor's list
/// (13.16.5), the name its hello gave, the address it dialled from and when the link stood. Nothing here is a secret.
/// </summary>
public sealed record LinkStanding(string Client, string Kid, string Verified, string Scope, string Lid, bool Tls)
{
    /// <summary>The name the connection's HELLO gave before its LINK; "" when it said none.</summary>
    public string Name { get; init; } = "";

    /// <summary>The address the connection came from, as every per-address record keeps it.</summary>
    public string Address { get; init; } = "";

    /// <summary>When the verdict was given.</summary>
    public DateTime SinceUtc { get; init; }
}

/// <summary>
/// The line wire's acceptor of PeerLink (Show Bus 1.3 §13.6.1–13.6.4), pure: the answer a <c>LINK</c> ask hears, the decision a
/// <c>LINK PROOF</c> gets, the verdict and the refusals. The arithmetic is <see cref="PeerLink"/>'s, not written twice. An unknown kid
/// is treated exactly like a known one until the proof — a K of its own from the CSPRNG, the same transcript and MAC, the same
/// constant-time compare, the same words — and is never counted. A pair the wrong-proof table locks is refused before its proof is
/// checked. One proof a connection: the caller hands each connection's one proof here once, and a refused one ends the connection.
/// The credentials are the ones in force: the pairing token as a code (scope <c>operate</c>, 13.5.1), and the app keys this desk
/// keeps (<see cref="AppKeys"/>): the <c>ptk_</c> keys its operator issued, each bound to one connector, and the link keys enrolled
/// from the code over TLS (13.5.7).
/// </summary>
public static class LinkAcceptor
{
    /// <summary>This desk's node kind (§2), the <c>t</c> its offer and its transcript name it by.</summary>
    public const string Kind = "patterns";

    /// <summary>The carrier T names on this desk's wire (13.6.1).</summary>
    public const string Carrier = "line";

    /// <summary>13.5.3's ladder, lowest first.</summary>
    public static IReadOnlyList<string> Scopes { get; } = new[] { "follow", "post", "drive", "operate" };

    /// <summary>Show Bus §14 <c>peerlink.wrong.proofs.per.minute</c>: wrong LINK proofs a (address, kid) pair may give in a minute (13.5.6).</summary>
    public const int WrongProofsPerMinute = 5;

    /// <summary>Show Bus §14 <c>peerlink.unpaired.per.address</c>: connections from one address that have not finished LINK (13.6.3).</summary>
    public const int UnfinishedPerAddress = 4;

    public const string AskWords = "LINK <offer> <t> <id> <cnonce> <kid>";
    public const string OneLinkWords = "one LINK a connection";
    public const string ProofBeforeAskWords = "LINK PROOF answers this desk's offer — send LINK first";
    public const string VersionWords = "this desk speaks PeerLink 1";
    public const string OpenWords = "this node asks for no key";
    public const string ProofsLocked = "too many wrong proofs for this key from this address — wait a minute";
    public const string UnfinishedWords = "too many links from this address have not finished proving themselves — wait";

    /// <summary>13.6.4's refusal, after its <c>ERR</c>: <c>LINK &lt;class&gt; — &lt;words&gt;</c>.</summary>
    public static string Refusal(string cls, string words) => "LINK " + cls + " — " + words;

    /// <summary>13.5.2's binding refusal: the key speaks for another connector. Never counted.</summary>
    public static string BindingWords(string bound)
    {
        var at = bound.IndexOf(':', StringComparison.Ordinal);
        return at > 0 ? $"this key speaks for {ShowBusPeers.KindWords(bound[..at])} {bound[(at + 1)..]}" : "this key speaks for another connector";
    }

    /// <summary>13.5.3's scope refusal, naming the link's scope and the desk whose operator raises it — "Patterns FOH" — and where.</summary>
    public static string ScopeWords(string scope, string desk)
        => $"this link's key is scope {scope} — {(desk.Length > 0 ? ShowBusPeers.KindWords(Kind) + " " + desk : "this desk")}'s operator can raise it on its Nodes page";

    /// <summary>A scope on 13.5.3's ladder.</summary>
    public static bool IsScope(string? scope) => scope is not null && Scopes.Contains(scope, StringComparer.Ordinal);

    /// <summary>A scope's place on 13.5.3's ladder, lowest 0; -1 for a word that is not on it.</summary>
    public static int Rank(string scope)
    {
        for (var i = 0; i < Scopes.Count; i++)
            if (string.Equals(Scopes[i], scope, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// 13.5.1: the pairing token as a code — K from its canonical form, kid <c>code</c>, scope <c>operate</c> — or null when no token is
    /// set or it is not of the canonical shape (such a token is never proved by LINK; the legacy proof keys it its own way).
    /// </summary>
    public static LinkCredential? Code(string? token)
        => PairingToken.Needed(token) && PeerLink.Canon(token) is { } canon ? new LinkCredential("code", PeerLink.CodeKey(canon), "code", "operate", null) : null;

    /// <summary>
    /// 13.5.1: a machine-made key (<c>&lt;prefix&gt;_</c> and 32 symbols of its alphabet) as a credential bound to the connector
    /// <paramref name="bound"/> at <paramref name="scope"/>; null for a key, a binding or a scope of another shape.
    /// </summary>
    public static LinkCredential? Key(string key, string bound, string scope)
    {
        if (!IsKeyShape(key) || !IsScope(scope) || bound.IndexOf(':', StringComparison.Ordinal) is not (var at and > 0)
            || !PeerLink.IsKind(bound[..at]) || !PeerLink.IsId(bound[(at + 1)..])) return null;
        var k = PeerLink.KeyKey(key);
        return new LinkCredential(PeerLink.Kid(k), k, "key", scope, bound);
    }

    private const string KeyAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>13.5.1's key: a lowercase prefix of 2–8 letters, <c>_</c>, and 32 symbols of <c>abcdefghijkmnpqrstuvwxyz23456789</c>.</summary>
    public static bool IsKeyShape(string? key)
    {
        if (key is null || key.IndexOf('_', StringComparison.Ordinal) is not (var at and >= 2 and <= 8)) return false;
        return key[..at].All(c => c is >= 'a' and <= 'z') && key.Length == at + 33 && key[(at + 1)..].All(c => KeyAlphabet.Contains(c, StringComparison.Ordinal));
    }

    /// <summary>The answer to a LINK ask, after its <c>OK</c> (13.6.4): <c>LINK &lt;speaks&gt; patterns &lt;id&gt; &lt;snonce&gt; &lt;bind&gt;</c>.</summary>
    public static string Offer(string ownId, string snonce, string bind)
        => string.Create(CultureInfo.InvariantCulture, $"LINK {PeerLink.Version} {Kind} {ownId} {snonce} {bind}");

    /// <summary>A fresh snonce: 16 bytes from the CSPRNG as 32 lowercase hex (13.6.1).</summary>
    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(PeerLink.NonceBytes));

    /// <summary>The credential in force whose kid is <paramref name="kid"/>, or null. Every credential is looked at, so the time does not say where it was found.</summary>
    public static LinkCredential? Of(IReadOnlyList<LinkCredential> credentials, string kid)
    {
        LinkCredential? hit = null;
        foreach (var c in credentials)
            if (string.Equals(c.Kid, kid, StringComparison.Ordinal)) hit ??= c;
        return hit;
    }

    /// <summary>
    /// The decision for one connection's one proof. <paramref name="ask"/> is the LINK this connection asked with and
    /// <paramref name="snonce"/> and <paramref name="bind"/> are the ones its offer named; <paramref name="proof"/> is null for a proof that
    /// is not 64 lowercase hex, a wrong proof (13.6.4). <paramref name="lockedFor"/> is asked once, for every kid, before its proof is
    /// checked: how many seconds its pair is still locked by the wrong-proof table, or 0 (a caller may hold the pair's place for
    /// this proof then, and settle it by <see cref="LinkDecision.CountKid"/>). With <paramref name="enrol"/>, a code verified over TLS
    /// enrols (13.5.7): the decision carries the link key both ends derive from it; over plain it never does.
    /// </summary>
    public static LinkDecision Decide(LinkAsk ask, string? proof, string ownId, string snonce, string bind, IReadOnlyList<LinkCredential> credentials, Func<string, int> lockedFor, bool enrol = false)
    {
        var credential = Of(credentials, ask.Kid);
        // The lock is asked for every kid, known or not, so the work a proof is given does not say whether its kid is held; an unknown
        // kid is never counted, so its pair never locks.
        if (lockedFor(ask.Kid) is var locked and > 0) return LinkDecision.Refused("locked", ProofsLocked, locked);
        var t = PeerLink.Transcript(Carrier, ask.Offer, PeerLink.Speaks(ask.Offer) ?? PeerLink.Version, Kind + ":" + ownId, ask.T + ":" + ask.Id, ask.Kid, snonce, ask.CNonce, bind);
        if (t is null) return LinkDecision.Refused("malformed", AskWords);
        // An unknown kid is checked against a K of its own, as long and as costly as a known one's, and refused the same way.
        var k = credential?.K ?? RandomNumberGenerator.GetBytes(32);
        var verified = PeerLink.Verify(PeerLink.ClientProof(k, t), proof);
        if (!verified || credential is null) return LinkDecision.Refused("refused", PeerLink.ProofWords, countKid: credential?.Kid);
        if (credential.Bound is { } bound && !string.Equals(bound, ask.T + ":" + ask.Id, StringComparison.Ordinal))
            return LinkDecision.Refused("binding", BindingWords(bound));
        var enrolK = enrol && credential.Verified == "code" && PeerLink.MayEnrol(ask.Kid, bind) ? PeerLink.EnrolKey(k, t) : null;
        return new LinkDecision(true, null, "", 0, credential, PeerLink.ServerProof(k, t), PeerLink.Lid(t), null, enrolK);
    }

    /// <summary>
    /// The verdict of a LINK verified, after its <c>OK</c> (13.6.4, 13.6.6): <c>paired</c> and its JSON — <c>v</c>, how it is verified, its
    /// scope, the acceptor's proof, the lid, the level, the features it uses on this link, this process's inst and its beat, and the kid
    /// a code enrolled (<c>enrolled</c>) when it did.
    /// </summary>
    public static string Verdict(LinkCredential credential, string proof, string lid, IReadOnlyList<string> can, string inst, int beatMs, string? enrolled = null)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("verified", credential.Verified);
            w.WriteString("scope", credential.Scope);
            w.WriteString("proof", proof);
            w.WriteString("lid", lid);
            w.WriteNumber("level", PeerLink.Version);
            w.WriteStartArray("can");
            foreach (var c in can) w.WriteStringValue(c);
            w.WriteEndArray();
            w.WriteString("inst", inst);
            w.WriteNumber("beatMs", beatMs);
            if (enrolled is not null) w.WriteString("enrolled", enrolled);
            w.WriteEndObject();
        }
        return "paired " + Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Whether a LINK line is the proof's form (<c>LINK PROOF …</c>), malformed or not.</summary>
    public static bool IsProofLine(string body)
    {
        var w = body.Split(' ');
        return w.Length >= 2 && w[0] == "LINK" && w[1] == "PROOF";
    }
}

/// <summary>
/// Show Bus 1.3 §13.5.6's wrong-proof table: LINK proofs that did not verify, counted per (address, kid) — the kid <c>code</c> making
/// it per address for the pairing token — in a minute that starts at a pair's first wrong proof; <see cref="Limit"/> of them lock that
/// pair for the rest of the minute. Apart from every other count this desk keeps: the legacy proof's five-and-close a connection,
/// the web remote's and the decks', so it never locks out a PIN, another key or a browser. A proof about to be checked holds a place
/// (<see cref="Reserve"/>) until it is settled, so proofs checked together never pass the limit between them. Times are milliseconds
/// on one monotonic clock, passed in. Thread-safe and bounded per minute: past <see cref="SweepAbove"/> pairs, those whose minute has
/// ended and that hold no place are dropped, at most once a minute, and no outcome changes; within one minute it holds a pair for
/// each (address, kid) that gave a wrong proof in it, and a right proof or an unknown kid leaves none.
/// </summary>
public sealed class WrongProofTable
{
    public const int SweepAbove = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<(string Address, string Kid), (int Count, int Pending, long StartMs)> _pairs = new();
    private long _nextSweepMs = long.MinValue;

    public WrongProofTable(int limit, long minuteMs = 60_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minuteMs, 1L);
        Limit = limit;
        MinuteMs = minuteMs;
    }

    /// <summary>Wrong proofs in a pair's minute that lock it for the rest of that minute.</summary>
    public int Limit { get; }

    /// <summary>The length of a pair's minute, in ms.</summary>
    public long MinuteMs { get; }

    /// <summary>The pairs held right now (bounded by the sweep).</summary>
    public int Pairs
    {
        get { lock (_gate) return _pairs.Count; }
    }

    /// <summary>The pair's wrong proofs counted in its minute at <paramref name="nowMs"/>; 0 once that minute has ended.</summary>
    public int Of(string address, string kid, long nowMs)
    {
        lock (_gate) return _pairs.TryGetValue((address, kid), out var p) ? Counted(p, nowMs) : 0;
    }

    /// <summary>
    /// A place for one proof about to be checked: 0 when it is taken (the caller then settles it once, <see cref="Settle"/>), otherwise
    /// the seconds, at least 1, until the pair's minute ends — its wrong proofs and the proofs in flight already reach the limit, so this
    /// one is refused unchecked.
    /// </summary>
    public int Reserve(string address, string kid, long nowMs)
    {
        lock (_gate)
        {
            var key = (address, kid);
            if (!_pairs.TryGetValue(key, out var p))
            {
                _pairs[key] = (0, 1, nowMs);
                return 0;
            }
            var counted = Counted(p, nowMs);
            if (counted + p.Pending >= Limit) return Left(p, nowMs);
            _pairs[key] = counted > 0 ? (p.Count, p.Pending + 1, p.StartMs) : (0, p.Pending + 1, nowMs);
            return 0;
        }
    }

    /// <summary>A reserved proof checked: <paramref name="wrong"/> counts it (the first in a new minute starts that minute); a right one only gives its place back and clears nothing.</summary>
    public void Settle(string address, string kid, long nowMs, bool wrong)
    {
        lock (_gate)
        {
            var key = (address, kid);
            if (!_pairs.TryGetValue(key, out var p)) return;
            var pending = Math.Max(0, p.Pending - 1);
            var next = wrong ? Counting(p, nowMs) with { Pending = pending } : p with { Pending = pending };
            if (next.Count == 0 && next.Pending == 0) _pairs.Remove(key);
            else _pairs[key] = next;
            Sweep(nowMs);
        }
    }

    /// <summary>How many whole seconds, rounded up, the pair stays locked at <paramref name="nowMs"/>; 0 when it is not locked.</summary>
    public int LockedFor(string address, string kid, long nowMs)
    {
        lock (_gate) return _pairs.TryGetValue((address, kid), out var p) && Counted(p, nowMs) >= Limit ? Left(p, nowMs) : 0;
    }

    private int Counted((int Count, int Pending, long StartMs) p, long nowMs) => nowMs - p.StartMs < MinuteMs ? p.Count : 0;

    private int Left((int Count, int Pending, long StartMs) p, long nowMs)
        => (int)Math.Max(1, (MinuteMs - Math.Clamp(nowMs - p.StartMs, 0, MinuteMs) + 999) / 1000);

    private (int Count, int Pending, long StartMs) Counting((int Count, int Pending, long StartMs) p, long nowMs)
        => Counted(p, nowMs) > 0 ? (Math.Min(p.Count + 1, Limit), p.Pending, p.StartMs) : (1, p.Pending, nowMs);

    private void Sweep(long nowMs)
    {
        if (_pairs.Count <= SweepAbove || nowMs < _nextSweepMs) return;
        _nextSweepMs = nowMs + MinuteMs;
        foreach (var (key, p) in _pairs.ToList())
            if (p.Pending == 0 && Counted(p, nowMs) == 0) _pairs.Remove(key);
    }
}
