using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Patterns.Core.Services;

/// <summary>
/// DNS-SD over multicast DNS, the part that is bytes and rules: a packet in and out of its wire
/// form (names, compression pointers on the way in), the records a Patterns process announces
/// itself with, the answers a query earns, and a browser that turns other announcements into
/// peers. Pure — the sockets are the App's — so every packet shape is tested here without a
/// network. RFC 6762 (mDNS) and RFC 6763 (DNS-SD) as far as a Companion needs them.
/// </summary>
public static class DnsSd
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort TypeTxt = 16;
    public const ushort TypeAaaa = 28;
    public const ushort TypeSrv = 33;
    public const ushort TypeAny = 255;
    public const ushort ClassIn = 1;
    /// <summary>The cache-flush bit on a record (this responder's word is the whole truth for the name), and the unicast-response bit on a question.</summary>
    public const ushort FlushBit = 0x8000;

    /// <summary>The multicast group and port every mDNS speaker shares.</summary>
    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    public const int Port = 5353;

    /// <summary>The name every DNS-SD browser asks for the list of service types.</summary>
    public static readonly string[] ServiceEnumeration = { "_services", "_dns-sd", "_udp", "local" };

    /// <summary>Two names are one name whatever the case — DNS is case-blind.</summary>
    public static bool SameName(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>"_patterns._tcp.local" as labels; a label may hold anything, a dot included, when given as a list.</summary>
    public static string[] Labels(string dotted) => dotted.Split('.', StringSplitOptions.RemoveEmptyEntries);

    public static string Dotted(IReadOnlyList<string> labels) => string.Join(".", labels);

    /// <summary>A host label: letters, digits and hyphens, lower case — what a machine name becomes on the network.</summary>
    public static string HostLabel(string machine)
    {
        var sb = new StringBuilder();
        foreach (var ch in machine.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "patterns" : s.Length > 50 ? s[..50] : s;
    }
}

public sealed record DnsQuestion(IReadOnlyList<string> Name, ushort Type, bool UnicastReply = false);

/// <summary>One resource record: the name, the type, the flush bit, the TTL, and the data as the type has it.</summary>
public sealed record DnsRecord(IReadOnlyList<string> Name, ushort Type, uint Ttl, bool Flush = false)
{
    /// <summary>PTR: the name pointed to. SRV: the target host.</summary>
    public IReadOnlyList<string>? Target { get; init; }
    public ushort Priority { get; init; }
    public ushort Weight { get; init; }
    public ushort Port { get; init; }
    /// <summary>TXT: the key=value strings.</summary>
    public IReadOnlyList<string> Text { get; init; } = Array.Empty<string>();
    /// <summary>A / AAAA: the address.</summary>
    public IPAddress? Address { get; init; }
    /// <summary>A type this code does not read: its bytes, kept so a packet re-encodes.</summary>
    public byte[] Raw { get; init; } = Array.Empty<byte>();

    public static DnsRecord Ptr(IReadOnlyList<string> name, IReadOnlyList<string> target, uint ttl) => new(name, DnsSd.TypePtr, ttl) { Target = target };
    public static DnsRecord Srv(IReadOnlyList<string> name, IReadOnlyList<string> host, int port, uint ttl) => new(name, DnsSd.TypeSrv, ttl, Flush: true) { Target = host, Port = (ushort)port };
    public static DnsRecord Txt(IReadOnlyList<string> name, IEnumerable<string> text, uint ttl) => new(name, DnsSd.TypeTxt, ttl, Flush: true) { Text = text.ToList() };
    public static DnsRecord A(IReadOnlyList<string> name, IPAddress address, uint ttl) => new(name, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? DnsSd.TypeAaaa : DnsSd.TypeA, ttl, Flush: true) { Address = address };

    /// <summary>The TXT strings as pairs; a string with no '=' is a key with an empty value.</summary>
    public IReadOnlyDictionary<string, string> TextPairs()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Text)
        {
            var at = s.IndexOf('=');
            if (at < 0) d[s] = "";
            else d[s[..at]] = s[(at + 1)..];
        }
        return d;
    }
}

/// <summary>A whole mDNS message: questions and answers, with the additional records a good answer carries.</summary>
public sealed class DnsPacket
{
    public ushort Id { get; init; }
    public bool IsResponse { get; init; }
    public List<DnsQuestion> Questions { get; } = new();
    public List<DnsRecord> Answers { get; } = new();
    public List<DnsRecord> Additionals { get; } = new();

