using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Patterns.Core.Services;

/// <summary>
/// This machine's addresses from its interfaces — never from the resolver (round 65). Asking DNS
/// for the machine's own name can wait as long as a venue's DNS wants, and the desk's thread
/// used to wait with it; the interfaces answer at once. Up interfaces, unicast IPv4, no loopback,
/// no link-local 169.254 (an address nobody can be told to type).
/// </summary>
public static class LocalAddresses
{
    public static IReadOnlyList<IPAddress> Enumerate()
    {
        try
        {
            var candidates = new List<IPAddress>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses) candidates.Add(unicast.Address);
            }
            return Pick(candidates);
        }
        catch
        {
            return Array.Empty<IPAddress>();                                 // no interfaces to read: fewer suggestions, never a wait
        }
    }

    /// <summary>The addresses worth telling someone: IPv4, not loopback, not link-local, each once, in a stable order.</summary>
    public static IReadOnlyList<IPAddress> Pick(IEnumerable<IPAddress> candidates)
    {
        var picked = new List<IPAddress>();
        foreach (var a in candidates)
        {
            if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a) || IsLinkLocal(a)) continue;
            if (picked.Any(p => p.Equals(a))) continue;
            picked.Add(a);
        }
        return picked;
    }

    public static bool IsLinkLocal(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }
}
