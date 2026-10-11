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
        var head = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: test\r\nContent-Length: {bodyBytes.Length}\r\n");
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

    /// <summary>A stand-in Stagewatch: /ws answers with state frames and pongs, /api/context with its context; it records the Authorization header it was dialled with.</summary>
    private sealed class FakeStagewatch : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private volatile string _authorization = "";
        private volatile string _url = "";
        private int _pings;

        public int Port { get; }
        public string Authorization => _authorization;
        public string Url => _url;
        public int Pings => Volatile.Read(ref _pings);

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
                        await ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, _cts.Token);
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
                                await ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, _cts.Token);
                            }
                        }
                        return;
                    }
                    var body = ctx.Request.Url.AbsolutePath == "/api/context"
                        ? "{\"v\":1,\"node\":{\"t\":\"main\",\"id\":\"a1b2c3d4\",\"name\":\"Hall A\"},\"at\":1,\"light\":\"amber\",\"headline\":\"Keynote — 12:30 left · Lighting standing by\","
                          + "\"items\":[{\"id\":\"cue:lighting\",\"kind\":\"department\",\"label\":\"Lighting\",\"light\":\"amber\",\"state\":\"standing by: LX 12\"}],\"links\":[],\"recent\":[],\"commands\":[],\"peers\":[]}"
                        : "not here";
                    ctx.Response.StatusCode = body == "not here" ? 404 : 200;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token);
                    ctx.Response.Close();
                }, _cts.Token);
            }
        }

        public void Dispose()
        {
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
}