    /// <summary>A query for one name and type, multicast (the answer comes to the group).</summary>
    public static DnsPacket Query(IReadOnlyList<string> name, ushort type)
    {
        var p = new DnsPacket();
        p.Questions.Add(new DnsQuestion(name, type));
        return p;
    }

    public static DnsPacket Response(IEnumerable<DnsRecord> answers, IEnumerable<DnsRecord>? additionals = null)
    {
        var p = new DnsPacket { IsResponse = true };
        p.Answers.AddRange(answers);
        if (additionals is not null) p.Additionals.AddRange(additionals);
        return p;
    }

    /// <summary>The wire form — no compression on the way out, which every reader accepts.</summary>
    public byte[] ToBytes()
    {
        var w = new List<byte>(512);
        void U16(int v) { w.Add((byte)(v >> 8)); w.Add((byte)v); }
        void U32(uint v) { w.Add((byte)(v >> 24)); w.Add((byte)(v >> 16)); w.Add((byte)(v >> 8)); w.Add((byte)v); }
        void Name(IReadOnlyList<string> labels)
        {
            foreach (var label in labels)
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                var n = Math.Min(63, bytes.Length);
                w.Add((byte)n);
                for (var i = 0; i < n; i++) w.Add(bytes[i]);
            }
            w.Add(0);
        }
        void Record(DnsRecord r)
        {
            Name(r.Name);
            U16(r.Type);
            U16(DnsSd.ClassIn | (r.Flush ? DnsSd.FlushBit : 0));
            U32(r.Ttl);
            var data = new List<byte>();
            switch (r.Type)
            {
                case DnsSd.TypePtr:
                    data.AddRange(NameBytes(r.Target ?? Array.Empty<string>()));
                    break;
                case DnsSd.TypeSrv:
                    data.Add((byte)(r.Priority >> 8)); data.Add((byte)r.Priority);
                    data.Add((byte)(r.Weight >> 8)); data.Add((byte)r.Weight);
                    data.Add((byte)(r.Port >> 8)); data.Add((byte)r.Port);
                    data.AddRange(NameBytes(r.Target ?? Array.Empty<string>()));
                    break;
                case DnsSd.TypeTxt:
                    if (r.Text.Count == 0) data.Add(0);
                    foreach (var s in r.Text)
                    {
                        var bytes = Encoding.UTF8.GetBytes(s);
                        var n = Math.Min(255, bytes.Length);
                        data.Add((byte)n);
                        for (var i = 0; i < n; i++) data.Add(bytes[i]);
                    }
                    break;
                case DnsSd.TypeA:
                case DnsSd.TypeAaaa:
                    data.AddRange(r.Address?.GetAddressBytes() ?? Array.Empty<byte>());
                    break;
                default:
                    data.AddRange(r.Raw);
                    break;
            }
            U16(data.Count);
            w.AddRange(data);
        }
        U16(Id);
        U16(IsResponse ? 0x8400 : 0x0000);
        U16(Questions.Count);
        U16(Answers.Count);
        U16(0);
        U16(Additionals.Count);
        foreach (var q in Questions)
        {
            Name(q.Name);
            U16(q.Type);
            U16(DnsSd.ClassIn | (q.UnicastReply ? DnsSd.FlushBit : 0));
        }
        foreach (var r in Answers) Record(r);
        foreach (var r in Additionals) Record(r);
        return w.ToArray();
    }

    private static byte[] NameBytes(IReadOnlyList<string> labels)
    {
        var w = new List<byte>();
        foreach (var label in labels)
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            var n = Math.Min(63, bytes.Length);
            w.Add((byte)n);
            for (var i = 0; i < n; i++) w.Add(bytes[i]);
        }
        w.Add(0);
        return w.ToArray();
    }

    /// <summary>A packet from the wire, or null for bytes that are not one; compression pointers followed, loops refused.</summary>
    public static DnsPacket? Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 12) return null;
            var flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
            var p = new DnsPacket { Id = BinaryPrimitives.ReadUInt16BigEndian(data), IsResponse = (flags & 0x8000) != 0 };
            int qd = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
            int an = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
            int ns = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
            int ar = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
            var at = 12;
            for (var i = 0; i < qd; i++)
            {
                var name = ReadName(data, ref at);
                if (at + 4 > data.Length) return null;
                var type = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
                var cls = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
                at += 4;
                p.Questions.Add(new DnsQuestion(name, type, (cls & DnsSd.FlushBit) != 0));
            }
            for (var i = 0; i < an; i++) p.Answers.Add(ReadRecord(data, ref at));
            for (var i = 0; i < ns; i++) ReadRecord(data, ref at);     // authorities: read past, never used
            for (var i = 0; i < ar; i++) p.Additionals.Add(ReadRecord(data, ref at));
            return p;
        }
        catch (Exception)
        {
            return null;    // a datagram that is not a packet is nobody's business
        }
    }

    private static DnsRecord ReadRecord(ReadOnlySpan<byte> data, ref int at)
    {
        var name = ReadName(data, ref at);
        var type = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
        var cls = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
        var ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(at + 4)..]);
        int len = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 8)..]);
        at += 10;
        var start = at;
        var end = at + len;
        if (end > data.Length) throw new InvalidDataException("record past the end");
        var r = new DnsRecord(name, type, ttl, (cls & DnsSd.FlushBit) != 0);
        switch (type)
        {
            case DnsSd.TypePtr:
            {
                var q = start;
                r = r with { Target = ReadName(data, ref q) };
                break;
            }
            case DnsSd.TypeSrv:
            {
                var q = start + 6;
                r = r with
                {
                    Priority = BinaryPrimitives.ReadUInt16BigEndian(data[start..]),
                    Weight = BinaryPrimitives.ReadUInt16BigEndian(data[(start + 2)..]),
                    Port = BinaryPrimitives.ReadUInt16BigEndian(data[(start + 4)..]),
                    Target = ReadName(data, ref q),
                };
                break;
            }
            case DnsSd.TypeTxt:
            {
                var text = new List<string>();
                var q = start;
                while (q < end)
                {
                    int n = data[q];
                    q++;
                    if (n == 0) continue;
                    if (q + n > end) break;
                    text.Add(Encoding.UTF8.GetString(data.Slice(q, n)));
                    q += n;
                }
                r = r with { Text = text };
                break;
            }
            case DnsSd.TypeA when len == 4:
            case DnsSd.TypeAaaa when len == 16:
                r = r with { Address = new IPAddress(data.Slice(start, len)) };
                break;
            default:
                r = r with { Raw = data.Slice(start, len).ToArray() };
                break;
        }
        at = end;
        return r;
    }

    private static string[] ReadName(ReadOnlySpan<byte> data, ref int at)
    {
        var labels = new List<string>();
        var hops = 0;
        var pos = at;
        var jumped = false;
        while (true)
        {
            if (pos >= data.Length) throw new InvalidDataException("name past the end");
            int n = data[pos];
            if (n == 0)
            {
                pos++;
                break;
            }
            if ((n & 0xC0) == 0xC0)
            {
                if (pos + 1 >= data.Length) throw new InvalidDataException("pointer past the end");
                var target = ((n & 0x3F) << 8) | data[pos + 1];
                if (!jumped) at = pos + 2;
                jumped = true;
                if (++hops > 64 || target >= data.Length) throw new InvalidDataException("a pointer loop");
                pos = target;
                continue;
            }
            pos++;
            if (pos + n > data.Length) throw new InvalidDataException("label past the end");
            labels.Add(Encoding.UTF8.GetString(data.Slice(pos, n)));
            pos += n;
        }
        if (!jumped) at = pos;
        return labels.ToArray();
    }
}

