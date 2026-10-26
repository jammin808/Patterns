using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 88 (Show Bus 1.2): the web remote's TLS on its one port (the handshake, the certificate the hello names,
/// /tls and /tls.cer, Require HTTPS), the Stagewatch link over TLS (trust on first use from the hello, a changed
/// certificate refused until ACCEPT), the approval queue as a client (a proposal sent, approved there, applied here,
/// answered; another app's approved line run through the wire path), and a desk's timer node following the desk's
/// Stagewatch by relay and sending its own context back — real sockets, a scripted Stagewatch.
/// </summary>
public class ShowBus12AppTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the condition never held");
        }
    }

    private static X509Certificate2 Cert(string name)
    {
        using var made = TlsIdentity.Create(name, new[] { "localhost" }, new[] { IPAddress.Loopback }, DateTime.UtcNow);
        return TlsIdentity.Import(TlsIdentity.Export(made));
    }

    /// <summary>One request to the web remote over plain TCP or TLS, made on a worker while the UI thread pumps (the desk answers some routes there).</summary>
    private static (string Status, string Body, string Fingerprint) Request(int port, string path, bool tls)
        => Pump(Task.Run(() => RequestOnWorker(port, path, tls)), 15000);

    private static (string Status, string Body, string Fingerprint) RequestOnWorker(int port, string path, bool tls)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        Stream stream = client.GetStream();
        var seen = "";
        if (tls)
        {
            var ssl = new SslStream(stream, false, (_, cert, _, _) =>
            {
                if (cert is not null)
                {
                    using var c2 = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
                    seen = TlsFingerprint.Of(c2);
                }
                return true;
            });
            ssl.AuthenticateAsClient("localhost");
            stream = ssl;
        }
        stream.Write(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost:{port}\r\nConnection: close\r\n\r\n"));   // a browser's Host carries the port
        using var all = new MemoryStream();
        stream.CopyTo(all);
        var text = Encoding.UTF8.GetString(all.ToArray());
        var cut = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (text[(text.IndexOf(' ') + 1)..(text.IndexOf(' ') + 4)], cut < 0 ? "" : text[(cut + 4)..], seen);
    }

    [AvaloniaFact]
    public void TheWebRemoteServesTlsOnItsOnePortWithTheCertificateItsHelloNames()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var trusted = ControlService.TrustLoopback;
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;
            var fp = services.Kernel.Tls.Fingerprint;
            Assert.True(TlsFingerprint.IsValid(fp));
            Assert.Equal(fp, services.Kernel.Beacon.BuildHello().Tls);                    // the hello names the certificate it serves
            Assert.Equal(port, services.Kernel.Beacon.BuildHello().Ports.Https);

            var overTls = Request(port, "/tls", tls: true);
            Assert.Equal("200", overTls.Status);
            Assert.Equal(fp, overTls.Fingerprint);                                         // the handshake showed that certificate
            Assert.Contains(fp, overTls.Body, StringComparison.Ordinal);
            var plain = Request(port, "/tls", tls: false);
            Assert.Equal("200", plain.Status);                                             // plain HTTP keeps working by default

            var cer = Request(port, "/tls.cer", tls: false);
            Assert.Equal("200", cer.Status);
            Assert.True(services.Control.TlsConnections >= 1);

            // Require HTTPS: another machine's plain request is refused, TLS still answers. (Loopback plays another machine.)
            ControlService.TrustLoopback = false;
            vm.State.Control.RequireHttps = true;
            var refused = Request(port, "/api/state", tls: false);
            Assert.Equal("403", refused.Status);
            Assert.Contains("requires HTTPS", refused.Body, StringComparison.Ordinal);
            Assert.Equal("200", Request(port, "/tls", tls: true).Status);
            ControlService.TrustLoopback = true;
            Assert.Equal("200", Request(port, "/tls", tls: false).Status);                 // this machine may stay plain
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.RequireHttps = false;
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    /// <summary>A scripted Stagewatch: /ws (frames, pongs), /api/context, /api/peers/context and the approval queue — plain or TLS.</summary>
    private sealed class FakeStagewatch : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _frame;

        public int Port { get; }
        public volatile X509Certificate2? Certificate;
        public readonly ConcurrentQueue<string> PostedContexts = new();
        public readonly ConcurrentDictionary<string, (string Json, string State, string Line, string Target, string From)> Proposals = new();
        public readonly ConcurrentDictionary<string, string> Results = new();
        public volatile string Authorization = "";
        /// <summary>Every Authorization header any request carried, in order — the key leaves the desk only where this fills.</summary>
        public readonly ConcurrentQueue<string> Authorizations = new();
        /// <summary>The Authorization headers that came on a connection without TLS — the key in clear.</summary>
        public readonly ConcurrentQueue<string> PlainAuthorizations = new();
        /// <summary>Its one port speaks both: a connection that opens with a TLS greeting gets TLS, any other is served plain.</summary>
        public volatile bool Sniff;
        /// <summary>How long it takes, after a TLS greeting arrives, to start its handshake — a busy desk or a slow path.</summary>
        public volatile int HandshakeDelayMs;
        /// <summary>With no certificate: a TLS greeting is answered at once as a bad request, the way a plain web server answers bytes it cannot read.</summary>
        public volatile bool RefuseTlsGreeting;
        /// <summary>Its show thread has stopped: each ping answered with the pong and Stagewatch's stuck beat, never a frame.</summary>
        public volatile bool Stuck;
        /// <summary>A collect of what was approved is refused 403 with these words (Stagewatch 2.18's binding refusal); null: answered.</summary>
        public volatile string? CollectRefusal;
        /// <summary>How many result posts still to refuse 403 (a key bound to another node, said by Stagewatch 2.18), each one counted in <see cref="ResultPosts"/>.</summary>
        public int RefuseResults;
        /// <summary>Every result post, refused or taken, by the proposal's id.</summary>
        public readonly ConcurrentDictionary<string, int> ResultPosts = new();
        private int _next;

        public FakeStagewatch(string frame, X509Certificate2? certificate, int port = 0)
        {
            _frame = frame;
            Certificate = certificate;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(Serve, _cts.Token);
        }

        public void Approve(string id) => Proposals.AddOrUpdate(id, _ => ("", "approved", "", "", ""), (_, p) => p with { State = "approved" });

        private string Target(string id) => Proposals.TryGetValue(id, out var p) ? p.Target : "";

        public string AddApproved(string line, string target, string from = "sw")
        {
            var id = "x" + Interlocked.Increment(ref _next);
            Proposals[id] = ("", "approved", line, target, from);
            return id;
        }

        private async Task Serve()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => Handle(c), _cts.Token);
            }
        }

        private async Task Handle(TcpClient client)
        {
            using var _ = client;
            try
            {
                Stream s = client.GetStream();
                var cert = Certificate;
                if (Sniff || RefuseTlsGreeting)
                {
                    var opening = new byte[1];
                    if (await client.Client.ReceiveAsync(opening, SocketFlags.Peek, _cts.Token) == 0) return;
                    var greeting = opening[0] == 0x16;
                    if (greeting && cert is null && RefuseTlsGreeting)
                    {
                        await s.WriteAsync("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _cts.Token);
                        return;
                    }
                    if (!greeting) cert = null;
                }
                var overTls = cert is not null;
                if (cert is not null)
                {
                    if (HandshakeDelayMs > 0) await Task.Delay(HandshakeDelayMs, _cts.Token);
                    var ssl = new SslStream(s);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, _cts.Token);
                    s = ssl;
                }
                var (head, body) = await ReadRequest(s);
                var first = head.Split("\r\n")[0].Split(' ');
                var (method, path) = (first[0], first[1]);
                var auth = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
                if (auth is not null)
                {
                    Authorization = auth["Authorization:".Length..].Trim();
                    Authorizations.Enqueue(Authorization);
                    if (!overTls) PlainAuthorizations.Enqueue(Authorization);
                }
                if (path.StartsWith("/ws", StringComparison.Ordinal))
                {
                    var key = head.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))["Sec-WebSocket-Key:".Length..].Trim();
                    var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _cts.Token);
                    using var ws = WebSocket.CreateFromStream(s, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
                    // Round 91: as Stagewatch 2.18 does, a node link hears its verdict before any frame.
                    if (StagewatchBytes.FirstFor(path, auth is null ? "" : auth["Authorization:".Length..].Trim()) is { } verdict)
                        await ws.SendAsync(Encoding.UTF8.GetBytes(verdict), WebSocketMessageType.Text, true, _cts.Token);
                    await ws.SendAsync(Encoding.UTF8.GetBytes(_frame), WebSocketMessageType.Text, true, _cts.Token);
                    var buffer = new byte[8192];
                    while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                    {
                        var r = await ws.ReceiveAsync(buffer, _cts.Token);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count));
                        var t = doc.RootElement.GetProperty("t").GetInt64();
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        await ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"type\":\"pong\",\"t\":{t},\"t2\":{now},\"t3\":{now}}}"), WebSocketMessageType.Text, true, _cts.Token);
                        await ws.SendAsync(Encoding.UTF8.GetBytes(Stuck ? "{\"type\":\"beat\",\"live\":false}" : _frame), WebSocketMessageType.Text, true, _cts.Token);
                    }
                    return;
                }
                var (status, reply) = Route(method, path, body);
                var bytes = Encoding.UTF8.GetBytes(reply);
                await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _cts.Token);
                await s.WriteAsync(bytes, _cts.Token);
                await s.FlushAsync(_cts.Token);
            }
            catch (Exception)
            {
                // a client that left, or a handshake the test meant to fail
            }
        }

        private (string Status, string Body) Route(string method, string path, string body)
        {
            if (path == "/api/context")
                return ("200 OK", "{\"v\":1,\"node\":{\"t\":\"main\",\"id\":\"swdesk01\",\"name\":\"Hall A\"},\"light\":\"green\",\"headline\":\"Keynote\",\"items\":[],\"commands\":[]}");
            if (path == "/api/peers/context" && method == "POST")
            {
                PostedContexts.Enqueue(body);
                return ("200 OK", "{\"ok\":true,\"verified\":\"key\"}");                       // 2.18 says what it holds the context as
            }
            if (path == "/api/proposals" && method == "POST")
            {
                var id = "p" + Interlocked.Increment(ref _next);
                using var doc = JsonDocument.Parse(body);
                Proposals[id] = (body, "pending", doc.RootElement.GetProperty("line").GetString()!, doc.RootElement.GetProperty("target").GetProperty("id").GetString()!, doc.RootElement.GetProperty("from").GetProperty("id").GetString()!);
                return ("200 OK", JsonSerializer.Serialize(new { id, state = "pending" }));
            }
            if (path.StartsWith("/api/proposals?", StringComparison.Ordinal))
            {
                var q = path[(path.IndexOf('?') + 1)..].Split('&').Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => Uri.UnescapeDataString(kv[1]));
                if (q.ContainsKey("target") && CollectRefusal is { } refusal) return ("403 Forbidden", JsonSerializer.Serialize(new { ok = false, error = refusal }));
                var rows = Proposals.Where(p =>
                        (!q.TryGetValue("from", out var f) || p.Value.From == f) &&
                        (!q.TryGetValue("target", out var t) || p.Value.Target == t) &&
                        (!q.TryGetValue("state", out var st) || p.Value.State == st))
                    .Select(p => new { id = p.Key, state = p.Value.State, line = p.Value.Line, target = new { t = "patterns", id = p.Value.Target }, from = new { id = p.Value.From } });
                return ("200 OK", JsonSerializer.Serialize(new { proposals = rows }));
            }
            if (path.StartsWith("/api/proposals/", StringComparison.Ordinal) && path.EndsWith("/result", StringComparison.Ordinal) && method == "POST")
            {
                var id = path["/api/proposals/".Length..^"/result".Length];
                ResultPosts.AddOrUpdate(id, 1, (_, n) => n + 1);
                if (Interlocked.Decrement(ref RefuseResults) >= 0) return ("403 Forbidden", JsonSerializer.Serialize(new { ok = false, error = "this key speaks for patterns 1a2b3c4d — make a key for patterns " + Target(id) }));
                Results[id] = body;
                using var doc = JsonDocument.Parse(body);
                Proposals.AddOrUpdate(id, _ => ("", "done", "", "", ""), (_, p) => p with { State = doc.RootElement.GetProperty("ok").GetBoolean() ? "done" : "failed" });
                return ("200 OK", "{\"ok\":true}");
            }
            return ("404 Not Found", "{}");
        }

        private static async Task<(string Head, string Body)> ReadRequest(Stream s)
        {
            var buf = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (await s.ReadAsync(one) == 0) break;
                buf.Add(one[0]);
                if (buf.Count >= 4 && buf[^1] == '\n' && buf[^2] == '\r' && buf[^3] == '\n' && buf[^4] == '\r') break;
            }
            var head = Encoding.ASCII.GetString(buf.ToArray());
            var lengthLine = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = lengthLine is null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var body = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = await s.ReadAsync(body.AsMemory(read));
                if (n == 0) break;
                read += n;
            }
            return (head, Encoding.UTF8.GetString(body));
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }

    private const string Frame = "{\"type\":\"state\",\"v\":1,\"seq\":4,\"mode\":\"countdown\",\"state\":\"running\",\"seconds\":600,\"total\":900,\"text\":\"10:00\",\"phase\":\"normal\",\"title\":\"Keynote — Ana\"}";

    /// <summary>A hello from the scripted Stagewatch, as the desk's listener would hear it (kept live by hearing it again).</summary>
    private static void Hear(AppServices services, int port, string fingerprint, params string[] caps)
    {
        var hello = JsonSerializer.Serialize(new { t = "main", v = 1, proto = 1, id = "swdesk01", name = "Hall A", ports = new { http = port, ws = port, https = fingerprint.Length > 0 ? port : 0 }, caps, tls = fingerprint.Length > 0 ? fingerprint : null });
        services.Kernel.Beacon.ShowBus.Hear(Encoding.UTF8.GetBytes(hello), "127.0.0.1", DateTime.UtcNow, "not-this-process");
    }

    [AvaloniaFact]
    public void TheLinkTrustsAStagewatchsCertificateOnFirstUseAndRefusesAChangeUntilAccepted()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var certA = Cert("Hall A");
        using var certB = Cert("Hall A (reinstalled)");
        var fpA = TlsFingerprint.Of(certA);
        var fpB = TlsFingerprint.Of(certB);
        var sw = new FakeStagewatch(Frame, certA);
        try
        {
            var link = vm.State.Control.Stagewatch;
            link.Host = "127.0.0.1:" + sw.Port;
            link.ApiKey = "swk_nodekey";
            Hear(services, sw.Port, fpA);
            link.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();
            PumpUntil(() => { Hear(services, sw.Port, fpA); return services.Stage.Following.Length > 0; });
            Assert.Equal("Bearer swk_nodekey", sw.Authorization);
            PumpUntil(() => { Hear(services, sw.Port, fpA); return link.Pins.Any(p => p.Fingerprint == fpA); });    // pinned once the key's read was answered
            Assert.Contains("over TLS", services.Kernel.Stagewatch.Status + " over TLS", StringComparison.Ordinal);

            // The Stagewatch is reinstalled: a new certificate on the same door. The link refuses it until ACCEPT.
            var port = sw.Port;
            sw.Dispose();
            sw = new FakeStagewatch(Frame, certB, port);
            link.Host = "127.0.0.1:" + port + " ";                                                // a nudge so the link dials again at once
            link.Host = "127.0.0.1:" + port;
            services.Kernel.Stagewatch.Reconcile();
            PumpUntil(() => { Hear(services, port, fpB); return services.Kernel.Stagewatch.PendingFingerprint == fpB; });
            Assert.StartsWith("CERTIFICATE CHANGED", services.Kernel.Stagewatch.TlsWords);
            Assert.Contains(services.ShowApps(DateTime.UtcNow), a => a.Words.StartsWith("CERTIFICATE CHANGED", StringComparison.Ordinal));
            Assert.DoesNotContain(link.Pins, p => p.Fingerprint == fpB);
            Assert.Contains("Accepted", services.Kernel.Stagewatch.AcceptPendingCertificate(), StringComparison.Ordinal);
            Assert.Contains(link.Pins, p => p.Fingerprint == fpB);
            PumpUntil(() => { Hear(services, port, fpB); return services.Stage.Following.Length > 0 && services.Kernel.Stagewatch.PendingFingerprint.Length == 0; });
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            sw.Dispose();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void OffThisMachineAStagewatchThatOffersNoTlsIsReadWithoutTheKey()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var sw = new FakeStagewatch(Frame, null);
        var link = services.Kernel.Stagewatch;
        try
        {
            link.IsLocal = _ => false;                                                                // loopback plays another machine
            link.TlsAnswerWithin = TimeSpan.FromMilliseconds(500);
            var config = vm.State.Control.Stagewatch;
            config.Host = "127.0.0.1:" + sw.Port;
            config.ApiKey = "swk_nodekey";
            Hear(services, sw.Port, "", "timer", "approvals");                                       // a hello naming no TLS: a hint, never the carrier
            config.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return services.Stage.Following.Length > 0 && link.Sight.Context is not null; });

            // The timer is followed and the context read — without the key: no request carried it, no context was posted,
            // no proposal was sent, and the link says why.
            Assert.Equal("keyless", link.Carrier);
            Assert.Empty(sw.Authorizations);
            Assert.True(sw.PostedContexts.IsEmpty);
            Assert.Empty(sw.Proposals);
            Assert.False(link.ApprovalsLive);
            Assert.Contains("Hall A offers no TLS — the key stays on this desk", link.Status, StringComparison.Ordinal);
            var row = JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("bus").GetProperty("stagewatch");
            Assert.Equal("keyless", row.GetProperty("carrier").GetString());
            Assert.Contains("the key stays on this desk", row.GetProperty("words").GetString(), StringComparison.Ordinal);
            services.Nodes.Poll();
            Assert.Contains("Hall A offers no TLS — the key stays on this desk", services.Nodes.ShowBusLine, StringComparison.Ordinal);   // the Nodes page's line

            // The same Stagewatch on this machine gets the key over plain ws, as before.
            config.Enabled = false;
            link.Reconcile();
            link.IsLocal = IPAddress.IsLoopback;
            config.Enabled = true;
            link.Reconcile();
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return link.Carrier == "plain" && !sw.Authorizations.IsEmpty; });
            Assert.All(sw.Authorizations, a => Assert.Equal("Bearer swk_nodekey", a));
            Assert.DoesNotContain("offers no TLS", link.Status, StringComparison.Ordinal);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            link.Reconcile();
            link.IsLocal = IPAddress.IsLoopback;
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void OffThisMachineAStagewatchThatAnswersTheTlsGreetingAsABadRequestIsReadWithoutTheKey()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var sw = new FakeStagewatch(Frame, null) { RefuseTlsGreeting = true };
        var link = services.Kernel.Stagewatch;
        try
        {
            link.IsLocal = _ => false;                                                                // loopback plays another machine
            link.TlsAnswerWithin = TimeSpan.FromSeconds(30);                                         // only its answer can end the TLS attempt in time
            var config = vm.State.Control.Stagewatch;
            config.Host = "127.0.0.1:" + sw.Port;
            config.ApiKey = "swk_nodekey";
            Hear(services, sw.Port, "", "timer", "approvals");
            config.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();

            // The handshake fails on the bad request it got instead of a certificate: plain at once, without the key.
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return link.Carrier == "keyless" && services.Stage.Following.Length > 0; }, 10000);
            Assert.Empty(sw.Authorizations);
            Assert.True(sw.PostedContexts.IsEmpty);
            Assert.Contains("Hall A offers no TLS — the key stays on this desk", link.Status, StringComparison.Ordinal);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            link.Reconcile();
            link.IsLocal = IPAddress.IsLoopback;
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStagewatchSlowToShowItsCertificateIsAskedAgainAndTheKeyGoesOnlyOverTls()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var cert = Cert("Hall A");
        var fp = TlsFingerprint.Of(cert);
        using var sw = new FakeStagewatch(Frame, cert) { Sniff = true, HandshakeDelayMs = 2500 };
        var link = services.Kernel.Stagewatch;
        try
        {
            link.IsLocal = _ => false;                                                                // loopback plays another machine
            link.TlsAnswerWithin = TimeSpan.FromMilliseconds(300);
            link.TlsReprobeEvery = TimeSpan.FromMilliseconds(300);
            var config = vm.State.Control.Stagewatch;
            config.Host = "127.0.0.1:" + sw.Port;
            config.ApiKey = "swk_nodekey";
            Hear(services, sw.Port, fp, "timer");                                                     // its hello names its certificate
            config.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();

            // Its certificate comes after the short wait: read without the key, and the words say its hello names one.
            PumpUntil(() => { Hear(services, sw.Port, fp, "timer"); return link.Carrier == "keyless"; });
            Assert.Contains("Hall A answered without TLS though its hello names a certificate — the key stays on this desk", link.Status, StringComparison.Ordinal);

            // Asked again while keyless, it shows its certificate: the link dials again over TLS, waiting long enough this
            // time, and the key goes over that — never over the plain connection.
            PumpUntil(() => { Hear(services, sw.Port, fp, "timer"); return link.Carrier == "tls" && sw.Authorization == "Bearer swk_nodekey"; }, 20000);
            Assert.Empty(sw.PlainAuthorizations);
            Assert.DoesNotContain("the key stays on this desk", link.Status, StringComparison.Ordinal);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            link.Reconcile();
            link.IsLocal = IPAddress.IsLoopback;
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheCarrierIsChosenByThePinAndTheProbeNeverByTheHello()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var cert = Cert("Hall A");
        var fp = TlsFingerprint.Of(cert);
        var sw = new FakeStagewatch(Frame, cert);
        var port = sw.Port;
        var link = services.Kernel.Stagewatch;
        try
        {
            link.IsLocal = _ => false;                                                                // loopback plays another machine
            link.TlsAnswerWithin = TimeSpan.FromMilliseconds(500);
            var config = vm.State.Control.Stagewatch;
            config.ApiKey = "swk_nodekey";
            Hear(services, port, "");                                                                 // its hello names no TLS (stale, or forged)
            config.Enabled = true;                                                                    // found on the show network: node:swdesk01
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();

            // No pin, off this machine: TLS first. It answers with TLS, so the key goes over it, and the certificate is
            // pinned once the key's read is answered.
            PumpUntil(() => { Hear(services, port, ""); return services.Stage.Following.Length > 0; });
            Assert.Equal("tls", link.Carrier);
            Assert.Equal("Bearer swk_nodekey", sw.Authorization);
            PumpUntil(() => { Hear(services, port, ""); return config.Pins.Any(p => p.Key == "node:swdesk01" && p.Fingerprint == fp); });

            // Pinned now: the next dial is TLS whatever the hello says, and the hello that names no TLS is a note, never a refusal.
            link.Reconcile();                                                                         // the pin moved: the link dials again with it
            PumpUntil(() => { Hear(services, port, ""); return services.Stage.Following.Length > 0 && link.Status.Contains("names no TLS", StringComparison.Ordinal); });
            Assert.Equal("tls", link.Carrier);
            Assert.Contains("the hello heard for Hall A names no TLS — a stale or forged hello; dialling TLS as before", link.Status, StringComparison.Ordinal);

            // A hello that names another certificate: the pin decides — TLS against the one pinned, the hello a note, the pin unchanged.
            using var other = Cert("Not Hall A");
            var fpOther = TlsFingerprint.Of(other);
            config.Enabled = false;
            link.Reconcile();
            Hear(services, port, fpOther);
            config.Enabled = true;
            link.Reconcile();
            PumpUntil(() => { Hear(services, port, fpOther); return services.Stage.Following.Length > 0 && link.Status.Contains("names another certificate", StringComparison.Ordinal); });
            Assert.Equal("tls", link.Carrier);
            Assert.Contains($"the hello heard for Hall A names another certificate ({TlsFingerprint.Short(fpOther)}) — the pin decides: dialling TLS against the one pinned", link.Status, StringComparison.Ordinal);
            Assert.Contains(config.Pins, p => p.Key == "node:swdesk01" && p.Fingerprint == fp);
            Assert.DoesNotContain(config.Pins, p => p.Fingerprint == fpOther);

            // Another machine answers at its address without TLS: the pin decides — nothing of this desk's is sent to it.
            // First one that reads the greeting and waits (it showed no certificate in the whole wait a pinned dial gives),
            // then one that answers the greeting as a bad request.
            sw.Dispose();
            sw = new FakeStagewatch(Frame, null, port);
            PumpUntil(() => { Hear(services, port, ""); return link.Status.Contains("showed no certificate within 5 s, and its certificate is pinned", StringComparison.Ordinal) && link.Carrier.Length == 0; }, 30000);
            sw.RefuseTlsGreeting = true;
            PumpUntil(() => { Hear(services, port, ""); return link.Status.Contains("answered without TLS, and its certificate is pinned", StringComparison.Ordinal) && link.Carrier.Length == 0; }, 30000);
            Assert.Empty(sw.Authorizations);
            Assert.True(sw.PostedContexts.IsEmpty);
            Assert.Contains("nothing sent", link.Status, StringComparison.Ordinal);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            link.Reconcile();
            link.IsLocal = IPAddress.IsLoopback;
            Dispatcher.UIThread.RunJobs();
            sw.Dispose();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AnApprovalDeskGetsTheAssistantsProposalAndItLandsHereOnceApproved()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var sw = new FakeStagewatch(Frame, null);
        try
        {
            var link = vm.State.Control.Stagewatch;
            link.Host = "127.0.0.1:" + sw.Port;
            link.ApiKey = "swk_nodekey";
            Hear(services, sw.Port, "", "timer", "approvals");
            link.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return services.Kernel.Stagewatch.ApprovalsLive; }, 60000);

            // This desk's own context reaches the Stagewatch (its show network, and the key's binding to this id).
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return !sw.PostedContexts.IsEmpty; }, 60000);
            Assert.Contains(services.Kernel.ShowBusId, sw.PostedContexts.First(), StringComparison.Ordinal);

            // The assistant proposes; the proposal goes to the desk, waits, is approved there and lands here.
            var applied = 0;
            var said = new ConcurrentQueue<string>();
            var local = services.Kernel.Stagewatch.Approvals.Add("Look", "Keynote blue", "the keynote's look", "review", () => { applied++; return (true, "look added"); }, said.Enqueue);
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return sw.Proposals.Any(p => p.Value.Line.Contains(local, StringComparison.Ordinal)); }, 60000);
            var deskId = sw.Proposals.First(p => p.Value.Line.Contains(local, StringComparison.Ordinal)).Key;
            Assert.Equal(services.Kernel.ShowBusId, sw.Proposals[deskId].Target);
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return said.Any(w => w.StartsWith("waiting for approval", StringComparison.Ordinal)); }, 60000);
            Assert.Equal(0, applied);                                                                 // nothing lands before the desk says so
            sw.Approve(deskId);
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return sw.Results.ContainsKey(deskId); }, 60000);
            Assert.Equal(1, applied);
            Assert.Contains("\"ok\":true", sw.Results[deskId], StringComparison.Ordinal);
            Assert.Contains(said, w => w.StartsWith("applied", StringComparison.Ordinal));

            // Another app's assistant proposed a wire line to this desk; approved there, it runs through the wire's own path.
            var lineId = sw.AddApproved("PING", services.Kernel.ShowBusId);
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return sw.Results.ContainsKey(lineId); }, 60000);
            Assert.Contains("\"ok\":true", sw.Results[lineId], StringComparison.Ordinal);
            var refusedId = sw.AddApproved("NOSUCHVERB here", services.Kernel.ShowBusId);
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return sw.Results.ContainsKey(refusedId); }, 60000);
            Assert.Contains("\"ok\":false", sw.Results[refusedId], StringComparison.Ordinal);    // its fences still say no
            Assert.Equal(1, applied);                                                                 // and nothing ran twice
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [Fact]
    public void AnApplyIsReadAgainstTheProposalItNamesEvenWhenAnotherHoldsTheSameApprovalDeskId()
    {
        // Round 91.3: an approval desk's ids can restart with its run, so an entry still held from before a restart and a new
        // one can carry the same approval-desk id. Each APPLY is read against the entry its line names — never the first
        // entry holding that id — and an entry that ran is refused, however the ids collected were remembered.
        const string self = "1a2b3c4d";
        var approvals = new DeskApprovals();
        var older = approvals.Add("Look", "Walk-in", "the doors' look", "review", () => (true, "look added"), _ => { });
        var newer = approvals.Add("Look", "Keynote blue", "the keynote's look", "review", () => (true, "look added"), _ => { });
        approvals.ByLocal(older)!.DeskId = "p1";
        approvals.ByLocal(newer)!.DeskId = "p1";
        DeskProposal Approved(string local) => new() { Id = "p1", State = "approved", Line = approvals.ByLocal(local)!.Line, FromId = self, TargetId = self };

        foreach (var local in new[] { older, newer })
        {
            var (entry, refusal) = approvals.ForApply(Approved(local), local, self);
            Assert.Null(refusal);
            Assert.Same(approvals.ByLocal(local), entry);
        }

        approvals.ByLocal(newer)!.Ran = true;
        Assert.Equal(DeskProposals.AlreadyApplied, approvals.ForApply(Approved(newer), newer, self).Refusal);
        Assert.Null(approvals.ForApply(Approved(older), older, self).Refusal);
        var (gone, words) = approvals.ForApply(Approved(newer) with { Line = "APPLY a00000000" }, "a00000000", self);
        Assert.Null(gone);
        Assert.Equal(DeskProposals.NoLongerHeld, words);
    }

    [AvaloniaFact]
    public void AnApplyRunsOnlyAsTheProposalThisDeskMadeAndACopyIsRefusedNeverApplied()
    {
        // Round 91.3: the approval desk lists this desk's APPLY line where any node can read it. Another node proposing the
        // same words to this desk, or this desk's id on a proposal the desk did not answer this desk's post with, names the
        // proposal without being it: refused with the reason, in the result and the log, and never applied. The proposal
        // itself, approved, still lands — once.
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var sw = new FakeStagewatch(Frame, null);
        var link = services.Kernel.Stagewatch;
        try
        {
            var self = services.Kernel.ShowBusId;
            var config = vm.State.Control.Stagewatch;
            config.Host = "127.0.0.1:" + sw.Port;
            config.ApiKey = "swk_nodekey";
            Hear(services, sw.Port, "", "timer", "approvals");
            config.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return link.ApprovalsLive; }, 60000);

            var applied = 0;
            var said = new ConcurrentQueue<string>();
            var local = link.Approvals.Add("Look", "Keynote blue", "the keynote's look", "review", () => { applied++; return (true, "look added"); }, said.Enqueue);
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return sw.Proposals.Any(p => p.Value.Line.Contains(local, StringComparison.Ordinal)); }, 60000);
            var deskId = sw.Proposals.First(p => p.Value.Line.Contains(local, StringComparison.Ordinal)).Key;
            var line = sw.Proposals[deskId].Line;
            Assert.Equal(self, sw.Proposals[deskId].From);

            string Words(string id) => JsonDocument.Parse(sw.Results[id]).RootElement.GetProperty("words").GetString()!;
            bool Ok(string id) => JsonDocument.Parse(sw.Results[id]).RootElement.GetProperty("ok").GetBoolean();
            var log = Path.Combine(b.Dir, "patterns.log");
            int Logged(string id) => File.ReadAllLines(log).Count(l => l.Contains($"approved {id} names this desk's proposal {local} — refused: ", StringComparison.Ordinal));

            // Another node re-proposed this desk's line to this desk, and it was approved there.
            var copied = sw.AddApproved(line, self, from: "pm-a1b2");
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return sw.Results.ContainsKey(copied); }, 60000);
            Assert.False(Ok(copied));
            Assert.Equal(DeskProposals.OnlyOwnApply + " — another node proposed this one", Words(copied));
            Assert.Equal(1, Logged(copied));
            Assert.Equal(0, applied);

            // This desk's own id on a proposal that is not the one the desk answered this desk's post with.
            var under = sw.AddApproved(line, self, from: self);
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return sw.Results.ContainsKey(under); }, 60000);
            Assert.False(Ok(under));
            Assert.Equal(DeskProposals.OnlyOwnApply + $" — this desk posted {local} under another id", Words(under));
            Assert.Equal(1, Logged(under));
            Assert.Equal(0, applied);
            Assert.DoesNotContain(said, w => w.StartsWith("applied", StringComparison.Ordinal));

            // The proposal itself, approved: applied here once, and said.
            sw.Approve(deskId);
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return sw.Results.ContainsKey(deskId); }, 60000);
            Assert.True(Ok(deskId));
            Assert.Equal(1, applied);
            Assert.Contains(said, w => w.StartsWith("applied", StringComparison.Ordinal));
            Assert.Equal(0, Logged(deskId));

            // Once applied, a copy approved later is refused the same way: the proposal never runs twice.
            var late = sw.AddApproved(line, self, from: "pm-a1b2");
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return sw.Results.ContainsKey(late); }, 60000);
            Assert.False(Ok(late));
            Assert.Equal(1, applied);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            link.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ARefusedCollectIsSaidInTheApprovalDesksOwnWordsAndItsNodeRoutesAreHeld()
    {
        // Round 91: a refused collect used to pass in silence; now its words are the approval desk's line, and a 403 holds the
        // key's node routes — the context it posts, the proposals, the collect and the results.
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var sw = new FakeStagewatch(Frame, null);
        var link = services.Kernel.Stagewatch;
        try
        {
            var id = services.Kernel.ShowBusId;
            sw.CollectRefusal = $"patterns {id} speaks with its own key";
            var config = vm.State.Control.Stagewatch;
            config.Host = "127.0.0.1:" + sw.Port;
            config.ApiKey = "swk_nodekey";
            Hear(services, sw.Port, "", "timer", "approvals");
            config.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return link.ApprovalWords.StartsWith("the approval desk refused", StringComparison.Ordinal); }, 30000);
            Assert.Equal($"the approval desk refused to hand over what was approved for this desk — patterns {id} speaks with its own key", link.ApprovalWords);
            Assert.False(link.ApprovalsLive);
            Assert.Equal("none", link.Key.Word);
            Assert.InRange(link.KeyHeld.TotalSeconds, 50, 60.5);
            services.Nodes.Poll();
            Assert.Contains("the approval desk refused to hand over what was approved for this desk", services.Nodes.ShowBusLine, StringComparison.Ordinal);
            Assert.Contains($"key not accepted for this desk — patterns {id} speaks with its own key", services.Nodes.ShowBusLine, StringComparison.Ordinal);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            link.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AResultAHoldStoppedIsOwedAndSentOnceTheKeyMayPostItAndItsProposalRunsOnce()
    {
        // Round 91: an approved proposal ran here, and its result was refused (the key bound to another node, 403): the
        // node routes are held, the result is owed, and once the hold ends it goes — the proposal never runs twice.
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var sw = new FakeStagewatch(Frame, null) { RefuseResults = 1 };
        var link = services.Kernel.Stagewatch;
        try
        {
            link.RefusedHoldAtLeast = TimeSpan.FromSeconds(1);
            var config = vm.State.Control.Stagewatch;
            config.Host = "127.0.0.1:" + sw.Port;
            config.ApiKey = "swk_nodekey";
            Hear(services, sw.Port, "", "timer", "approvals");
            config.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return link.ApprovalsLive; }, 60000);

            var applied = 0;
            var local = link.Approvals.Add("Look", "Keynote blue", "the keynote's look", "review", () => { applied++; return (true, "look added"); }, _ => { });
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return sw.Proposals.Any(p => p.Value.Line.Contains(local, StringComparison.Ordinal)); }, 60000);
            var deskId = sw.Proposals.First(p => p.Value.Line.Contains(local, StringComparison.Ordinal)).Key;
            sw.Approve(deskId);

            // Refused once: held, said, and owed — not lost, and not run again when the collect lists it again.
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return sw.Results.ContainsKey(deskId); }, 60000);
            Assert.Equal(2, sw.ResultPosts[deskId]);                                                 // refused, then sent again past the hold
            Assert.Equal(1, applied);
            Assert.Contains("\"ok\":true", sw.Results[deskId], StringComparison.Ordinal);
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return link.ApprovalsLive; }, 30000);
            Assert.Equal(2, sw.ResultPosts[deskId]);                                                 // settled: never sent a third time
            Assert.Equal(1, applied);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            link.Reconcile();
            link.RefusedHoldAtLeast = StagewatchFrame.RefusedHold;
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheBenchsRoadQueuesALookAtTheApprovalDeskAndItLandsInThePreviewOnlyOnceApproved()
    {
        // Round 90: the road the bench host's `propose look` takes (AssistantPage.ProposeToApprovalDesk) — queued there, nothing
        // here until the desk approves it, and then the look in the preview with EDIT SAFE open and the air as it was.
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var sw = new FakeStagewatch(Frame, null);
        try
        {
            vm.IsSandboxActive = false;
            var link = vm.State.Control.Stagewatch;
            link.Host = "127.0.0.1:" + sw.Port;
            link.ApiKey = "swk_nodekey";
            Hear(services, sw.Port, "", "timer", "approvals");
            link.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return services.Kernel.Stagewatch.ApprovalsLive; }, 60000);
            var air = JsonUtil.Serialize(services.AirState.Pattern);
            var airLook = services.AirLookId;

            var chip = new ViewModels.AssistantChip(vm.Assistant, new Patterns.Assistant.AssistantProposal
            {
                Kind = "look", Title = "Keynote blue", Looks = new[] { new Patterns.Assistant.LookPart("Keynote blue", 0, null, null) },
            });
            var (localId, _) = vm.Assistant.ProposeToApprovalDesk(chip);
            Assert.NotNull(localId);
            Assert.True(chip.IsApplied);                                                              // spent: it waits at the desk, APPLY is gone
            Assert.DoesNotContain("Keynote blue", services.State.LooksAndCues.Looks.Select(l => l.Name));
            Assert.False(vm.IsSandboxActive);

            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return sw.Proposals.Any(p => p.Value.Line.Contains(localId!, StringComparison.Ordinal)); }, 60000);
            var deskId = sw.Proposals.First(p => p.Value.Line.Contains(localId!, StringComparison.Ordinal)).Key;
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return chip.AppliedText.StartsWith("waiting for approval", StringComparison.Ordinal); }, 60000);
            Assert.DoesNotContain("Keynote blue", services.State.LooksAndCues.Looks.Select(l => l.Name));   // nothing lands before the desk says so

            sw.Approve(deskId);
            PumpUntil(() => { Hear(services, sw.Port, "", "timer", "approvals"); return sw.Results.ContainsKey(deskId); }, 60000);
            Assert.Contains("\"ok\":true", sw.Results[deskId], StringComparison.Ordinal);
            Assert.Contains("Keynote blue", services.State.LooksAndCues.Looks.Select(l => l.Name));
            Assert.True(vm.IsSandboxActive);                                                         // in the preview
            Assert.Equal(air, JsonUtil.Serialize(services.AirState.Pattern));                        // one air: the programme as it was
            Assert.Equal(airLook, services.AirLookId);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    private static void Settings(string dir, Action<ShowState> edit)
    {
        var state = SettingsStore.Fresh();
        edit(state);
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(state));
    }

    [AvaloniaFact]
    public void ATimerNodeFollowsItsDesksStagewatchByRelayAndTheDeskDrawsTheNodesOwnContext()
    {
        var twinPort = FreePort();
        using var sw = new FakeStagewatch(Frame, null);
        var desk = TestApp.Boot("patterns-tests-desk88-", dir => Settings(dir, s =>
        {
            s.Name = "Gala";
            s.Twin.Port = twinPort;
            s.Twin.Key = "hunter2";
            s.Control.HttpPort = FreePort();
            s.Control.TcpPort = FreePort();
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            s.Control.Stagewatch.Enabled = true;
            s.Control.Stagewatch.Host = "127.0.0.1:" + sw.Port;
            s.Control.Stagewatch.ApiKey = "swk_desk";
        }));
        NodeHost? node = null;
        var dir = Path.Combine(Path.GetTempPath(), "patterns-tests-node88-" + Guid.NewGuid().ToString("N"));
        try
        {
            var d = desk.Services;
            PumpUntil(() => d.Twin.LinkPort == twinPort);
            d.Kernel.Stagewatch.Reconcile();
            PumpUntil(() => d.Stage.Following.Length > 0);

            Directory.CreateDirectory(dir);
            Settings(dir, s =>
            {
                s.Twin.Key = "hunter2";
                s.Control.Enabled = true;
                s.Control.HttpPort = FreePort();
                s.Control.TcpPort = FreePort();
                s.Watchdog.BeaconListenPort = FreePort();
                s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            });
            node = NodeHost.Build(NodeKind.Timer, new SettingsStore(dir));
            node.Start();
            var t = node;
            Assert.False(t.State.Control.Stagewatch.Enabled);                                       // the node has no link, and no key, of its own
            t.Twin!.LinkTo(new NodeCard("d1", NodeKind.Desk, "FOH-PC", IPAddress.Loopback, "Gala", "", twinPort, 0, TimeSpan.Zero, false));
            PumpUntil(() => t.Twin.Phase == TwinPhase.InStep);

            // The desk's Stagewatch reaches the node by relay: its stage shows the same time.
            PumpUntil(() => t.FollowedStagewatch.Following);
            Assert.Equal("via the desk", t.FollowedStagewatch.Address);
            Assert.Equal("Keynote — Ana", t.FollowedStagewatch.Frame!.Title);
            Assert.Equal(Patterns.Core.Services.StageTimerPhase.Running, t.Stage.Time().Phase);
            Assert.InRange(t.Stage.Time().RemainingSeconds, 580, 600.5);

            // The node says what it is, itself, and its desk draws it from that.
            var own = ShowContext.Parse(Pump(t.NewRouter().ContextJsonAsync()))!;
            Assert.Equal(t.Kernel.ShowBusId, own.Node.Id);
            Assert.Contains(own.Items, i => i.Id == "stagewatch" && i.State.Contains("relayed by the desk", StringComparison.Ordinal));
            Assert.Contains(own.Items, i => i.Id == "desk" && i.Light == "green");
            PumpUntil(() => d.Twin.NodeContexts(DateTime.UtcNow).Any(), 20000);
            Assert.Contains(d.ShowApps(DateTime.UtcNow), a => a.KindWords == "Patterns node" && a.Context is { } c && c.Items.Any(i => i.Id == "stage"));
            d.Eye.Refresh();
            Assert.Contains(d.Eye.Graph.Nodes, n => n.Kind == EyeKind.ShowApp && n.Label.Length > 0 && n.Sub.Contains("stage timer node", StringComparison.Ordinal));

            // Round 89: the Stagewatch's show stalls. No seq moves; the relay carries the stall and the node's stage shows no time,
            // still following; its own context says so. The stall alone moves the node's stage revision, so a speaker's page
            // waiting on its long-poll stops counting at once. The next frame brings the time back on the node too.
            var stageRev = t.Stage.Rev;
            sw.Stuck = true;
            PumpUntil(() => t.FollowedStagewatch.Stuck && t.Stage.Time().Phase == Patterns.Core.Services.StageTimerPhase.Idle, 20000);
            Assert.True(t.Stage.Rev > stageRev);
            Assert.True(t.FollowedStagewatch.Following);
            Assert.Equal(4, t.FollowedStagewatch.Frame!.Seq);
            Assert.StartsWith("Stage timer shows no time — following Stagewatch ", t.Stage.StatusLine);
            Assert.EndsWith("says its show is stuck", t.Stage.NoTimeLabel);
            using (var follows = JsonDocument.Parse(t.Stage.StatusJson()))
                Assert.True(follows.RootElement.GetProperty("follows").GetProperty("stuck").GetBoolean());
            var stalled = ShowContext.Parse(Pump(t.NewRouter().ContextJsonAsync()))!;
            Assert.Contains(stalled.Items, i => i.Id == "stagewatch" && i.Light == "amber");
            Assert.Contains(stalled.Items, i => i.Id == "stage" && i.Light == "red" && i.State.StartsWith("no time shown", StringComparison.Ordinal));
            stageRev = t.Stage.Rev;
            sw.Stuck = false;
            PumpUntil(() => !t.FollowedStagewatch.Stuck && t.Stage.Time().Phase == Patterns.Core.Services.StageTimerPhase.Running, 20000);
            Assert.True(t.Stage.Rev > stageRev);
            Assert.Equal("No timer running", t.Stage.NoTimeLabel);

            // The desk stops following: the node's stage goes back to its own clock once the relay says so.
            d.State.Control.Stagewatch.Enabled = false;
            d.Kernel.Stagewatch.Reconcile();
            PumpUntil(() => !t.FollowedStagewatch.Following, 20000);
        }
        finally
        {
            node?.Dispose();
            desk.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static T Pump<T>(Task<T> task, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("pumped task timed out");
        }
        return task.GetAwaiter().GetResult();
    }
}
