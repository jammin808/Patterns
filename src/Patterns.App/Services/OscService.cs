using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// OSC over UDP: messages in on one port, mapped through <see cref="OscMap"/> onto the same
/// protocol lines the TCP port runs — so an OSC command has exactly the TCP command's meaning,
/// checks and journal entry — with /patterns/pong and /patterns/error answered to the sender;
/// and the state out as one /patterns/state/… bundle to a feedback host on every change,
/// throttled like the TCP pushes. A raw UdpClient on purpose: no admin rights, portable.
/// </summary>
public sealed class OscService : IDisposable
{
    private readonly AppServices _services;
    private readonly CommandRouter _router;
    private readonly DispatcherTimer _pushTimer;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private volatile IPEndPoint? _feedback;
    private string _activeKey = "";
    private volatile string _status = "OSC off.";
    private volatile string _lastLine = "";
    private long _received;
    private long _sent;
    private bool _pushPending;
    private bool _stackHooked;

    public OscService(AppServices services)
    {
        _services = services;
        _router = new CommandRouter(services);

        // Feedback is throttled to a trailing 200 ms, like the TCP STATE pushes.
        _pushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _pushTimer.Tick += (_, _) =>
        {
            _pushTimer.Stop();
            if (!_pushPending) return;
            _pushPending = false;
            SendFeedback();
        };
        _services.SnapshotPublished += MarkChanged;
    }

    /// <summary>"OSC in on port 9698 · feedback to 10.0.0.9:9699." — or why not.</summary>
    public string Status => _status;

    /// <summary>The last message handled: what came in, the line it became, the answer.</summary>
    public string LastLine => _lastLine;

    public long Received => Interlocked.Read(ref _received);
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>Where feedback goes once the host resolved; null = nowhere.</summary>
    public IPEndPoint? FeedbackEndpoint => _feedback;

    /// <summary>The status with the counts and the last message, for the Remote page.</summary>
    public string StatusLine
    {
        get
        {
            var s = _status;
            var received = Received;
            if (received > 0) s += $" {received} in, {Sent} out.";
            if (_lastLine.Length > 0) s += $" Last: {_lastLine}";
            return s;
        }
    }

    private void MarkChanged()
    {
        if (_udp is null || _feedback is null) return;
        _pushPending = true;
        if (!_pushTimer.IsEnabled) _pushTimer.Start();
    }

    /// <summary>The stack's runtime is not in the snapshot: STANDBY, ARM and HOLD push on their own event. Hooked lazily — the stack is built after this service.</summary>
    private void HookStack()
    {
        if (_stackHooked || _services.CueStack is null) return;
        _stackHooked = true;
        _services.CueStack.Changed += MarkChanged;
    }

