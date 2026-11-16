using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// Where one app key was seen in clear (Show Bus 1.3 §13.6.9): the machine it came from, how ("web" — X-Patterns-Token on the web
/// remote's plain listener — or "wire" — plain AUTH on the Companion port), when first and last, and how many times.
/// </summary>
public sealed record KeyClearMark(string Address, string Via, DateTime FirstUtc, DateTime LastUtc, long Count);

/// <summary>
/// One app key this desk accepts on its wire (Show Bus 1.3 §13.5): its kid, its K (the only thing kept of it: SHA-256 of an issued
/// key, or the enrolled link key itself), the one connector <c>t:id</c> it speaks for, its scope on 13.5.3's ladder, how it was made
/// (<see cref="AppKeys.Issued"/> by the operator, or <see cref="AppKeys.Enrolled"/> from the pairing token over TLS), when, the
/// connector's name as it said it, when it last proved itself, and where it was seen in clear. An enrolled key carries the mark of
/// the code it was enrolled from (<see cref="CodeMark"/>), so a new code leaves it out of force whatever restarts. K is a secret: no
/// log line, exception, journal row or STATE row prints it.
/// </summary>
public sealed record AppKey(string Kid, byte[] K, string Bound, string Scope, string Made, DateTime MadeUtc, string Name, DateTime? LastUseUtc, KeyClearMark? ClearSeen, string CodeMark)
{
    /// <summary>Enrolled from the pairing token (13.5.7), not made by the operator.</summary>
    public bool Enrolled => string.Equals(Made, AppKeys.Enrolled, StringComparison.Ordinal);

    /// <summary>What LINK proves this key against: verified <c>link</c> for an enrolled key, <c>key</c> for an issued one (13.6.6).</summary>
    public LinkCredential Credential => new(Kid, K, Enrolled ? "link" : "key", Scope, Bound);

    /// <summary>What a log line or an exception may say of a key: never K, never the code's mark.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Kid = ").Append(Kid).Append(", Bound = ").Append(Bound).Append(", Scope = ").Append(Scope).Append(", Made = ").Append(Made);
        return true;
    }
}

/// <summary>
/// Show Bus 1.3 §13.5 on this desk, pure: the <c>ptk_</c> keys it mints (160 bits from the CSPRNG, shown once), the operator's
/// changes to its list of keys (make, bind, revoke), enrolment from the pairing token over TLS, a new code's revocation of the keys
/// enrolled from the old one, the seen-in-clear mark, and the kept form of the list — the bytes the App seals to this Windows user
/// (DPAPI) beside the TLS key, never in the show, a backup, the twin or STATE. Every change is a new list: the caller swaps it in.
/// </summary>
public static class AppKeys
{
    /// <summary>13.5.1: the prefix of the keys this desk issues.</summary>
    public const string Prefix = "ptk_";

    /// <summary>The kept keys' file beside the settings (sealed; never bundled, never mirrored to a twin).</summary>
    public const string FileName = "patterns.appkeys.json";

    /// <summary>Made by the operator (KEY MAKE).</summary>
    public const string Issued = "issued";

    /// <summary>Enrolled from the pairing token over TLS (13.5.7).</summary>
    public const string Enrolled = "enrolled";

    /// <summary>13.5.7: the scope an enrolled key starts at — <c>follow</c>, the contract's default; the operator raises it per key (KEY BIND).</summary>
    public const string EnrolScope = "follow";

    /// <summary>
    /// The most keys kept at once, issued and enrolled together: a bound on a list the operator reads whole on the Nodes page and in
    /// STATE, not a governor — no measurement stands behind the number this round. A make or an enrolment past it is refused with
    /// words, never a key dropped.
    /// </summary>
    public const int MaxKeys = 64;

