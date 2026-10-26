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

/// <summary>
/// The ports a hello names — only those its node serves (§2): a door it does not serve is left out of the hello, never
/// sent as 0. A reader takes 0 for a door it does not find.
/// </summary>
public sealed record ShowBusPorts
{
    [JsonPropertyName("http")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Http { get; init; }
    [JsonPropertyName("wire")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Wire { get; init; }
    [JsonPropertyName("ws")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Ws { get; init; }
    [JsonPropertyName("osc")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Osc { get; init; }
    /// <summary>Round 88 (Show Bus 1.2 §10): where TLS answers — the same port as http when the node sniffs the first byte.</summary>
    [JsonPropertyName("https")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Https { get; init; }
}

/// <summary>
/// Round 87: another show app heard on the hello (UDP 9928) — what it is, where it says its doors are, what it
/// can do, and when it was last heard. Nothing about the show: a hello never carries any. Show Bus 1.3 §2: one id is one
/// peer — <see cref="Address"/> is the one to dial, kept while it is still heard, and <see cref="Addresses"/> every
/// address the id was heard from.
/// </summary>
public sealed record ShowBusPeer(string Kind, string Id, string Name, string Host, string Address, ShowBusPorts Ports, IReadOnlyList<string> Caps, int Proto, long ClockMs, DateTime HeardUtc)
{
    public ShowBusState StateAt(DateTime utcNow) => ShowBusLiveness.StateOf(utcNow - HeardUtc);

    /// <summary>"Stagewatch", "Patterns", "AGMessage", "Slides agent" — the kind as the operator calls it.</summary>
    public string KindWords => ShowBusPeers.KindWords(Kind);

    /// <summary>Round 88 (Show Bus 1.2 §10): the fingerprint of the certificate the peer says it serves ("" when none).</summary>
    public string Tls { get; init; } = "";

    /// <summary>Round 88: the peer offers TLS and says which certificate.</summary>
    public bool OffersTls => Tls.Length > 0;

    /// <summary>The http door to dial, or null when it names none.</summary>
    public string? HttpBase => Ports.Http > 0 && Address.Length > 0 ? $"http://{Wrap(Address)}:{Ports.Http}" : null;

    /// <summary>Show Bus 1.3 §2: the sender's process, eight lowercase hex new at every start ("" when its hello names none).</summary>
    public string Instance { get; init; } = "";

    /// <summary>
    /// Show Bus 1.3 §2: every address this id was heard from within <c>peer.stale.ms</c> of its last hello, at most
    /// <see cref="ShowBusPeers.MaxAddresses"/> — <see cref="Address"/>, the one to dial, first; the others in a fixed
    /// order, so a node heard on two addresses reads the same at every beat.
    /// </summary>
    public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Show Bus 1.3 §2: two running nodes say this id — null when none does. While it stands this desk dials no new link
    /// to the id, keeps a link it has, and every reader shows the id amber with both machines named.
    /// </summary>
    public ShowBusConflict? Conflict { get; init; }

    /// <summary>
    /// Whether an address names this peer (§2): one its hellos came from — the datagrams' own source, never what a hello
    /// says of itself. A hello's <see cref="Host"/> is the sender's word, and a host name alike on two machines proves
    /// nothing, so a typed address or the link's own address never borrows a peer's pin key, certificate or context by it.
    /// </summary>
    public bool HeardAt(string address)
    {
        var a = (address ?? "").Trim().Trim('[', ']');
        if (a.Length == 0) return false;
        return string.Equals(Address, a, StringComparison.OrdinalIgnoreCase) || Addresses.Contains(a, StringComparer.OrdinalIgnoreCase);
    }

    private static string Wrap(string address) => IPAddress.TryParse(address, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address;
}

/// <summary>
/// Show Bus 1.3 §2's two nodes with one id: the two machines that were heard taking turns to say it — their names and
/// addresses, in a fixed order so the words do not move at every beat — and when the last turn was heard. The mark
/// clears <see cref="ShowBusPeers.ConflictClearsAfter"/> after that.
/// </summary>
public sealed record ShowBusConflict(string Id, string HostA, string AddressA, string HostB, string AddressB, DateTime LastUtc)
{
    /// <summary>When the mark clears, unless another turn is heard first.</summary>
    public DateTime ClearsUtc => LastUtc + ShowBusPeers.ConflictClearsAfter;

    /// <summary>"two machines say they are 3f9c2a1b7d0e: FOH-PC at 10.0.0.5 and FOH-PC2 at 10.0.0.7" — what every reader says of it.</summary>
    public string Words => string.Equals(AddressA, AddressB, StringComparison.OrdinalIgnoreCase) && string.Equals(HostA, HostB, StringComparison.OrdinalIgnoreCase)
        ? $"two copies running at {AddressA}{(HostA.Length > 0 ? $" ({HostA})" : "")} say they are {Id}"
        : $"two machines say they are {Id}: {Named(HostA)} at {AddressA} and {Named(HostB)} at {AddressB}";

    private static string Named(string host) => host.Length > 0 ? host : "a machine";
}

/// <summary>
/// Round 87: every other show app heard on UDP 9928, kept by the listener thread and read by the desk's tick —
/// the NODES list, the Eye's CONTROL plane, the context's peers and the Stagewatch link's discovery read the same
/// table. Pure: the caller hands in the bytes, the address they came from and the time.
/// <para>
/// Show Bus 1.3 §2: one id is one peer, with every address it was heard from and the one to dial kept while it is still
/// heard; an id two running nodes say is marked in conflict. The table is bounded: a hello is unauthenticated and anything
/// on the LAN can send thousands, so it keeps at most <see cref="MaxPeers"/> ids — a new id past that takes the place of
/// the one heard longest ago that this desk does not need (<see cref="Need"/>; at most <see cref="MaxNeeded"/> are, so a
/// new app always finds room) — and counts every id it put out (<see cref="Dropped"/>).
/// </para>
/// </summary>
public sealed class ShowBusPeers
{
    /// <summary>The most ids held (§14 <c>peers.max</c>).</summary>
    public const int MaxPeers = 128;

    /// <summary>The most addresses kept for one id (§14 <c>peer.addresses</c>).</summary>
    public const int MaxAddresses = 4;

    /// <summary>The most bytes of a datagram read as a hello: at least §14's <c>hello.read.bytes</c> (2048).</summary>
    public const int ReadBytes = 4096;

    /// <summary>The most caps a hello's peer keeps (§14 <c>hello.caps</c>), each <see cref="CapChars"/> at most.</summary>
    public const int KeptCaps = 32;

    /// <summary>A cap word's most characters (§14 <c>hello.cap.chars</c>).</summary>
    public const int CapChars = 24;

    /// <summary>A kind's most characters (§14 <c>hello.kind.chars</c>).</summary>
    public const int KindChars = 16;

    /// <summary>An id's fewest and most characters (§14 <c>hello.id.min</c>, <c>hello.id.max</c>).</summary>
    public const int IdMin = 6, IdMax = 40;

    /// <summary>An inst's characters (§14 <c>hello.inst.chars</c>): lowercase hex.</summary>
    public const int InstChars = 8;

    /// <summary>The hello's format this reader knows (§2's <c>v</c>): a hello naming another is dropped.</summary>
    public const int Format = 1;

    /// <summary>A conflict mark clears this long after the last turn that set it (§14 <c>peer.conflict.clear.ms</c>).</summary>
    public static readonly TimeSpan ConflictClearsAfter = TimeSpan.FromSeconds(60);

    /// <summary>The most needed ids the table holds on to: past it a new app always finds room.</summary>
    public const int MaxNeeded = MaxPeers / 2;

    // The senders of one id since its last switch that was not close, oldest first: a turn back to one of them is the
    // alternation §2 names. Bounded, so a flood of insts for one id costs a fixed amount.
    private const int TurnsKept = 8;

    private const string HostWho = "host:";

    private sealed class Entry
    {
        public ShowBusPeer Peer = null!;
        // Every address heard within the stale time of the last hello, with the last hello heard from it.
        public readonly Dictionary<string, ShowBusPeer> Addresses = new(StringComparer.OrdinalIgnoreCase);
        public string Dial = "";
        public string Who = "";
        public DateTime WhoUtc;
        public string WhoHost = "";
        public string WhoAddress = "";
        // Who sent the last hello heard from the address to dial: kept among the turns whatever else is heard.
        public string DialWho = "";
        public readonly List<string> Turns = new();
        public ShowBusConflict? Conflict;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _peers = new(StringComparer.Ordinal);
    private HashSet<string> _needed = new(StringComparer.Ordinal);
    private long _heard;
    private long _dropped;

    /// <summary>Hellos taken in since start.</summary>
    public long Heard => Interlocked.Read(ref _heard);

    /// <summary>
    /// Ids put out of the table to make room for a new one since start — what the Nodes page, the log and STATE's
    /// <c>bus.dropped</c> say.
    /// </summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// The ids this desk needs — the Stagewatch its link dials, the ones it pinned — which a full table never puts out to
    /// make room. At most <see cref="MaxNeeded"/> are held on to. Any thread.
    /// </summary>
    public void Need(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids.Where(i => i.Length > 0).Take(MaxNeeded), StringComparer.Ordinal);
        lock (_gate) _needed = set;
    }

    /// <summary>
    /// One datagram: kept when it is a hello and not this process's own (<paramref name="selfInstance"/> is the
    /// "inst" this process says). False for anything else — a stray packet, a newer format that is not JSON.
    /// </summary>
    public bool Hear(ReadOnlySpan<byte> datagram, string fromAddress, DateTime utcNow, string selfInstance)
    {
        var peer = Parse(datagram, fromAddress, utcNow, out var instance);
        if (peer is null) return false;
        if (instance.Length > 0 && instance == selfInstance) return false;
        lock (_gate)
        {
            if (!_peers.TryGetValue(peer.Id, out var e))
            {
                if (!Room(utcNow))
                {
                    Interlocked.Increment(ref _dropped);
                    return false;
                }
                _peers[peer.Id] = e = new Entry();
            }
            Address(e, peer, fromAddress, utcNow);
            if (e.Conflict is not null && utcNow > e.Conflict.ClearsUtc) e.Conflict = null;
            Turn(e, peer, fromAddress, utcNow);
            e.Peer = Kept(e, utcNow);
        }
        Interlocked.Increment(ref _heard);
        return true;
    }

    /// <summary>Every peer heard within <see cref="ShowBusLiveness.ForgetAfter"/>, by kind and name; older ones are dropped.</summary>
    public IReadOnlyList<ShowBusPeer> Snapshot(DateTime utcNow)
    {
        lock (_gate)
        {
            Forget(utcNow);
            foreach (var e in _peers.Values)
            {
                if (e.Conflict is null || utcNow <= e.Conflict.ClearsUtc) continue;
                e.Conflict = null;
                e.Peer = e.Peer with { Conflict = null };
            }
            return _peers.Values.Select(e => e.Peer).OrderBy(p => p.Kind, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// Whether a new id may be held: the forgotten go first, then the one heard longest ago that is not needed. With at
    /// most <see cref="MaxNeeded"/> needed there is always one; were there none, the new id would be the one left out.
    /// </summary>
    private bool Room(DateTime utcNow)
    {
        if (_peers.Count < MaxPeers) return true;
        Forget(utcNow);
        if (_peers.Count < MaxPeers) return true;
        string? oldest = null;
        var oldestUtc = DateTime.MaxValue;
        foreach (var (id, e) in _peers)
        {
            if (_needed.Contains(id) || e.Peer.HeardUtc >= oldestUtc) continue;
            oldest = id;
            oldestUtc = e.Peer.HeardUtc;
        }
        if (oldest is null) return false;
        _peers.Remove(oldest);
        Interlocked.Increment(ref _dropped);
        return true;
    }

    private void Forget(DateTime utcNow)
    {
        List<string>? old = null;
        foreach (var (id, e) in _peers)
            if (utcNow - e.Peer.HeardUtc > ShowBusLiveness.ForgetAfter) (old ??= new List<string>()).Add(id);
        if (old is null) return;
        foreach (var id in old) _peers.Remove(id);
    }

    /// <summary>
    /// §2's addresses: every one heard within the stale time of this hello, at most <see cref="MaxAddresses"/> — a new one
    /// past that takes the place of the one heard longest ago, never of the one to dial — and the one to dial kept while
    /// it is still heard, else this one.
    /// </summary>
    private static void Address(Entry e, ShowBusPeer heard, string address, DateTime utcNow)
    {
        List<string>? stale = null;
        foreach (var (a, last) in e.Addresses)
            if (utcNow - last.HeardUtc > ShowBusLiveness.StaleAfter) (stale ??= new List<string>()).Add(a);
        if (stale is not null)
            foreach (var a in stale) e.Addresses.Remove(a);
        e.Addresses[address] = heard;
        while (e.Addresses.Count > MaxAddresses)
        {
            var oldest = e.Addresses.Where(a => !string.Equals(a.Key, e.Dial, StringComparison.OrdinalIgnoreCase) && !string.Equals(a.Key, address, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Value.HeardUtc).ThenBy(a => a.Key, StringComparer.Ordinal).First().Key;
            e.Addresses.Remove(oldest);
        }
        if (!e.Addresses.ContainsKey(e.Dial)) e.Dial = address;
    }

    /// <summary>
    /// §2's two nodes with one id. Who sent a hello is its inst, or its host when it names none. A switch to another sender
    /// within the stale time of the last hello is close; a close switch back to a sender heard since the last switch that
    /// was not close — A, then B, then A again — or a close switch between two hosts that name no inst, marks the id in
    /// conflict. A restart (A, then only B) is a switch and never a turn back; one inst from two addresses is no switch.
    /// The senders remembered are bounded, but the one heard from the address to dial is never forgotten for others, so
    /// a flood of hellos with new insts for an id still shows when its own node speaks again.
    /// </summary>
    private static void Turn(Entry e, ShowBusPeer peer, string address, DateTime utcNow)
    {
        var who = peer.Instance.Length > 0 ? peer.Instance : HostWho + peer.Host;
        if (e.Who.Length == 0)
        {
            e.Turns.Add(who);
        }
        else if (!string.Equals(who, e.Who, StringComparison.Ordinal))
        {
            var close = utcNow - e.WhoUtc <= ShowBusLiveness.StaleAfter;
            var twoHosts = who.StartsWith(HostWho, StringComparison.Ordinal) && e.Who.StartsWith(HostWho, StringComparison.Ordinal);
            if (!close)
            {
                e.Turns.Clear();
                e.Turns.Add(who);
            }
            else
            {
                if (twoHosts || e.Turns.Contains(who, StringComparer.Ordinal)) e.Conflict = Marked(peer.Id, e.WhoHost, e.WhoAddress, peer.Host, address, utcNow);
                if (!e.Turns.Contains(who, StringComparer.Ordinal)) e.Turns.Add(who);
                while (e.Turns.Count > TurnsKept) e.Turns.RemoveAt(string.Equals(e.Turns[0], e.DialWho, StringComparison.Ordinal) ? 1 : 0);
            }
        }
        if (string.Equals(address, e.Dial, StringComparison.OrdinalIgnoreCase)) e.DialWho = who;
        e.Who = who;
        e.WhoUtc = utcNow;
        e.WhoHost = peer.Host;
        e.WhoAddress = address;
    }

    private static ShowBusConflict Marked(string id, string hostA, string addressA, string hostB, string addressB, DateTime utcNow)
    {
        var swap = string.CompareOrdinal(addressA, addressB) is var c && (c > 0 || (c == 0 && string.CompareOrdinal(hostA, hostB) > 0));
        return swap ? new ShowBusConflict(id, hostB, addressB, hostA, addressA, utcNow) : new ShowBusConflict(id, hostA, addressA, hostB, addressB, utcNow);
    }

    /// <summary>
    /// The peer as the table keeps it: what the hello last heard from the address to dial says — so the words do not flip
    /// between two machines that say one id, nor follow a forged hello from another address — heard now, with the address
    /// to dial first, the others in a fixed order, and the conflict mark.
    /// </summary>
    private static ShowBusPeer Kept(Entry e, DateTime utcNow)
    {
        var addresses = new List<string>(e.Addresses.Count) { e.Dial };
        addresses.AddRange(e.Addresses.Keys.Where(a => !string.Equals(a, e.Dial, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a, StringComparer.Ordinal));
        return e.Addresses[e.Dial] with { Address = e.Dial, Addresses = addresses, Conflict = e.Conflict, HeardUtc = utcNow };
    }

    /// <summary>
    /// A hello as a peer, or null when the bytes are not one: not JSON, over <see cref="ReadBytes"/>, a format <c>v</c> other
    /// than <see cref="Format"/>, or a kind or an id outside §2's shapes (a kind of letters, digits, '-' and '_', at most <see cref="KindChars"/>; an id of those,
    /// <see cref="IdMin"/> to <see cref="IdMax"/>). It keeps the first <see cref="KeptCaps"/> caps of §2's shape, a name
    /// and a host whole up to <see cref="ShowBusHello.NameChars"/>, and an inst only of §2's shape (<paramref name="instance"/>
    /// is what the hello said, for the caller's own-hello check).
    /// </summary>
    public static ShowBusPeer? Parse(ReadOnlySpan<byte> datagram, string fromAddress, DateTime utcNow, out string instance)
    {
        instance = "";
        if (datagram.Length is 0 or > ReadBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(datagram.ToArray());
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            // §2: a hello whose format word is not one this reader knows is dropped; one that names none is an older app's.
            if (r.TryGetProperty("v", out var v) && !(v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var format) && format == Format)) return null;
            var kind = Str(r, "t");
            var id = Str(r, "id");
            if (!Token(kind, 1, KindChars) || !Token(id, IdMin, IdMax)) return null;
            instance = Str(r, "inst");
            var ports = new ShowBusPorts();
            if (r.TryGetProperty("ports", out var p) && p.ValueKind == JsonValueKind.Object)
                ports = new ShowBusPorts { Http = Int(p, "http"), Wire = Int(p, "wire"), Ws = Int(p, "ws"), Osc = Int(p, "osc"), Https = Int(p, "https") };
            // A hello from before 1.1 named its doors at the top: Patterns' "web" and "wire", Stagewatch's "port".
            if (ports.Http == 0 && ports.Wire == 0 && ports.Ws == 0)
                ports = new ShowBusPorts { Http = Int(r, "web") is > 0 and var w ? w : Int(r, "port"), Wire = Int(r, "wire") };
            var caps = r.TryGetProperty("caps", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").Where(IsCap).Take(KeptCaps).ToList()
                : new List<string>();
            var name = Str(r, "name");
            var host = Str(r, "host");
            if (host.Length == 0) host = Str(r, "machine");
            return new ShowBusPeer(kind, id, OneLine(name, ShowBusHello.NameChars), OneLine(host, ShowBusHello.NameChars), fromAddress, ports, caps, Int(r, "proto"), Long(r, "clock"), utcNow)
            {
                Tls = TlsFingerprint.Normal(Str(r, "tls")),
                Instance = IsInstance(instance) ? instance : "",
                Addresses = new[] { fromAddress },
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

    /// <summary>A cap word of §2's shape: lowercase letters, digits, '.' and '-', at most <see cref="CapChars"/>.</summary>
    public static bool IsCap(string? cap) => cap is { Length: > 0 and <= CapChars } && cap.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-');

    /// <summary>An inst of §2's shape: <see cref="InstChars"/> lowercase hex.</summary>
    public static bool IsInstance(string? inst) => inst is { Length: InstChars } && inst.All(ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool Token(string s, int min, int max) => s.Length >= min && s.Length <= max && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

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

    /// <summary>
    /// A name a hello gives or says, as every reader shows it: one line. A hello is unauthenticated, and its name reaches the
    /// log, the status line and the Eye, so a control or line-break character is a space — no hello writes a line of its
    /// own — and a lone surrogate is the replacement character; cut at <paramref name="max"/> characters, never inside a
    /// surrogate pair.
    /// </summary>
    internal static string OneLine(string s, int max)
    {
        var chars = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                chars[i] = ch;
                chars[i + 1] = s[i + 1];
                i++;
                continue;
            }
            chars[i] = char.IsSurrogate(ch) ? '�'
                : char.IsControl(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' '
                : ch;
        }
        var line = new string(chars).Trim();
        if (line.Length <= max) return line;
        return line[..(char.IsHighSurrogate(line[max - 1]) ? max - 1 : max)];
    }
}

/// <summary>One line for the log: a warning, or an information when <see cref="Warn"/> is false.</summary>
public readonly record struct ShowBusLogLine(bool Warn, string Text);

/// <summary>
/// The log's lines for the hello table (Show Bus 1.3 §2), bounded whatever is heard. A hello is unauthenticated and three
/// datagrams mark an id in conflict, so a flood could otherwise write a line for every id it marks, every second. In any
/// minute this names at most <see cref="NamedPerMinute"/> ids marked in conflict or no longer marked; the rest are counted
/// and said in one line when the minute is over; and how many apps the full table put out, at most once a minute. At most
/// <see cref="NamedPerMinute"/> + 2 lines a minute, however many ids are heard. Pure: the table's snapshot, its drop count and
/// the time are handed in; one poller calls it.
/// </summary>
public sealed class ShowBusTableLog
{
    /// <summary>The most ids named one by one in a minute; past it they are counted.</summary>
    public const int NamedPerMinute = 4;

    /// <summary>How often the counted ones and the drops are said at most.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    // The ids marked at the last call, with when each was last heard: at most the table's size, as the snapshot is.
    private Dictionary<string, DateTime> _marked = new(StringComparer.Ordinal);
    private DateTime _windowUtc = DateTime.MinValue;
    private int _named;
    private int _countedMarked;
    private int _countedCleared;
    private int _countedLeft;
    private long _droppedSaid;
    private DateTime _droppedSaidUtc = DateTime.MinValue;

    /// <summary>The lines to write for this look at the table (none when nothing changed that is said).</summary>
    public IReadOnlyList<ShowBusLogLine> Lines(IReadOnlyList<ShowBusPeer> peers, long dropped, DateTime utcNow)
    {
        var lines = new List<ShowBusLogLine>();
        if (utcNow - _windowUtc >= Every)
        {
            if (Counted() is { Length: > 0 } counted) lines.Add(new ShowBusLogLine(true, counted));
            _windowUtc = utcNow;
            _named = _countedMarked = _countedCleared = _countedLeft = 0;
        }
        if (dropped > _droppedSaid && utcNow - _droppedSaidUtc >= Every)
        {
            var more = dropped - _droppedSaid;
            lines.Add(new ShowBusLogLine(true, string.Create(CultureInfo.InvariantCulture,
                $"Show network: {more} more {(more == 1 ? "app" : "apps")} dropped from the hello table ({dropped} since start) — it keeps {ShowBusPeers.MaxPeers}, the one heard longest ago that this desk does not need going first; something on the network says hello as many apps.")));
            _droppedSaid = dropped;
            _droppedSaidUtc = utcNow;
        }
        var marked = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in peers)
        {
            held.Add(p.Id);
            if (p.Conflict is null) continue;
            marked[p.Id] = p.HeardUtc;
            if (_marked.ContainsKey(p.Id)) continue;
            if (_named < NamedPerMinute)
            {
                _named++;
                lines.Add(new ShowBusLogLine(true, $"Show network: {p.Conflict.Words} — {ShowBusView.ConflictHoldWords}."));
            }
            else
            {
                _countedMarked++;
            }
        }
        foreach (var (id, heardUtc) in _marked)
        {
            if (marked.ContainsKey(id)) continue;
            // Still held unmarked: no turns for a minute. Not held: unheard for a minute, or put out of the full table.
            var cleared = held.Contains(id);
            var forgotten = !cleared && utcNow - heardUtc > ShowBusLiveness.ForgetAfter;
            if (_named < NamedPerMinute)
            {
                _named++;
                lines.Add(new ShowBusLogLine(false, cleared ? $"Show network: {id} — no two machines heard taking turns to say it for a minute: its conflict mark is cleared."
                    : forgotten ? $"Show network: {id} has not been heard for a minute — forgotten, and its conflict mark with it."
                    : $"Show network: {id} was put out of the full hello table to make room — its conflict mark goes with it; heard again, it is marked again only once two machines are heard taking turns."));
            }
            else if (cleared)
            {
                _countedCleared++;
            }
            else
            {
                _countedLeft++;
            }
        }
        _marked = marked;
        return lines;
    }

    private string Counted()
    {
        var parts = new List<string>(3);
        if (_countedMarked > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{_countedMarked} more {(_countedMarked == 1 ? "id" : "ids")} marked in conflict"));
        if (_countedCleared > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{_countedCleared} {(_countedCleared == 1 ? "mark" : "marks")} cleared"));
        if (_countedLeft > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{_countedLeft} marked {(_countedLeft == 1 ? "id" : "ids")} gone from the hello table"));
        return parts.Count == 0 ? ""
            : $"Show network: {string.Join(", ", parts)} in the last minute, not named one by one (the log names {NamedPerMinute} a minute) — something on the network may be saying hello as many apps; the Nodes page and STATE show every one.";
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

    public static string LoadOrCreate(string folder) => LoadOrCreate(folder, out _);

    /// <summary>
    /// Round 91: the id, and whether it is kept — read from its file, or written there now. An id that is not kept is this
    /// run's only, so a Stagewatch key bound to it refuses this desk after a restart; the Nodes page says so.
    /// </summary>
    public static string LoadOrCreate(string folder, out bool kept)
    {
        kept = false;
        var path = Path.Combine(folder, FileName);
        try
        {
            if (File.Exists(path))
            {
                var held = File.ReadAllText(path).Trim();
                if (IsId(held))
                {
                    kept = true;
                    return held;
                }
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
            kept = true;
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
