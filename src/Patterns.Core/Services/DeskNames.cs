using System.Globalization;
using System.Net;
using System.Text;

namespace Patterns.Core.Services;

/// <summary>
/// Round 89 (PeerLink profile 13.3.4): whether a web request names this desk by one of its own hosts, which loopback trust
/// and the pages' cookie both want. A browser sends as Host the name the page was opened by; a page whose name a resolver
/// rebinds to this machine keeps its own name there, and a forwarder that carries another machine's traffic in over
/// loopback carries that machine's name and port. Neither is this machine's own browser at this desk's pages.
/// <para>A Host names this desk when its port is the one the connection arrived on (no port: 80, or 443 over TLS) and its
/// name is one of: <c>localhost</c> or a loopback address, when the connection arrived over loopback; the address the
/// connection arrived on (the one a browser that typed an address connected to); or one of the machine's own names
/// (<see cref="Of"/>). Anything else names something else — an absent Host, a name with a trailing dot, an address
/// written in a form no browser sends, a port with a sign or a leading zero. Pure: the names are handed in.</para>
/// </summary>
public static class DeskNames
{
    /// <summary>Whether <paramref name="host"/> (a request's Host, as sent) names this desk for a connection that arrived on <paramref name="arrivedOn"/>.</summary>
    public static bool Named(string host, IPEndPoint? arrivedOn, bool tls, IReadOnlyCollection<string> machineNames)
    {
        if (arrivedOn is null || !Split(host, out var name, out var port, out var literal)) return false;
        if ((port ?? (tls ? 443 : 80)) != arrivedOn.Port) return false;
        var local = Plain(arrivedOn.Address);
        var overLoopback = IPAddress.IsLoopback(local);
        if (literal is not null) return IPAddress.IsLoopback(literal) ? overLoopback : literal.Equals(local);
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return overLoopback;
        return machineNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The machine's own names, lower case, each once: its NetBIOS name and its host name, each also under <c>.local</c>
    /// (the system's own mDNS answer) and under each DNS suffix it has — the primary one and each live interface's own,
    /// the one a venue's or a home router hands out with its address (<c>.lan</c>, <c>.home</c>, <c>.fritz.box</c>) — and
    /// the name this process's own mDNS responder answers to (<c>&lt;label&gt;-patterns.local</c>, <see cref="MdnsAdvert.HostName"/>).
    /// </summary>
    public static IReadOnlyList<string> Of(string machine, string hostName, IEnumerable<string> suffixes, string beaconName)
    {
        var names = new List<string>();
        var dotted = new List<string>();
        foreach (var raw in suffixes)
        {
            var s = (raw ?? "").Trim().Trim('.').ToLowerInvariant();
            if (s.Length > 0 && s != "local" && !dotted.Contains(s, StringComparer.Ordinal)) dotted.Add(s);
        }
        foreach (var raw in new[] { machine, hostName })
        {
            var n = raw.Trim().ToLowerInvariant();
            if (n.Length == 0) continue;
            Add(n);
            Add(n + ".local");
            foreach (var suffix in dotted) Add(n + "." + suffix);
        }
        if (beaconName.Trim().Length > 0) Add(DnsSd.Dotted(MdnsAdvert.HostFor(beaconName)));
        return names;

        void Add(string n)
        {
            if (!names.Contains(n, StringComparer.Ordinal)) names.Add(n);
        }
    }

    /// <summary>A Host as the log may show it: printable ASCII only, 64 characters at most, quoted; "no Host" when there was none.</summary>
    public static string Shown(string host)
    {
        var t = host.Trim();
        if (t.Length == 0) return "no Host";
        var sb = new StringBuilder("'");
        foreach (var ch in t.Length > 64 ? t[..64] : t) sb.Append(ch is >= ' ' and <= '~' ? ch : '?');
        if (t.Length > 64) sb.Append('…');
        return sb.Append('\'').ToString();
    }

    /// <summary>An address as compared: IPv4 plainly even when it arrived mapped into IPv6, and no scope.</summary>
    private static IPAddress Plain(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) return a.MapToIPv4();
        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && a.ScopeId != 0) return new IPAddress(a.GetAddressBytes());
        return a;
    }

    /// <summary>
    /// A Host split into its name and port: <c>name</c>, <c>name:port</c>, <c>[v6]</c> or <c>[v6]:port</c>. An IPv4 name must
    /// be written as a browser writes it (four decimal parts, nothing else); a port is 1 to 65535 in plain decimal.
    /// </summary>
    private static bool Split(string host, out string name, out int? port, out IPAddress? literal)
    {
        name = "";
        port = null;
        literal = null;
        var t = host.Trim();
        if (t.Length == 0) return false;
        string rest;
        if (t[0] == '[')
        {
            var close = t.IndexOf(']');
            if (close < 0) return false;
            if (!IPAddress.TryParse(t[1..close], out var v6) || v6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
            literal = Plain(v6);
            name = t[..(close + 1)];
            rest = t[(close + 1)..];
        }
        else
        {
            var colon = t.IndexOf(':');
            if (colon >= 0 && t.IndexOf(':', colon + 1) >= 0) return false;          // an IPv6 address unbracketed: no browser sends it
            name = colon < 0 ? t : t[..colon];
            rest = colon < 0 ? "" : t[colon..];
            if (name.Length == 0) return false;
            if (IsDottedQuad(name))
            {
                if (!IPAddress.TryParse(name, out var v4) || !v4.ToString().Equals(name, StringComparison.Ordinal)) return false;
                literal = v4;
            }
            else if (name.All(c => char.IsAsciiDigit(c) || c == '.') || name.EndsWith('.')) return false;   // a number a parser might take as an address, or a dotted root name
        }
        if (rest.Length == 0) return true;
        if (rest[0] != ':') return false;
        var digits = rest[1..];
        if (digits.Length is 0 or > 5 || !digits.All(char.IsAsciiDigit) || digits[0] == '0') return false;
        var p = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        if (p > 65535) return false;
        port = p;
        return true;
    }

    private static bool IsDottedQuad(string name)
    {
        var parts = name.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }
}
