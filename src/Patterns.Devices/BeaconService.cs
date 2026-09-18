using System.Net;
using System.Net.Sockets;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.Devices;

/// <summary>
/// The heartbeat beacon: this machine's <see cref="Beacon"/> once a second as a UDP datagram
/// to a host or the whole network, and — on the machine that is the backup — a listener that
/// keeps the last beacon heard and says, on the health line and the Machine page, whether the
/// main machine is alive, silent, or stood down. The supervisor sends one last beacon when it
/// gives up, so a backup hears about a crash loop as well as a dead machine.
/// </summary>
public sealed class BeaconService : IDisposable, IBeaconIdentity
{
    private readonly IBeaconHost _services;
    private readonly IDispatchTimer _timer;
    private UdpClient? _sender;
    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private volatile IPEndPoint? _target;
    private string _activeKey = "";
    private volatile string _status = "Beacon off.";
    private long _seq;
    private long _sent;
    private long _heard;
    private volatile Beacon? _last;
    private DateTime? _lastSeenUtc;
    private volatile IPEndPoint? _lastFrom;

    public BeaconService(IBeaconHost kernel)
    {
        _services = kernel;
        _timer = Dispatch.Timer(TimeSpan.FromSeconds(1));
        _timer.Tick += () => Tick();
    }

    /// <summary>A random id per process, so a machine hearing its own broadcast ignores it.</summary>
    public string Instance { get; } = Guid.NewGuid().ToString("N")[..8];

    public string Status => _status;

    /// <summary>Round 77: the listener's last bind failed (the port held by the desk this one replaces, usually); the poll asks again.</summary>
    public bool BindFailed { get; private set; }

    /// <summary>Round 78: how many binds in a row have failed — the first is the warning, then a line a minute (the poll asks every five seconds).</summary>
    public int BindFailures { get; private set; }

    public bool Sending => _sender is not null && _target is not null;
    public bool Listening => _listener is not null;
    public long Sent => Interlocked.Read(ref _sent);
    public long Heard => Interlocked.Read(ref _heard);
    public Beacon? LastBeacon => _last;

    /// <summary>Where the last beacon came from — the address a standby twin dials when no main was named.</summary>
    public IPEndPoint? LastFrom => _lastFrom;

    /// <summary>How this machine names itself.</summary>
    public string MachineName => _services.State.Watchdog.BeaconName.Length > 0 ? _services.State.Watchdog.BeaconName : Environment.MachineName;

    /// <summary>The health line's words while listening — alive, silent, stood down; "" otherwise.</summary>
    public string WatchText => Listening ? BeaconWatch.Describe(_last, _lastSeenUtc, DateTime.UtcNow) : "";

