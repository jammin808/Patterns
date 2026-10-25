using System.Net.NetworkInformation;
using System.Net.Sockets;
using Patterns.Core.Services;

namespace Patterns.App.Services;

public sealed partial class ControlService
{
    /// <summary>The machine's own names as the web remote compares a Host with them, kept with the beacon name and the network generation they were read under.</summary>
    private sealed record DeskNameSet(string Beacon, int Generation, IReadOnlyList<string> Names);

    private volatile DeskNameSet? _deskNames;

    /// <summary>Moves each time the machine's addresses change — a DHCP lease, a cable, a network joined — which is when an interface's DNS suffix can change.</summary>
    private int _namesGeneration;

    /// <summary>0 before this service listens for the machine's address changes, 1 while it does, 2 once <see cref="Dispose"/> ended it (never again).</summary>
    private int _namesWatched;

    /// <summary>The addresses a request this desk did not grant by its name or page has been logged for: each once per kind, at most <see cref="ForeignSaidMost"/>, and one line when no more are named.</summary>
    private readonly SaidOnce _foreignSaid = new(ForeignSaidMost);

    private const int ForeignSaidMost = 64;

    /// <summary>Why a request was answered as another machine's although it arrived over loopback or carried the pages' cookie, or refused on an open desk.</summary>
    private enum Foreign
    {
        /// <summary>Its Host does not name this desk (<see cref="DeskNames"/>): a page whose name was rebound to this machine, or a forwarder's traffic.</summary>
        Host,

        /// <summary>A page of another origin sent it (<see cref="HttpHead.CrossSite"/>): a site open in a browser on this machine.</summary>
        Page,

        /// <summary>On an open desk, a browser's request under a Host that does not name this desk: a page whose name was rebound to it.</summary>
        OpenHost,
    }

    /// <summary>
    /// The machine's own names (<see cref="DeskNames.Of"/>), read again when the beacon's name changes or the machine's
    /// addresses do (an interface's DNS suffix comes with its address, and a desk that started before its network was up
    /// learns it then). The addresses themselves are not read here: a Host that is an address is compared with the one its
    /// connection arrived on.
    /// </summary>
    private IReadOnlyList<string> MachineNames()
    {
        var beacon = _kernel.Beacon.MachineName;
        var generation = Volatile.Read(ref _namesGeneration);
        if (_deskNames is { } known && known.Generation == generation && string.Equals(known.Beacon, beacon, StringComparison.Ordinal)) return known.Names;
        WatchAddresses();
        var names = DeskNames.Of(Environment.MachineName, HostName(), DnsSuffixes(), beacon);
        _deskNames = new DeskNameSet(beacon, generation, names);
        return names;
    }

    private void WatchAddresses()
    {
        if (_disposed || Interlocked.CompareExchange(ref _namesWatched, 1, 0) != 0) return;   // once; never after Dispose
        try
        {
            NetworkChange.NetworkAddressChanged += AddressesChanged;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or SocketException)
        {
            Log.Warn("Web remote: the machine's address changes cannot be followed here; the desk's names are read once, as they are now.", ex);
        }
    }

    private void AddressesChanged(object? sender, EventArgs e) => Interlocked.Increment(ref _namesGeneration);

    /// <summary>The subscription <see cref="WatchAddresses"/> made, ended (from <see cref="Dispose"/>).</summary>
    private void UnwatchAddresses()
    {
        if (Interlocked.Exchange(ref _namesWatched, 2) != 1) return;
        try
        {
            NetworkChange.NetworkAddressChanged -= AddressesChanged;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or SocketException)
        {
            Log.Warn("Web remote: the address-change subscription could not be ended.", ex);
        }
    }

    private static string HostName()
    {
        try
        {
            return System.Net.Dns.GetHostName();
        }
        catch (SocketException ex)
        {
            Log.Warn("Web remote: the machine's host name could not be read; its NetBIOS name and addresses still name this desk.", ex);
            return "";
        }
    }

    /// <summary>The primary DNS suffix and each live interface's own, as the system has them now (an empty one is skipped by <see cref="DeskNames.Of"/>).</summary>
    private static IReadOnlyList<string> DnsSuffixes()
    {
        var suffixes = new List<string>();
        try
        {
            suffixes.Add(IPGlobalProperties.GetIPGlobalProperties().DomainName);
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    suffixes.Add(nic.GetIPProperties().DnsSuffix);
                }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
                {
                    Log.Warn($"Web remote: the DNS suffix of interface '{nic.Name}' could not be read; the machine's other names still name this desk.", ex);
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            Log.Warn("Web remote: the machine's DNS suffixes could not be read; its names without them still name this desk.", ex);
        }
        return suffixes;
    }

    /// <summary>
    /// Round 89 (PeerLink profile 13.3.4): a request that this machine's own trust or the pages' cookie would have paired —
    /// or, on an open desk, any browser's request — was answered as another machine's, because its Host is not one of this
    /// desk's names (a page whose name was rebound to this machine, a forwarder's traffic) or a page of another origin sent
    /// it. The first such request of each kind from each address writes one log line naming the address and the Host or
    /// the Origin; later ones write none.
    /// </summary>
    private void ForeignSaid(TcpClient client, Foreign why, HttpHead request)
    {
        var address = AddressKey(client);
        var said = _foreignSaid.Note(why + "|" + address);
        if (said == SaidOnceAnswer.Full)
        {
            Log.Warn($"Web remote: requests answered as another machine's have been named for {ForeignSaidMost} addresses and kinds — no more are named in this log.");
            return;
        }
        if (said != SaidOnceAnswer.Say) return;
        Log.Warn(why switch
        {
            Foreign.Host => $"Web remote: a request from {address} named this desk {DeskNames.Shown(request.Host)}, which is not one of its names — answered as another machine's, with no trust for this machine and no pairing cookie (a page whose name was rebound to this machine does this). Said once for this address.",
            Foreign.Page => $"Web remote: a page from another origin ({(request.Origin.Trim().Length > 0 ? DeskNames.Shown(request.Origin) : "Sec-Fetch-Site " + DeskNames.Shown(request.SecFetchSite))}) asked this desk from {address} — answered as another machine's, with no trust for this machine (a site open in a browser on this machine does this). Said once for this address.",
            _ => $"Web remote: a browser at {address} named this desk {DeskNames.Shown(request.Host)}, which is not one of its names — with no pairing token set, its requests to the show are refused (a page whose name was rebound to this desk does this; open the desk by its own name). Said once for this address.",
        });
    }
}
