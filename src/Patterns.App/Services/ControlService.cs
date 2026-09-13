using Patterns.Core.Model;
using Patterns.Core.Play;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Threading;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// The remote-control server: a TCP line protocol on one port (Bitfocus Companion — generic
/// TCP or the Patterns module, which also receives pushed STATE lines for feedback) and a
/// tiny HTTP server on another serving the phone/tablet web remote plus /api endpoints.
/// Raw TcpListener on purpose: no admin rights, no URL ACLs, portable.
/// </summary>
public sealed partial class ControlService : IDisposable
{
    private readonly ServiceKernel _kernel;
    private readonly IWireHost _services;
    private readonly CommandRouter _router;
    private readonly object _gate = new();
    private readonly List<TcpClient> _tcpClients = new();
    private TcpListener? _tcp;
    private TcpListener? _http;
    private TcpListener? _audience;
    private int _audienceConnections;
    private readonly Dictionary<string, int> _audienceByAddress = new();
    private CancellationTokenSource? _cts;
    private string _activeKey = "";
    private volatile string _status = "Remote control off.";
    private readonly DispatcherTimer _pushTimer;
    private bool _pushPending;

    public ControlService(ServiceKernel kernel, IWireHost host)
    {
        _kernel = kernel;
        _services = host;
        _router = host.NewRouter();

        // State pushes to Companion are throttled to a trailing 200 ms.
        _pushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _pushTimer.Tick += (_, _) =>
        {
            _pushTimer.Stop();
            if (!_pushPending) return;
            _pushPending = false;
            var json = _router.StateJson();
            _ = Task.Run(() => Broadcast("STATE " + json));
        };
        void Moved()
        {
            Interlocked.Increment(ref _rev);
            _pushPending = true;
            if (!_pushTimer.IsEnabled) _pushTimer.Start();
        }
        _services.SnapshotPublished += Moved;
        _services.RuntimeChanged += Moved;   // "audio playing", "stream live": in STATE, never in a snapshot
        _router.Rev = () => Interlocked.Read(ref _rev);

        // The caller's VT clock moves every second while a clip is on air, and so does a running
        // countdown: the remotes get a push each second then — only then, and only while someone
        // listens — so a phone, a Stream Deck and an OSC desk count down with the desk. Nothing
        // else is rebuilt for it.
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => ClockTick();
        _clockTimer.Start();
    }

    private long _rev; // bumped on the UI thread, read by the HTTP long-poll threads
    private bool _stackHooked;
    private readonly DispatcherTimer _clockTimer;
    private int _longPollers;

    private void ClockTick()
    {
        try
        {
            if (_services.VideoOnAir() is null && !OverlayControl.CountsEverySecond(_services.AirState.Countdown, DateTime.Now, DateTime.UtcNow)) return;
            bool listening;
            lock (_gate)
            {
                listening = _tcpClients.Count > 0;
            }
            listening |= Volatile.Read(ref _longPollers) > 0 || _services.Osc is { FeedbackEndpoint: not null };
            if (!listening) return;
            Interlocked.Increment(ref _rev);
            _pushPending = true;
            if (!_pushTimer.IsEnabled) _pushTimer.Start();
            _services.Osc?.MarkChanged();
        }
        catch (Exception ex)
        {
            Log.Warn("The VT clock push failed.", ex);
        }
    }

    /// <summary>
    /// The stack's runtime is deliberately not in the snapshot, so STANDBY, ARM, HOLD and a
    /// pending confirm push on their own event, throttled like the snapshot pushes. Hooked
    /// lazily: the stack service is built after this one.
    /// </summary>
    private void HookStack()
    {
        if (_stackHooked || _services.CueStack is null) return;
        _stackHooked = true;
        _services.CueStack.Changed += () =>
        {
            Interlocked.Increment(ref _rev);
            _pushPending = true;
            if (!_pushTimer.IsEnabled) _pushTimer.Start();
        };
    }

    public string Status => _status;

    /// <summary>How long the remote's addresses are kept before the machine is asked again.</summary>
    public static readonly TimeSpan RemoteUrlsKeptFor = TimeSpan.FromSeconds(30);

    private IReadOnlyList<string>? _urls;
    private int _urlsPort;
    private DateTime _urlsAtUtc;

