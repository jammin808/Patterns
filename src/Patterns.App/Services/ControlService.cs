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
public sealed class ControlService : IDisposable
{
    private readonly AppServices _services;
    private readonly CommandRouter _router;
    private readonly object _gate = new();
    private readonly List<TcpClient> _tcpClients = new();
    private TcpListener? _tcp;
    private TcpListener? _http;
    private CancellationTokenSource? _cts;
    private string _activeKey = "";
    private volatile string _status = "Remote control off.";
    private readonly DispatcherTimer _pushTimer;
    private bool _pushPending;

    public ControlService(AppServices services)
    {
        _services = services;
        _router = new CommandRouter(services);

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
        _services.SnapshotPublished += () =>
        {
            _pushPending = true;
            if (!_pushTimer.IsEnabled) _pushTimer.Start();
        };
    }

    public string Status => _status;

    /// <summary>LAN URLs the web remote answers on (for the settings panel / QR-by-eye).</summary>
    public IReadOnlyList<string> RemoteUrls()
    {
        var port = _services.State.Control.HttpPort;
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
        return urls;
    }

    /// <summary>Starts/stops/rebinds the listeners to match the config (UI thread).</summary>
    public void Reconcile()
    {
        var cfg = _services.State.Control;
        var key = cfg.Enabled ? $"{cfg.HttpPort}|{cfg.TcpPort}" : "";
        if (key == _activeKey) return;
        _activeKey = key;

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
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);