/// <summary>
/// What one Patterns process announces: an instance of the service type, on a host, on a port, with
/// its facts in the TXT record — the desk's kind and name, the show, its ports, its version. The
/// instance name is what a Companion's list shows: "Patterns desk FOH-PC".
/// </summary>
public sealed record MdnsAdvert(string Kind, string Machine, string Show, int WirePort, int HttpPort, int LinkPort, string Instance, string Version, IReadOnlyList<IPAddress> Addresses)
{
    public const string ServiceType = "_patterns._tcp";
    public const uint DefaultTtl = 120;

    public static readonly string[] ServiceName = { "_patterns", "_tcp", "local" };

    /// <summary>"Patterns desk FOH-PC", "Patterns stage timer STAGE-PI".</summary>
    public string InstanceLabel => $"Patterns {KindWords} {Machine}".Trim();

    private string KindWords => Kind switch { "caller" => "caller", "arcade" => "arcade", "timer" => "stage timer", _ => "desk" };

    public string[] InstanceName => new[] { InstanceLabel, "_patterns", "_tcp", "local" };

    /// <summary>The host the SRV points at — the machine's name as a label, with "-patterns" so the system's own record for the machine is never argued with.</summary>
    public string[] HostName => new[] { DnsSd.HostLabel(Machine) + "-patterns", "local" };