    /// <summary>Opens / closes / rebinds the port to match the config (UI thread).</summary>
    public void Reconcile()
    {
        HookStack();
        var cfg = _services.State.Control;
        var on = cfg.Enabled && cfg.OscEnabled;
        var key = on ? $"{cfg.OscPort}|{cfg.OscFeedbackHost}|{cfg.OscFeedbackPort}" : "";
        if (key == _activeKey) return;
        _activeKey = key;

        Stop();
        if (!on)
        {
            _status = cfg.Enabled ? "OSC off." : "Remote control off.";
            return;
        }

        _cts = new CancellationTokenSource();
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, cfg.OscPort));
            _ = ReceiveLoop(_udp, _cts.Token);
            _status = $"OSC in on port {cfg.OscPort} · {ResolveFeedback(cfg.OscFeedbackHost, cfg.OscFeedbackPort, cfg.OscPort)}.";
            Log.Info(_status);
        }
        catch (Exception ex)
        {
            _status = $"OSC failed to open port {cfg.OscPort}: {ex.Message}";
            Log.Error("OSC start failed.", ex);
            Stop();
            _activeKey = ""; // retry on the next change
        }
    }

    /// <summary>
    /// The feedback endpoint: an address at once, a host name looked up off the UI thread
    /// (a wrong name on a show LAN can take seconds to fail) — the status says which.
    /// </summary>
    private string ResolveFeedback(string host, int port, int inPort)
    {
        if (host.Length == 0) return "no feedback host (replies still go to the sender)";
        if (IPAddress.TryParse(host, out var address))
        {
            _feedback = new IPEndPoint(address, port);
            MarkChanged(); // the first state goes out at once
            return $"feedback to {address}:{port}";
        }
        _ = Task.Run(() =>
        {
            try
            {
                var addresses = Dns.GetHostAddresses(host);
                var pick = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                Dispatcher.UIThread.Post(() =>
                {
                    if (_udp is null) return; // stopped meanwhile
                    if (pick is null)
                    {
                        _status = $"OSC in on port {inPort} · feedback host '{host}' not found.";
                        return;
                    }
                    _feedback = new IPEndPoint(pick, port);
                    _status = $"OSC in on port {inPort} · feedback to {pick}:{port} ({host}).";
                    MarkChanged();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => { if (_udp is not null) _status = $"OSC in on port {inPort} · feedback host '{host}' not found ({ex.Message})."; });
            }
        });
        return $"looking up feedback host '{host}'";
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try
                {
                    r = await udp.ReceiveAsync(ct);
                }
                catch (SocketException)
                {
                    // Windows reports an ICMP "port unreachable" (a reply nobody listens for) on the next receive: the port stays open.
                    await Task.Delay(10, ct);
                    continue;
                }
                Interlocked.Increment(ref _received);
                foreach (var m in OscCodec.Decode(r.Buffer))
                {
                    await HandleAsync(m, r.RemoteEndPoint);
                }
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
            _ = ex; // closed underneath us — normal shutdown
        }
        catch (Exception ex)
        {
            Log.Warn("OSC receive loop ended.", ex);
            _status = $"OSC stopped: {ex.Message}";
        }
    }

    private async Task HandleAsync(OscMessage m, IPEndPoint from)
    {
        var line = OscMap.ToLine(m);
        if (line is null)
        {
            _lastLine = $"{m} → not a Patterns address";
            Reply(from, OscMessage.Of("/patterns/error", $"unknown address {m.Address}"));
            return;
        }
        var cmd = ControlProtocol.Parse(line);
        var response = await _router.ExecuteAsync(cmd, new ActionOrigin(OriginKind.Osc, "", from.ToString()));
        _lastLine = $"{m} → {line} → {Shorten(response)}";
        if (cmd.Kind == RemoteCommandKind.Ping)
        {
            Reply(from, OscMessage.Of("/patterns/pong"));
        }
        else if (cmd.Kind == RemoteCommandKind.Status)
        {
            Reply(from, OscMessage.Of("/patterns/status", response.Length > 3 ? response[3..] : ""));
        }
        else if (!response.StartsWith("OK", StringComparison.Ordinal))
        {
            Reply(from, OscMessage.Of("/patterns/error", response));
        }
    }

    private void Reply(IPEndPoint to, OscMessage m)
    {
        var udp = _udp;
        if (udp is null) return;
        try
        {
            udp.Send(OscCodec.Encode(m), to);
            Interlocked.Increment(ref _sent);
        }
        catch (Exception ex)
        {
            Log.Warn("OSC reply failed.", ex);
        }
    }

    /// <summary>The state as one bundle to the feedback host: the JSON is built here on the UI thread, the send is not.</summary>
    private void SendFeedback()
    {
        var udp = _udp;
        var to = _feedback;
        if (udp is null || to is null) return;
        var json = _router.StateJson();
        _ = Task.Run(() =>
        {
            try
            {
                udp.Send(OscCodec.EncodeBundle(OscFeedback.FromState(json)), to);
                Interlocked.Increment(ref _sent);
            }
            catch (Exception ex)
            {
                Log.Warn("OSC feedback failed.", ex);
            }
        });
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _feedback = null;
        var udp = _udp;
        _udp = null;
        try { udp?.Close(); } catch { /* already down */ }
    }

    public void Dispose()
    {
        _pushTimer.Stop();
        _services.SnapshotPublished -= MarkChanged;
        Stop();
    }

    private static string Shorten(string s) => s.Length <= 72 ? s : s[..70] + "…";
}
