using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The heartbeat beacon: this machine's <see cref="Beacon"/> once a second as a UDP datagram
/// to a host or the whole network, and — on the machine that is the backup — a listener that
/// keeps the last beacon heard and says, on the health line and the Machine page, whether the
/// main machine is alive, silent, or stood down. The supervisor sends one last beacon when it
/// gives up, so a backup hears about a crash loop as well as a dead machine.
/// </summary>
public sealed class BeaconService : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
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

    public BeaconService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>A random id per process, so a machine hearing its own broadcast ignores it.</summary>
    public string Instance { get; } = Guid.NewGuid().ToString("N")[..8];

    public string Status => _status;
    public bool Sending => _sender is not null && _target is not null;
    public bool Listening => _listener is not null;
    public long Sent => Interlocked.Read(ref _sent);
    public long Heard => Interlocked.Read(ref _heard);
    public Beacon? LastBeacon => _last;
    public DateTime? LastSeenUtc => _lastSeenUtc;

    /// <summary>How this machine names itself.</summary>
    public string MachineName => _services.State.Watchdog.BeaconName.Length > 0 ? _services.State.Watchdog.BeaconName : Environment.MachineName;

    /// <summary>The health line's words while listening — alive, silent, stood down; "" otherwise.</summary>
    public string WatchText => Listening ? BeaconWatch.Describe(_last, _lastSeenUtc, DateTime.UtcNow) : "";

    /// <summary>Opens / closes the sender and the listener to match the config (UI thread).</summary>
    public void Reconcile()
    {
        var cfg = _services.State.Watchdog;
        var key = $"{cfg.BeaconEnabled}|{cfg.BeaconHost}|{cfg.BeaconPort}|{cfg.BeaconListen}|{cfg.BeaconListenPort}";
        if (key == _activeKey) return;
        _activeKey = key;
        Stop();
        var notes = new List<string>();
        if (cfg.BeaconEnabled)
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
        if (cfg.BeaconListen)
        {
            try
            {
                _cts ??= new CancellationTokenSource();
                _listener = new UdpClient(new IPEndPoint(IPAddress.Any, cfg.BeaconListenPort));
                _ = ReceiveLoop(_listener, _cts.Token);
                notes.Add($"listening on port {cfg.BeaconListenPort}");
            }
            catch (Exception ex)
            {
                notes.Add($"could not listen on port {cfg.BeaconListenPort}: {ex.Message}");
                Log.Warn("Beacon listener failed.", ex);
                _listener = null;
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
            Dispatcher.UIThread.Post(() =>
            {
                if (_sender is null) return;
                if (pick is null)
                {
                    _status = $"beacon host '{host}' not found — nothing goes out.";
                    return;
                }
                _target = new IPEndPoint(pick, port);
                _status = _status.Replace($"looking up '{host}'", $"beacon to {pick}:{port} ({host}) as {MachineName}");
            });
        });
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
            });
        }
    }

    /// <summary>This machine's heartbeat, from the live show (UI thread).</summary>
    public Beacon Build()
    {
        var s = _services.State;
        var stack = _services.CueStack;
        var standby = stack?.StandbyCue;
        var metrics = _services.Metrics.Current;
        return new Beacon
        {
            Machine = MachineName,
            Instance = Instance,
            Seq = Interlocked.Increment(ref _seq),
            Utc = DateTime.UtcNow,
            Up = Math.Round((DateTime.UtcNow - HealthMonitor.StartedUtc).TotalSeconds),
            Live = _services.Outputs.IsLive,
            Blackout = s.Blackout,
            Program = _services.AirLabel,
            Armed = stack?.Runtime.Armed ?? false,
            Standby = standby is null ? "" : $"{standby.Number} {standby.Name}".Trim(),
            Last = stack?.LastCue?.Number ?? "",
            Health = HealthMonitor.Summary(DateTime.UtcNow),
            Faults = HealthMonitor.Faults,
            Restarts = HealthMonitor.Restarts,
            Fps = metrics is null ? 0 : Math.Round(metrics.OutputWindows > 0 ? metrics.OutputFps : metrics.PreviewFps, 1),
            Windows = metrics?.OutputWindows ?? 0,
            Stream = s.Stream.Active,
            Show = s.Name,
        };
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
                _last = beacon;
                _lastSeenUtc = DateTime.UtcNow;
                Interlocked.Increment(ref _heard);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
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
                Thread.Sleep(100);
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
        _cts = null;
        var sender = _sender;
        var listener = _listener;
        _sender = null;
        _listener = null;
        _target = null;
        try { sender?.Close(); } catch { /* already down */ }
        try { listener?.Close(); } catch { /* already down */ }
        _last = null;
        _lastSeenUtc = null;
    }

    public void Dispose() => Stop();
}