    public IReadOnlyList<string> TxtStrings => new[]
    {
        "proto=1",
        $"kind={Kind}",
        $"name={Machine}",
        $"show={Show}",
        $"wire={WirePort}",
        $"http={HttpPort}",
        $"link={LinkPort}",
        $"instance={Instance}",
        $"version={Version}",
    };

    /// <summary>Every record this process answers with: the type's pointer to the instance, the instance's SRV and TXT, the host's addresses, and the type in the service enumeration.</summary>
    public IReadOnlyList<DnsRecord> Records(uint ttl = DefaultTtl)
    {
        var list = new List<DnsRecord>
        {
            DnsRecord.Ptr(ServiceName, InstanceName, ttl),
            DnsRecord.Srv(InstanceName, HostName, WirePort, ttl),
            DnsRecord.Txt(InstanceName, TxtStrings, ttl),
        };
        foreach (var a in Addresses) list.Add(DnsRecord.A(HostName, a, ttl));
        list.Add(DnsRecord.Ptr(DnsSd.ServiceEnumeration, ServiceName, ttl));
        return list;
    }

    /// <summary>The announcement: every record, unasked.</summary>
    public DnsPacket Announcement() => DnsPacket.Response(Records());

    /// <summary>The goodbye: the same records with a TTL of zero, so a browser forgets this process at once.</summary>
    public DnsPacket Goodbye() => DnsPacket.Response(Records(0));

    /// <summary>A signature of the facts: when it changes, the announcement goes out again.</summary>
    public string Signature => string.Join("|", InstanceLabel, WirePort, HttpPort, LinkPort, Show, Version, string.Join(",", Addresses.Select(a => a.ToString())));
}

/// <summary>The answers a query earns from an advert — none when the query is not about this process.</summary>
public static class MdnsResponder
{
    public static DnsPacket? Answer(DnsPacket query, MdnsAdvert advert)
    {
        if (query.IsResponse) return null;
        var answers = new List<DnsRecord>();
        var additionals = new List<DnsRecord>();
        var records = advert.Records();
        var ptr = records[0];
        var srv = records[1];
        var txt = records[2];
        var addresses = records.Where(r => r.Type is DnsSd.TypeA or DnsSd.TypeAaaa).ToList();
        var enumeration = records[^1];
        foreach (var q in query.Questions)
        {
            var wantsAll = q.Type == DnsSd.TypeAny;
            if (DnsSd.SameName(q.Name, MdnsAdvert.ServiceName) && (q.Type == DnsSd.TypePtr || wantsAll))
            {
                Add(answers, ptr);
                Add(additionals, srv);
                Add(additionals, txt);
                foreach (var a in addresses) Add(additionals, a);
            }
            else if (DnsSd.SameName(q.Name, DnsSd.ServiceEnumeration) && (q.Type == DnsSd.TypePtr || wantsAll))
            {
                Add(answers, enumeration);
            }
            else if (DnsSd.SameName(q.Name, advert.InstanceName))
            {
                if (q.Type == DnsSd.TypeSrv || wantsAll) Add(answers, srv);
                if (q.Type == DnsSd.TypeTxt || wantsAll) Add(answers, txt);
                if (q.Type is DnsSd.TypeSrv or DnsSd.TypeAny) foreach (var a in addresses) Add(additionals, a);
            }
            else if (DnsSd.SameName(q.Name, advert.HostName) && (q.Type is DnsSd.TypeA or DnsSd.TypeAaaa || wantsAll))
            {
                foreach (var a in addresses)
                {
                    if (wantsAll || a.Type == q.Type) Add(answers, a);
                }
            }
        }
        if (answers.Count == 0) return null;
        // What is already answered is not repeated as an additional.
        additionals.RemoveAll(a => answers.Any(x => ReferenceEquals(x, a)));
        return DnsPacket.Response(answers, additionals);
    }

    private static void Add(List<DnsRecord> list, DnsRecord r)
    {
        if (!list.Any(x => ReferenceEquals(x, r))) list.Add(r);
    }
}

/// <summary>One service found on the network: its instance, where it is, what its TXT record says, and when it was last heard.</summary>
public sealed record MdnsPeer(string Instance, string Host, IPAddress? Address, int Port, IReadOnlyDictionary<string, string> Txt, DateTime HeardUtc, uint Ttl)
{
    public bool Expired(DateTime utcNow) => Ttl == 0 || utcNow - HeardUtc > TimeSpan.FromSeconds(Math.Max(1, Ttl));

