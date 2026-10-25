using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Patterns.Core.Services;

/// <summary>
/// Round 85 (P1-06): the bind setting read one way by every listener — the control ports, the OSC port, the
/// audience socket. "" is every interface; an IP address is that address; anything else is a problem the
/// listener refuses to open on. It never falls back to every interface: on a desk with two networks a typo
/// in the control network's address must not put the control ports on the audience's.
/// </summary>
public static class BindAddress
{
    /// <summary>
    /// The address to bind, null for every interface, and true; or false with the problem in words. The words are
    /// one whole address (<see cref="TryAddress"/>) or nothing.
    /// </summary>
    public static bool TryParse(string? words, out IPAddress? address, out string problem)
    {
        var w = (words ?? "").Trim();
        address = null;
        problem = "";
        if (w.Length == 0) return true;
        if (TryAddress(w, out var parsed))
        {
            address = parsed;
            return true;
        }
        problem = $"'{w}' is not an address — bind to one address (10.0.0.5) or leave it empty for every interface";
        return false;
    }

    /// <summary>
    /// One whole address, read the one way every address field on the desk is read (the bind, OSC ALLOW): IPv6 as
    /// written; IPv4 only as four dotted parts of decimal digits, each 0–255, read as decimal. The parser's shorthand
    /// ("10.0.0" as 10.0.0.0, "1" as 0.0.0.1), its octal ("010" as 8) and its hex ("0x0A") never stand for an address
    /// here: a typo in an address field is said as a typo, never read as some other machine.
    /// </summary>
    public static bool TryAddress(string? words, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        var w = (words ?? "").Trim();
        if (w.Length == 0) return false;
        if (w.Contains(':'))
        {
            if (!IPAddress.TryParse(w, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6) return false;
            address = v6;
            return true;
        }
        var parts = w.Split('.');
        if (parts.Length != 4) return false;
        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit)
                || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i])) return false;
        }
        address = new IPAddress(bytes);
        return true;
    }

    /// <summary>The problem with the words, or "" when they bind.</summary>
    public static string Problem(string? words) => TryParse(words, out _, out var problem) ? "" : problem;
}
