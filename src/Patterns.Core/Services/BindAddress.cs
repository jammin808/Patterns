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
    /// The address to bind, null for every interface, and true; or false with the problem in words. An IPv4
    /// address must have all four parts — the parser's shorthand ("10.0.0" as 10.0.0.0, "1" as 0.0.0.1) is a
    /// typo here, never an address.
    /// </summary>
    public static bool TryParse(string? words, out IPAddress? address, out string problem)
    {
        var w = (words ?? "").Trim();
        address = null;
        problem = "";
        if (w.Length == 0) return true;
        if (IPAddress.TryParse(w, out var parsed) && (parsed.AddressFamily != AddressFamily.InterNetwork || w.Split('.').Length == 4))
        {
            address = parsed;
            return true;
        }
        problem = $"'{w}' is not an address — bind to one address (10.0.0.5) or leave it empty for every interface";
        return false;
    }

    /// <summary>The problem with the words, or "" when they bind.</summary>
    public static string Problem(string? words) => TryParse(words, out _, out var problem) ? "" : problem;
}
