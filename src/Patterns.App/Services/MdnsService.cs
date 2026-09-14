using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia.Threading;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// This process on the network by name: an mDNS responder that announces "Patterns desk FOH-PC"
/// under _patterns._tcp (so a Companion lists it under Desk on the network and nobody types an
/// address), answers the queries a browser sends, says goodbye when the wire closes — and a
/// browser of its own for the Companions that announce themselves, so the Remote page can say
/// which Companion is on the network and where. One socket on 5353, shared with the system's
/// own responder; a socket that will not open is said, and nothing else stops.
/// </summary>
public sealed class MdnsService : IDisposable
{
    private readonly ServiceKernel _kernel;
    private readonly DispatcherTimer _timer;
    private readonly MdnsBrowser _companions = new(CompanionModule.SatelliteServiceType);
    private readonly object _gate = new();
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private MdnsAdvert? _advert;
    private string _signature = "";
    private volatile string _status = "";
    private volatile string _fault = "";
    private int _ticks;
    private long _queries;
    private long _answers;
    private IReadOnlyList<MdnsPeer> _heard = Array.Empty<MdnsPeer>();

    public MdnsService(ServiceKernel kernel)
    {
        _kernel = kernel;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>What the responder is doing, for the Remote page: announced as what, or why not.</summary>
    public string Status => _status;

    /// <summary>The socket's fault, "" while it is open.</summary>
    public string Fault => _fault;

    public bool Announcing => _socket is not null && _advert is not null;

    /// <summary>The advert as last built — what a browser hears.</summary>
    public MdnsAdvert? Advert => _advert;

    public long Queries => Interlocked.Read(ref _queries);
    public long Answers => Interlocked.Read(ref _answers);

    /// <summary>The Companions heard announcing their satellite port, newest reading.</summary>
    public IReadOnlyList<MdnsPeer> Companions => _heard;

    /// <summary>Opens, re-announces or closes to match the settings (UI thread, on every publish).</summary>
    public void Reconcile()
    {
        var cfg = _kernel.State.Control;
        var on = cfg.Enabled && cfg.Announce;
        if (!on)
        {
            if (_socket is not null) Close(goodbye: true);
            _advert = null;
            _signature = "";
            _status = cfg.Enabled ? "Not announced on the network (ANNOUNCE is off)." : "";
            return;
        }
        var advert = Build();
        if (_socket is null)
        {
            _advert = advert;
            _signature = advert.Signature;
            Open();
            return;
        }
        if (advert.Signature != _signature)
        {
            _advert = advert;
            _signature = advert.Signature;
            Announce();
        }
    }

    /// <summary>This process's advert from the live settings and the machine's addresses.</summary>
    public MdnsAdvert Build()
    {
        var s = _kernel.State;
        return new MdnsAdvert(
            NodeKinds.Wire(_kernel.Profile), _kernel.Beacon.MachineName, s.Name, s.Control.TcpPort, s.Control.HttpPort, _kernel.Link.LinkPort,
            _kernel.Beacon.Instance, AppVersion.Current, LocalAddresses());
    }

    /// <summary>Every IPv4 address this machine has on a live interface — a Companion on any of its networks resolves the desk.</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address)) list.Add(ua.Address);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("mDNS: the interfaces could not be read.", ex);
        }
        return list;
    }

    private void Open()
    {
        var notes = new List<string>();
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.ExclusiveAddressUse = false;
            socket.Bind(new IPEndPoint(IPAddress.Any, DnsSd.Port));
            var joined = 0;
            foreach (var address in LocalAddresses())
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(DnsSd.Group, address));
                    joined++;
                }
                catch (Exception ex)
                {
                    notes.Add($"no multicast on {address}: {ex.Message}");
                }
            }
            if (joined == 0)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(DnsSd.Group, IPAddress.Any));
                    joined++;
                }
                catch (Exception ex)
                {
                    notes.Add($"no multicast: {ex.Message}");
                }
            }
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            }
            catch (Exception)
            {
                // Defaults are fine.
            }
            _socket = socket;
            _cts = new CancellationTokenSource();
            _fault = "";
            _ = ReceiveLoop(socket, _cts.Token);
            _status = $"Announced on the network as \"{_advert!.InstanceLabel}\" — pick it under Desk on the network in Companion's connection settings."
                      + (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "");
            Log.Info($"mDNS: announcing {_advert.InstanceLabel} on {MdnsAdvert.ServiceType}, port {_advert.WirePort}.");
            Announce();
            Send(_companions.Query());
            _timer.Start();
        }
        catch (Exception ex)
        {
            _fault = ex.Message;
            _status = $"Not announced — port {DnsSd.Port} could not be opened ({ex.Message}); a Companion needs this machine's address typed.";
            Log.Warn("mDNS responder could not open.", ex);
            _socket?.Dispose();
            _socket = null;
        }
    }

    private void Close(bool goodbye)
    {
        _timer.Stop();
        var socket = _socket;
        if (socket is null) return;
        try
        {
            if (goodbye && _advert is not null) Send(_advert.Goodbye(), socket);
        }
        catch (Exception)
        {
            // The socket may already be gone.
        }
        _cts?.Cancel();
        _cts = null;
        _socket = null;
        try { socket.Dispose(); } catch (Exception) { }
    }

    /// <summary>The announcement, unasked: on open, on a change, and once a minute so a browser that joined late hears it before the TTL runs out.</summary>
    private void Announce()
    {
        if (_advert is null) return;
        Send(_advert.Announcement());
    }

    private void Tick()
    {
        _ticks++;
        if (_ticks % 60 == 0) Announce();
        if (_ticks % 30 == 1) Send(_companions.Query());
        lock (_gate)
        {
            _heard = _companions.Peers(DateTime.UtcNow);
        }
    }

    /// <summary>A packet to the group, from every interface the machine has — a Companion on any of its networks hears it.</summary>
    private void Send(DnsPacket packet, Socket? socket = null)
    {
        socket ??= _socket;
        if (socket is null) return;
        var bytes = packet.ToBytes();
        var to = new IPEndPoint(DnsSd.Group, DnsSd.Port);
        var sent = false;
        foreach (var address in LocalAddresses())
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                socket.SendTo(bytes, to);
                sent = true;
            }
            catch (Exception)
            {
                // That interface will not carry multicast; the next may.
            }
        }
        if (!sent)
        {
            try { socket.SendTo(bytes, to); } catch (Exception ex) { Log.Warn("mDNS send failed.", ex); }
        }
    }

    /// <summary>
    /// A datagram heard: a query about this process is answered — to the asker alone when it asked
    /// from a port of its own (a one-shot resolver), to the group otherwise — and an answer from a
    /// Companion goes to the browser. Returns the reply, for the tests; null when there is none.
    /// </summary>
    public DnsPacket? Handle(byte[] data, IPEndPoint from)
    {
        var packet = DnsPacket.Parse(data);
        if (packet is null) return null;
        if (packet.IsResponse)
        {
            lock (_gate)
            {
                if (_companions.Hear(packet, from.Address, DateTime.UtcNow)) _heard = _companions.Peers(DateTime.UtcNow);
            }
            return null;
        }
        var advert = _advert;
        if (advert is null) return null;
        Interlocked.Increment(ref _queries);
        var reply = MdnsResponder.Answer(packet, advert);
        if (reply is null) return null;
        Interlocked.Increment(ref _answers);
        var legacy = from.Port != DnsSd.Port || packet.Questions.Any(q => q.UnicastReply);
        var socket = _socket;
        if (socket is not null)
        {
            try
            {
                if (legacy) socket.SendTo(reply.ToBytes(), from);
                else Send(reply, socket);
            }
            catch (Exception ex)
            {
                Log.Warn("mDNS reply failed.", ex);
            }
        }
        return reply;
    }

    private async Task ReceiveLoop(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[9000];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
                if (result.ReceivedBytes <= 0) continue;
                var data = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
                Handle(data, (IPEndPoint)result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Log.Warn("mDNS receive failed.", ex);
                await Task.Delay(200, CancellationToken.None);
            }
        }
    }

    public void Dispose() => Close(goodbye: true);
}
