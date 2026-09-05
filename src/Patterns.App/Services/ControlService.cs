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
            Interlocked.Increment(ref _rev);
            _pushPending = true;
            if (!_pushTimer.IsEnabled) _pushTimer.Start();
        };
        _router.Rev = () => Interlocked.Read(ref _rev);
    }

    private long _rev; // bumped on the UI thread, read by the HTTP long-poll threads
    private bool _stackHooked;

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
        HookStack();
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

            var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "";
            var origin = new ActionOrigin(OriginKind.Tcp, "", endpoint);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Trim().Length == 0) continue;
                var cmd = ControlProtocol.Parse(line);
                if (cmd.Kind == RemoteCommandKind.Hello)
                {
                    // "HELLO FOH deck": history reads "GO from tcp FOH deck", not an address.
                    origin = new ActionOrigin(OriginKind.Tcp, cmd.TextArg, endpoint);
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
            var clientHeader = false;
            while (await reader.ReadLineAsync(ct) is { } header && header.Length > 0)
            {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(header[15..].Trim(), out var len))
                {
                    contentLength = Math.Min(len, 4096);
                }
                if (header.StartsWith("X-Patterns-Client:", StringComparison.OrdinalIgnoreCase)) clientHeader = true;
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
                binary = RenderMultiviewJpeg(
                    int.TryParse(QueryValue(path, "w"), out var mvw) ? Math.Clamp(mvw, 320, 1920) : 1024);
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
                    while (Interlocked.Read(ref _rev) == seen && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(150, ct);
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
                if (IsCueVerb(cmd.Kind) && !clientHeader)
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

    private static bool IsCueVerb(RemoteCommandKind kind) => kind is
        RemoteCommandKind.CueGo or RemoteCommandKind.CueStandby or RemoteCommandKind.CueStandbyNext or RemoteCommandKind.CueStandbyPrev or
        RemoteCommandKind.CueHoldOn or RemoteCommandKind.CueHoldOff or RemoteCommandKind.CueArmOn or RemoteCommandKind.CueArmOff or
        RemoteCommandKind.StopAll;

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
            var snap = _services.Bus.Current;
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
    private byte[] RenderMultiviewJpeg(int width)
    {
        lock (_mvGate)
        {
            var snap = _services.Bus.Current;
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
            _mvEngine.RenderMultiview(surface.Canvas, in frame, _mvSink, snap.State.Pattern.Multiview);
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
