using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Patterns.Core.Services;

/// <summary>
/// Round 87 (Show Bus 1.1, docs/SHOWBUS.md §4): one rule of liveness for every link to another show app —
/// a beat at least every 2 s, stale after 6 s of silence, dropped and redialled after 12 s, the redial
/// backing off from half a second to five with jitter. The twin keeps its own constants (a twin is
/// Patterns-to-Patterns and has its own reasons); these are the ones another app can rely on.
/// </summary>
public static class ShowBusLiveness
{
    public static readonly TimeSpan Beat = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan DropAfter = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan RedialFirst = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan RedialMost = TimeSpan.FromSeconds(5);

    /// <summary>A peer heard this long ago is forgotten: off the Eye and the NODES list.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The wait before redial number <paramref name="attempt"/> (0-based): 0.5 s doubling to 5 s, with up to a
    /// fifth of jitter either way from <paramref name="jitter01"/> (0..1) so a room of clients does not redial in step.
    /// </summary>
    public static TimeSpan Redial(int attempt, double jitter01)
    {
        var ms = RedialFirst.TotalMilliseconds * Math.Pow(2, Math.Clamp(attempt, 0, 10));
        ms = Math.Min(ms, RedialMost.TotalMilliseconds);
        var j = 1 + (Math.Clamp(jitter01, 0, 1) - 0.5) * 0.4;
        return TimeSpan.FromMilliseconds(Math.Round(ms * j));
    }

    /// <summary>Live, stale or gone, from the time since the last word.</summary>
    public static ShowBusState StateOf(TimeSpan silent) => silent <= StaleAfter ? ShowBusState.Live : silent <= DropAfter ? ShowBusState.Stale : ShowBusState.Gone;
}

/// <summary>Where a peer or a link stands: heard within 6 s, quiet up to 12 s, quiet past it.</summary>
public enum ShowBusState
{
    Live,
    Stale,
    Gone,
}

/// <summary>The ports a hello names — only those its node serves (0 = not served).</summary>
public sealed record ShowBusPorts
{
    [JsonPropertyName("http")] public int Http { get; init; }
    [JsonPropertyName("wire")] public int Wire { get; init; }
    [JsonPropertyName("ws")] public int Ws { get; init; }
    [JsonPropertyName("osc")] public int Osc { get; init; }
    /// <summary>Round 88 (Show Bus 1.2 §10): where TLS answers — the same port as http when the node sniffs the first byte.</summary>
    [JsonPropertyName("https")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Https { get; init; }
}

/// <summary>
/// Round 87: another show app heard on the hello (UDP 9928) — what it is, where it says its doors are, what it
/// can do, and when it was last heard. Nothing about the show: a hello never carries any.
/// </summary>
public sealed record ShowBusPeer(string Kind, string Id, string Name, string Host, string Address, ShowBusPorts Ports, IReadOnlyList<string> Caps, int Proto, long ClockMs, DateTime HeardUtc)
{
    /// <summary>The key a peer is kept by: its id and its first door, so two desks of one install on one machine stay two.</summary>
    public string Key => $"{Id}:{(Ports.Wire > 0 ? Ports.Wire : Ports.Http > 0 ? Ports.Http : Ports.Ws)}";

    public ShowBusState StateAt(DateTime utcNow) => ShowBusLiveness.StateOf(utcNow - HeardUtc);

    /// <summary>"Stagewatch", "Patterns", "AGMessage", "Slides agent" — the kind as the operator calls it.</summary>
    public string KindWords => ShowBusPeers.KindWords(Kind);

    /// <summary>Round 88 (Show Bus 1.2 §10): the fingerprint of the certificate the peer says it serves ("" when none).</summary>
    public string Tls { get; init; } = "";

    /// <summary>Round 88: the peer offers TLS and says which certificate.</summary>
    public bool OffersTls => Tls.Length > 0;