    /// <summary>Opens / closes the sender and the listener to match the config (UI thread).</summary>
    public void Reconcile()
    {
        var cfg = _services.State.Watchdog;
        // Nodes find each other on the beacon: a node always sends and hears, and so does a desk
        // that accepts callers — the Machine page's own switches add to that, never take from it.
        var nodes = !_services.IsDesk || _services.State.Twin.AcceptCallers;
        var send = cfg.BeaconEnabled || nodes;
        var listen = cfg.BeaconListen || nodes;
        var key = $"{send}|{cfg.BeaconHost}|{cfg.BeaconPort}|{listen}|{cfg.BeaconListenPort}";
        if (key == _activeKey) return;
        _activeKey = key;
        BindFailed = false;
        Stop();
        var notes = new List<string>();
        if (send)
        {
            try
            {
                _sender = new UdpClient { EnableBroadcast = true };
                notes.Add(ResolveTarget(cfg.BeaconHost, cfg.BeaconPort));
            }
            catch (Exception ex)
            {
                notes.Add($"beacon could not open a socket: {ex.Message}");
                Log.Warn("Beacon sender failed.", ex);
            }
        }
        if (listen)
        {
            try
            {
                _cts ??= new CancellationTokenSource();
                _listener = new UdpClient(new IPEndPoint(IPAddress.Any, cfg.BeaconListenPort));
                _ = ReceiveLoop(_listener, _cts.Token);
                notes.Add($"listening on port {cfg.BeaconListenPort}");
                if (BindFailures > 0) Log.Info($"Beacon listening on port {cfg.BeaconListenPort} after {BindFailures} failed {(BindFailures == 1 ? "try" : "tries")}.");
                BindFailures = 0;
            }
            catch (Exception ex)
            {
                notes.Add($"could not listen on port {cfg.BeaconListenPort}: {ex.Message}");
                // Round 78: the port is usually the old desk's through a handover, gone within seconds — the poll asks every
                // five seconds (AppServices.RetryListeners), so the first failure is the warning and the rest a line a minute.
                if (BindFailures++ == 0) Log.Warn("Beacon listener failed.", ex);
                else if (BindFailures % 12 == 0) Log.Warn($"Beacon still cannot listen on port {cfg.BeaconListenPort} ({BindFailures} tries): {ex.Message}");
                _listener = null;
                BindFailed = true;
                _activeKey = "";   // round 77: asked again on the next change, and from the desk's poll
            }
        }
        _status = notes.Count == 0 ? "Beacon off." : string.Join(" · ", notes) + ".";
        if (_sender is not null || _listener is not null)
        {
            _timer.Start();
            Tick();
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <summary>The target: an address (or the broadcast address) at once, a host name looked up off the UI thread.</summary>
    private string ResolveTarget(string host, int port)
    {
        if (host.Length == 0 || host == "255.255.255.255" || host.Equals("broadcast", StringComparison.OrdinalIgnoreCase))
        {
            _target = new IPEndPoint(IPAddress.Broadcast, port);
            return $"beacon to everyone on this network (port {port}) as {MachineName}";
        }
        if (IPAddress.TryParse(host, out var address))
        {
            _target = new IPEndPoint(address, port);
            return $"beacon to {address}:{port} as {MachineName}";
        }
        var sender = _sender; // the lookup belongs to this socket; an answer for a host since changed is thrown away
        _ = Task.Run(() =>
        {
            IPAddress? pick = null;
            try
            {
                var all = Dns.GetHostAddresses(host);
                pick = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault();
            }
            catch
            {
                // Not found: the status below says so.
            }
            Dispatch.Post(() =>
            {
                if (_sender is null || !ReferenceEquals(_sender, sender)) return;
                if (pick is null)
                {
                    _status = $"beacon host '{host}' not found — nothing goes out.";
                    return;
                }
                _target = new IPEndPoint(pick, port);
                _status = _status.Replace($"looking up '{host}'", $"beacon to {pick}:{port} ({host}) as {MachineName}");
            });
        }, _cts?.Token ?? CancellationToken.None);
        return $"looking up '{host}'";
    }

    private void Tick()
    {
        var sender = _sender;
        var to = _target;
        if (sender is not null && to is not null)
        {
            var bytes = Build().ToBytes();
            _ = Task.Run(() =>
            {
                try
                {
                    sender.Send(bytes, to);
                    Interlocked.Increment(ref _sent);
                }
                catch (Exception ex)
                {
                    Log.Warn("Beacon send failed.", ex);
                }
            }, _cts?.Token ?? CancellationToken.None);
        }
    }

    /// <summary>This machine's heartbeat, from the live show (UI thread).</summary>
    public Beacon Build()
    {
        var s = _services.State;
        var air = _services.Air;                       // the desk's outputs, stack and metrics; nothing on a node
        return new Beacon
        {
            Machine = MachineName,
            Instance = Instance,
            Seq = Interlocked.Increment(ref _seq),
            Utc = DateTime.UtcNow,
            Up = Math.Round((DateTime.UtcNow - HealthMonitor.StartedUtc).TotalSeconds),
            Live = air.OutputsLive,
            Blackout = s.Blackout,
            Program = air.AirLabel,
            Armed = air.Armed,
            Standby = air.StandbyWords,
            Last = air.LastCueNumber,
            Health = HealthMonitor.Summary(DateTime.UtcNow),
            Faults = HealthMonitor.Faults,
            Restarts = HealthMonitor.Restarts,
            Fps = air.Fps,
            Windows = air.Windows,
            Stream = s.Stream.Active,
            Show = s.Name,
            Twin = s.Twin.Role == TwinRole.Main ? s.Twin.Port : 0,
            Kind = NodeKinds.Wire(_services.Profile),
            Wire = s.Control.Enabled ? s.Control.TcpPort : 0,
            Http = s.Control.Enabled ? s.Control.HttpPort : 0,
            Link = _services.Link.LinkPort,
        };
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Beacon Beacon, IPEndPoint From, DateTime HeardUtc)> _peers = new(StringComparer.Ordinal);

    /// <summary>Every other process heard, by instance, with where and when — the Nodes page's raw material.</summary>
    public IReadOnlyList<(Beacon Beacon, IPEndPoint From, DateTime HeardUtc)> Peers()
    {
        var now = DateTime.UtcNow;
        foreach (var stale in _peers.Where(p => now - p.Value.HeardUtc > NodeRegistry.ForgottenAfter).Select(p => p.Key).ToList()) _peers.TryRemove(stale, out _);
        return _peers.Values.ToList();
    }

    /// <summary>A beacon heard — from the socket, or handed in by a test.</summary>
    public void Hear(Beacon beacon, IPEndPoint from)
    {
        if (beacon.Instance == Instance) return;
        var now = DateTime.UtcNow;
        _last = beacon;
        _lastFrom = from;
        _lastSeenUtc = now;
        _peers[beacon.Instance] = (beacon, from, now);
        Interlocked.Increment(ref _heard);
    }

    private async Task ReceiveLoop(UdpClient listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try
                {
                    r = await listener.ReceiveAsync(ct);
                }
                catch (SocketException)
                {
                    await Task.Delay(10, ct);
                    continue;
                }
                var beacon = Beacon.Parse(r.Buffer);
                if (beacon is null || beacon.Instance == Instance) continue; // not a beacon, or our own broadcast coming back
                Hear(beacon, r.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped: the token was cancelled
        }
        catch (ObjectDisposedException)
        {
            // stopped: the socket was closed under the receive
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            _ = ex;
        }
        catch (Exception ex)
        {
            Log.Warn("Beacon listener ended.", ex);
            _status = $"Beacon listener stopped: {ex.Message}";
        }
    }

    /// <summary>
    /// A supervisor that stands down (a crash loop, an app it could not start) tells the network
    /// once — three datagrams, so one lost packet does not lose the news. No app is running then,
    /// so this needs only the settings.
    /// </summary>
    public static void SendEvent(WatchdogConfig cfg, string eventName)
    {
        try
        {
            var host = cfg.BeaconHost;
            IPAddress address;
            if (host.Length == 0 || host == "255.255.255.255" || host.Equals("broadcast", StringComparison.OrdinalIgnoreCase))
            {
                address = IPAddress.Broadcast;
            }
            else if (!IPAddress.TryParse(host, out address!))
            {
                var all = Dns.GetHostAddresses(host);
                address = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.First();
            }
            var beacon = new Beacon
            {
                Machine = cfg.BeaconName.Length > 0 ? cfg.BeaconName : Environment.MachineName,
                Instance = "supervisor",
                Utc = DateTime.UtcNow,
                Health = $"watchdog {eventName}",
                Event = eventName,
            };
            using var udp = new UdpClient { EnableBroadcast = true };
            var bytes = beacon.ToBytes();
            var to = new IPEndPoint(address, cfg.BeaconPort);
            for (var i = 0; i < 3; i++)
            {
                udp.Send(bytes, to);
#pragma warning disable RS0030 // the supervisor's last word at stand-down: three datagrams 100 ms apart on its own thread, which has nothing else to do
                Thread.Sleep(100);
#pragma warning restore RS0030
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Beacon event failed.", ex);
        }
    }

    private void Stop()
    {
        _timer.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        var sender = _sender;
        var listener = _listener;
        _sender = null;
        _listener = null;
        _target = null;
        try { sender?.Close(); } catch { /* already down */ }
        try { listener?.Close(); } catch { /* already down */ }
        _last = null;
        _lastFrom = null;
        _lastSeenUtc = null;
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }
}
