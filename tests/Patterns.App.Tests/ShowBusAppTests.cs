using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 87 (Show Bus 1.1): the wire's receipts, the clock exchange, pairing by proof with the token never on the line,
/// CONTEXT and /api/context, the /api/cmd id — real sockets against the live desk, from loopback with the exemptions
/// off — and the Stagewatch link end to end against a stand-in Stagewatch: the key in the header, the stage timer
/// following its frames, the Eye drawing its things, pause refused while it follows.
/// </summary>
public class ShowBusAppTests
{
    private const string Token = "K7QM-3XWD-P9RA";

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Pump(Task task, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("pumped task timed out");
        }
        task.GetAwaiter().GetResult();
    }

    private static T Pump<T>(Task<T> task, int timeoutMs = 8000)
    {
        Pump((Task)task, timeoutMs);
        return task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> done, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the condition never came");
        }
    }

    /// <summary>
    /// A wire connection with its first line read — ADR-016: the nonce, and no STATE, when the connection has the token to
    /// prove; the greeting STATE when it is paired from the start. Say skips STATE pushes and keeps any later NONCE.
    /// </summary>
    private sealed class Wire : IDisposable
    {
        private readonly TcpClient _client = new();
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;

        public string Nonce { get; private set; } = "";

        public Wire(int port, bool expectNonce)
        {
            Pump(_client.ConnectAsync(IPAddress.Loopback, port));
            _stream = _client.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8);
            var first = Pump(_reader.ReadLineAsync());
            if (expectNonce)
            {
                Assert.StartsWith("NONCE ", first);
                Nonce = first!["NONCE ".Length..];
            }
            else
            {
                Assert.StartsWith("STATE {", first);
            }
        }

        public string Say(string line)
        {
            _stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
            while (true)
            {
                var reply = Pump(_reader.ReadLineAsync());
                Assert.NotNull(reply);
                if (reply!.StartsWith("STATE ", StringComparison.Ordinal)) continue;
                if (reply.StartsWith("NONCE ", StringComparison.Ordinal))
                {
                    Nonce = reply["NONCE ".Length..];
                    continue;
                }
                return reply;
            }
        }

        public void Dispose() => _client.Dispose();
    }

    private static (string Status, string Body) Http(int port, string method, string path, string body = "", params (string Name, string Value)[] headers)
    {
        using var client = new TcpClient();
        Pump(client.ConnectAsync(IPAddress.Loopback, port));
        using var stream = client.GetStream();
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var ownHost = headers.Any(h => h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase));      // a Host of the caller's replaces "test"
        var head = new StringBuilder($"{method} {path} HTTP/1.1\r\n{(ownHost ? "" : "Host: test\r\n")}Content-Length: {bodyBytes.Length}\r\n");
        foreach (var (name, value) in headers) head.Append($"{name}: {value}\r\n");
        head.Append("\r\n");
        stream.Write(Encoding.UTF8.GetBytes(head.ToString()));
        stream.Write(bodyBytes);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var n = Pump(stream.ReadAsync(chunk, 0, chunk.Length));
            if (n <= 0) break;
            buffer.Write(chunk, 0, n);
        }
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var cut = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (text[(text.IndexOf(' ') + 1)..text.IndexOf("\r\n", StringComparison.Ordinal)], cut < 0 ? "" : text[(cut + 4)..]);
    }

    [AvaloniaFact]
    public void ARemoteProvesTheTokenAgainstItsNonceAndEveryAnswerNamesItsLine()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var (trusted, plain) = (ControlService.TrustLoopback, ControlService.PlainAuthOnLoopback);
        ControlService.TrustLoopback = false;
        ControlService.PlainAuthOnLoopback = false;                              // this connection plays another machine
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();

            using var wire = new Wire(vm.State.Control.TcpPort, expectNonce: true);
            Assert.Matches("^[0-9a-f]{32}$", wire.Nonce);

            // The clock exchange: t1 echoed, the desk's read and answer stamps beside it.
            var before = ClockPing.Ms(DateTime.UtcNow);
            var pong = wire.Say("@a1 PING 1760101234567");
            Assert.StartsWith("OK @a1 PONG 1760101234567 ", pong);
            var stamps = pong.Split(' ');
            var (t2, t3) = (long.Parse(stamps[4], System.Globalization.CultureInfo.InvariantCulture), long.Parse(stamps[5], System.Globalization.CultureInfo.InvariantCulture));
            Assert.InRange(t2, before - 60_000, before + 60_000);
            Assert.True(t3 >= t2);
            Assert.Equal("OK PONG", wire.Say("PING"));                            // the bare ping as before

            Assert.StartsWith("ERR @b2 not paired", wire.Say("@b2 BLACKOUT ON"));
            Assert.Contains("plain AUTH is for this machine only", wire.Say("AUTH " + Token));
            Assert.Equal("OK", wire.Say("HELLO Stagewatch"));
            var first = wire.Nonce;
            Assert.StartsWith("ERR wrong token", wire.Say("AUTH-PROOF " + WireProof.Proof(Token, first, "FOH deck")));   // the wrong name
            Assert.Equal("OK PONG", wire.Say("PING"));                             // the fresh NONCE line follows the refusal; this reads past it
            Assert.NotEqual(first, wire.Nonce);                                    // a nonce is used once: a fresh one came
            Assert.False(services.State.Blackout);
            Assert.Equal("OK paired", wire.Say("AUTH-PROOF " + WireProof.Proof(Token, wire.Nonce, "Stagewatch")));
            Assert.Equal("OK @c3", wire.Say("@c3 BLACKOUT ON"));
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);                                   // the proof is this connection's; its address earns nothing (OscAppTests)

            Assert.Matches("^OK [0-9a-f]{32}$", wire.Say("NONCE"));
            Assert.StartsWith("ERR a request id", wire.Say("@bad! STATUS"));

            // CONTEXT: the desk in the shared shape — no token anywhere in it.
            var context = wire.Say("CONTEXT");
            Assert.StartsWith("OK {", context);
            var parsed = ShowContext.Parse(context[3..]);
            Assert.NotNull(parsed);
            Assert.Equal("patterns", parsed!.Node.Kind);
            Assert.Equal(services.Kernel.ShowBusId, parsed.Node.Id);
            Assert.Contains(parsed.Items, i => i.Id == "desk");
            Assert.DoesNotContain(PairingToken.Normal(Token), PairingToken.Normal(context));

            // The web: /api/context wants the token like /api/state; /api/cmd gives the id back.
            var port = vm.State.Control.HttpPort;
            Assert.Equal("403 Forbidden", Http(port, "GET", "/api/context").Status);
            var (status, body) = Http(port, "GET", "/api/context", "", ("X-Patterns-Token", Token));
            Assert.Equal("200 OK", status);
            Assert.NotNull(ShowContext.Parse(body));
            var (cmdStatus, cmdBody) = Http(port, "POST", "/api/cmd", "@x9 PING", ("X-Patterns-Token", Token), ("X-Patterns-Client", "test"));
            Assert.Equal("200 OK", cmdStatus);
            using (var doc = JsonDocument.Parse(cmdBody))
            {
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("x9", doc.RootElement.GetProperty("id").GetString());
            }
            Assert.Equal("400 Bad Request", Http(port, "POST", "/api/cmd", "@!! PING", ("X-Patterns-Token", Token), ("X-Patterns-Client", "test")).Status);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            ControlService.PlainAuthOnLoopback = plain;
            vm.State.Control.Token = "";
            services.State.Blackout = false;
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ATokenSeenInClearFromAnotherMachineIsMarkedInEveryReaderAndRefusesNothing()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var (trusted, plain) = (ControlService.TrustLoopback, ControlService.PlainAuthOnLoopback);
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;
            var router = new CommandRouter(services);
            int Rows() => services.Journal.Tail(1000).Count(r => r.Kind == "TokenSeenInClear");
            var rowsBefore = Rows();

            // This machine's own plain AUTH and plain header are no crossing of the network: nothing is marked. (Its browser names
            // the desk by one of its own names — round 89: under another name it could be a forwarder's traffic, which is marked.)
            using (var own = new Wire(vm.State.Control.TcpPort, expectNonce: false))
                Assert.Equal("OK paired", own.Say("AUTH " + Token));
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", $"localhost:{port}"), ("X-Patterns-Token", Token)).Status);
            Assert.Empty(services.Kernel.TokenSeenInClear.For(Token));

            // Another machine (loopback plays it). A wrong token is no secret of this desk's: refused, nothing marked.
            ControlService.TrustLoopback = false;
            ControlService.PlainAuthOnLoopback = false;
            using var wire = new Wire(vm.State.Control.TcpPort, expectNonce: true);
            Assert.Contains("plain AUTH is for this machine only", wire.Say("AUTH WRNG-TKN0-0000"));
            Assert.Empty(services.Kernel.TokenSeenInClear.For(Token));

            // The token itself, plain on the wire: refused as before — and marked.
            Assert.Contains("plain AUTH is for this machine only", wire.Say("AUTH " + Token));
            var seen = Assert.Single(services.Kernel.TokenSeenInClear.For(Token));
            Assert.Equal(("127.0.0.1", "wire", 1L), (seen.Address, seen.Via, seen.Count));

            // In a header on the web remote's plain listener: answered as before (refusing nothing), the way it came added.
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("X-Patterns-Token", Token)).Status);
            seen = Assert.Single(services.Kernel.TokenSeenInClear.For(Token));
            Assert.Equal(("127.0.0.1", "wire,web", 2L), (seen.Address, seen.Via, seen.Count));

            // One journal row for the address, however often it is seen; it names the address and the door, never the token.
            Assert.Equal(rowsBefore + 1, Rows());
            var row = services.Journal.Tail(1000).Last(r => r.Kind == "TokenSeenInClear");
            Assert.Equal(("Remote", "127.0.0.1", "Alert"), (row.Origin, row.Target, row.Outcome));
            Assert.Contains("rotate it: NEW TOKEN", row.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(PairingToken.Normal(Token), PairingToken.Normal(row.Message));

            // Every reader: STATE's control row at the end of STATE, the Super Check's REMOTE row, the Nodes and Remote
            // pages' words and the assistant's brief (which reads every amber row).
            var json = router.StateJson();
            var state = JsonDocument.Parse(json).RootElement;
            Assert.Equal("control", state.EnumerateObject().Last().Name);
            var control = state.GetProperty("control");
            var first = Assert.Single(control.GetProperty("clearSeen").EnumerateArray());
            Assert.Equal("127.0.0.1", first.GetProperty("address").GetString());
            Assert.Equal("wire,web", first.GetProperty("via").GetString());
            Assert.Equal("seen in clear from 127.0.0.1 — rotate it: NEW TOKEN", control.GetProperty("words").GetString());
            Assert.DoesNotContain(PairingToken.Normal(Token), PairingToken.Normal(json));
            var check = Assert.Single(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Section == "REMOTE" && r.Item == "Pairing token");
            Assert.Equal(CheckLight.Amber, check.Light);
            Assert.Equal("seen in clear from 127.0.0.1 — rotate it: NEW TOKEN", check.Value);
            Assert.Contains("over plain http and as plain AUTH on the wire", check.Note, StringComparison.Ordinal);
            Assert.Equal("Pairing token seen in clear from 127.0.0.1 — rotate it: NEW TOKEN (Remote page, TRUST)", vm.TokenClearWords);
            Assert.Contains(services.DeskHealthWords().Attention, a => a.Contains("seen in clear from 127.0.0.1", StringComparison.Ordinal));

            // NEW TOKEN: the mark was the old token's — none stands, in any reader.
            vm.NewPairingTokenCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(services.Kernel.TokenSeenInClear.For(vm.State.Control.Token));
            Assert.Equal("", vm.TokenClearWords);
            Assert.DoesNotContain(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Item == "Pairing token");
            Assert.Empty(JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("control").GetProperty("clearSeen").EnumerateArray());
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            ControlService.PlainAuthOnLoopback = plain;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void UnderRequireHttpsATokenSentInClearIsRefusedAsBeforeAndStillMarked()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var trusted = ControlService.TrustLoopback;
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            vm.State.Control.RequireHttps = true;
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;
            int Rows() => services.Journal.Tail(1000).Count(r => r.Kind == "TokenSeenInClear");
            var rowsBefore = Rows();

            // Another machine (loopback plays it) with no token: refused for plain, nothing marked.
            ControlService.TrustLoopback = false;
            var bare = Http(port, "GET", "/api/state");
            Assert.Equal("403 Forbidden", bare.Status);
            Assert.Empty(services.Kernel.TokenSeenInClear.For(Token));

            // The pages' cookie over plain http: refused as before — the token has crossed the network all the same, so it is marked.
            var refused = Http(port, "GET", "/api/state", "", ("Cookie", HttpHead.TokenCookie + "=" + Token));
            Assert.Equal("403 Forbidden", refused.Status);
            Assert.Contains("requires HTTPS", refused.Body, StringComparison.Ordinal);
            var seen = Assert.Single(services.Kernel.TokenSeenInClear.For(Token));
            Assert.Equal(("127.0.0.1", "web", 1L), (seen.Address, seen.Via, seen.Count));

            // The header again from the same address: counted, no second journal row.
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/cmd", "BLACKOUT", ("X-Patterns-Token", Token)).Status);
            Assert.Equal(2L, Assert.Single(services.Kernel.TokenSeenInClear.For(Token)).Count);
            Assert.Equal(rowsBefore + 1, Rows());
            var row = services.Journal.Tail(1000).Last(r => r.Kind == "TokenSeenInClear");
            Assert.Equal(("Remote", "127.0.0.1", "Alert"), (row.Origin, row.Target, row.Outcome));
            Assert.DoesNotContain(PairingToken.Normal(Token), PairingToken.Normal(row.Message));
            var control = JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("control");
            Assert.Equal("web", Assert.Single(control.GetProperty("clearSeen").EnumerateArray()).GetProperty("via").GetString());
            Assert.Equal(CheckLight.Amber, Assert.Single(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Section == "REMOTE" && r.Item == "Pairing token").Light);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.RequireHttps = false;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AnOpenDeskSaysNoNonceAndThisMachinesPlainAuthStillPairs()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            using (var open = new Wire(vm.State.Control.TcpPort, expectNonce: false))
            {
                Assert.Equal("OK PONG", open.Say("PING"));                       // no NONCE line came between
                Assert.Equal("", open.Nonce);
            }
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            using var local = new Wire(vm.State.Control.TcpPort, expectNonce: true);
            Assert.Equal("OK paired", local.Say("AUTH " + Token));               // from this machine, plain AUTH as before
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ADeskIsPairedFromItsFirstRunAndAnExistingSettingsFileKeepsItsChoice()
    {
        var fresh = TestApp.Boot();
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", fresh.Vm.State.Control.Token);
        var kept = TestApp.Boot(prepare: dir => File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), "{}"));
        Assert.Equal("", kept.Vm.State.Control.Token);
    }

    /// <summary>
    /// A stand-in Stagewatch: /ws answers with state frames and pongs, /api/context with its context; it records the
    /// Authorization header it was dialled with. While <see cref="Stuck"/> it does what Stagewatch does when its show thread
    /// stops turning over: no frame, the stuck beat instead — the same bytes (Stagewatch's WebServer.cs StuckBeat).
    /// Round 91: it speaks Stagewatch 2.18's /ws — every dial upgraded, a node link's verdict before any frame
    /// (<see cref="StagewatchBytes"/>, or <see cref="First"/> when a test sets it; none while <see cref="Legacy"/>), a word
    /// said again on an open socket (<see cref="Say"/>), a viewing code's refusal and the close after it — and answers the
    /// node routes, each request recorded with the key it carried.
    /// </summary>
    private sealed class FakeStagewatch : IDisposable
    {
        public const string StuckBeat = "{\"type\":\"beat\",\"live\":false}";

        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _open = new();
        private volatile string _authorization = "";
        private volatile string _url = "";
        private int _pings;
        private int _contextReads;
        private int _stuckBeats;
        private int _dials;

        public int Port { get; }
        public string Authorization => _authorization;
        public string Url => _url;
        public int Pings => Volatile.Read(ref _pings);
        public int ContextReads => Volatile.Read(ref _contextReads);
        public int StuckBeats => Volatile.Read(ref _stuckBeats);

        /// <summary>How many times /ws was dialled.</summary>
        public int Dials => Volatile.Read(ref _dials);

        /// <summary>Every request in arrival order: a /ws dial as "ws", else "METHOD path", with the Authorization it carried ("" for none).</summary>
        public readonly ConcurrentQueue<(string What, string Authorization)> Requests = new();

        /// <summary>The requests that carried the key — every one but a dial.</summary>
        public int KeyedCalls => Requests.Count(r => r.What != "ws" && r.Authorization.Length > 0);

        /// <summary>Its show thread has stopped: every answer to a ping is the pong and the stuck beat, never a frame.</summary>
        public volatile bool Stuck;

        /// <summary>/api/context answers (else 404, as an older Stagewatch does), and what its Lighting item says.</summary>
        public volatile bool ServeContext = true;

        public volatile string LightingState = "standing by: LX 12";

        /// <summary>A Stagewatch before 2.18 (2.16's bytes): no verdict — the first message is a frame — and a context taken says nothing of its proof.</summary>
        public volatile bool Legacy;

        /// <summary>The first message every dial hears in place of the verdict its key earns; null: as a Stagewatch 2.18 would.</summary>
        public volatile string? First;

        /// <summary>After <see cref="First"/>, the socket is closed (the viewing code's refusal).</summary>
        public volatile bool CloseAfterFirst;

        /// <summary>How long a dial waits after its upgrade before it hears anything (a busy show thread, a slow path).</summary>
        public volatile int FirstDelayMs;

        /// <summary>What POST /api/peers/context answers: the status line, its JSON body and a Retry-After (0: none).</summary>
        public volatile string ContextStatus = "";

        public volatile string ContextBody = "";

        public volatile int ContextRetryAfter;

        public FakeStagewatch(string frame)
        {
            Port = FreePort();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _ = Task.Run(() => Serve(frame), _cts.Token);
        }

        /// <summary>A word on every open socket, as Stagewatch sends a fresh verdict when a key, its binding or the PIN changes.</summary>
        public void Say(string json)
        {
            foreach (var (ws, gate) in _open)
            {
                gate.Wait(_cts.Token);
                try
                {
                    ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _cts.Token).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
                {
                    // a socket that closed under it: nothing to say to
                }
                finally
                {
                    gate.Release();
                }
            }
        }

        private async Task Serve(string frame)
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    var auth = ctx.Request.Headers["Authorization"] ?? "";
                    if (ctx.Request.Url!.AbsolutePath == "/ws" && ctx.Request.IsWebSocketRequest)
                    {
                        Interlocked.Increment(ref _dials);
                        Requests.Enqueue(("ws", auth));
                        _authorization = auth;
                        _url = ctx.Request.Url.ToString();
                        var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                        var gate = new SemaphoreSlim(1, 1);
                        if (FirstDelayMs > 0) await Task.Delay(FirstDelayMs, _cts.Token);
                        var first = First ?? (Legacy ? null : StagewatchBytes.FirstFor(ctx.Request.Url.Query, auth));
                        if (first is not null)
                        {
                            Requests.Enqueue(("first", ""));                                       // where the socket's first word falls among the requests
                            await ws.SendAsync(Encoding.UTF8.GetBytes(first), WebSocketMessageType.Text, true, _cts.Token);
                        }
                        if (CloseAfterFirst)
                        {
                            await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "viewing code", _cts.Token);
                            return;
                        }
                        _open[ws] = gate;
                        try
                        {
                            await SendShow(ws, gate, frame);
                            var buffer = new byte[4096];
                            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                            {
                                var r = await ws.ReceiveAsync(buffer, _cts.Token);
                                if (r.MessageType == WebSocketMessageType.Close) break;
                                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count));
                                if (doc.RootElement.GetProperty("cmd").GetString() == "ping")
                                {
                                    Interlocked.Increment(ref _pings);
                                    var t = doc.RootElement.GetProperty("t").GetInt64();
                                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                    await Send(ws, gate, $"{{\"type\":\"pong\",\"t\":{t},\"t2\":{now},\"t3\":{now}}}");
                                    await SendShow(ws, gate, frame);
                                }
                            }
                        }
                        finally
                        {
                            _open.TryRemove(ws, out _);
                        }
                        return;
                    }
                    var path = ctx.Request.Url.AbsolutePath;
                    Requests.Enqueue((ctx.Request.HttpMethod + " " + ctx.Request.Url.PathAndQuery, auth));
                    if (path == "/api/context") Interlocked.Increment(ref _contextReads);
                    var (status, body, retryAfter) = path switch
                    {
                        "/api/context" when ServeContext => (200,
                            "{\"v\":1,\"node\":{\"t\":\"main\",\"id\":\"a1b2c3d4\",\"name\":\"Hall A\"},\"at\":1,\"light\":\"amber\",\"headline\":\"Keynote — 12:30 left · Lighting standing by\","
                            + "\"items\":[{\"id\":\"timer\",\"kind\":\"timer\",\"label\":\"Keynote — Ana\",\"light\":\"green\",\"state\":\"running, 12:30 left\"},"
                            + "{\"id\":\"cue:lighting\",\"kind\":\"department\",\"label\":\"Lighting\",\"light\":\"amber\",\"state\":\"" + LightingState + "\"}],\"links\":[],\"recent\":[],\"commands\":[],\"peers\":[]}", 0),
                        "/api/peers/context" when ContextStatus.Length > 0 => (int.Parse(ContextStatus, System.Globalization.CultureInfo.InvariantCulture), ContextBody, ContextRetryAfter),
                        "/api/peers/context" => (200, Legacy ? "{\"ok\":true}" : "{\"ok\":true,\"verified\":\"key\"}", 0),
                        _ => (404, "not here", 0),
                    };
                    ctx.Response.StatusCode = status;
                    if (retryAfter > 0) ctx.Response.Headers["Retry-After"] = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token);
                    ctx.Response.Close();
                }, _cts.Token);
            }
        }

        private async Task Send(WebSocket ws, SemaphoreSlim gate, string text)
        {
            await gate.WaitAsync(_cts.Token);
            try
            {
                await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, _cts.Token);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>What the show says on the socket: the frame while it turns over, the stuck beat while it does not.</summary>
        private async Task SendShow(WebSocket ws, SemaphoreSlim gate, string frame)
        {
            if (Stuck) Interlocked.Increment(ref _stuckBeats);
            await Send(ws, gate, Stuck ? StuckBeat : frame);
        }

        private int _disposed;

        /// <summary>Gone: its listener closed and its links dropped. Once is enough — a test that ends it early still owns it.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            _listener.Close();
            _cts.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheStageTimerFollowsALinkedStagewatchAndTheEyeDrawsItsThings()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var stagewatch = new FakeStagewatch("{\"type\":\"state\",\"v\":1,\"seq\":3,\"mode\":\"countdown\",\"state\":\"running\",\"seconds\":750,\"total\":1800,\"text\":\"12:30\",\"phase\":\"warning\",\"title\":\"Keynote — Ana\",\"next\":\"Panel\"}");
        try
        {
            var link = vm.State.Control.Stagewatch;
            link.Host = "localhost:" + stagewatch.Port;
            link.ApiKey = "swk_testkey";
            link.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();
            PumpUntil(() => services.Stage.Following.Length > 0);

            Assert.Equal("Bearer swk_testkey", stagewatch.Authorization);           // the key in the header…
            Assert.DoesNotContain("swk_", stagewatch.Url);                          // …never in the URL
            Assert.Equal("localhost", services.Stage.Following);
            var time = services.Stage.Time();
            Assert.Equal(Patterns.Core.Services.StageTimerPhase.Running, time.Phase);
            Assert.InRange(time.RemainingSeconds, 740, 750.5);
            Assert.Equal("amber", time.Colour);
            Assert.False(services.Stage.Pause().Ok);                                 // the timer is the Stagewatch's
            Assert.Contains("follows Stagewatch", services.Stage.Pause().Message);
            using (var doc = JsonDocument.Parse(services.Stage.StatusJson()))
            {
                Assert.Equal("live", doc.RootElement.GetProperty("follows").GetProperty("state").GetString());
                Assert.Equal("Keynote — Ana", doc.RootElement.GetProperty("timer").GetProperty("label").GetString());
            }

            // Its context lands: the Eye draws the Stagewatch and its own things; the brief and STATE read them.
            PumpUntil(() => services.Kernel.Stagewatch.Sight.Context is not null);
            services.Eye.Refresh();
            Assert.Contains(services.Eye.Graph.Nodes, n => n.Kind == Patterns.Core.Services.EyeKind.ShowApp && n.Sub.StartsWith("Keynote", StringComparison.Ordinal));
            Assert.Contains(services.Eye.Graph.Nodes, n => n.Kind == Patterns.Core.Services.EyeKind.ShowAppItem && n.Label == "Lighting");
            Assert.Contains(ShowBusView.BriefLines(services.ShowApps(DateTime.UtcNow)), l => l.Contains("Lighting (department) — amber", StringComparison.Ordinal));
            using (var state = JsonDocument.Parse(services.Router.StateJson()))
            {
                var sw = state.RootElement.GetProperty("bus").GetProperty("stagewatch");
                Assert.Equal("live", sw.GetProperty("state").GetString());
                Assert.True(sw.GetProperty("following").GetBoolean());
                Assert.Equal("amber", sw.GetProperty("light").GetString());
            }
            PumpUntil(() => stagewatch.Pings > 0);                                      // the beat is the clock exchange
            PumpUntil(() => services.Kernel.Stagewatch.ClockOffset is not null);

            // Off: this desk's own stage timer runs again.
            link.Enabled = false;
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();
            Assert.Equal("", services.Stage.Following);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private const string RunningFrame = "{\"type\":\"state\",\"v\":1,\"seq\":3,\"mode\":\"countdown\",\"state\":\"running\",\"seconds\":750,\"total\":1800,\"text\":\"12:30\",\"phase\":\"warning\",\"title\":\"Keynote — Ana\",\"next\":\"Panel\"}";

    /// <summary>The desk's log lines that carry these words (the boot's own patterns.log).</summary>
    private static int LogLines(string dir, string words)
        => File.ReadAllLines(Path.Combine(dir, "patterns.log")).Count(l => l.Contains(words, StringComparison.Ordinal));

    private static JsonElement StagewatchRow(AppServices services)
        => JsonDocument.Parse(services.Router.StateJson()).RootElement.GetProperty("bus").GetProperty("stagewatch").Clone();

    private static void Link(MainViewModel vm, AppServices services, FakeStagewatch stagewatch)
    {
        var link = vm.State.Control.Stagewatch;
        link.Host = "localhost:" + stagewatch.Port;
        link.ApiKey = "swk_testkey";
        link.Enabled = true;
        Dispatcher.UIThread.RunJobs();
        services.Kernel.Stagewatch.Reconcile();
    }

    [AvaloniaFact]
    public void AStuckStagewatchShowsNoTimeEverywhereAndItsNextFrameBringsTheTimeBack()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var stagewatch = new FakeStagewatch(RunningFrame);
        const string stuckLine = "Stagewatch link: localhost says its show is stuck — the stage shows no time.";
        const string runsLine = "Stagewatch link: localhost's show runs again.";
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;                                                         // the stage page reads as a speaker's screen would
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs();
            using var page = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{vm.State.Control.HttpPort}/") };
            Link(vm, services, stagewatch);
            PumpUntil(() => services.Stage.Following.Length > 0 && services.Kernel.Stagewatch.Sight.Context is not null);
            Assert.Equal(StageTimerPhase.Running, services.Stage.Time().Phase);
            Assert.False(StagewatchRow(services).GetProperty("stuck").GetBoolean());

            // Its context goes unanswered first, so below nothing but the stall itself can wake a waiting stage page.
            stagewatch.ServeContext = false;
            var unanswered = stagewatch.ContextReads;
            PumpUntil(() => stagewatch.ContextReads > unanswered, 20000);

            // Its show thread stops: Stagewatch sends the stuck beat — its bytes as it sends them today — and no frame. The
            // speaker's page, waiting on its long-poll, hears it within a beat, not at the poll's 25 s: no seq moved and no
            // context landed, so the stall alone moved the page's revision.
            var since = services.Stage.Rev;
            stagewatch.Stuck = true;
            var started = Environment.TickCount64;
            while (true)
            {
                using var polled = JsonDocument.Parse(Pump(page.GetStringAsync($"api/stage?since={since}"), 15000));
                if (polled.RootElement.GetProperty("follows").GetProperty("stuck").GetBoolean())
                {
                    Assert.Equal("idle", polled.RootElement.GetProperty("timer").GetProperty("phase").GetString());
                    break;
                }
                since = polled.RootElement.GetProperty("rev").GetInt64();                                 // a frame already on its way when it stopped
            }
            Assert.True(Environment.TickCount64 - started < 10000, $"the stage page heard the stall after {Environment.TickCount64 - started} ms");

            var sight = services.Kernel.Stagewatch.Sight;
            Assert.Equal(ShowBusState.Live, sight.State);                                           // the pongs still come: the link is live
            Assert.True(sight.Stuck);
            Assert.False(sight.Frame!.Live);
            Assert.Equal(StageTimerPhase.Idle, services.Stage.Time().Phase);
            Assert.Equal("localhost", services.Stage.Following);                                     // still its timer: the verbs stay its
            Assert.Contains("follows Stagewatch", services.Stage.Pause().Message);
            Assert.Contains("says its show is stuck — the stage shows no time", services.Kernel.Stagewatch.Status);
            Assert.Equal("Stage timer shows no time — following Stagewatch localhost, which says its show is stuck.", services.Stage.StatusLine);
            Assert.Equal("No time shown — Stagewatch localhost says its show is stuck", services.Stage.NoTimeLabel);
            using (var doc = JsonDocument.Parse(services.Stage.StatusJson()))
            {
                Assert.True(doc.RootElement.GetProperty("follows").GetProperty("stuck").GetBoolean());
                Assert.Contains("the time is not shown", doc.RootElement.GetProperty("follows").GetProperty("words").GetString());
                Assert.Equal("idle", doc.RootElement.GetProperty("timer").GetProperty("phase").GetString());
            }

            // STATE: the link live, no time in the row, the light no better than amber, the mark at the row's end.
            var row = StagewatchRow(services);
            Assert.Equal("live", row.GetProperty("state").GetString());
            Assert.True(row.GetProperty("following").GetBoolean());
            Assert.Equal("", row.GetProperty("text").GetString());
            Assert.Equal("amber", row.GetProperty("light").GetString());
            Assert.Equal(ShowBusView.StuckWords, row.GetProperty("headline").GetString());
            Assert.True(row.GetProperty("stuck").GetBoolean());
            Assert.Equal(new[] { "stuck", "carrier", "key", "held" }, row.EnumerateObject().Select(p => p.Name).TakeLast(4));   // stuck appended, the carrier after it, then the key (round 91), then the hold (Show Bus 1.3 §2)
            Assert.Equal("plain", row.GetProperty("carrier").GetString());                              // this machine's Stagewatch, the key over plain ws

            // The Eye: the card amber, the link live and amber, its timer red with no time; the Nodes card; the brief.
            services.Eye.Refresh();
            var card = services.Eye.Graph.Nodes.Single(n => n.Kind == EyeKind.ShowApp && n.Label.StartsWith("Stagewatch", StringComparison.Ordinal));
            Assert.Equal((CheckLight.Amber, ShowBusView.StuckWords), (card.Light, card.Sub));
            Assert.Equal(CheckLight.Amber, services.Eye.Graph.Edges.Single(e => e.From == card.Id && e.To == EyeGraph.DeskId).Light);
            var timer = services.Eye.Graph.Nodes.Single(n => n.Kind == EyeKind.ShowAppItem && n.Label == "Keynote — Ana");
            Assert.Equal((CheckLight.Red, ShowBusView.NoTimeWords), (timer.Light, timer.Sub));
            Assert.Equal(CheckLight.Amber, services.Eye.Graph.Nodes.Single(n => n.Kind == EyeKind.ShowAppItem && n.Label == "Lighting").Light);
            services.Nodes.Poll();
            var nodesCard = Assert.Single(services.Nodes.ShowApps, c => c.Linked);
            Assert.Equal("#FFB020", nodesCard.Hue);
            Assert.Contains(ShowBusView.StuckWords, nodesCard.Line);
            var brief = ShowBusView.BriefLines(services.ShowApps(DateTime.UtcNow));
            Assert.Contains(brief, l => l.Contains("— linked — amber: says its show is stuck — no time shown", StringComparison.Ordinal));
            Assert.Contains(brief, l => l.Contains("Keynote — Ana (timer) — red: no time shown", StringComparison.Ordinal));

            // Its context answers again beside the stall: what it says now is taken, and its timer still shows no time.
            stagewatch.LightingState = "GO: LX 13";
            stagewatch.ServeContext = true;
            PumpUntil(() =>
            {
                Assert.Equal(StageTimerPhase.Idle, services.Stage.Time().Phase);
                return services.Kernel.Stagewatch.Sight.Context?.Items.Any(i => i.State == "GO: LX 13") == true && stagewatch.StuckBeats >= 3;
            }, 20000);
            services.Eye.Refresh();
            Assert.Equal("GO: LX 13", services.Eye.Graph.Nodes.Single(n => n.Kind == EyeKind.ShowAppItem && n.Label == "Lighting").Sub);
            Assert.Equal((CheckLight.Red, ShowBusView.NoTimeWords), services.Eye.Graph.Nodes.Where(n => n.Kind == EyeKind.ShowAppItem && n.Label == "Keynote — Ana").Select(n => (n.Light, n.Sub)).Single());
            Assert.True(services.Kernel.Stagewatch.Sight.Stuck);
            Assert.Equal(1, LogLines(b.Dir, stuckLine));                                              // one line, however many beats

            // Its next frame: the time back, the mark gone, one line.
            stagewatch.Stuck = false;
            PumpUntil(() => services.Stage.Time().Phase == StageTimerPhase.Running);
            Assert.False(services.Kernel.Stagewatch.Sight.Stuck);
            Assert.True(services.Kernel.Stagewatch.Sight.Frame!.Live);
            Assert.Equal("No timer running", services.Stage.NoTimeLabel);
            Assert.EndsWith("— following Stagewatch localhost.", services.Stage.StatusLine);
            row = StagewatchRow(services);
            Assert.False(row.GetProperty("stuck").GetBoolean());
            Assert.Equal("12:30", row.GetProperty("text").GetString());
            services.Eye.Refresh();
            Assert.Equal(CheckLight.Green, services.Eye.Graph.Nodes.Single(n => n.Kind == EyeKind.ShowAppItem && n.Label == "Keynote — Ana").Light);
            Assert.Equal(1, LogLines(b.Dir, runsLine));
            Assert.Equal(1, LogLines(b.Dir, stuckLine));
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStagewatchStuckBeforeItsFirstFrameLeavesTheDesksTimer()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var stagewatch = new FakeStagewatch(RunningFrame) { Stuck = true };
        try
        {
            Link(vm, services, stagewatch);
            PumpUntil(() => services.Kernel.Stagewatch.Sight.Stuck);
            Assert.Equal("", services.Stage.Following);                                               // no frame held: nothing to follow
            Assert.DoesNotContain("follows Stagewatch", services.Stage.Pause().Message);              // the desk's own timer and its verbs
            Assert.StartsWith("OK", TestApp.Pump(services.Router.ExecuteAsync(ControlProtocol.Parse("COUNTDOWN START 5"))));
            Assert.Equal(StageTimerPhase.Running, services.Stage.Time().Phase);
            Assert.EndsWith("— Stagewatch localhost says its show is stuck — this desk's own stage timer runs until its first frame.", services.Stage.StatusLine);
            Assert.True(services.Stage.Pause().Ok);
            var row = StagewatchRow(services);
            Assert.Equal("live", row.GetProperty("state").GetString());
            Assert.False(row.GetProperty("following").GetBoolean());
            Assert.True(row.GetProperty("stuck").GetBoolean());
            Assert.Equal("amber", row.GetProperty("light").GetString());
            using (var doc = JsonDocument.Parse(services.Stage.StatusJson()))
            {
                var follows = doc.RootElement.GetProperty("follows");
                Assert.True(follows.GetProperty("stuck").GetBoolean());
                Assert.Equal("Stagewatch localhost says its show is stuck — this desk's own stage timer runs until its first frame", follows.GetProperty("words").GetString());
            }

            // Its first frame once it turns over: the stage follows it.
            stagewatch.Stuck = false;
            PumpUntil(() => services.Stage.Following.Length > 0);
            Assert.False(services.Kernel.Stagewatch.Sight.Stuck);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    /// <summary>A Stagewatch's hello on the show network as the desk's beacon hears it, its doors at this machine's loopback name.</summary>
    private static void Hello(AppServices services, string id, string name, int port)
        => services.Kernel.Beacon.ShowBus.Hear(Encoding.UTF8.GetBytes($"{{\"t\":\"main\",\"v\":1,\"id\":\"{id}\",\"name\":\"{name}\",\"ports\":{{\"http\":{port},\"ws\":{port}}},\"caps\":[\"context\"]}}"), "localhost", DateTime.UtcNow, "not-this-desk");

    [AvaloniaFact]
    public void AStallSaidByOneStagewatchIsNeverClaimedOfTheNextOneTheShowNetworkOffers()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var hallB = new FakeStagewatch(RunningFrame) { Stuck = true };
        using var annex = new FakeStagewatch(RunningFrame);
        const string movedOn = "Stagewatch link: connected to Annex — Hall B, whose show was stuck, is no longer followed.";
        try
        {
            // Found on the show network, no address typed: the one Stagewatch heard is dialled, and its show is stuck.
            Hello(services, "b0b0b0b0", "Hall B", hallB.Port);
            var link = vm.State.Control.Stagewatch;
            link.Host = "";
            link.ApiKey = "swk_testkey";
            link.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();
            PumpUntil(() =>
            {
                Hello(services, "b0b0b0b0", "Hall B", hallB.Port);
                return services.Kernel.Stagewatch.Sight is { Name: "Hall B", Stuck: true };
            }, 20000);
            Assert.Equal(1, LogLines(b.Dir, "Stagewatch link: Hall B says its show is stuck"));

            // Hall B goes away and the show network offers another Stagewatch: the redial reaches Annex, whose show was never
            // said stuck — no mark, no "runs again" said of it, one line saying the stuck one is no longer followed.
            hallB.Dispose();
            PumpUntil(() =>
            {
                Hello(services, "a0a0a0a0", "Annex", annex.Port);                                     // the one still heard
                return services.Kernel.Stagewatch.Sight is { Name: "Annex", Frame: not null } && services.Stage.Following == "Annex";
            }, 30000);
            Assert.False(services.Kernel.Stagewatch.Sight.Stuck);
            Assert.Equal(StageTimerPhase.Running, services.Stage.Time().Phase);
            Assert.Equal(1, LogLines(b.Dir, movedOn));
            Assert.Equal(0, LogLines(b.Dir, "show runs again"));
            Assert.False(StagewatchRow(services).GetProperty("stuck").GetBoolean());
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    // ---- round 91: the node link and its verdict ------------------------------------------------------------------

    /// <summary>Pumps the desk for <paramref name="ms"/> without waiting on anything: what the link does on its own meanwhile.</summary>
    private static void PumpFor(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    [AvaloniaFact]
    public void TheLinkDialsAsANodeAndSendsNothingKeyedBeforeItsVerdict()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        // The verdict comes late: a link that sent a keyed call at its connect would be caught before it. Its context echoes
        // the bearer: masked before any reader takes it.
        using var stagewatch = new FakeStagewatch(RunningFrame) { FirstDelayMs = 1500, LightingState = "standing by: LX 12 (Bearer swk_testkey)" };
        var link = services.Kernel.Stagewatch;
        try
        {
            var id = services.Kernel.ShowBusId;
            Link(vm, services, stagewatch);
            PumpUntil(() => services.Stage.Following.Length > 0 && link.Key.Word == "key", 15000);

            // The dial names this desk on the show network and its kind; cid and page for a Stagewatch older than its node links.
            Assert.Equal($"/ws?node={id}&t=patterns&cid={id}&page=/patterns", new Uri(stagewatch.Url).PathAndQuery);
            Assert.Equal("Bearer swk_testkey", stagewatch.Authorization);
            Assert.DoesNotContain("swk_", stagewatch.Url);

            // Nothing that carries the key went before the socket's first word; the side calls follow it.
            PumpUntil(() => stagewatch.KeyedCalls >= 2, 15000);
            var order = stagewatch.Requests.ToList();
            var said = order.FindIndex(r => r.What == "first");
            Assert.True(said >= 0);
            Assert.True(order.FindIndex(r => r.What != "ws" && r.What != "first" && r.Authorization.Length > 0) > said, string.Join(", ", order.Select(r => r.What)));

            // Taken: every reader says nothing of the key, STATE says key at the row's end; one log line.
            var row = StagewatchRow(services);
            Assert.Equal("key", row.GetProperty("key").GetString());
            Assert.Equal(new[] { "key", "held" }, row.EnumerateObject().Select(p => p.Name).TakeLast(2));   // the key appended, the hold (Show Bus 1.3 §2) after it
            Assert.Equal("", link.Key.Line);
            Assert.Equal(TimeSpan.Zero, link.KeyHeld);
            services.Nodes.Poll();
            var card = Assert.Single(services.Nodes.ShowApps, c => c.Linked);
            Assert.Equal("#35E0D0", card.Hue);
            Assert.DoesNotContain("KEY REFUSED", card.Line, StringComparison.Ordinal);
            Assert.DoesNotContain(StagewatchKey.NotAcceptedWords, card.Line, StringComparison.Ordinal);
            Assert.Equal(1, LogLines(b.Dir, "Stagewatch link: localhost takes this desk's key — verified key."));
            Assert.Contains($"show network id {id}", services.Nodes.Identity, StringComparison.Ordinal);   // the id a Stagewatch's key speaks for
            PumpUntil(() => link.Sight.Context is not null, 15000);
            services.Eye.Refresh();
            Assert.Contains(services.Eye.Graph.Nodes, n => n.Kind == EyeKind.ShowAppItem && n.Sub == "standing by: LX 12 (Bearer •••)");
            Assert.DoesNotContain("swk_testkey", services.ContextJson(), StringComparison.Ordinal);
            Assert.DoesNotContain("swk_testkey", services.Router.StateJson(), StringComparison.Ordinal);
            Assert.DoesNotContain("not kept", services.Nodes.Identity, StringComparison.Ordinal);
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
    public void ARefusedKeyIsHeldTheTimerFollowsTheWordsReachEveryReaderAndAFreshVerdictTakesItAgain()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        // Stagewatch 2.18 on an unknown or revoked key: one refusal, then the public frame on the same socket.
        using var stagewatch = new FakeStagewatch(RunningFrame) { First = StagewatchBytes.Refusal("unknown or revoked API key", 60) };
        var link = services.Kernel.Stagewatch;
        const string refused = "KEY REFUSED — unknown or revoked API key";
        try
        {
            var id = services.Kernel.ShowBusId;
            Link(vm, services, stagewatch);
            PumpUntil(() => services.Stage.Following.Length > 0 && link.Key.Refused, 15000);
            Assert.InRange(link.KeyHeld.TotalSeconds, 50, 60.5);                                     // the larger of 60 s and its retry

            // The hold, by count: over six seconds — three beats, a context read and a post due — nothing carries the key,
            // and the link is not dialled again; the timer follows on the public frame meanwhile.
            PumpFor(6000);
            Assert.Equal(0, stagewatch.KeyedCalls);
            Assert.Equal(1, stagewatch.Dials);
            Assert.Equal(StageTimerPhase.Running, services.Stage.Time().Phase);
            Assert.Equal("localhost", services.Stage.Following);

            // Every reader: STATE, the Nodes card and its line, the Eye, the context this desk posts, the assistant's facts.
            var row = StagewatchRow(services);
            Assert.Equal(("refused", "amber", "live"), (row.GetProperty("key").GetString(), row.GetProperty("light").GetString(), row.GetProperty("state").GetString()));
            Assert.Contains(refused, row.GetProperty("words").GetString(), StringComparison.Ordinal);
            services.Nodes.Poll();
            var card = Assert.Single(services.Nodes.ShowApps, c => c.Linked);
            Assert.Equal("#FFB020", card.Hue);
            Assert.EndsWith(refused, card.Line, StringComparison.Ordinal);
            Assert.Contains(refused, services.Nodes.ShowBusLine, StringComparison.Ordinal);
            services.Eye.Refresh();
            var node = services.Eye.Graph.Nodes.Single(n => n.Kind == EyeKind.ShowApp && n.Label.StartsWith("Stagewatch", StringComparison.Ordinal));
            Assert.Equal((CheckLight.Amber, refused), (node.Light, node.Sub));
            Assert.Equal(CheckLight.Amber, services.Eye.Graph.Edges.Single(e => e.From == node.Id && e.To == EyeGraph.DeskId).Light);
            var peers = ShowContext.Parse(services.ContextJson())!.Peers;
            Assert.Contains(peers, p => p.Kind == "main" && p.Light == "amber" && p.Headline == refused);
            Assert.Contains("  this desk's key: " + refused, ShowBusView.BriefLines(services.ShowApps(DateTime.UtcNow)));
            Assert.Equal(1, LogLines(b.Dir, "Stagewatch link: localhost — " + refused + "; this desk's keyed calls wait 60 s."));
            Assert.Equal(0, LogLines(b.Dir, "swk_testkey"));                                         // the words are Stagewatch's, never the key

            // The operator lets the key speak for this desk on the Stagewatch: a fresh verdict on the open socket takes it,
            // the hold lifts, and the keyed calls go again.
            stagewatch.Say(StagewatchBytes.Verdict("node", "patterns", id, "key"));
            PumpUntil(() => link.Key.Word == "key" && link.KeyHeld == TimeSpan.Zero, 10000);
            PumpUntil(() => stagewatch.KeyedCalls > 0, 15000);
            Assert.Equal(1, stagewatch.Dials);
            Assert.Equal("key", StagewatchRow(services).GetProperty("key").GetString());
            services.Nodes.Poll();
            Assert.Equal("#35E0D0", Assert.Single(services.Nodes.ShowApps, c => c.Linked).Hue);
            Assert.Equal(1, LogLines(b.Dir, "Stagewatch link: localhost takes this desk's key — verified key."));
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            link.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    /// <summary>The requests to a node route — the context this desk posts, its proposals — in arrival order.</summary>
    private static int NodeRouteCalls(FakeStagewatch stagewatch)
        => stagewatch.Requests.Count(r => r.What.StartsWith("POST /api/peers/context", StringComparison.Ordinal) || r.What.Contains("/api/proposals", StringComparison.Ordinal));

    [AvaloniaFact]
    public void AKeyThatSpeaksForAnotherNodeHoldsItsNodeRoutesWhileItsFramesAndItsContextKeepComing()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var id = services.Kernel.ShowBusId;
        // Two installs pasted one key: Stagewatch keeps the frames and says the key speaks for the other one. The binding
        // governs contexts and proposals, not reading (Stagewatch §7.1): the key goes on reading the context.
        using var stagewatch = new FakeStagewatch(RunningFrame) { First = StagewatchBytes.Verdict("node", "patterns", id, "none", StagewatchBytes.SpeaksFor("1a2b3c4d", id)) };
        var link = services.Kernel.Stagewatch;
        var words = $"key not accepted for this desk — this key speaks for patterns 1a2b3c4d — make a key for patterns {id}";
        try
        {
            Link(vm, services, stagewatch);
            PumpUntil(() => services.Stage.Following.Length > 0 && link.Key.NotAccepted && link.Sight.Context is not null, 15000);
            Assert.Equal(words, link.Key.Line);
            Assert.InRange(link.KeyHeld.TotalSeconds, 50, 60.5);
            PumpFor(3000);
            Assert.Equal(0, NodeRouteCalls(stagewatch));                                             // the node routes held
            Assert.Contains(stagewatch.Requests, r => r.What == "GET /api/context" && r.Authorization == "Bearer swk_testkey");   // its read goes on, keyed
            Assert.Equal(1, stagewatch.Dials);
            Assert.Equal(StageTimerPhase.Running, services.Stage.Time().Phase);
            var row = StagewatchRow(services);
            Assert.Equal(("none", "amber", words), (row.GetProperty("key").GetString(), row.GetProperty("light").GetString(), row.GetProperty("headline").GetString()));
            var apps = JsonDocument.Parse(services.Router.StateJson()).RootElement.GetProperty("bus").GetProperty("apps").Clone();
            Assert.Contains(apps.EnumerateArray(), a => a.GetProperty("headline").GetString() == words);                              // STATE's two readers agree
            services.Nodes.Poll();
            Assert.EndsWith(words, Assert.Single(services.Nodes.ShowApps, c => c.Linked).Line, StringComparison.Ordinal);
            services.Eye.Refresh();
            var node = services.Eye.Graph.Nodes.Single(n => n.Kind == EyeKind.ShowApp && n.Label.StartsWith("Stagewatch", StringComparison.Ordinal));
            Assert.Equal((CheckLight.Amber, words), (node.Light, node.Sub));
            Assert.Contains(services.Eye.Graph.Nodes, n => n.Id == node.Id + ":cue:lighting");      // its things still drawn: the context is read
            Assert.Equal(1, LogLines(b.Dir, $"Stagewatch link: localhost — {words}; the context this desk posts and its proposals wait 60 s."));

            // A pin set starts a new run for the same key at the same address: what was said goes on — the standing and the
            // node routes' hold — and the dial, which that hold never holds, goes at once.
            vm.State.Control.Stagewatch.SetPin("addr:elsewhere:1", TlsFingerprint.Prefix + new string('a', 64));
            link.Reconcile();
            Assert.Equal(words, link.Key.Line);
            Assert.True(link.KeyHeld > TimeSpan.FromSeconds(45), $"held {link.KeyHeld}");
            PumpUntil(() => stagewatch.Dials == 2 && link.Sight.Context is not null, 15000);
            PumpFor(2000);
            Assert.Equal(0, NodeRouteCalls(stagewatch));
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
    public void AKeyNotSpeakingForThisDeskNeverMakesItsContextBlinkAsItsHoldsEndAndStartAgain()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var id = services.Kernel.ShowBusId;
        // The key reads, and every post of this desk's context is refused 403 (a read key; a key bound to another node). Each
        // refusal holds the node routes for 7 s — past a context tick inside it, and past its end to the next post.
        using var stagewatch = new FakeStagewatch(RunningFrame)
        {
            ContextStatus = "403",
            ContextBody = "{\"ok\":false,\"error\":\"" + StagewatchBytes.SpeaksFor("1a2b3c4d", id) + "\"}",
        };
        var link = services.Kernel.Stagewatch;
        try
        {
            link.RefusedHoldAtLeast = TimeSpan.FromSeconds(7);
            Link(vm, services, stagewatch);
            PumpUntil(() => link.Key.NotAccepted && link.Sight.Context is not null, 15000);
            int Posts() => stagewatch.Requests.Count(r => r.What.StartsWith("POST /api/peers/context", StringComparison.Ordinal));
            var reads = stagewatch.ContextReads;

            // Through a hold, its end and the next refusal: the context never leaves any reader, and the light never moves.
            var until = Environment.TickCount64 + 25000;
            var blinks = new List<string>();
            while (Posts() < 2 && Environment.TickCount64 < until)
            {
                Dispatcher.UIThread.RunJobs();
                if (link.Sight.Context is null) blinks.Add("context gone");
                if (!link.Key.NotAccepted) blinks.Add("key " + link.Key.Word);
                services.Nodes.Poll();
                if (services.Nodes.ShowApps.SingleOrDefault(c => c.Linked) is { Hue: not "#FFB020" } card) blinks.Add("card " + card.Hue);
                Thread.Sleep(50);
            }
            Assert.Equal(2, Posts());                                                                // a hold ended, and the next post was refused again
            Assert.Empty(blinks);
            Assert.True(stagewatch.ContextReads > reads + 1, $"{stagewatch.ContextReads} reads");     // read on through the holds
            var row = StagewatchRow(services);
            Assert.Equal(("none", "amber"), (row.GetProperty("key").GetString(), row.GetProperty("light").GetString()));
            Assert.Equal(1, stagewatch.Dials);                                                       // nothing of it holds the link
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
    public void AnOlderStagewatchsViewingCodeThatLetsTheKeyNotPassIsReadAsItsRefusalAndLabelled()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        // Stagewatch 2.16 with a viewing code, the key unknown there: the bare view refusal, then the close — no verdict.
        using var stagewatch = new FakeStagewatch(RunningFrame)
        {
            Legacy = true,
            First = "{\"type\":\"auth\",\"ok\":false,\"view\":true,\"error\":\"This screen needs the viewing code\"}",
            CloseAfterFirst = true,
        };
        var link = services.Kernel.Stagewatch;
        try
        {
            Link(vm, services, stagewatch);
            PumpUntil(() => link.Key.Refused, 15000);
            Assert.True(link.Key.Legacy);
            Assert.Equal("KEY REFUSED — " + StagewatchKey.ViewWords + " — This screen needs the viewing code · " + StagewatchKey.LegacyWords, link.Key.Line);
            Assert.InRange(link.KeyHeld.TotalSeconds, 50, 60.5);
            PumpFor(1500);
            Assert.Equal(1, stagewatch.Dials);
            Assert.Equal(0, stagewatch.KeyedCalls);
            Assert.Equal("refused", StagewatchRow(services).GetProperty("key").GetString());
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
    public void TheViewingCodesRefusalClosesTheLinkAndItsRedialWaitsOutTheHoldItsRetryNames()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        // A refused key on a Stagewatch with a viewing code: one view refusal with the key's words and its retry, then the close.
        using var stagewatch = new FakeStagewatch(RunningFrame)
        {
            First = StagewatchBytes.ViewRefusal("This screen needs the viewing code", "unknown or revoked API key", 3),
            CloseAfterFirst = true,
        };
        var link = services.Kernel.Stagewatch;
        try
        {
            link.RefusedHoldAtLeast = TimeSpan.FromSeconds(1);                                       // the retry, 3 s, names the hold
            Link(vm, services, stagewatch);
            PumpUntil(() => stagewatch.Dials == 1 && link.Key.Refused, 15000);
            var at = Environment.TickCount64;
            Assert.InRange(link.KeyHeld.TotalSeconds, 2, 3.05);
            PumpFor(2000);
            Assert.Equal(1, stagewatch.Dials);                                                       // a redial inside the hold waits it out
            Assert.Contains("closed the link — This screen needs the viewing code; dialling again in 3 s · KEY REFUSED — unknown or revoked API key.", link.Status, StringComparison.Ordinal);
            PumpUntil(() => stagewatch.Dials >= 2, 15000);
            Assert.True(Environment.TickCount64 - at >= 2500, $"dialled again after {Environment.TickCount64 - at} ms");
            Assert.Equal(0, stagewatch.KeyedCalls);
            Assert.Equal("refused", StagewatchRow(services).GetProperty("key").GetString());
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
    public void ARateLimitHoldsKeyedCallsWithTheVerdictStandingAndA403IsSaidInItsWords()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var stagewatch = new FakeStagewatch(RunningFrame)
        {
            ContextStatus = "429",
            ContextBody = "{\"ok\":false,\"error\":\"rate limit: too many requests with this key\"}",
            ContextRetryAfter = 4,
        };
        var link = services.Kernel.Stagewatch;
        try
        {
            var id = services.Kernel.ShowBusId;
            Link(vm, services, stagewatch);
            PumpUntil(() => link.Key.Word == "key" && link.KeyHeld > TimeSpan.Zero, 15000);
            Assert.InRange(link.KeyHeld.TotalSeconds, 2, 4.05);                                     // its Retry-After
            Assert.Equal("", link.Key.Line);                                                         // a good key over its rate: nothing said of the key
            Assert.Equal("key", StagewatchRow(services).GetProperty("key").GetString());
            Assert.Equal(1, LogLines(b.Dir, "rate-limits this desk's key — its keyed calls wait 4 s (rate limit: too many requests with this key)"));
            var keyed = stagewatch.KeyedCalls;
            PumpFor(2500);
            Assert.Equal(keyed, stagewatch.KeyedCalls);                                              // held

            // Past it, the keyed calls go again; a 403 to this desk's context is said in Stagewatch's own words, and held.
            stagewatch.ContextBody = "{\"ok\":false,\"error\":\"" + StagewatchBytes.SpeaksFor("1a2b3c4d", id) + "\"}";
            stagewatch.ContextRetryAfter = 0;
            stagewatch.ContextStatus = "403";
            PumpUntil(() => link.Key.NotAccepted, 20000);
            Assert.True(stagewatch.KeyedCalls > keyed);
            Assert.Equal($"Stagewatch localhost refused this desk's context — {StagewatchBytes.SpeaksFor("1a2b3c4d", id)}", link.ApprovalWords);
            Assert.Equal("none", StagewatchRow(services).GetProperty("key").GetString());
            Assert.InRange(link.KeyHeld.TotalSeconds, 50, 60.5);
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
    public void AStagewatchThatSendsNoVerdictIsJudgedByItsHttpAnswersAndSaidInAmber()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        // Stagewatch 2.16's bytes: the first thing a socket hears is a frame, and a context taken says nothing of its proof.
        using var stagewatch = new FakeStagewatch(RunningFrame) { Legacy = true };
        var link = services.Kernel.Stagewatch;
        try
        {
            Link(vm, services, stagewatch);
            PumpUntil(() => services.Stage.Following.Length > 0 && link.Key.Legacy && link.Sight.Context is not null && stagewatch.KeyedCalls >= 2, 15000);

            // P-legacy: the fallback labelled in amber, in every reader, and STATE's key empty — Stagewatch said neither.
            Assert.Equal(StagewatchKey.LegacyWords, link.Key.Line);
            var row = StagewatchRow(services);
            Assert.Equal(("", "amber"), (row.GetProperty("key").GetString(), row.GetProperty("light").GetString()));
            Assert.Contains(StagewatchKey.LegacyWords, row.GetProperty("words").GetString(), StringComparison.Ordinal);
            services.Nodes.Poll();
            var card = Assert.Single(services.Nodes.ShowApps, c => c.Linked);
            Assert.Equal("#FFB020", card.Hue);
            Assert.EndsWith(StagewatchKey.LegacyWords, card.Line, StringComparison.Ordinal);
            services.Eye.Refresh();
            var node = services.Eye.Graph.Nodes.Single(n => n.Kind == EyeKind.ShowApp && n.Label.StartsWith("Stagewatch", StringComparison.Ordinal));
            Assert.Equal(CheckLight.Amber, node.Light);
            Assert.StartsWith("Keynote", node.Sub, StringComparison.Ordinal);                        // the label is no fault: its headline stays
            Assert.Contains("this desk's key: " + StagewatchKey.LegacyWords, node.Words);
            Assert.Contains("  this desk's key: " + StagewatchKey.LegacyWords, ShowBusView.BriefLines(services.ShowApps(DateTime.UtcNow)));
            Assert.Equal(1, LogLines(b.Dir, "Stagewatch link: localhost — " + StagewatchKey.LegacyWords + "."));

            // Its HTTP answers are the evidence: a keyed call refused (401) refuses the key, held, and what a held key
            // cannot read is not shown as current. A far end that echoes the bearer: the key masked in every reader.
            stagewatch.ContextBody = "{\"ok\":false,\"error\":\"unknown or revoked API key: Bearer swk_testkey\"}";
            stagewatch.ContextStatus = "401";
            PumpUntil(() => link.Key.Refused, 20000);
            Assert.Equal("KEY REFUSED — unknown or revoked API key: Bearer ••• · " + StagewatchKey.LegacyWords, link.Key.Line);
            Assert.Equal("refused", StagewatchRow(services).GetProperty("key").GetString());
            Assert.DoesNotContain("swk_testkey", services.Router.StateJson(), StringComparison.Ordinal);
            Assert.DoesNotContain("swk_testkey", link.Status, StringComparison.Ordinal);
            Assert.Equal(0, LogLines(b.Dir, "swk_testkey"));
            Assert.Equal(1, LogLines(b.Dir, "unknown or revoked API key: Bearer •••"));
            Assert.InRange(link.KeyHeld.TotalSeconds, 50, 60.5);
            PumpUntil(() => link.Sight.Context is null, 15000);
            var keyed = stagewatch.KeyedCalls;
            PumpFor(3000);
            Assert.Equal(keyed, stagewatch.KeyedCalls);
            Assert.Equal(StageTimerPhase.Running, services.Stage.Time().Phase);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            link.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    // ---- Show Bus 1.3 §2: one id, one peer; two machines with one id; the table bounded ----------------------------

    /// <summary>A hello as the desk's listener hears it, now, from <paramref name="address"/> — the datagram's own source.</summary>
    private static bool Heard(AppServices services, string t, string id, string inst, string host, string address, string name = "Hall A", int port = 9924, string tls = "")
        => services.Kernel.Beacon.ShowBus.Hear(Encoding.UTF8.GetBytes($"{{\"t\":\"{t}\",\"v\":1,\"proto\":3,\"id\":\"{id}\",\"inst\":\"{inst}\",\"name\":\"{name}\",\"host\":\"{host}\",\"ports\":{{\"http\":{port},\"ws\":{port}}},\"caps\":[\"context\"]{(tls.Length > 0 ? $",\"tls\":\"{tls}\"" : "")}}}"), address, DateTime.UtcNow, "not-this-desk");

    /// <summary>A forged id of the floods, from <paramref name="i"/>: the same at every run.</summary>
    private static string Forged(string prefix, int i) => prefix + i.ToString("x4", System.Globalization.CultureInfo.InvariantCulture);

    [AvaloniaFact]
    public void AnIdTwoMachinesSayIsAmberInEveryReaderAndGetsNoNewLinkWhileAFloodIsBoundedCountedAndSaid()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        const string words = "two machines say they are c1a5c1a5c1a5: FOH-PC at 10.0.0.5 and FOH-PC2 at 10.0.0.7";
        try
        {
            // Two running Stagewatches say one id, taking turns.
            Heard(services, "main", "c1a5c1a5c1a5", "aaaaaaaa", "FOH-PC", "10.0.0.5");
            Heard(services, "main", "c1a5c1a5c1a5", "bbbbbbbb", "FOH-PC2", "10.0.0.7");
            Heard(services, "main", "c1a5c1a5c1a5", "aaaaaaaa", "FOH-PC", "10.0.0.5");

            // The link that finds a Stagewatch on the show network dials no new link to it while the mark stands, and says why.
            var link = vm.State.Control.Stagewatch;
            link.Host = "";
            link.ApiKey = "swk_testkey";
            link.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();
            PumpUntil(() =>
            {
                Heard(services, "main", "c1a5c1a5c1a5", "aaaaaaaa", "FOH-PC", "10.0.0.5");
                return services.Kernel.Stagewatch.Status.Contains(words + " — no new link to it", StringComparison.Ordinal);
            });
            Assert.Equal("c1a5c1a5c1a5", services.Kernel.Stagewatch.NodeId);                        // held off, the id stays the link's
            Assert.Equal(words, services.Kernel.Stagewatch.Held);
            Assert.Equal("", services.Kernel.Stagewatch.Sight.Name);                                  // nothing dialled

            // STATE: the app's row amber with both machines named, its conflict flagged at the row's end; the link's row amber
            // with the same words while nothing is linked, so a deck that reads its light and headline sees the hold.
            var bus = JsonDocument.Parse(services.Router.StateJson()).RootElement.GetProperty("bus");
            var row = bus.GetProperty("apps").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "c1a5c1a5c1a5");
            Assert.Equal(("amber", words, true), (row.GetProperty("light").GetString(), row.GetProperty("headline").GetString(), row.GetProperty("conflict").GetBoolean()));
            var sw = bus.GetProperty("stagewatch");
            Assert.Equal(("off", "amber", words, words), (sw.GetProperty("state").GetString(), sw.GetProperty("light").GetString(), sw.GetProperty("headline").GetString(), sw.GetProperty("held").GetString()));
            Assert.Contains(words, sw.GetProperty("words").GetString(), StringComparison.Ordinal);
            Assert.Equal(0, bus.GetProperty("dropped").GetInt64());

            // The Nodes page: the card amber, the words on it; one log line for the mark.
            services.Nodes.Poll();
            var card = services.Nodes.ShowApps.Single(c => c.Id == "c1a5c1a5c1a5");
            Assert.Equal("#FFB020", card.Hue);
            Assert.Contains(words, card.Line, StringComparison.Ordinal);
            PumpUntil(() => LogLines(b.Dir, "Show network: " + words) == 1);
            // The context any app's assistant reads, and the Eye.
            Assert.Contains(words, services.ContextJson(), StringComparison.Ordinal);
            Assert.Contains(services.ShowApps(DateTime.UtcNow), a => a.Conflict == words);

            // A flood while the link is held: the id it is held off is needed, so its entry and its mark stay whatever is
            // heard, and the link still dials nothing — a flood cannot wipe the mark and let the next hello be dialled.
            for (var i = 0; i < 300; i++)
                Assert.True(Heard(services, "messenger", Forged("f0rged", i), "0a0a0a0a", "X", "10.9." + (i / 250) + "." + (i % 250)));
            var kept = services.Kernel.Beacon.ShowBus.Snapshot(DateTime.UtcNow);
            Assert.Equal(ShowBusPeers.MaxPeers, kept.Count);
            Assert.NotNull(kept.Single(p => p.Id == "c1a5c1a5c1a5").Conflict);
            Assert.Equal(300 + 1 - ShowBusPeers.MaxPeers, services.Kernel.Beacon.ShowBus.Dropped);
            PumpFor(1500);
            Assert.Equal(("c1a5c1a5c1a5", ""), (services.Kernel.Stagewatch.NodeId, services.Kernel.Stagewatch.Sight.Name));
            Assert.Contains(words + " — no new link to it", services.Kernel.Stagewatch.Status, StringComparison.Ordinal);
            services.Nodes.Poll();
            PumpUntil(() => LogLines(b.Dir, "apps dropped from the hello table (173 since start)") == 1);

            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();

            // A Stagewatch this desk pinned is needed: a flood of 300 more forged ids never puts it out; the id no longer held
            // goes first, said so — put out, not cleared; every drop is counted, and said once a minute.
            Heard(services, "main", "5ca1ab1e0001", "0a1b2c3d", "STAGE-PC", "10.0.0.9");
            link.SetPin("node:5ca1ab1e0001", TlsFingerprint.Prefix + new string('a', 64));
            services.Nodes.Poll();
            for (var i = 0; i < 300; i++)
                Assert.True(Heard(services, "messenger", Forged("f1rged", i), "0a0a0a0a", "X", "10.8." + (i / 250) + "." + (i % 250)));
            var held = services.Kernel.Beacon.ShowBus.Snapshot(DateTime.UtcNow);
            Assert.Equal(ShowBusPeers.MaxPeers, held.Count);
            Assert.Contains(held, p => p.Id == "5ca1ab1e0001");
            Assert.DoesNotContain(held, p => p.Id == "c1a5c1a5c1a5");                                    // not needed once the link is off: out first
            Assert.Equal(474, services.Kernel.Beacon.ShowBus.Dropped);                                   // 173, then the old mark, then 300
            Assert.Equal(474, JsonDocument.Parse(services.Router.StateJson()).RootElement.GetProperty("bus").GetProperty("dropped").GetInt64());
            services.Nodes.Poll();
            Assert.Contains("474 apps dropped from the hello table since start", services.Nodes.ShowBusLine, StringComparison.Ordinal);
            PumpUntil(() => LogLines(b.Dir, "Show network: c1a5c1a5c1a5 was put out of the full hello table") == 1);
            Assert.Equal(0, LogLines(b.Dir, "c1a5c1a5c1a5 — no two machines heard taking turns"));        // never said cleared
            Assert.Equal(1, LogLines(b.Dir, "apps dropped from the hello table ("));                      // once in the minute
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void FoundOnTheShowNetworkTheLinkHoldsOffAStagewatchInConflictAndMovesOnlyToOneThisDeskPinned()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        try
        {
            // Hall A, the first heard, is said by two machines; Hall B is heard alone (at a door nothing answers on).
            var annex = FreePort();
            void Hall()
            {
                Heard(services, "main", "a0a0a0a0a0a0", "aaaaaaaa", "FOH-PC", "10.0.0.5", "Hall A");
                Heard(services, "main", "b0b0b0b0b0b0", "cccccccc", "ANNEX-PC", "127.0.0.1", "Hall B", annex);
            }
            Hall();
            Heard(services, "main", "a0a0a0a0a0a0", "bbbbbbbb", "FOH-PC2", "10.0.0.7", "Hall A");
            Hall();

            var link = vm.State.Control.Stagewatch;
            link.Host = "";
            link.ApiKey = "swk_testkey";
            link.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            PumpUntil(() =>
            {
                Hall();
                seen.Add(services.Kernel.Stagewatch.NodeId);
                return services.Kernel.Stagewatch.Held.Length > 0;
            });
            // Held, and held: the link does not move to Hall B, whose id this desk never pinned, with this desk's key.
            for (var i = 0; i < 30; i++)
            {
                Hall();
                PumpFor(50);
                seen.Add(services.Kernel.Stagewatch.NodeId);
            }
            Assert.DoesNotContain("b0b0b0b0b0b0", seen);
            Assert.Equal("a0a0a0a0a0a0", services.Kernel.Stagewatch.NodeId);
            Assert.StartsWith("two machines say they are a0a0a0a0a0a0", services.Kernel.Stagewatch.Held, StringComparison.Ordinal);

            // Hall B pinned: a Stagewatch this desk has linked to before is one the link may move to.
            link.SetPin("node:b0b0b0b0b0b0", TlsFingerprint.Prefix + new string('b', 64));
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();
            PumpUntil(() =>
            {
                Hall();
                return services.Kernel.Stagewatch.NodeId == "b0b0b0b0b0b0";
            });
            Assert.Equal("", services.Kernel.Stagewatch.Held);
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATypedAddressNeverBorrowsTheRecordOfAHelloThatOnlySaysItIsThatHost()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var stagewatch = new FakeStagewatch(RunningFrame);
        try
        {
            // A machine elsewhere says hello naming the typed address as its host, the typed door, a name that sorts first and
            // another certificate. Its datagrams come from its own address: it is heard, never taken for the typed one.
            void Forger() => Heard(services, "main", "e1110001", "0e0e0e0e", "localhost", "10.0.0.66", "A", stagewatch.Port, TlsFingerprint.Prefix + new string('c', 64));
            Forger();
            Link(vm, services, stagewatch);
            PumpUntil(() =>
            {
                Forger();
                return services.Kernel.Stagewatch.Sight is { State: ShowBusState.Live, Frame: not null };
            }, 20000);

            // The link keeps the typed address's own record: no node id borrowed, its own name, no hello's TLS words.
            Assert.Equal("", services.Kernel.Stagewatch.NodeId);
            Assert.Equal("localhost", services.Stage.Following);
            Assert.DoesNotContain("cccccc", services.Kernel.Stagewatch.TlsWords, StringComparison.Ordinal);   // never the hello's certificate
            Assert.Equal(0, LogLines(b.Dir, "the hello heard for A"));
            // Every reader: the forged app is only heard; the link is drawn as an app of its own.
            var apps = services.ShowApps(DateTime.UtcNow);
            Assert.False(apps.Single(a => a.Id == "e1110001").Linked);
            var linked = Assert.Single(apps, a => a.Linked);
            Assert.NotEqual("e1110001", linked.Id);
            var row = JsonDocument.Parse(services.Router.StateJson()).RootElement.GetProperty("bus").GetProperty("apps").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "e1110001");
            Assert.Equal("heard on the show network", row.GetProperty("headline").GetString());
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ALiveLinkIsKeptWhenItsIdFallsIntoConflictAndEveryReaderSaysSo()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        using var stagewatch = new FakeStagewatch(RunningFrame);
        const string words = "two machines say they are a1b2c3d4: STAGE-PC2 at 10.0.0.7 and STAGE-PC at localhost";
        try
        {
            // Found on the show network and linked.
            void Stage() => Heard(services, "main", "a1b2c3d4", "0a1b2c3d", "STAGE-PC", "localhost", "Hall A", stagewatch.Port);
            Stage();
            var link = vm.State.Control.Stagewatch;
            link.Host = "";
            link.ApiKey = "swk_testkey";
            link.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            services.Kernel.Stagewatch.Reconcile();
            PumpUntil(() =>
            {
                Stage();
                return services.Kernel.Stagewatch.Sight is { State: ShowBusState.Live, Frame: not null } && services.Kernel.Stagewatch.Sight.Context is not null;
            }, 20000);
            var dials = stagewatch.Dials;

            // Another machine starts saying its id: A, B, A — the id is in conflict, and the link it has is kept (13.4.4).
            Heard(services, "main", "a1b2c3d4", "0b1c2d3e", "STAGE-PC2", "10.0.0.7", "Hall A", stagewatch.Port);
            Stage();
            Assert.Equal(words, services.Kernel.Beacon.ShowBus.Snapshot(DateTime.UtcNow).Single(p => p.Id == "a1b2c3d4").Conflict?.Words);
            for (var i = 0; i < 40; i++)
            {
                Stage();
                PumpFor(50);
            }
            Assert.Equal(ShowBusState.Live, services.Kernel.Stagewatch.Sight.State);
            Assert.Equal(dials, stagewatch.Dials);                                                     // never dropped, never redialled
            Assert.Equal("", services.Kernel.Stagewatch.Held);

            // Every reader: the linked app amber with both machines named — STATE's apps row and the link's own row.
            var bus = JsonDocument.Parse(services.Router.StateJson()).RootElement.GetProperty("bus");
            var row = bus.GetProperty("apps").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "a1b2c3d4");
            Assert.Equal(("amber", true), (row.GetProperty("light").GetString(), row.GetProperty("conflict").GetBoolean()));
            var sw = bus.GetProperty("stagewatch");
            Assert.Equal(("live", "amber", words, ""), (sw.GetProperty("state").GetString(), sw.GetProperty("light").GetString(), sw.GetProperty("headline").GetString(), sw.GetProperty("held").GetString()));
            var app = services.ShowApps(DateTime.UtcNow).Single(a => a.Id == "a1b2c3d4");
            Assert.True(app.Linked);
            Assert.True(ShowBusView.ConflictAmber(app));
        }
        finally
        {
            vm.State.Control.Stagewatch.Enabled = false;
            services.Kernel.Stagewatch.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ACertificateMadeBefore13IsReplacedOnceAtAStartAndItsReplacementIsKeptAtTheNext()
    {
        // A five-year certificate as Patterns made before Show Bus 1.3, kept beside the settings, 800 days left.
        string oldPrint = "";
        var b = TestApp.Boot(prepare: dir =>
        {
            using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=FOH", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
            var notAfter = DateTime.UtcNow.AddDays(800);
            using var old = request.CreateSelfSigned(new DateTimeOffset(notAfter.AddDays(-1826)), new DateTimeOffset(notAfter));
            oldPrint = TlsFingerprint.Of(old);
            File.WriteAllText(Path.Combine(dir, TlsCertificateService.FileName),
                JsonSerializer.Serialize(new { v = 1, protection = "owner-only-file", pkcs12 = Convert.ToBase64String(TlsIdentity.Export(old)) }));
        });
        try
        {
            var made = b.Services.Kernel.Tls.Certificate;
            Assert.NotNull(made);
            var print = TlsFingerprint.Of(made!);
            Assert.NotEqual(oldPrint, print);                                                         // replaced at this start
            Assert.Equal(TlsIdentity.Life, made!.NotAfter.ToUniversalTime() - made.NotBefore.ToUniversalTime());
            PumpUntil(() => LogLines(b.Dir, $"TLS: this desk's certificate spans 1826 days, more than the 825") == 1);
            Assert.Equal(1, LogLines(b.Dir, $"every client that pinned {oldPrint} sees CERTIFICATE CHANGED once"));

            // The next start reads the replacement and keeps it: the change is seen once.
            using var next = new TlsCertificateService(b.Dir, "FOH");
            Assert.Equal(print, next.Fingerprint);
            Assert.Equal(1, LogLines(b.Dir, "more than the 825"));
            Assert.Equal(0, LogLines(b.Dir, $"every client that pinned {print}"));
        }
        finally
        {
            b.Dispose();
        }
    }
}