            // Greet with current state so feedback initialises immediately.
            var hello = await _router.StateJsonAsync();
            await WriteLine(stream, "STATE " + hello, ct);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Trim().Length == 0) continue;
                var response = await _router.ExecuteAsync(ControlProtocol.Parse(line));
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

    private async Task HandleHttpClient(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(requestLine)) return;
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;
            var method = parts[0];
            var path = parts[1];

            var contentLength = 0;
            while (await reader.ReadLineAsync(ct) is { } header && header.Length > 0)
            {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(header[15..].Trim(), out var len))
                {
                    contentLength = Math.Min(len, 4096);
                }
            }

            var body = "";
            if (contentLength > 0)
            {
                var buffer = new char[contentLength];
                var read = 0;
                while (read < contentLength)
                {
                    var n = await reader.ReadAsync(buffer.AsMemory(read, contentLength - read), ct);
                    if (n <= 0) break;
                    read += n;
                }
                body = new string(buffer, 0, read);
            }

            string status = "200 OK", contentType = "text/html; charset=utf-8";
            string payload;
            byte[]? binary = null;
            if (method == "GET" && (path == "/" || path == "/index.html"))
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
                binary = RenderMultiviewJpeg();
            }
            else if (method == "GET" && path == "/api/state")
            {
                contentType = "application/json";
                payload = await _router.StateJsonAsync();
            }
            else if (method == "POST" && path == "/api/cmd")
            {
                contentType = "application/json";
                var response = await _router.ExecuteAsync(ControlProtocol.Parse(body));
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

    private void StopListeners()
    {
        _cts?.Cancel();
        _cts = null;
        try { _tcp?.Stop(); } catch { /* already down */ }
        try { _http?.Stop(); } catch { /* already down */ }
        _tcp = null;
        _http = null;
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
    private byte[] RenderMultiviewJpeg()
    {
        lock (_mvGate)
        {
            var snap = _services.Bus.Current;
            const int w = 1024;
            const int h = w * 9 / 16;
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
            _mvEngine.RenderMultiview(surface.Canvas, in frame, _mvSink, snap.State.Pattern.Multiview);
            surface.Canvas.Flush();
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 72);
            return data.ToArray();
        }
    }

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
  next.src = '/mv.jpg?t=' + Date.now();
}, 1000);
</script>
</body>
</html>
""";

    // ---- the phone/tablet remote (embedded, no files to lose) ---------------

    private const string RemotePage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
<title>Patterns Remote</title>
<style>
  :root { --bg:#0D0F14; --panel:#151A22; --line:#2A313E; --text:#E8ECF2; --mut:#98A1B1;
          --acc:#3EC1F3; --good:#2EE68A; --bad:#F0524D; }
  * { box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
  body { margin:0; background:var(--bg); color:var(--text);
         font:16px/1.4 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; padding:14px; }
  h1 { font-size:15px; letter-spacing:.14em; margin:2px 0 12px; color:var(--mut); }
  h1 b { color:var(--text); }
  .grid { display:grid; gap:10px; }
  button { border:1px solid var(--line); border-radius:12px; background:var(--panel);
           color:var(--text); font:inherit; font-weight:600; padding:16px 10px; cursor:pointer; }
  button:active { background:#20242E; }
  .row2 { grid-template-columns:1fr 1fr; }
  .row3 { grid-template-columns:1fr 1fr 1fr; }
  .big { font-size:22px; padding:26px 10px; }
  .go { background:#0F3D2A; border-color:#1E6A4A; }
  .stop { background:#3A2020; border-color:#6A3A3A; }
  .bo { background:#3A0F0F; border-color:var(--bad); color:#FFB0B0; }
  .bo.on { background:var(--bad); color:#fff; }
  .look { padding:18px 6px; }
  .look .k { display:block; font-size:11px; color:var(--acc); }
  .scr.off { opacity:.45; }
  .sec { margin:18px 0 8px; font-size:11px; letter-spacing:.16em; color:var(--mut); }
  #step { text-align:center; color:var(--mut); font-size:13px; margin-top:6px; }
  #err { color:var(--bad); font-size:12px; min-height:16px; text-align:center; margin-top:8px; }
</style>
</head>
<body>
<h1><b>PATTERNS</b> REMOTE <a href="/multiview" style="float:right;color:var(--acc);font-size:12px;text-decoration:none">MULTIVIEW ⟩</a></h1>

<div class="sec">PRESENTER</div>
<div class="grid row2">
  <button class="big" onclick="cmd('PREV')">⟨ Back</button>
  <button class="big go" onclick="cmd('NEXT')">Next ⟩</button>
</div>
<div id="step"></div>

<div class="sec">TRANSPORT</div>
<div class="grid row3">
  <button class="go" onclick="cmd('GO')">GO</button>
  <button class="stop" onclick="cmd('STOP')">STOP</button>
  <button onclick="cmd('IDENTIFY')">IDENTIFY</button>
</div>
<div class="grid" style="margin-top:10px">
  <button id="bo" class="bo big" onclick="cmd('BLACKOUT TOGGLE')">BLACKOUT</button>
</div>

<div class="sec">LOOKS</div>
<div id="looks" class="grid row3"></div>

<div class="sec">SCREENS</div>
<div id="screens" class="grid row3"></div>

<div class="sec" id="secsec" hidden>SHOW PARTS (PLAYLIST)</div>
<div id="sections" class="grid row3"></div>

<div class="sec">STINGERS</div>
<div id="stingers" class="grid row2"></div>
<div id="stingnow" class="grid" style="margin-top:10px"></div>

<div class="sec">AUDIO TRACK</div>
<div class="grid row2">
  <button class="go" onclick="cmd('AUDIO PLAY')">▶ Play</button>
  <button class="stop" onclick="cmd('AUDIO STOP')">■ Stop</button>
</div>
<div id="err"></div>

<script>
function cmd(c) {
  fetch('/api/cmd', { method:'POST', body:c })
    .then(function(r){ return r.json(); })
    .then(function(j){ document.getElementById('err').textContent = j.ok ? '' : j.msg; poll(); })
    .catch(function(){ document.getElementById('err').textContent = 'Connection lost'; });
}
function esc(s){ var d=document.createElement('div'); d.textContent=s; return d.innerHTML; }
function render(s) {
  var bo = document.getElementById('bo');
  bo.classList.toggle('on', s.blackout);
  bo.textContent = s.blackout ? 'BLACKOUT — ON' : 'BLACKOUT';

  var p = s.presenter;
  document.getElementById('step').textContent =
    p.count === 0 ? 'No presenter steps' :
    (p.index < 0 ? p.count + ' steps ready' : 'Step ' + (p.index+1) + ' / ' + p.count +
      (p.steps[p.index] ? ' — ' + p.steps[p.index] : ''));

  var lk = document.getElementById('looks'); lk.innerHTML = '';
  s.looks.forEach(function(l){
    var b = document.createElement('button');
    b.className = 'look';
    b.innerHTML = (l.slot > 0 ? '<span class="k">F' + l.slot + '</span>' : '') + esc(l.name);
    b.onclick = function(){ cmd('LOOK ' + (l.slot > 0 ? l.slot : l.name)); };
    lk.appendChild(b);
  });
  if (s.looks.length === 0) lk.innerHTML = '<button disabled>No looks saved</button>';

  var sc = document.getElementById('screens'); sc.innerHTML = '';
  s.screens.forEach(function(x){
    var b = document.createElement('button');
    b.className = 'scr' + (x.enabled ? '' : ' off');
    b.innerHTML = esc(x.n + ' · ' + x.label) + (x.group ? ' <span class="k">[' + x.group + ']</span>' : '');
    b.onclick = function(){ cmd('SCREEN ' + x.n + ' TOGGLE'); };
    sc.appendChild(b);
  });

  var se = document.getElementById('sections'); se.innerHTML = '';
  var hasSections = s.sections && s.sections.length > 0;
  document.getElementById('secsec').hidden = !hasSections;
  (s.sections || []).forEach(function(x){
    var b = document.createElement('button');
    b.textContent = x.name;
    if (x.active) { b.style.borderColor = 'var(--good)'; b.style.color = 'var(--good)'; }
    b.onclick = function(){ cmd('SECTION ' + x.n); };
    se.appendChild(b);
  });

  var st = document.getElementById('stingers'); st.innerHTML = '';
  (s.stingers || []).forEach(function(x){
    var b = document.createElement('button');
    b.textContent = x.name;
    b.onclick = function(){ cmd('STINGER ' + x.n); };
    st.appendChild(b);
  });
  if (!s.stingers || s.stingers.length === 0) st.innerHTML = '<button disabled>No stingers set up</button>';
  var sn = document.getElementById('stingnow'); sn.innerHTML = '';
  if (s.stingerPlaying) {
    var b2 = document.createElement('button');
    b2.className = 'stop';
    b2.textContent = '■ Stop: ' + s.stingerPlaying;
    b2.onclick = function(){ cmd('STINGER STOP'); };
    sn.appendChild(b2);
  }
}
function poll() {
  fetch('/api/state')
    .then(function(r){ return r.json(); })
    .then(render)
    .catch(function(){ document.getElementById('err').textContent = 'Connection lost'; });
}
poll();
setInterval(poll, 1500);
</script>
</body>
</html>
""";
}