    /// <summary>"Companion (FOH-PC) at 10.0.0.5:16622 · v5.0.3".</summary>
    public string Line
    {
        get
        {
            var where = Address is null ? Host : $"{Address}:{Port}";
            var version = Txt.TryGetValue("version", out var v) && v.Length > 0 ? $" · v{v}" : "";
            return $"{Instance} at {where}{version}";
        }
    }
}

/// <summary>
/// A browser for one service type: the announcements and answers heard, kept as peers by instance
/// and merged across packets (a PTR here, the SRV and the address there), forgotten when their TTL
/// runs out or a goodbye arrives. Pure: packets and the clock in, peers out.
/// </summary>
public sealed class MdnsBrowser
{
    private readonly IReadOnlyList<string> _service;
    private readonly Dictionary<string, MdnsPeer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"_companion-satellite-tcp._tcp" — the type as DNS-SD spells it, without the domain.</summary>
    public MdnsBrowser(string serviceType)
    {
        _service = DnsSd.Labels(serviceType).Append("local").ToArray();
    }

    public IReadOnlyList<string> ServiceName => _service;

    /// <summary>The query that asks every speaker of the type to answer.</summary>
    public DnsPacket Query() => DnsPacket.Query(_service, DnsSd.TypePtr);

    /// <summary>A packet heard, from an address: the peers it names are kept or updated. Returns whether anything of the type was in it.</summary>
    public bool Hear(DnsPacket packet, IPAddress? from, DateTime utcNow)
    {
        if (!packet.IsResponse) return false;
        var all = packet.Answers.Concat(packet.Additionals).ToList();
        foreach (var a in all.Where(r => r.Type is DnsSd.TypeA && r.Address is not null))
        {
            _hosts[DnsSd.Dotted(a.Name)] = a.Address!;
        }
        var touched = false;
        foreach (var ptr in all.Where(r => r.Type == DnsSd.TypePtr && DnsSd.SameName(r.Name, _service) && r.Target is { Count: > 0 }))
        {
            var instanceName = ptr.Target!;
            var key = DnsSd.Dotted(instanceName);
            var label = instanceName[0];
            var srv = all.FirstOrDefault(r => r.Type == DnsSd.TypeSrv && DnsSd.SameName(r.Name, instanceName));
            var txt = all.FirstOrDefault(r => r.Type == DnsSd.TypeTxt && DnsSd.SameName(r.Name, instanceName));
            _peers.TryGetValue(key, out var known);
            if (ptr.Ttl == 0)
            {
                _peers.Remove(key);
                touched = true;
                continue;
            }
            var host = srv?.Target is { Count: > 0 } t ? DnsSd.Dotted(t) : known?.Host ?? "";
            var port = srv?.Port ?? known?.Port ?? 0;
            var address = (host.Length > 0 && _hosts.TryGetValue(host, out var a) ? a : null) ?? known?.Address ?? from;
            var pairs = txt is not null ? txt.TextPairs() : known?.Txt ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _peers[key] = new MdnsPeer(label, host, address, port, pairs, utcNow, ptr.Ttl);
            touched = true;
        }
        // An SRV or TXT heard on its own (an answer to a follow-up query) completes a peer already known.
        foreach (var r in all.Where(r => r.Type is DnsSd.TypeSrv or DnsSd.TypeTxt))
        {
            var key = DnsSd.Dotted(r.Name);
            if (!_peers.TryGetValue(key, out var known)) continue;
            var host = r.Type == DnsSd.TypeSrv && r.Target is { Count: > 0 } t ? DnsSd.Dotted(t) : known.Host;
            var port = r.Type == DnsSd.TypeSrv ? r.Port : known.Port;
            var address = (host.Length > 0 && _hosts.TryGetValue(host, out var a) ? a : null) ?? known.Address;
            var pairs = r.Type == DnsSd.TypeTxt ? r.TextPairs() : known.Txt;
            _peers[key] = known with { Host = host, Port = port, Address = address, Txt = pairs, HeardUtc = utcNow };
            touched = true;
        }
        return touched;
    }

    /// <summary>The peers still within their TTL, by instance name.</summary>
    public IReadOnlyList<MdnsPeer> Peers(DateTime utcNow)
    {
        foreach (var gone in _peers.Where(p => p.Value.Expired(utcNow)).Select(p => p.Key).ToList()) _peers.Remove(gone);
        return _peers.Values.OrderBy(p => p.Instance, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
