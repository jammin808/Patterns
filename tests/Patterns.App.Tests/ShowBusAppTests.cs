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

    /// <summary>A wire connection: the greeting read, and the nonce when the desk says one; Reply skips STATE pushes and keeps any later NONCE.</summary>
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
            Assert.StartsWith("STATE {", Pump(_reader.ReadLineAsync()));
            if (expectNonce)
            {
                var second = Pump(_reader.ReadLineAsync());
                Assert.StartsWith("NONCE ", second);
                Nonce = second!["NONCE ".Length..];
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
            Assert.True(services.State.Blackout);
            Assert.Contains("127.0.0.1", services.Control.PairedAddresses());       // OSC from here may move the show now

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
    /// </summary>
    private sealed class FakeStagewatch : IDisposable
    {
        public const string StuckBeat = "{\"type\":\"beat\",\"live\":false}";

        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private volatile string _authorization = "";
        private volatile string _url = "";
        private int _pings;
        private int _contextReads;
        private int _stuckBeats;

        public int Port { get; }
        public string Authorization => _authorization;
        public string Url => _url;
        public int Pings => Volatile.Read(ref _pings);
        public int ContextReads => Volatile.Read(ref _contextReads);
        public int StuckBeats => Volatile.Read(ref _stuckBeats);

        /// <summary>Its show thread has stopped: every answer to a ping is the pong and the stuck beat, never a frame.</summary>
        public volatile bool Stuck;

        /// <summary>/api/context answers (else 404, as an older Stagewatch does), and what its Lighting item says.</summary>
        public volatile bool ServeContext = true;

        public volatile string LightingState = "standing by: LX 12";

        public FakeStagewatch(string frame)
        {
            Port = FreePort();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _ = Task.Run(() => Serve(frame), _cts.Token);
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
                    if (ctx.Request.Url!.AbsolutePath == "/ws" && ctx.Request.IsWebSocketRequest)
                    {
                        _authorization = ctx.Request.Headers["Authorization"] ?? "";
                        _url = ctx.Request.Url.ToString();
                        var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                        await SendShow(ws, frame);
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
                                await ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"type\":\"pong\",\"t\":{t},\"t2\":{now},\"t3\":{now}}}"), WebSocketMessageType.Text, true, _cts.Token);
                                await SendShow(ws, frame);
                            }
                        }
                        return;
                    }
                    if (ctx.Request.Url.AbsolutePath == "/api/context") Interlocked.Increment(ref _contextReads);
                    var body = ctx.Request.Url.AbsolutePath == "/api/context" && ServeContext
                        ? "{\"v\":1,\"node\":{\"t\":\"main\",\"id\":\"a1b2c3d4\",\"name\":\"Hall A\"},\"at\":1,\"light\":\"amber\",\"headline\":\"Keynote — 12:30 left · Lighting standing by\","
                          + "\"items\":[{\"id\":\"timer\",\"kind\":\"timer\",\"label\":\"Keynote — Ana\",\"light\":\"green\",\"state\":\"running, 12:30 left\"},"
                          + "{\"id\":\"cue:lighting\",\"kind\":\"department\",\"label\":\"Lighting\",\"light\":\"amber\",\"state\":\"" + LightingState + "\"}],\"links\":[],\"recent\":[],\"commands\":[],\"peers\":[]}"
                        : "not here";
                    ctx.Response.StatusCode = body == "not here" ? 404 : 200;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token);
                    ctx.Response.Close();
                }, _cts.Token);
            }
        }

        /// <summary>What the show says on the socket: the frame while it turns over, the stuck beat while it does not.</summary>
        private async Task SendShow(WebSocket ws, string frame)
        {
            if (Stuck) Interlocked.Increment(ref _stuckBeats);
            await ws.SendAsync(Encoding.UTF8.GetBytes(Stuck ? StuckBeat : frame), WebSocketMessageType.Text, true, _cts.Token);
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
            Assert.Equal(new[] { "stuck", "carrier" }, row.EnumerateObject().Select(p => p.Name).TakeLast(2));   // stuck appended, the carrier after it
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
}