    /// <summary>The http door to dial, or null when it names none.</summary>
    public string? HttpBase => Ports.Http > 0 && Address.Length > 0 ? $"http://{Wrap(Address)}:{Ports.Http}" : null;

    private static string Wrap(string address) => IPAddress.TryParse(address, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address;
}

/// <summary>
/// Round 87: every other show app heard on UDP 9928, kept by the listener thread and read by the desk's tick —
/// the NODES list, the Eye's CONTROL plane, the context's peers and the Stagewatch link's discovery read the same
/// table. Pure: the caller hands in the bytes, the address they came from and the time.
/// </summary>
public sealed class ShowBusPeers
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ShowBusPeer> _peers = new(StringComparer.Ordinal);
    private long _heard;

    /// <summary>Hellos taken in since start.</summary>
    public long Heard => Interlocked.Read(ref _heard);

    /// <summary>
    /// One datagram: kept when it is a hello and not this process's own (<paramref name="selfInstance"/> is the
    /// "inst" this process says). False for anything else — a stray packet, a newer format that is not JSON.
    /// </summary>
    public bool Hear(ReadOnlySpan<byte> datagram, string fromAddress, DateTime utcNow, string selfInstance)
    {
        var peer = Parse(datagram, fromAddress, utcNow, out var instance);
        if (peer is null) return false;
        if (instance.Length > 0 && instance == selfInstance) return false;
        lock (_gate) _peers[peer.Key] = peer;
        Interlocked.Increment(ref _heard);
        return true;
    }