    /// <summary>Show Bus §14 <c>peerlink.key.bits.min</c>: the random bits of a key this desk issues.</summary>
    public const int KeyBits = 160;

    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>A new key: <c>ptk_</c> and 32 symbols, 160 bits from the CSPRNG (13.5.1), 5 bits a symbol. Shown once, never kept.</summary>
    public static string NewKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(KeyBits / 8);
        var sb = new StringBuilder(Prefix, Prefix.Length + 32);
        var acc = 0;
        var bits = 0;
        foreach (var b in bytes)
        {
            acc = (acc << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(acc >> bits) & 31]);
            }
        }
        CryptographicOperations.ZeroMemory(bytes);
        return sb.ToString();
    }

    /// <summary>A connector as a key is bound to it: <c>t:id</c>, its kind in lower case, both of §2's shapes; null for anything else.</summary>
    public static string? Connector(string? words)
    {
        var w = (words ?? "").Trim();
        var at = w.IndexOf(':', StringComparison.Ordinal);
        if (at <= 0) return null;
        var t = w[..at].ToLowerInvariant();
        var id = w[(at + 1)..];
        return PeerLink.IsKind(t) && PeerLink.IsId(id) ? t + ":" + id : null;
    }

    /// <summary>"Stagewatch 3f9c2a1b7d0e" for a binding <c>main:3f9c2a1b7d0e</c>.</summary>
    public static string ConnectorWords(string bound)
    {
        var at = bound.IndexOf(':', StringComparison.Ordinal);
        return at > 0 ? ShowBusPeers.KindWords(bound[..at]) + " " + bound[(at + 1)..] : bound;
    }

    /// <summary>
    /// The mark an enrolled key keeps of the code it came from: a keyed hash under this list's own random salt, so the file never
    /// carries a form of the token a guess could be checked against without the salt beside it, and the salt is sealed with it.
    /// "" when no code is set or the token is not of 13.5.1's shape.
    /// </summary>
    public static string CodeMark(byte[] salt, string? token)
        => LinkAcceptor.Code(token) is { } code ? Convert.ToHexStringLower(HMACSHA256.HashData(salt, code.K)) : "";

    /// <summary>The keys in force for <paramref name="codeMark"/> (the mark of the token set now): an enrolled key from another code is not.</summary>
    public static IReadOnlyList<AppKey> InForce(IReadOnlyList<AppKey> keys, string codeMark)
        => keys.Where(k => !k.Enrolled || (codeMark.Length > 0 && string.Equals(k.CodeMark, codeMark, StringComparison.Ordinal))).ToList();

    /// <summary>
    /// KEY MAKE: a new key for <paramref name="bound"/> at <paramref name="scope"/>. The list with it added, the key itself — to be shown
    /// once and kept nowhere — and the words; or the list unchanged, no key and the reason it is refused.
    /// </summary>
    public static (IReadOnlyList<AppKey> Keys, string? Secret, AppKey? Made, string Words) Make(IReadOnlyList<AppKey> keys, string bound, string scope, DateTime utcNow)
    {
        if (Connector(bound) is not { } who) return (keys, null, null, $"'{bound}' is not a connector — name it as kind:id (main:3f9c2a1b7d0e for a Stagewatch, playmap:… for a PlayMap), as its hello says it");
        if (!LinkAcceptor.IsScope(scope)) return (keys, null, null, $"'{scope}' is not a scope — follow, post, drive or operate");
        if (keys.Count >= MaxKeys) return (keys, null, null, $"this desk keeps at most {MaxKeys} app keys — revoke one first");
        string secret;
        LinkCredential? credential;
        do
        {
            secret = NewKey();
            credential = LinkAcceptor.Key(secret, who, scope);
        }
        while (credential is null || keys.Any(k => k.Kid == credential.Kid));
        var key = new AppKey(credential.Kid, credential.K, who, scope, Issued, utcNow, "", null, null, "");
        return (keys.Append(key).ToList(), secret, key,
            $"key {key.Kid} made for {ConnectorWords(who)}, scope {scope} — shown once on this desk's Nodes page; give it to that connector and nobody else");
    }

    /// <summary>
    /// KEY BIND: the key <paramref name="kid"/> speaks for <paramref name="bound"/> from now on, at <paramref name="scope"/> (its own
    /// when null). A change that would change nothing is refused with the reason. The list after it, whether its live links must end
    /// (its binding moved, or its scope fell), and the words.
    /// </summary>
    public static (IReadOnlyList<AppKey> Keys, bool Ok, bool EndsLinks, string Words) Bind(IReadOnlyList<AppKey> keys, string kid, string bound, string? scope)
    {
        if (keys.FirstOrDefault(k => k.Kid == kid) is not { } key) return (keys, false, false, $"no app key {kid} is kept here");
        if (Connector(bound) is not { } who) return (keys, false, false, $"'{bound}' is not a connector — name it as kind:id, as its hello says it");
        var to = scope ?? key.Scope;
        if (!LinkAcceptor.IsScope(to)) return (keys, false, false, $"'{scope}' is not a scope — follow, post, drive or operate");
        if (key.Bound == who && key.Scope == to) return (keys, false, false, $"key {kid} speaks for {ConnectorWords(who)} at scope {to} already");
        var moved = key.Bound != who;
        // An enrolled key's K lives only at the connector that enrolled it: no other connector could link with it, so it is never moved.
        if (moved && key.Enrolled) return (keys, false, false, $"key {kid} was enrolled by {ConnectorWords(key.Bound)} and only it holds that key — its scope can change here, its connector cannot; make a key for {ConnectorWords(who)} instead (KEY MAKE)");
        var lowered = LinkAcceptor.Rank(to) < LinkAcceptor.Rank(key.Scope);
        var next = key with { Bound = who, Scope = to };
        var words = moved
            ? $"key {kid} speaks for {ConnectorWords(who)} now, scope {to} — {ConnectorWords(key.Bound)} can no longer link with it"
            : $"key {kid} is scope {to} now (was {key.Scope})";
        return (keys.Select(k => k.Kid == kid ? next : k).ToList(), true, moved || lowered, words);
    }

    /// <summary>KEY REVOKE: the key leaves the list; its live links end at once (13.5.5). Refused with the reason when no such key is kept.</summary>
    public static (IReadOnlyList<AppKey> Keys, bool Ok, string Words) Revoke(IReadOnlyList<AppKey> keys, string kid)
    {
        if (keys.FirstOrDefault(k => k.Kid == kid) is not { } key) return (keys, false, $"no app key {kid} is kept here");
        return (keys.Where(k => k.Kid != kid).ToList(), true, $"key {kid} ({ConnectorWords(key.Bound)}, {key.Made}) revoked — a link that used it is refused now");
    }

    /// <summary>
    /// 13.5.7: a code proved over TLS enrols <paramref name="k"/> for <paramref name="bound"/> — the key enrolled before for that
    /// connector gives way to it, and the new one takes its scope when it came from the same code (a raise the operator made with KEY
    /// BIND outlives a connector proving the code again), else <see cref="EnrolScope"/>. Null, the list unchanged, past
    /// <see cref="MaxKeys"/>.
    /// </summary>
    public static (IReadOnlyList<AppKey> Keys, AppKey? Enrolled) Enrol(IReadOnlyList<AppKey> keys, byte[] k, string bound, string name, string codeMark, DateTime utcNow)
    {
        var kept = keys.Where(x => !(x.Enrolled && x.Bound == bound)).ToList();
        if (kept.Count >= MaxKeys || Connector(bound) is null || codeMark.Length == 0) return (keys, null);
        var scope = keys.FirstOrDefault(x => x.Enrolled && x.Bound == bound && string.Equals(x.CodeMark, codeMark, StringComparison.Ordinal))?.Scope ?? EnrolScope;
        var key = new AppKey(PeerLink.Kid(k), k, bound, scope, Enrolled, utcNow, Clip(name, 40), null, null, codeMark);
        kept.Add(key);
        return (kept, key);
    }

    /// <summary>13.5.5: a new code revokes every key enrolled from the old one: the list without them, and the kids that went.</summary>
    public static (IReadOnlyList<AppKey> Keys, IReadOnlyList<string> Revoked) RevokeEnrolled(IReadOnlyList<AppKey> keys, string codeMark)
    {
        var gone = keys.Where(k => k.Enrolled && (codeMark.Length == 0 || k.CodeMark != codeMark)).Select(k => k.Kid).ToList();
        return gone.Count == 0 ? (keys, gone) : (keys.Where(k => !gone.Contains(k.Kid)).ToList(), gone);
    }

    /// <summary>The kept key whose K a secret presented in clear makes, or null (an issued key's K is SHA-256 of the key itself).</summary>
    public static AppKey? OfPresented(IReadOnlyList<AppKey> keys, string? secret)
    {
        var s = (secret ?? "").Trim();
        if (!LinkAcceptor.IsKeyShape(s)) return null;
        var k = PeerLink.KeyKey(s);
        AppKey? hit = null;
        foreach (var key in keys)
            if (!key.Enrolled && CryptographicOperations.FixedTimeEquals(key.K, k)) hit ??= key;
        return hit;
    }

    /// <summary>
    /// 13.6.9: <paramref name="kid"/> was seen in clear from <paramref name="address"/>. The list with the mark (the first sighting from
    /// any address starts it; later ones move its last time and count), and whether this is that key's first mark.
    /// </summary>
    public static (IReadOnlyList<AppKey> Keys, bool First) MarkClear(IReadOnlyList<AppKey> keys, string kid, string address, string via, DateTime utcNow)
    {
        if (keys.FirstOrDefault(k => k.Kid == kid) is not { } key) return (keys, false);
        var how = via is "web" or "wire" ? via : "web";
        var mark = key.ClearSeen is { } m
            ? m with { LastUtc = utcNow, Count = m.Count + 1, Via = m.Via.Split(',').Contains(how) ? m.Via : m.Via + "," + how }
            : new KeyClearMark(address, how, utcNow, utcNow, 1);
        return (keys.Select(k => k.Kid == kid ? k with { ClearSeen = mark } : k).ToList(), key.ClearSeen is null);
    }

    /// <summary>"from 10.0.0.7 (plain AUTH on the Companion port) — replace this key: KEY REVOKE, then KEY MAKE".</summary>
    public static string ClearWords(KeyClearMark mark)
        => $"seen in clear from {mark.Address} ({string.Join(" and ", mark.Via.Split(',').Select(TokenClearSightings.HowWords))}), {mark.Count.ToString(CultureInfo.InvariantCulture)} time{(mark.Count == 1 ? "" : "s")} — replace this key: KEY REVOKE, then KEY MAKE";

    /// <summary>A key's row as the Nodes page, STATE and the brief say it — its kid, connector, scope, how and when made, last use and mark; never K.</summary>
    public static string Words(AppKey key)
    {
        var sb = new StringBuilder();
        sb.Append(key.Kid).Append(" · ").Append(ConnectorWords(key.Bound));
        if (key.Name.Length > 0) sb.Append(" (").Append(key.Name).Append(')');
        sb.Append(" · scope ").Append(key.Scope).Append(" · ").Append(key.Made).Append(' ').Append(Stamp(key.MadeUtc));
        sb.Append(" · ").Append(key.LastUseUtc is { } used ? "last linked " + Stamp(used) : "never linked yet");
        if (key.ClearSeen is { } mark) sb.Append(" · △ ").Append(ClearWords(mark));
        return sb.ToString();
    }

    /// <summary>The list in one line for the brief and the Super Check: "2 app keys (1 issued, 1 enrolled); 1 seen in clear"; "" with none.</summary>
    public static string Summary(IReadOnlyList<AppKey> keys)
    {
        if (keys.Count == 0) return "";
        var enrolled = keys.Count(k => k.Enrolled);
        var clear = keys.Count(k => k.ClearSeen is not null);
        return $"{keys.Count} app key{(keys.Count == 1 ? "" : "s")} ({keys.Count - enrolled} issued, {enrolled} enrolled)"
               + (clear > 0 ? $"; {clear} seen in clear — replace {(clear == 1 ? "it" : "them")}" : "");
    }

    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static string Clip(string words, int max)
    {
        var w = new string((words ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return w.Length <= max ? w : w[..max];
    }

    /// <summary>
    /// The list as the bytes the App seals: version 1, the salt the code marks are keyed with, and each key with its K in hex — never
    /// a key itself. Only <see cref="Read"/> reads it, and only from inside the seal.
    /// </summary>
    public static byte[] Write(IReadOnlyList<AppKey> keys, byte[] salt)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("salt", Convert.ToHexStringLower(salt));
            w.WriteStartArray("keys");
            foreach (var k in keys)
            {
                w.WriteStartObject();
                w.WriteString("kid", k.Kid);
                w.WriteString("k", Convert.ToHexStringLower(k.K));
                w.WriteString("bound", k.Bound);
                w.WriteString("scope", k.Scope);
                w.WriteString("made", k.Made);
                w.WriteString("madeUtc", k.MadeUtc.ToString("o", CultureInfo.InvariantCulture));
                w.WriteString("name", k.Name);
                w.WriteString("lastUseUtc", k.LastUseUtc?.ToString("o", CultureInfo.InvariantCulture) ?? "");
                w.WriteString("codeMark", k.CodeMark);
                if (k.ClearSeen is { } m)
                {
                    w.WriteStartObject("clearSeen");
                    w.WriteString("address", m.Address);
                    w.WriteString("via", m.Via);
                    w.WriteString("first", m.FirstUtc.ToString("o", CultureInfo.InvariantCulture));
                    w.WriteString("last", m.LastUtc.ToString("o", CultureInfo.InvariantCulture));
                    w.WriteNumber("count", m.Count);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// The list <see cref="Write"/> wrote, and its salt; fail closed: a row whose kid is not its K's, whose binding, scope or way of
    /// being made is not one this build knows, is left out (and counted in <c>Dropped</c>), never defaulted; a document that is not
    /// one at all reads as null.
    /// </summary>
    public static (IReadOnlyList<AppKey> Keys, byte[] Salt, int Dropped)? Read(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1) return null;
            if (PeerLink.Text(r, "salt") is not { Length: 64 } saltHex || !PeerLink.IsHex(saltHex, 64)) return null;
            var keys = new List<AppKey>();
            var dropped = 0;
            if (r.TryGetProperty("keys", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in list.EnumerateArray())
                {
                    if (Row(e) is { } key && keys.Count < MaxKeys && keys.All(k => k.Kid != key.Kid)) keys.Add(key);
                    else dropped++;
                }
            }
            return (keys, Convert.FromHexString(saltHex), dropped);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static AppKey? Row(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var kHex = PeerLink.Text(e, "k");
        if (kHex is null || !PeerLink.IsHex(kHex, 64)) return null;
        var k = Convert.FromHexString(kHex);
        var kid = PeerLink.Text(e, "kid");
        if (kid != PeerLink.Kid(k)) return null;
        if (Connector(PeerLink.Text(e, "bound")) is not { } bound) return null;
        var scope = PeerLink.Text(e, "scope");
        if (!LinkAcceptor.IsScope(scope)) return null;
        var made = PeerLink.Text(e, "made");
        if (made is not (Issued or Enrolled)) return null;
        var codeMark = PeerLink.Text(e, "codeMark") ?? "";
        if (made == Enrolled && !PeerLink.IsHex(codeMark, 64)) return null;
        var madeUtc = When(PeerLink.Text(e, "madeUtc")) ?? DateTime.MinValue;
        KeyClearMark? mark = null;
        if (e.TryGetProperty("clearSeen", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            var count = m.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var n) ? n : 1;
            mark = new KeyClearMark(PeerLink.Text(m, "address") ?? "", PeerLink.Text(m, "via") ?? "web",
                When(PeerLink.Text(m, "first")) ?? madeUtc, When(PeerLink.Text(m, "last")) ?? madeUtc, Math.Max(1, count));
        }
        return new AppKey(kid, k, bound, scope!, made, madeUtc, Clip(PeerLink.Text(e, "name") ?? "", 40), When(PeerLink.Text(e, "lastUseUtc")), mark, made == Enrolled ? codeMark : "");
    }

    private static DateTime? When(string? text)
        => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : null;
}

/// <summary>
/// The owner's P-scopes on this desk's verbs (Show Bus 1.3 §13.5.3, §8.2's row): a key's link at <c>follow</c> reads — STATE,
/// STATUS, CONTEXT, EYE and every other read — and subscribes (RECORD); at <c>post</c> it adds its own context (CONTEXT POST, Show
/// Bus 1.3 §6; proposals and their results this wire has none of yet); at <c>drive</c> it adds the verbs a playout desk
/// runs — clip transport (VIDEO), CUE GO and CUE STANDBY, the playlist's section (SECTION) and STAGE MESSAGE; at <c>operate</c>
/// everything, as the operator. No OUTPUTS, TAKE or CUT, SCREEN or setting is a line a key below <c>operate</c> may send: every
/// action and line kind not named below is <c>operate</c>'s, so a verb the wire gains later waits for <c>operate</c> until it is
/// named here. What <c>drive</c> reaches through the operator's own content is the operator's: CUE GO runs the cue as built, whatever
/// steps were put in it (an OUTPUTS, TAKE or SCREEN step among them), and SECTION puts that part of the playlist on air. Pure.
/// </summary>
public static class AppKeyScopes
{
    /// <summary>The verbs a playout desk runs (§8.2 <c>drive</c>).</summary>
    public static IReadOnlySet<ShowActionKind> DriveKinds { get; } = new HashSet<ShowActionKind>
    {
        ShowActionKind.VideoPlay, ShowActionKind.VideoPause, ShowActionKind.VideoStop, ShowActionKind.VideoSeek,
        ShowActionKind.VideoRestart, ShowActionKind.VideoToEnd,
        ShowActionKind.CueGo, ShowActionKind.CueStandby,
        ShowActionKind.PlaylistPart,
        ShowActionKind.StageMessage,
    };

    /// <summary>
    /// The line kinds a <c>follow</c> link runs, each by name: the reads (STATUS, CONTEXT, EYE and every other status), RECORD (a
    /// subscription to the action feed), and the handshake's words and the unknown line, which the port answers and nothing runs. A
    /// kind the wire gains later is in neither list and waits for <c>operate</c> until it is named here.
    /// </summary>
    public static IReadOnlySet<RemoteCommandKind> FollowKinds { get; } = new HashSet<RemoteCommandKind>
    {
        RemoteCommandKind.Unknown, RemoteCommandKind.Ping, RemoteCommandKind.Status, RemoteCommandKind.Hello, RemoteCommandKind.Auth,
        RemoteCommandKind.AuthProof, RemoteCommandKind.Nonce, RemoteCommandKind.Context, RemoteCommandKind.CueList, RemoteCommandKind.TwinStatus,
        RemoteCommandKind.ShowLockStatus, RemoteCommandKind.CalibrationStatus, RemoteCommandKind.NodesStatus, RemoteCommandKind.StageStatus,
        RemoteCommandKind.ArcadeStatus, RemoteCommandKind.PlayStatus, RemoteCommandKind.RigDayStatus, RemoteCommandKind.Menu,
        RemoteCommandKind.ScreenSignal, RemoteCommandKind.ScreenEdid, RemoteCommandKind.RigStatus, RemoteCommandKind.CommissionStatus,
        RemoteCommandKind.EyeStatus, RemoteCommandKind.EyeAt, RemoteCommandKind.MidiStatus, RemoteCommandKind.NavStatus,
        RemoteCommandKind.Record, RemoteCommandKind.StagewatchStatus, RemoteCommandKind.Link,
    };

    /// <summary>The line kinds a <c>post</c> link adds to <see cref="FollowKinds"/>, each by name: CONTEXT POST, its own context (§6).</summary>
    public static IReadOnlySet<RemoteCommandKind> PostKinds { get; } = new HashSet<RemoteCommandKind> { RemoteCommandKind.ContextPost };

    /// <summary>
    /// The line kinds only an <c>operate</c> link runs, each by name: every action not in <see cref="DriveKinds"/>, NAV DECK (a deck's
    /// whereabouts written into STATE — a deck's, not an app's) and ASSISTANT ASK (the desk's assistant and its key, asked on the
    /// desk's account).
    /// </summary>
    public static IReadOnlySet<RemoteCommandKind> OperateKinds { get; } = new HashSet<RemoteCommandKind>
    {
        RemoteCommandKind.Action, RemoteCommandKind.NavDeck, RemoteCommandKind.AssistantAsk,
    };

    /// <summary>The lowest scope that runs <paramref name="cmd"/>: <c>follow</c>, <c>post</c> (CONTEXT POST alone), <c>drive</c> or <c>operate</c>.</summary>
    public static string Needed(RemoteCommand cmd)
    {
        if (FollowKinds.Contains(cmd.Kind)) return "follow";
        if (PostKinds.Contains(cmd.Kind)) return "post";
        if (cmd.Kind == RemoteCommandKind.Action && DriveKinds.Contains(cmd.Action.Kind)) return "drive";
        return "operate";
    }

    /// <summary>Whether a link at <paramref name="scope"/> may run <paramref name="cmd"/>.</summary>
    public static bool Allows(string scope, RemoteCommand cmd) => LinkAcceptor.Rank(scope) >= LinkAcceptor.Rank(Needed(cmd));
}

/// <summary>
/// KEY MAKE, KEY BIND and KEY REVOKE as the wire spells them: <c>KEY MAKE &lt;t:id&gt; [&lt;scope&gt;]</c> (follow when none is
/// named), <c>KEY BIND &lt;kid&gt; &lt;t:id&gt; [&lt;scope&gt;]</c> and <c>KEY REVOKE &lt;kid&gt;</c>. They are this desk's operator's,
/// on its Nodes page and its right-click menus: a line names them only to be refused in words (<see cref="DeskOnlyWords"/>), a key is
/// shown once on the desk and never rides a line, and no deck, peer, running order or assistant makes, binds or revokes a key. Any
/// other word after KEY is no line of the wire. Pure.
/// </summary>
public static class KeyVerbs
{
    /// <summary>What every surface but the desk hears for the KEY verbs.</summary>
    public const string DeskOnlyWords = "KEY MAKE, KEY BIND and KEY REVOKE are this desk's operator's, on its Nodes page — never a deck's, a peer's, a running order's or an assistant's; a key is shown once there and never rides a line";

    /// <summary>The words after KEY as one of the three verbs, or null when they are none.</summary>
    public static RemoteCommand? Parse(string arg)
    {
        var w = (arg ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (w.Length == 0) return null;
        switch (w[0].ToUpperInvariant())
        {
            case "MAKE" when w.Length is 2 or 3:
                return Act(ShowActionKind.AppKeyMake, w[1], w.Length == 3 ? w[2].ToLowerInvariant() : "follow");
            case "BIND" when w.Length is 3 or 4 && PeerLink.IsHex(w[1], PeerLink.KidBytes * 2):
                return Act(ShowActionKind.AppKeyBind, w[1], w.Length == 4 ? w[2] + " " + w[3].ToLowerInvariant() : w[2]);
            case "REVOKE" when w.Length == 2 && PeerLink.IsHex(w[1], PeerLink.KidBytes * 2):
                return Act(ShowActionKind.AppKeyRevoke, w[1]);
            default:
                return null;
        }

        static RemoteCommand Act(ShowActionKind kind, string target, string value = "") => RemoteCommand.Of(new ShowAction(kind, target, value));
    }

    /// <summary>KEY BIND's value split: the connector and the scope named, or null for the key's own.</summary>
    public static (string Bound, string? Scope) BindValue(string value)
    {
        var w = (value ?? "").Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (w.Length > 0 ? w[0] : "", w.Length > 1 ? w[1] : null);
    }

    /// <summary>The assistant's brief beside the keys: the verbs are the operator's, named in words, never proposed.</summary>
    public const string BriefWords = "app keys are the operator's: KEY MAKE, KEY BIND and KEY REVOKE on the Nodes page and its right-click menus only — never proposed, never on the wire or a deck";
}