    /// <summary>
    /// LAN URLs the web remote answers on (for the settings panel / QR-by-eye). The desk reads
    /// these every second for its status line and the Install page; the machine's own addresses
    /// come from the resolver, which can block the UI thread for as long as a venue's DNS wants —
    /// so the list is kept for half a minute and asked again only then, or when the port changes.
    /// </summary>
    public IReadOnlyList<string> RemoteUrls()
    {
        var port = _kernel.State.Control.HttpPort;
        var now = DateTime.UtcNow;
        if (_urls is not null && _urlsPort == port && now - _urlsAtUtc < RemoteUrlsKeptFor) return _urls;
        var urls = new List<string> { $"http://localhost:{port}/" };
        try
        {
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address))
                {
                    urls.Add($"http://{address}:{port}/");
                }
            }
        }
        catch
        {
            // Name resolution trouble just means fewer suggestions.
        }
        _urls = urls;
        _urlsPort = port;
        _urlsAtUtc = now;
        return urls;
    }

    /// <summary>Drops the kept addresses so the next read asks the machine again (the listeners rebound, a test).</summary>
    public void ForgetRemoteUrls() => _urls = null;

    /// <summary>The audience listener's addresses — the same machine, the audience port (the bound address alone when one is set); empty while it is off.</summary>
    public IReadOnlyList<string> AudienceUrls()
    {
        var cfg = _kernel.State.Control;
        if (!cfg.Enabled || !cfg.AudienceEnabled) return Array.Empty<string>();
        if (IPAddress.TryParse(cfg.AudienceBind, out var bound)) return new[] { $"http://{bound}:{cfg.AudiencePort}/" };
        return RemoteUrls().Select(u => u.Replace($":{cfg.HttpPort}/", $":{cfg.AudiencePort}/")).ToList();
    }

    /// <summary>Starts/stops/rebinds the listeners to match the config (UI thread).</summary>
    public void Reconcile()
    {
        HookStack();
        var cfg = _kernel.State.Control;
        var key = cfg.Enabled ? $"{cfg.HttpPort}|{cfg.TcpPort}|{(cfg.AudienceEnabled ? $"{cfg.AudiencePort}@{cfg.AudienceBind}" : "")}" : "";
        if (key == _activeKey) return;
        _activeKey = key;
        ForgetRemoteUrls();

        StopListeners();
        if (!cfg.Enabled)
        {
            _status = "Remote control off.";
            return;
        }

        _cts = new CancellationTokenSource();
        try
        {
            _tcp = new TcpListener(IPAddress.Any, cfg.TcpPort);
            _tcp.Start();
            _ = AcceptLoop(_tcp, _cts.Token, HandleTcpClient);

            _http = new TcpListener(IPAddress.Any, cfg.HttpPort);
            _http.Start();
            _ = AcceptLoop(_http, _cts.Token, HandleHttpClient);

            _status = $"Web remote on port {cfg.HttpPort} · Companion (TCP) on port {cfg.TcpPort}.";
            if (cfg.AudienceEnabled)
            {
                // The room's own socket: the play pages and nothing else, on the audience network's address when the hub has one.
                var bind = IPAddress.TryParse(cfg.AudienceBind, out var address) ? address : IPAddress.Any;
                _audience = new TcpListener(bind, cfg.AudiencePort);
                _audience.Start();
                _ = AcceptLoop(_audience, _cts.Token, HandleAudienceClient);
                _status += $" Audience on port {cfg.AudiencePort}{(bind.Equals(IPAddress.Any) ? "" : $" at {bind}")} — the play pages only.";
            }
            Log.Info(_status);
        }
        catch (Exception ex)
        {
            _status = $"Remote control failed to start: {ex.Message}";
            Log.Error("Control server start failed.", ex);
            StopListeners();
            _activeKey = ""; // retry on the next change
        }
    }

    private static async Task AcceptLoop(TcpListener listener, CancellationToken ct, Func<TcpClient, CancellationToken, Task> handler)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => handler(client, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            _ = ex; // listener stopped underneath us — normal shutdown
        }
        catch (Exception ex)
        {
            Log.Warn("Control accept loop ended.", ex);
        }
    }

    // ---- TCP line protocol (Companion) --------------------------------------

    private async Task HandleTcpClient(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        lock (_gate)
        {
            _tcpClients.Add(client);
        }
        try
        {
            using var stream = client.GetStream();
            var reader = new BoundedLineReader(stream, WireLineBytes);

            // Greet with current state so feedback initialises immediately.
            var hello = await _router.StateJsonAsync();
            await WriteLine(stream, "STATE " + hello, ct);

            var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "";
            var origin = new ActionOrigin(OriginKind.Tcp, "", endpoint);
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (InvalidDataException ex)
                {
                    // A line that never ends is not a command: said once, and the door closed.
                    await WriteLine(stream, ControlProtocol.Err($"{ex.Message} — the wire's lines are commands, and this one was not; closed"), ct);
                    break;
                }
                if (line is null) break;
                if (line.Trim().Length == 0) continue;
                var cmd = ControlProtocol.Parse(line);
                if (cmd.Kind == RemoteCommandKind.Hello)
                {
                    // "HELLO FOH deck": history reads "GO from tcp FOH deck", not an address.
                    origin = new ActionOrigin(OriginKind.Tcp, cmd.Text, endpoint);
                }
                var response = await _router.ExecuteAsync(cmd, origin);
                await WriteLine(stream, response, ct);
            }
        }
        catch (Exception)
        {
            // Disconnects are routine.
        }
        finally
        {
            lock (_gate)
            {
                _tcpClients.Remove(client);
            }
            client.Dispose();
        }
    }

    /// <summary>The most bytes a line on the wire may run to: a command is a few dozen, a plan or a show file a few thousand.</summary>
    public const int WireLineBytes = 64 * 1024;

    private static async Task WriteLine(NetworkStream stream, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, ct);
    }

    private void Broadcast(string line)
    {
        List<TcpClient> clients;
        lock (_gate)
        {
            clients = _tcpClients.ToList();
        }
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        foreach (var client in clients)
        {
            try
            {
                client.GetStream().Write(bytes);
            }
            catch
            {
                lock (_gate)
                {
                    _tcpClients.Remove(client);
                }
                client.Dispose();
            }
        }
    }

    // ---- minimal HTTP (web remote) ------------------------------------------

    private Task HandleHttpClient(TcpClient client, CancellationToken ct) => HandleHttp(client, ct, audience: false);

    /// <summary>Whether the audience listener is up — the room's door is open.</summary>
    public bool AudienceListening => _audience is not null;

    /// <summary>How many audience connections are open right now.</summary>
    public int AudienceConnections => Volatile.Read(ref _audienceConnections);

    /// <summary>
    /// The audience's socket: counted against the budgets (so many at once, so many from one
    /// address), then the same handler with the audience's own route table — nothing else answers.
    /// </summary>
    private async Task HandleAudienceClient(TcpClient client, CancellationToken ct)
    {
        var budget = _services.Play.Budget;
        var address = client.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : "?";
        var admitted = false;
        lock (_audienceByAddress)
        {
            _audienceByAddress.TryGetValue(address, out var mine);
            if (_audienceConnections < budget.MaxConnections && mine < budget.MaxConnectionsPerAddress)
            {
                _audienceByAddress[address] = mine + 1;
                _audienceConnections++;
                admitted = true;
            }
        }
        if (!admitted)
        {
            try
            {
                using var stream = client.GetStream();
                var words = Encoding.UTF8.GetBytes("busy");
                var head = $"HTTP/1.1 503 Service Unavailable\r\nContent-Type: text/plain\r\nContent-Length: {words.Length}\r\nRetry-After: 2\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
                await stream.WriteAsync(words, ct);
            }
            catch (Exception)
            {
                // A phone that left; nothing to say.
            }
            finally
            {
                client.Dispose();
            }
            return;
        }
        try
        {
            await HandleHttp(client, ct, audience: true);
        }
        finally
        {
            lock (_audienceByAddress)
            {
                _audienceConnections--;
                if (_audienceByAddress.TryGetValue(address, out var mine))
                {
                    if (mine <= 1) _audienceByAddress.Remove(address); else _audienceByAddress[address] = mine - 1;
                }
            }
        }
    }

    /// <summary>The control port's limits on a request: how much head and body it reads and how long it waits. Settable for the tests.</summary>
    public HttpLimits ControlLimits { get; set; } = HttpLimits.Control;

    /// <summary>The audience port's — tighter, because nobody vouches for what is on the other end.</summary>
    public HttpLimits AudienceLimits { get; set; } = HttpLimits.Audience;

    /// <summary>
    /// A request's head, read as bytes up to the blank line and never past the limit, within the
    /// head's seconds, with whatever of the body came along with it. Null when the client sent
    /// nothing, or went quiet; a fault when it sent more head than a request has.
    /// </summary>
    private static async Task<(byte[] Head, byte[] Extra, string Fault)?> ReadHeadAsync(NetworkStream stream, HttpLimits limits, CancellationToken ct)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(TimeSpan.FromSeconds(limits.HeadSeconds));
        var chunk = new byte[2048];
        using var head = new MemoryStream();
        while (true)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(chunk, timed.Token);
            }
            catch (OperationCanceledException)
            {
                return null;                                    // quiet past the head's seconds, or the port closing: nothing to answer
            }
            if (n <= 0) return null;
            head.Write(chunk, 0, n);
            var bytes = head.ToArray();
            var (at, length) = HttpHead.EndOfHead(bytes);
            if (at >= 0) return (bytes[..at], bytes[(at + length)..], "");
            if (bytes.Length > limits.MaxHeadBytes) return (Array.Empty<byte>(), Array.Empty<byte>(), $"a head past {limits.MaxHeadBytes} bytes");
        }
    }

    /// <summary>The body: the bytes the head's read brought along, then the rest up to the content length, within the body's seconds; as many as came when the client stopped short.</summary>
    private static async Task<string> ReadBodyAsync(NetworkStream stream, byte[] rest, int contentLength, HttpLimits limits, CancellationToken ct)
    {
        if (contentLength <= 0) return "";
        var body = new byte[contentLength];
        var read = Math.Min(rest.Length, contentLength);
        Array.Copy(rest, body, read);
        if (read < contentLength)
        {
            using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timed.CancelAfter(TimeSpan.FromSeconds(limits.BodySeconds));
            try
            {
                while (read < contentLength)
                {
                    var n = await stream.ReadAsync(body.AsMemory(read, contentLength - read), timed.Token);
                    if (n <= 0) break;
                    read += n;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // the client stopped short of its own content length: what came is the body
            }
        }
        return Encoding.UTF8.GetString(body, 0, read);
    }

    /// <summary>A short answer with a status and a line of plain text — the faults, before any route.</summary>
    private static async Task WriteShortAsync(NetworkStream stream, string status, string words, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(words);
        var head = $"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
    }

    private async Task HandleHttp(TcpClient client, CancellationToken ct, bool audience)
    {
        try
        {
            using var stream = client.GetStream();
            var limits = audience ? AudienceLimits : ControlLimits;

            // The head as bytes, bounded and timed; the body as the bytes its content length says,
            // never as characters — a name with an accent is more bytes than characters, and a
            // reader counting characters waited for bytes that never came.
            var read = await ReadHeadAsync(stream, limits, ct);
            if (read is null) return;
            var (headBytes, rest, headFault) = read.Value;
            if (headFault.Length > 0)
            {
                await WriteShortAsync(stream, "431 Request Header Fields Too Large", headFault, ct);
                return;
            }
            var request = HttpHead.Parse(headBytes, limits);
            if (!request.Ok)
            {
                await WriteShortAsync(stream, request.Status, request.Fault, ct);
                return;
            }
            var method = request.Method;
            var path = request.Path;
            var clientHeader = request.ClientHeader;
            var body = await ReadBodyAsync(stream, rest, request.ContentLength, limits, ct);

            string status = "200 OK", contentType = "text/html; charset=utf-8";
            string payload;
            byte[]? binary = null;
            if (audience && !AudienceRoutes.Allows(method, path))
            {
                // The trust boundary: the audience port answers the play pages and their calls, nothing else — not a command, not the state, not a picture.
                status = "404 Not Found";
                contentType = "text/plain";
                payload = "Not here — the audience port answers the play pages only.";
            }
            else if (!audience && AudienceRoutes.AudienceOnly(path))
            {
                // And the other way: the control port never carries the room, so a phone that finds it finds nothing.
                status = "404 Not Found";
                contentType = "text/plain";
                payload = "The audience pages answer on the audience port — Remote page, AUDIENCE.";
            }
            else if (audience && method == "GET" && (path == "/" || path == "/index.html"))
            {
                payload = PlayPage;
            }
            else if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                payload = RemotePage;
            }
            else if (method == "GET" && (path == "/multiview" || path == "/mv"))
            {
                payload = MultiviewPage;
            }
            else if (method == "GET" && path.StartsWith("/mv.jpg"))
            {
                contentType = "image/jpeg";
                payload = "";
                binary = RenderMultiviewJpeg(
                    int.TryParse(QueryValue(path, "w"), out var mvw) ? Math.Clamp(mvw, 320, 1920) : 1024,
                    int.TryParse(QueryValue(path, "n"), out var mvn) ? mvn : 1);
            }
            else if (method == "GET" && (path == "/api/state" || path.StartsWith("/api/state?")))
            {
                contentType = "application/json";
                // ?since=<rev> long-polls: the handler is already asynchronous, so it can wait
                // up to 25 s for the next change instead of a tablet polling every 1.5 s.
                var since = QueryValue(path, "since");
                if (long.TryParse(since, out var seen))
                {
                    var deadline = DateTime.UtcNow.AddSeconds(25);
                    Interlocked.Increment(ref _longPollers); // a tablet is waiting: the VT clock's second ticks reach it
                    try
                    {
                        while (Interlocked.Read(ref _rev) == seen && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                        {
                            await Task.Delay(150, ct);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _longPollers);
                    }
                }
                payload = await _router.StateJsonAsync();
            }
            else if (method == "GET" && path == "/api/cues")
            {
                contentType = "application/json";
                payload = await _router.CueListJsonAsync();
            }
            else if (method == "GET" && path == "/run")
            {
                payload = RunPage;
            }
            else if (method == "GET" && (path == "/stage" || path.StartsWith("/stage?")))
            {
                payload = StagePage;
            }
            else if (method == "GET" && path == "/timer")
            {
                payload = TimerPage;
            }
            else if (method == "GET" && (path == "/api/stage" || path.StartsWith("/api/stage?")))
            {
                contentType = "application/json";
                // ?since=<rev> long-polls the stage's own revision: a message, a receipt, the timer moved.
                if (long.TryParse(QueryValue(path, "since"), out var seenStage))
                {
                    var deadline = DateTime.UtcNow.AddSeconds(25);
                    while (_services.Stage.Rev == seenStage && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(150, ct);
                    }
                }
                payload = await UiThread.InvokeAsync(() => _services.Stage.StatusJson());
            }
            else if (method == "POST" && path == "/api/stage/ack")
            {
                contentType = "application/json";
                var id = body.Trim().Trim('"');
                var acked = await UiThread.InvokeAsync(() => _services.Stage.Ack(id));
                payload = acked ? "{\"ok\":true}" : "{\"ok\":false,\"msg\":\"no such message, or seen already\"}";
            }
            else if (method == "GET" && (path == "/pad" || path.StartsWith("/pad?")))
            {
                payload = PadPage;
            }
            else if (method == "GET" && (path == "/api/arcade" || path.StartsWith("/api/arcade?")))
            {
                contentType = "application/json";
                // On the arcade node its own state; on a desk the arcade nodes' — asked on their wires.
                var forward = _kernel.Profile != NodeKind.Arcade && await UiThread.InvokeAsync(() => _kernel.Nodes.Arcades().Count) > 0;
                payload = forward
                    ? await _kernel.Nodes.AskArcadesAsync("ARCADE STATUS")
                    : await UiThread.InvokeAsync(() => _kernel.Arcade.StatusJson(QueryValue(path, "what")));
            }
            else if (method == "POST" && path == "/api/arcade/key")
            {
                // "<player> <button> DOWN|UP|TAP" from the phone pad: the same verb the wire runs.
                contentType = "application/json";
                var words = body.Trim();
                var padOrigin = new ActionOrigin(OriginKind.Http, "pad", client.Client.RemoteEndPoint?.ToString() ?? "");
                var result = await UiThread.InvokeAsync(() => _services.Actions.Execute(new ShowAction(ShowActionKind.ArcadeKey, "", words), padOrigin));
                payload = JsonUtil.SerializeCompact(new { ok = result.Ok, msg = result.Message });
            }
            else if (method == "GET" && (path == "/play" || path.StartsWith("/play?")))
            {
                payload = PlayPage;
            }
            else if (method == "GET" && (path == "/host" || path.StartsWith("/host?")))
            {
                payload = HostPage;
            }
            else if (method == "POST" && path == "/api/play/join")
            {
                contentType = "application/json";
                var from = client.Client.RemoteEndPoint is IPEndPoint joinEp ? joinEp.Address.ToString() : "?";
                payload = await UiThread.InvokeAsync(() => _services.Play.JoinJson(body, from));
            }
            else if (method == "GET" && (path == "/api/play/state" || path.StartsWith("/api/play/state?")))
            {
                contentType = "application/json";
                // ?rev=<rev> long-polls the room: a question opened, an answer counted, a message sent, the wall changed.
                var token = QueryValue(path, "token");
                long.TryParse(QueryValue(path, "since"), out var sinceSeq);
                // The wait is a signal, not a poll: the room wakes every waiting phone at once when it moves; past the budget a phone is answered now.
                if (long.TryParse(QueryValue(path, "rev"), out var seenRev)) await _services.Play.WaitForChangeAsync(seenRev, TimeSpan.FromSeconds(20), ct);
                ct.ThrowIfCancellationRequested();     // the port closed while the phone waited: nothing of the desk is asked for a phone that is gone
                payload = await UiThread.InvokeAsync(() => _services.Play.StateJson(token, sinceSeq));
            }
            else if (method == "POST" && path == "/api/play/answer")
            {
                contentType = "application/json";
                payload = await UiThread.InvokeAsync(() => _services.Play.AnswerJson(body));
            }
            else if (method == "POST" && path == "/api/play/say")
            {
                contentType = "application/json";
                payload = await UiThread.InvokeAsync(() => _services.Play.SayJson(body));
            }
            else if (method == "POST" && path == "/api/play/vote")
            {
                contentType = "application/json";
                payload = await UiThread.InvokeAsync(() => _services.Play.VoteJson(body));
            }
            else if (method == "POST" && path == "/api/play/draughts")
            {
                contentType = "application/json";
                payload = await UiThread.InvokeAsync(() => _services.Play.DraughtsJson(body));
            }
            else if (method == "POST" && path == "/api/play/host")
            {
                // The host's data behind the admin passcode: the room's phones share this server.
                contentType = "application/json";
                var passcode = body.Trim();
                if (!await UiThread.InvokeAsync(() => _kernel.Gate.Check(_kernel.State.Install.AdminPasscode, passcode, DateTime.UtcNow)))
                {
                    status = "403 Forbidden";
                    payload = "{\"ok\":false}";
                }
                else
                {
                    payload = await UiThread.InvokeAsync(() => _services.Play.HostJson());
                }
            }
            else if (method == "GET" && (path == "/api/play" || path.StartsWith("/api/play?")))
            {
                contentType = "application/json";
                payload = await UiThread.InvokeAsync(() => _services.Play.StatusJson(QueryValue(path, "what")));
            }
            else if (method == "GET" && path == "/api/play/feed.csv")
            {
                contentType = "text/csv; charset=utf-8";
                payload = await UiThread.InvokeAsync(() => _services.Play.FeedCsv());
            }
            else if (method == "GET" && path == "/admin")
            {
                payload = AdminPage;
            }
            else if (method == "POST" && path == "/api/admin")
            {
                // "<passcode>\n<command line>": the gate first, then the line through the router with the admin as its origin.
                contentType = "application/json";
                var cut = body.IndexOf('\n');
                var passcode = (cut < 0 ? body : body[..cut]).Trim();
                var line = cut < 0 ? "" : body[(cut + 1)..].Trim();
                var adminOrigin = new ActionOrigin(OriginKind.Http, "admin", client.Client.RemoteEndPoint?.ToString() ?? "");
                string response;
                if (!await UiThread.InvokeAsync(() => _kernel.Gate.Check(_kernel.State.Install.AdminPasscode, passcode, DateTime.UtcNow)))
                {
                    status = "403 Forbidden";
                    response = ControlProtocol.Err(_kernel.Gate.Reason);
                }
                else if (line.Length == 0)
                {
                    response = ControlProtocol.Ok();        // a passcode check on its own: the page unlocking
                }
                else
                {
                    response = await _router.ExecuteAsync(ControlProtocol.Parse(line), adminOrigin);
                }
                var ok = response.StartsWith("OK");
                payload = $"{{\"ok\":{(ok ? "true" : "false")},\"msg\":{System.Text.Json.JsonSerializer.Serialize(response)}}}";
            }
            else if (method == "GET" && path.StartsWith("/api/admin/log"))
            {
                contentType = "text/plain; charset=utf-8";
                if (!await UiThread.InvokeAsync(() => _kernel.Gate.Check(_kernel.State.Install.AdminPasscode, QueryValue(path, "pass") ?? "", DateTime.UtcNow)))
                {
                    status = "403 Forbidden";
                    payload = _kernel.Gate.Reason;
                }
                else
                {
                    payload = LogTail(80);
                }
            }
            else if (method == "GET" && path.StartsWith("/support-bundle.zip"))
            {
                if (!await UiThread.InvokeAsync(() => _kernel.Gate.Check(_kernel.State.Install.AdminPasscode, QueryValue(path, "pass") ?? "", DateTime.UtcNow)))
                {
                    status = "403 Forbidden";
                    contentType = "text/plain";
                    payload = _kernel.Gate.Reason;
                }
                else
                {
                    contentType = "application/zip";
                    payload = "";
                    // The words come off the UI thread with the show they describe; the zip is built off it.
                    var info = await UiThread.InvokeAsync(SupportBundleInfo);
                    binary = await Task.Run(() => BuildSupportBundle(info));
                }
            }
            else if (method == "GET" && path.StartsWith("/pgm.jpg"))
            {
                contentType = "image/jpeg";
                payload = "";
                binary = RenderProgramJpeg();
            }
            else if (method == "POST" && path == "/api/cmd")
            {
                contentType = "application/json";
                var cmd = ControlProtocol.Parse(body);
                var httpOrigin = new ActionOrigin(OriginKind.Http, "", client.Client.RemoteEndPoint?.ToString() ?? "");
                string response;
                if (IsCueVerb(cmd) && !clientHeader)
                {
                    // A cross-origin page cannot fire cues: the embedded pages and any deliberate
                    // client send this header; plain commands (LOOK, BLACKOUT…) keep working without it.
                    response = ControlProtocol.Err("X-Patterns-Client header required for cue commands");
                }
                else
                {
                    response = await _router.ExecuteAsync(cmd, httpOrigin);
                }
                var ok = response.StartsWith("OK");
                payload = $"{{\"ok\":{(ok ? "true" : "false")},\"msg\":{System.Text.Json.JsonSerializer.Serialize(response)}}}";
            }
            else
            {
                status = "404 Not Found";
                contentType = "text/plain";
                payload = "Not found";
            }

            var bytes = binary ?? Encoding.UTF8.GetBytes(payload);
            var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.WriteAsync(bytes, ct);
        }
        catch (Exception)
        {
            // Broken sockets are routine for one-shot HTTP.
        }
        finally
        {
            client.Dispose();
        }
    }

    private static bool IsCueVerb(RemoteCommand cmd) => cmd.IsAction && cmd.Action.Kind is
        ShowActionKind.CueGo or ShowActionKind.CueStandby or ShowActionKind.CueHoldOn or ShowActionKind.CueHoldOff or
        ShowActionKind.ListArm or ShowActionKind.ListDisarm or ShowActionKind.StopAll;

    /// <summary>The last lines of patterns.log, for the ADMIN page — read with a shared lock, the app keeps writing.</summary>
    private string LogTail(int lines)
    {
        try
        {
            var path = Path.Combine(_kernel.Store.BaseDirectory, "patterns.log");
            if (!File.Exists(path)) return "(no log yet)";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n');
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception ex)
        {
            return "(the log could not be read: " + ex.Message + ")";
        }
    }

    /// <summary>The bundle's own page of facts, read on the UI thread where the show and the services live.</summary>
    private string SupportBundleInfo()
        => string.Join(Environment.NewLine,
            $"Patterns support bundle — {DateTime.Now:yyyy-MM-dd HH:mm} (from the ADMIN page)",
            $"Site: {(_kernel.State.Install.SiteName.Length > 0 ? _kernel.State.Install.SiteName : "(unnamed)")} · machine {Environment.MachineName}",
            $"Build: {UpdateService.RunningVersion} · .NET {Environment.Version} · {Environment.OSVersion}",
            $"Health: {HealthMonitor.Summary(DateTime.UtcNow)}",
            $"Install: {_services.Install.Status}",
            $"Update: {_services.Updates.Status}",
            $"Management: {_services.Management.Status}");

    /// <summary>The support bundle as bytes for the ADMIN page's download: written beside the settings, then read back.</summary>
    private byte[] BuildSupportBundle(string info)
    {
        var dir = _kernel.Store.BaseDirectory;
        var path = Path.Combine(dir, SupportBundle.FileNameFor(DateTime.Now));
        SupportBundle.Build(dir, path, info);
        Log.Info($"Support bundle written for the ADMIN page: {path}");
        return File.ReadAllBytes(path);
    }

    private static string? QueryValue(string path, string key)
    {
        var q = path.IndexOf('?');
        if (q < 0) return null;
        foreach (var pair in path[(q + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            var k = eq < 0 ? pair : pair[..eq];
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    /// <summary>The program as a thumbnail for the /run page — the engine over the current snapshot, like /mv.jpg.</summary>
    private byte[] RenderProgramJpeg()
    {
        lock (_mvGate)
        {
            var snap = _kernel.Bus.Current;
            const int w = 640;
            const int h = 360;
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(w, h),
                ReferenceSize = new SKSizeI(w, h),
                Time = ShowClock.Seconds,
                Now = DateTime.Now,
                UtcNow = DateTime.UtcNow,
                Sink = Patterns.Core.Model.SinkKind.Thumbnail,
                SinkIndex = 0,
                SinkLabel = "pgm-remote",
            };
            _mvEngine.Render(surface.Canvas, snap, in ctx, _mvSink);
            surface.Canvas.Flush();
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 72);
            return data.ToArray();
        }
    }

    private void StopListeners()
    {
        _cts?.Cancel();
        _cts = null;
        try { _tcp?.Stop(); } catch { /* already down */ }
        try { _http?.Stop(); } catch { /* already down */ }
        try { _audience?.Stop(); } catch { /* already down */ }
        _tcp = null;
        _http = null;
        _audience = null;
        lock (_gate)
        {
            foreach (var client in _tcpClients)
            {
                client.Dispose();
            }
            _tcpClients.Clear();
        }
    }

    public void Dispose()
    {
        _pushTimer.Stop();
        _clockTimer.Stop();
        StopListeners();
        lock (_mvGate)
        {
            _mvSink.Dispose();
        }
    }

    // ---- remote multiview ---------------------------------------------------

    private readonly PatternEngine _mvEngine = new();
    private readonly SinkState _mvSink = new();
    private readonly object _mvGate = new();

    /// <summary>
    /// Renders the configured multiview (Pattern tab) to a JPEG for /mv.jpg — the engine is
    /// thread-safe over immutable snapshots, so this runs on the socket task, ~1 fps/viewer.
    /// </summary>
    private byte[] RenderMultiviewJpeg(int width, int number)
    {
        lock (_mvGate)
        {
            var snap = _kernel.Bus.Current;
            // The page frame stays 16:9 — the tiles inside it are what carry their targets' shapes.
            var w = width;
            var h = w * 9 / 16;
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(w, h),
                ReferenceSize = new SKSizeI(w, h),
                Time = ShowClock.Seconds,
                Now = DateTime.Now,
                UtcNow = DateTime.UtcNow,
                Sink = Patterns.Core.Model.SinkKind.Thumbnail,
                SinkIndex = 0,
                SinkLabel = "mv-remote",
            };
            var frame = new PatternFrame
            {
                Snapshot = snap,
                Config = snap.State.Pattern,
                Ctx = ctx,
                Sink = _mvSink,
                Canvas = new SKSizeI(w, h),
                Palette = Palette.Resolve(snap),
            };
            _mvEngine.RenderMultiview(surface.Canvas, in frame, _mvSink, Multiviews.Primary(snap.State, number));
            surface.Canvas.Flush();
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 72);
            return data.ToArray();
        }
    }

    private const string RunPage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
<title>Patterns Run</title>
<style>
  :root { --bg:#0D0F14; --panel:#151A22; --line:#2A313E; --text:#E8ECF2; --mut:#98A1B1;
          --acc:#3EC1F3; --pgm:#E0342E; --pvw:#2EE68A; --hold:#FFC24D; --off:#4A505E; }
  * { box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
  body { margin:0; background:var(--bg); color:var(--text); font:16px/1.35 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; padding:12px; }
  .live { display:flex; align-items:center; gap:12px; border-bottom:2px solid var(--pgm); padding:8px 4px 10px; }
  .live .tag { font-size:13px; letter-spacing:.14em; color:var(--mut); font-weight:700; }
  .live .label { font-size:26px; font-weight:800; flex:1; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .chip { font-size:13px; font-weight:800; letter-spacing:.08em; border-radius:6px; padding:4px 8px; display:none; }
  .chip.on { display:inline-block; }
  .armed { background:#3A2E10; color:var(--hold); border:1px solid var(--hold); }
  .hold { background:var(--hold); color:#0E0F13; }
  .bo { background:#000; color:var(--pgm); border:1px solid var(--pgm); }
  .music { background:#10303A; color:var(--acc); border:1px solid var(--acc); }
  .card { background:var(--panel); border:1px solid var(--line); border-radius:12px; padding:12px; margin-top:12px; }
  .card.standby { border-color:var(--pvw); border-width:2px; }
  .card .k { font-size:12px; letter-spacing:.14em; color:var(--mut); font-weight:700; }
  .card .n { font-size:30px; font-weight:800; }
  .card .num { color:var(--mut); font-family:ui-monospace,Menlo,Consolas,monospace; margin-right:8px; }
  .card .notes { color:var(--hold); margin-top:4px; }
  .card .broken { color:var(--pgm); margin-top:4px; }
  .row { display:flex; gap:10px; margin-top:12px; }
  button { border:1px solid var(--line); border-radius:12px; background:var(--panel); color:var(--text); font:inherit; font-weight:800; padding:18px 10px; cursor:pointer; flex:1; font-size:20px; }
  button:disabled { opacity:.35; }
  #go { background:#1E9E5A; border-color:#1E9E5A; color:#fff; flex:2; font-size:26px; }
  #go.confirm { background:var(--hold); color:#0E0F13; }
  #hold.on { background:var(--hold); color:#0E0F13; }
  .next div, .hist div { display:flex; gap:10px; padding:5px 0; border-top:1px solid var(--line); font-size:16px; }
  .next div:first-child, .hist div:first-child { border-top:none; }
  .hist .bad { color:var(--pgm); font-weight:700; }
  img { width:100%; border-radius:8px; margin-top:12px; border:1px solid var(--line); }
  #err { color:var(--pgm); font-size:13px; min-height:16px; margin-top:8px; text-align:center; }
</style>
</head>
<body>
<div class="live">
  <span class="tag">LIVE</span>
  <span class="label" id="air">—</span>
  <span class="chip bo" id="cbo">BLACKOUT</span>
  <span class="chip hold" id="chold">HOLD</span>
  <span class="chip armed" id="carmed">ARMED</span>
  <span class="chip music" id="cmusic">♪ MUSIC</span>
  <span class="chip hold" id="chold2">STING HOLD</span>
  <span class="chip hold" id="cduck">DUCK</span>
</div>
<div class="card standby">
  <div class="k">STANDBY</div>
  <div class="n" id="sb">No cue on standby</div>
  <div class="notes" id="sbnotes"></div>
  <div class="broken" id="sbbroken"></div>
</div>
<div class="row">
  <button id="up" onclick="cmd('CUE STANDBY PREV')">▲</button>
  <button id="down" onclick="cmd('CUE STANDBY NEXT')">▼</button>
  <button id="go" onclick="go()">GO</button>
  <button id="hold" onclick="hold()">HOLD</button>
</div>
<div id="err"></div>
<div class="card next"><div class="k">NEXT</div><div id="next"></div></div>
<img id="pgm" src="/pgm.jpg" alt="program">
<div class="card hist"><div class="k">HISTORY</div><div id="hist"></div></div>
<script>
var st = null, rev = 0, standbyId = '';
function esc(s){ var d=document.createElement('div'); d.textContent=s==null?'':s; return d.innerHTML; }
function cmd(c) {
  return fetch('/api/cmd', { method:'POST', body:c, headers:{'X-Patterns-Client':'run-page'} })
    .then(function(r){ return r.json(); })
    .then(function(j){ document.getElementById('err').textContent = j.ok ? '' : j.msg; })
    .catch(function(){ document.getElementById('err').textContent = 'Connection lost'; });
}
function go(){ if (standbyId) cmd('CUE GO ' + standbyId); }
function hold(){ var h = st && st.cuestack && st.cuestack.hold; cmd('CUE HOLD ' + (h ? 'OFF' : 'ON')); }
function render(s) {
  st = s; rev = s.rev || 0;
  var c = s.cuestack || {};
  document.getElementById('air').textContent = s.airLabel || '—';
  document.getElementById('cbo').classList.toggle('on', !!s.blackout);
  document.getElementById('chold').classList.toggle('on', !!c.hold);
  document.getElementById('carmed').classList.toggle('on', !!c.armed);
  document.getElementById('cmusic').classList.toggle('on', !!(s.music && s.music.playing));
  var h2 = document.getElementById('chold2');
  h2.classList.toggle('on', !!s.stingHold);
  h2.textContent = s.stingHold ? 'STING HOLD: ' + s.stingHold : 'STING HOLD';
  document.getElementById('cduck').classList.toggle('on', !!s.duck);
  var sb = c.standby; standbyId = sb ? sb.id : '';
  document.getElementById('sb').innerHTML = sb ? '<span class="num">' + esc(sb.number) + '</span>' + esc(sb.name) : 'No cue on standby';
  document.getElementById('sbnotes').textContent = sb ? (sb.notes || '') : '';
  var go = document.getElementById('go');
  go.disabled = !(c.armed && sb);
  go.classList.toggle('confirm', !!c.confirm);
  go.textContent = c.confirm ? c.confirm : (sb ? 'GO ' + sb.number : 'GO');
  var hold = document.getElementById('hold');
  hold.disabled = !c.armed; hold.classList.toggle('on', !!c.hold);
  var nx = document.getElementById('next'); nx.innerHTML = '';
  (c.next || []).forEach(function(x){ var d=document.createElement('div'); d.innerHTML='<span class="num">'+esc(x.number)+'</span>'+esc(x.name); nx.appendChild(d); });
  if (!c.next || c.next.length === 0) nx.innerHTML = '<div style="color:var(--mut)">end of the list</div>';
  var h = document.getElementById('hist'); h.innerHTML = '';
  (c.history || []).forEach(function(r){
    var d=document.createElement('div');
    var bad = /Failed|Refused/.test(r.outcome);
    d.innerHTML = '<span class="num">'+esc((r.at||'').slice(11,19))+'</span><span style="flex:1">'+esc(r.number+' '+r.name)+'</span><span class="'+(bad?'bad':'')+'">'+esc(r.outcome)+'</span><span style="color:var(--mut)">'+esc(r.origin)+'</span>';
    h.appendChild(d);
  });
}
function poll() {
  fetch('/api/state?since=' + rev).then(function(r){ return r.json(); })
    .then(function(s){ render(s); document.getElementById('err').textContent=''; poll(); })
    .catch(function(){ document.getElementById('err').textContent = 'Connection lost — retrying…'; setTimeout(poll, 1500); });
}
fetch('/api/state').then(function(r){ return r.json(); }).then(function(s){ render(s); poll(); });
setInterval(function(){ var i=document.getElementById('pgm'); var n=new Image(); n.onload=function(){ i.src=n.src; }; n.src='/pgm.jpg?t='+Date.now(); }, 2000);
</script>
</body>
</html>
""";

    private const string MultiviewPage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Patterns Multiview</title>
<style>
  body { margin:0; background:#000; }
  img { width:100vw; height:auto; display:block; }
  #err { color:#F0524D; font:13px system-ui; text-align:center; padding:8px; }
</style>
</head>
<body>
<img id="mv" src="/mv.jpg" alt="multiview">
<div id="err"></div>
<script>
var img = document.getElementById('mv');
setInterval(function () {
  var next = new Image();
  next.onload = function(){ img.src = next.src; document.getElementById('err').textContent=''; };
  next.onerror = function(){ document.getElementById('err').textContent = 'Connection lost — retrying…'; };
  next.src = '/mv.jpg?w=' + Math.min(1920, Math.max(320, Math.round(window.innerWidth * (window.devicePixelRatio || 1)))) + '&t=' + Date.now();
}, 1000);
</script>
</body>
</html>
""";

}