    /// <summary>Every peer heard within <see cref="ShowBusLiveness.ForgetAfter"/>, by kind and name; older ones are dropped.</summary>
    public IReadOnlyList<ShowBusPeer> Snapshot(DateTime utcNow)
    {
        lock (_gate)
        {
            foreach (var old in _peers.Where(p => utcNow - p.Value.HeardUtc > ShowBusLiveness.ForgetAfter).Select(p => p.Key).ToList()) _peers.Remove(old);
            return _peers.Values.OrderBy(p => p.Kind, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>A hello as a peer, or null when the bytes are not one (no "t", no "id", not JSON, too big).</summary>
    public static ShowBusPeer? Parse(ReadOnlySpan<byte> datagram, string fromAddress, DateTime utcNow, out string instance)
    {
        instance = "";
        if (datagram.Length is 0 or > 4096) return null;
        try
        {
            using var doc = JsonDocument.Parse(datagram.ToArray());
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var kind = Str(r, "t");
            var id = Str(r, "id");
            if (kind.Length is 0 or > 32 || id.Length is 0 or > 64) return null;
            instance = Str(r, "inst");
            var ports = new ShowBusPorts();
            if (r.TryGetProperty("ports", out var p) && p.ValueKind == JsonValueKind.Object)
                ports = new ShowBusPorts { Http = Int(p, "http"), Wire = Int(p, "wire"), Ws = Int(p, "ws"), Osc = Int(p, "osc"), Https = Int(p, "https") };
            // A hello from before 1.1 named its doors at the top: Patterns' "web" and "wire", Stagewatch's "port".
            if (ports.Http == 0 && ports.Wire == 0 && ports.Ws == 0)
                ports = new ShowBusPorts { Http = Int(r, "web") is > 0 and var w ? w : Int(r, "port"), Wire = Int(r, "wire") };
            var caps = r.TryGetProperty("caps", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").Where(s => s.Length is > 0 and <= 32).Take(32).ToList()
                : new List<string>();
            var name = Str(r, "name");
            var host = Str(r, "host");
            if (host.Length == 0) host = Str(r, "machine");
            return new ShowBusPeer(kind, id, OneLine(name, 64), OneLine(host, 64), fromAddress, ports, caps, Int(r, "proto"), Long(r, "clock"), utcNow)
            {
                Tls = TlsFingerprint.Normal(Str(r, "tls")),
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>The kind as the operator calls it.</summary>
    public static string KindWords(string kind) => kind switch
    {
        "main" => "Stagewatch",
        "agent" => "Slides agent",
        "patterns" => "Patterns",
        "messenger" => "AGMessage",
        _ => kind.Length > 0 ? char.ToUpperInvariant(kind[0]) + kind[1..] : "App",
    };

    private static string Str(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    private static int Int(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i is > 0 and <= 65535 ? i : 0;

    private static long Long(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) && l > 0 ? l : 0;

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>
    /// A name a hello gives, as every reader shows it: one line. A hello is unauthenticated, and its name reaches the log,
    /// the status line and the Eye, so a control or line-break character is a space — no hello writes a line of its own.
    /// </summary>
    private static string OneLine(string s, int max)
    {
        var line = new string(s.Select(c => (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) ? ' ' : c).ToArray()).Trim();
        return Trim(line, max);
    }
}

/// <summary>
/// Round 87: this install's id on the show network — eight hex characters made once and kept beside the settings,
/// so a renamed desk is still the same node to every other app. A file that cannot be read or written leaves a
/// fresh id for this run (the network sees a new node; nothing else breaks).
/// </summary>
public static class ShowBusIdentity
{
    public const string FileName = "patterns.node-id";

    public static string LoadOrCreate(string folder)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            if (File.Exists(path))
            {
                var kept = File.ReadAllText(path).Trim();
                if (IsId(kept)) return kept;
            }
        }
        catch (IOException)
        {
            // unreadable: a fresh id below
        }
        catch (UnauthorizedAccessException)
        {
            // unreadable: a fresh id below
        }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        try
        {
            Directory.CreateDirectory(folder);
            AtomicFile.WriteAllText(path, id);
        }
        catch (IOException ex)
        {
            Log.Warn("The show network id could not be kept — this run has a fresh one.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn("The show network id could not be kept — this run has a fresh one.", ex);
        }
        return id;
    }

    public static bool IsId(string s) => s.Length == 8 && s.All(Uri.IsHexDigit);
}

/// <summary>
/// Round 87 (Show Bus 1.1 §3): a request id on a wire line and the receipt that names it. A line "@a1 VIDEO END 10"
/// is the line "VIDEO END 10" with the id a1; its answer is "OK @a1 …" or "ERR @a1 …" — the sender matches the
/// answer to the line it sent, so a refusal is never mistaken for another line's. A line without an id is answered
/// as before. An id is 1–32 of [A-Za-z0-9_-]; a line that starts with "@" and a malformed id is refused, never run.
/// </summary>
public static class WireReceipt
{
    public const int MaxId = 32;

    /// <summary>The id and the line without it. <paramref name="malformed"/> is true for "@" followed by something that is not an id.</summary>
    public static (string Id, string Line) Split(string line, out bool malformed)
    {
        malformed = false;
        var s = (line ?? "").TrimStart();
        if (s.Length == 0 || s[0] != '@') return ("", line ?? "");
        var space = s.IndexOf(' ');
        var id = space < 0 ? s[1..] : s[1..space];
        var rest = space < 0 ? "" : s[(space + 1)..].Trim();
        if (!IsId(id) || rest.Length == 0)
        {
            malformed = true;
            return ("", rest);
        }
        return (id, rest);
    }

    public static bool IsId(string id) => id.Length is > 0 and <= MaxId && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-');

    /// <summary>The answer with the id after its first word: "OK paired" → "OK @a1 paired", "ERR x" → "ERR @a1 x". Lines that are not answers (a STATE push) are untouched.</summary>
    public static string Tag(string reply, string id)
    {
        if (id.Length == 0) return reply;
        if (reply == "OK") return "OK @" + id;
        if (reply.StartsWith("OK ", StringComparison.Ordinal)) return "OK @" + id + reply[2..];
        if (reply == "ERR") return "ERR @" + id;
        if (reply.StartsWith("ERR ", StringComparison.Ordinal)) return "ERR @" + id + reply[3..];
        return reply;
    }

    /// <summary>The refusal for a line whose id is not one: said without an id (there is none to give back).</summary>
    public const string Malformed = "a request id is @ and 1-32 letters, digits, '-' or '_', then the line — \"@a1 VIDEO END 10\"";
}

/// <summary>
/// Round 87 (Show Bus 1.1 §5): the clock exchange on the wire. "PING &lt;t1&gt;" is answered "OK PONG t1 t2 t3" —
/// t1 echoed, t2 when this desk read the line, t3 when it answered, all Unix milliseconds on the show clock
/// (which never steps backwards). The caller adds t4 when the answer lands and has NTP's arithmetic (LinkClock).
/// A bare PING is answered "OK PONG" as before.
/// </summary>
public static class ClockPing
{
    /// <summary>The caller's stamp, or null for a bare PING (or one that is not a stamp).</summary>
    public static long? Stamp(string? arg)
    {
        var a = (arg ?? "").Trim();
        return a.Length > 0 && long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : null;
    }

    public static string Pong(long t1, long t2, long t3) => ControlProtocol.Ok(string.Create(CultureInfo.InvariantCulture, $"PONG {t1} {t2} {t3}"));

    /// <summary>Unix milliseconds of a UTC instant.</summary>
    public static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>Parses "PONG t1 t2 t3" (after the OK); null when it is not one.</summary>
    public static (long T1, long T2, long T3)? ParsePong(string? payload)
    {
        var p = (payload ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 4 || p[0] != "PONG") return null;
        return long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var a)
               && long.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var b)
               && long.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out var c)
            ? (a, b, c) : null;
    }
}

/// <summary>
/// Round 87 (Show Bus 1.1 §7): pairing without the token on the wire. On connect the desk says "NONCE &lt;hex&gt;";
/// a remote answers "AUTH-PROOF &lt;hex&gt;" = HMAC-SHA256 with the token (as compared: no dashes or spaces, upper
/// case) as the key over the nonce followed by the name the connection gave in HELLO ("" before any HELLO), lowercase
/// hex. Somebody listening on the network sees a nonce used once and a proof only that nonce accepts. Plain
/// "AUTH &lt;token&gt;" stays accepted from this machine only.
/// </summary>
public static class WireProof
{
    /// <summary>Sixteen random bytes as lowercase hex — one per connection.</summary>
    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string Proof(string token, string nonce, string name)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(PairingToken.Normal(token)), Encoding.UTF8.GetBytes((nonce ?? "") + (name ?? "")));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>Whether a proof answers this nonce for this token and name, in constant time; false with no token set.</summary>
    public static bool Verify(string token, string nonce, string name, string proof)
    {
        if (!PairingToken.Needed(token) || string.IsNullOrEmpty(nonce) || string.IsNullOrWhiteSpace(proof)) return false;
        var expected = Encoding.ASCII.GetBytes(Proof(token, nonce, name));
        var given = Encoding.ASCII.GetBytes(proof.Trim().ToLowerInvariant());
        return expected.Length == given.Length && CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>The refusal for a plain AUTH from another machine.</summary>
    public const string UseProof = "plain AUTH is for this machine only — answer the NONCE line: AUTH-PROOF <hex of HMAC-SHA256(token, nonce + your HELLO name)>";
}

/// <summary>
/// Round 87: the wire's STATE pushes skip one that would repeat the last word for word. A change event that moved
/// nothing STATE carries (a snapshot published for a field the wire does not show) used to cost every peer a whole
/// STATE line; now it costs a string compare. A peer that connects later hears the whole STATE as its greeting, so
/// a skip never leaves anybody behind. Not thread-safe: the push timer's thread is its only caller.
/// </summary>
public sealed class StatePushGate
{
    private string _last = "";

    /// <summary>Pushes skipped as repeats since start.</summary>
    public long Skipped { get; private set; }

    public bool ShouldPush(string json)
    {
        if (string.Equals(json, _last, StringComparison.Ordinal))
        {
            Skipped++;
            return false;
        }
        _last = json;
        return true;
    }
}
