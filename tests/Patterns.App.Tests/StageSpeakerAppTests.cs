using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Views.Sections;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Core.Tests;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// One speaker message in the room, on a live desk against a scripted Stagewatch that answers with the bytes Stagewatch's own
/// contract pins (<see cref="StagewatchMessageBytes"/>): with a node key the message goes through the Stagewatch — its path, key
/// and 13.12.1 id seen there, its receipt and the speaker's ACK back here, one journal row per step, STATE and the pages
/// following; an accepted message is bound by a frame that names its words and never by another message in the gap; each of
/// 13.12.3's outcomes ends where it should and none sends the message again; when the link cannot carry it the message stays
/// on this desk with the reason; a cue's row settles on the receipt and never on the clock; STAGE CLEAR clears only what this
/// desk posted; and the speaker's calls never wait behind the context's read.
/// </summary>
public class StageSpeakerAppTests
{
    private const string Key = "swk_speakercanary94a1b2c3";
    private const string Name = "Hall A";

    /// <summary>The scripted Stagewatch's hello, heard again while a test waits: the link names it Hall A as long as its hello is fresh.</summary>
    [ThreadStatic] private static Action? t_hear;

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 20000, Func<string>? seen = null)
        => ShowBus12AppTests.PumpUntil(() =>
        {
            t_hear?.Invoke();
            return condition();
        }, timeoutMs, seen);

    // ---- the scripted Stagewatch ---------------------------------------------------------------------------------------

    /// <summary>One call the scripted Stagewatch took on its HTTP door.</summary>
    internal sealed record Call(string Method, string Path, string Authorization, string RequestId, long AtMs, bool DuringContext = false);

    /// <summary>
    /// A Stagewatch as a show app sees it: /ws with its verdict, frames and pongs; its context; and the speaker's three routes
    /// answered with the contract's bytes, its frames carrying the message and its signal's three fields. The test plays its
    /// operator (a message of its own, a clear), its speaker (an answer) and its faults (a refusal, a rate limit, a slow show, a
    /// held answer, a stuck show, a dropped link).
    /// </summary>
    internal sealed class SpeakerStagewatch : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _gate = new();
        private readonly List<(WebSocket Socket, SemaphoreSlim Gate)> _sockets = new();
        private readonly List<TcpClient> _clients = new();
        private readonly List<(string Text, string Flash)> _slow = new();
        private string _message = "";
        private string _flash = "off";
        private long _messageId;
        private string _ack = "";
        private string _by = "";
        private long _signals;
        private long _seq = 4;
        private TaskCompletionSource? _hold;
        private TaskCompletionSource? _holdAnswer;
        private int _held;
        private int _contextInFlight;

        public int Port { get; }

        /// <summary>Every call on its HTTP door, in order.</summary>
        public readonly ConcurrentQueue<Call> Calls = new();

        /// <summary>The status it answers a message or a clear with (0: it takes it), its words and its Retry-After.</summary>
        public volatile int SpeakerStatus;
        public volatile string SpeakerWords = "";
        public volatile int RetryAfter;

        /// <summary>The status it answers a forwarded ACK with at its door (0: by its board), and its words.</summary>
        public volatile int AckStatus;
        public volatile string AckWords = "";

        /// <summary>Its show thread is slow: a message's receipt says only accepted, and the message runs at <see cref="RunShow"/>.</summary>
        public volatile bool Slow;

        /// <summary>It has no message signals: an /ack message is applied with no refs.messageId.</summary>
        public volatile bool NoSignals;

        /// <summary>Its show thread has stopped: each ping answered with the pong and the stuck beat, never a frame.</summary>
        public volatile bool Stuck;

        /// <summary>It stops answering on its socket — no pong, no frame — and keeps the socket open: quiet, not gone.</summary>
        public volatile bool Mute;

        /// <summary>How long it takes to answer a read of its context.</summary>
        public volatile int ContextDelayMs;

        public int ContextInFlight => Volatile.Read(ref _contextInFlight);

        /// <summary>The speaker's calls it holds unanswered now.</summary>
        public int Held => Volatile.Read(ref _held);

        public SpeakerStagewatch()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(Serve, _cts.Token);
        }

        public IReadOnlyList<Call> Speaker => Calls.Where(c => c.Path.StartsWith("/api/message/", StringComparison.Ordinal) || c.Path == "/api/clearmessage" || c.Path.StartsWith("/api/ack/", StringComparison.Ordinal)).ToList();

        public IReadOnlyList<Call> Messages => Calls.Where(c => c.Path.StartsWith("/api/message/", StringComparison.Ordinal)).ToList();

        public long MessageId
        {
            get
            {
                lock (_gate) return _messageId;
            }
        }

        /// <summary>The next of the speaker's calls waits, unanswered and not yet run, until <see cref="Release"/>.</summary>
        public void HoldNext()
        {
            lock (_gate) _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// The next message runs — on its stage, its frame pushed — and its answer waits until <see cref="Release"/>: an answer
        /// lost after the request ran.
        /// </summary>
        public void HoldNextAnswer()
        {
            lock (_gate) _holdAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Release()
        {
            TaskCompletionSource? hold;
            TaskCompletionSource? answer;
            lock (_gate)
            {
                (hold, answer) = (_hold, _holdAnswer);
                (_hold, _holdAnswer) = (null, null);
            }
            hold?.TrySetResult();
            answer?.TrySetResult();
        }

        /// <summary>Its own operator puts a message on its stage.</summary>
        public long OperatorMessage(string text, bool ack = true, string flash = "slow")
        {
            long id;
            lock (_gate) id = Put(text, flash, ack);
            Push();
            return id;
        }

        /// <summary>Its own operator clears the stage.</summary>
        public void OperatorClear()
        {
            lock (_gate) Put("", "off", false);
            Push();
        }

        /// <summary>The speaker answers the signal on its stage with the viewer's own button: done, by the device's address.</summary>
        public void SpeakerAnswers(string by = "10.0.0.30")
        {
            lock (_gate)
            {
                if (_messageId == 0 || _ack != "sent") return;
                _ack = "done";
                _by = by;
            }
            Push();
        }

        /// <summary>30 s after an answer: the frame's three fields go back to 0, "", "", the message still on stage.</summary>
        public void ResetSignalFields()
        {
            lock (_gate) (_messageId, _ack, _by) = (0, "", "");
            Push();
        }

        /// <summary>Its show thread runs again: what it accepted is applied, each with a signal of its own.</summary>
        public void RunShow()
        {
            lock (_gate)
            {
                foreach (var (text, flash) in _slow) Put(text, flash, true);
                _slow.Clear();
            }
            Push();
        }

        /// <summary>Every connection closed at once: the link drops.</summary>
        public void DropAll()
        {
            List<TcpClient> clients;
            lock (_gate)
            {
                clients = _clients.ToList();
                _clients.Clear();
                _sockets.Clear();
            }
            foreach (var c in clients) c.Close();
        }

        /// <summary>It goes away: every connection closed and its door shut — a dial of it is refused.</summary>
        public void GoAway()
        {
            _listener.Stop();
            DropAll();
        }

        /// <summary>A frame to every socket now.</summary>
        public void Push()
        {
            if (Mute) return;
            List<(WebSocket Socket, SemaphoreSlim Gate)> sockets;
            lock (_gate) sockets = _sockets.ToList();
            foreach (var (socket, gate) in sockets) _ = SendAsync(socket, gate, Stuck ? "{\"type\":\"beat\",\"live\":false}" : Frame());
        }

        private long Put(string text, string flash, bool ack)
        {
            _message = text;
            _flash = text.Length == 0 ? "off" : flash;
            (_messageId, _ack, _by) = ack && text.Length > 0 && !NoSignals ? (++_signals, "sent", "") : (0, "", "");
            return _messageId;
        }

        private string Frame()
        {
            lock (_gate) return StagewatchMessageBytes.Frame(++_seq, _message, _flash, _messageId, _ack, _by);
        }

        private async Task SendAsync(WebSocket socket, SemaphoreSlim gate, string text)
        {
            try
            {
                await gate.WaitAsync(_cts.Token);
                try
                {
                    if (socket.State == WebSocketState.Open) await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, _cts.Token);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (Exception)
            {
                // a socket that left, or the fake going away
            }
        }

        private async Task Serve()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception) { return; }
                lock (_gate) _clients.Add(c);
                _ = Task.Run(() => Handle(c), _cts.Token);
            }
        }

        private async Task Handle(TcpClient client)
        {
            try
            {
                var s = client.GetStream();
                var (head, _) = await ReadRequest(s);
                var lines = head.Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length < 2) return;
                var (method, path) = (first[0], first[1]);
                string Header(string name) => lines.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) is { } l ? l[(name.Length + 1)..].Trim() : "";
                var auth = Header("Authorization");
                if (path.StartsWith("/ws", StringComparison.Ordinal))
                {
                    var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(Header("Sec-WebSocket-Key") + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _cts.Token);
                    using var ws = WebSocket.CreateFromStream(s, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
                    using var gate = new SemaphoreSlim(1, 1);
                    if (StagewatchBytes.FirstFor(path, auth) is { } verdict) await SendAsync(ws, gate, verdict);
                    await SendAsync(ws, gate, Frame());
                    lock (_gate) _sockets.Add((ws, gate));
                    var buffer = new byte[8192];
                    while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                    {
                        var r = await ws.ReceiveAsync(buffer, _cts.Token);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        if (Mute) continue;
                        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count));
                        var t = doc.RootElement.GetProperty("t").GetInt64();
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        await SendAsync(ws, gate, $"{{\"type\":\"pong\",\"t\":{t},\"t2\":{now},\"t3\":{now}}}");
                        await SendAsync(ws, gate, Stuck ? "{\"type\":\"beat\",\"live\":false}" : Frame());
                    }
                    return;
                }
                var requestId = Header("X-Request-Id");
                Calls.Enqueue(new Call(method, path, auth, requestId, Environment.TickCount64, ContextInFlight > 0));
                var (status, reply, retryAfter) = await Route(method, path, auth, requestId);
                var bytes = Encoding.UTF8.GetBytes(reply);
                var retry = retryAfter > 0 ? $"Retry-After: {retryAfter}\r\n" : "";
                await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\n{retry}Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _cts.Token);
                await s.WriteAsync(bytes, _cts.Token);
                await s.FlushAsync(_cts.Token);
            }
            catch (Exception)
            {
                // a client that left, or a link the test dropped
            }
            finally
            {
                lock (_gate) _clients.Remove(client);
                client.Dispose();
            }
        }

        private async Task<(string Status, string Body, int RetryAfter)> Route(string method, string path, string auth, string requestId)
        {
            if (path == "/api/context")
            {
                Interlocked.Increment(ref _contextInFlight);
                try
                {
                    if (ContextDelayMs > 0) await Task.Delay(ContextDelayMs, _cts.Token);
                }
                finally
                {
                    Interlocked.Decrement(ref _contextInFlight);
                }
                return ("200 OK", "{\"v\":1,\"node\":{\"t\":\"main\",\"id\":\"swdesk01\",\"name\":\"Hall A\"},\"light\":\"green\",\"headline\":\"Keynote\",\"items\":[],\"commands\":[]}", 0);
            }
            if (path == "/api/peers/context" && method == "POST") return ("200 OK", "{\"ok\":true,\"verified\":\"key\"}", 0);
            var message = path.StartsWith("/api/message/", StringComparison.Ordinal);
            var clear = path == "/api/clearmessage";
            var ack = path.StartsWith("/api/ack/", StringComparison.Ordinal);
            if (method != "POST" || !(message || clear || ack)) return ("404 Not Found", "{}", 0);
            // A held call is neither run nor answered until the test lets it go.
            Task? hold;
            lock (_gate) hold = _hold?.Task;
            if (hold is not null)
            {
                Interlocked.Increment(ref _held);
                try
                {
                    await hold.WaitAsync(_cts.Token);
                }
                finally
                {
                    Interlocked.Decrement(ref _held);
                }
            }
            if (!auth.StartsWith("Bearer swk_", StringComparison.Ordinal)) return ("401 Unauthorized", StagewatchMessageBytes.Door("unknown or revoked API key"), 0);
            var parts = path["/api/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
            if (ack)
            {
                if (AckStatus != 0) return (AckStatus + " Refused", StagewatchMessageBytes.Door(AckWords), 0);
                var id = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                string by;
                lock (_gate)
                {
                    if (id != _messageId) return ("403 Forbidden", StagewatchMessageBytes.Door(), 0);
                    if (_ack != "sent") return ("409 Conflict", StagewatchMessageBytes.Refused(requestId), 0);
                    _ack = "done";
                    _by = by = StagewatchMessageBytes.AnsweredBy(parts.Count > 3 ? parts[3] : "", "127.0.0.1");
                }
                Push();
                return ("200 OK", StagewatchMessageBytes.AppliedNoRefs(requestId, "✓ speaker confirmed (" + by + ")"), 0);
            }
            if (SpeakerStatus != 0)
            {
                var status = SpeakerStatus;
                var body = status == 409 ? StagewatchMessageBytes.Refused(requestId, SpeakerWords) : StagewatchMessageBytes.Door(SpeakerWords);
                return (status + " Refused", body, status == 429 ? RetryAfter : 0);
            }
            if (clear)
            {
                lock (_gate) Put("", "off", false);
                Push();
                return ("200 OK", StagewatchMessageBytes.AppliedNoRefs(requestId), 0);
            }
            var text = parts[1];
            var flash = parts.Count > 3 ? parts[3] : "off";
            var asksAck = parts.Count > 4 && parts[4] == "ack";
            if (Slow)
            {
                lock (_gate) _slow.Add((text, flash));
                return ("200 OK", StagewatchMessageBytes.Accepted(requestId), 0);
            }
            long signal;
            Task? answer;
            lock (_gate)
            {
                signal = Put(text, flash, asksAck);
                answer = _holdAnswer?.Task;
            }
            Push();
            // It ran and its frame is out; its answer is held until the test lets it go.
            if (answer is not null) await answer.WaitAsync(_cts.Token);
            return ("200 OK", signal > 0 ? StagewatchMessageBytes.Applied(requestId, signal, "the rig's own words") : StagewatchMessageBytes.AppliedNoRefs(requestId, "the rig's own words"), 0);
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

        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            _listener.Stop();
            DropAll();
            _cts.Dispose();
        }
    }

    // ---- the desk's side -----------------------------------------------------------------------------------------------

    /// <summary>The desk linked to the scripted Stagewatch on this machine, with the key or without, and following its stage.</summary>
    private static void Link(TestApp.Booted b, SpeakerStagewatch sw, string key = Key)
    {
        b.Vm.IsSandboxActive = false;
        var services = b.Services;
        var port = sw.Port;
        t_hear = () => ShowBus12AppTests.Hear(services, port, "", "timer");
        t_hear();
        var config = b.Vm.State.Control.Stagewatch;
        config.Host = "127.0.0.1:" + sw.Port;
        config.ApiKey = key;
        config.Enabled = true;
        Dispatcher.UIThread.RunJobs();
        var link = b.Services.Kernel.Stagewatch;
        link.Reconcile();
        if (key.Length > 0) PumpUntil(() => link.Route is { Can: true, Name: Name }, 30000, () => "the route: " + link.Route + " · " + link.Status);
        else PumpUntil(() => link.Sight is { Following: true, Name: Name }, 30000, () => link.Status);
    }

    private static void Unlink(TestApp.Booted b)
    {
        t_hear = null;
        b.Vm.State.Control.Stagewatch.Enabled = false;
        b.Services.Kernel.Stagewatch.Reconcile();
        Dispatcher.UIThread.RunJobs();
    }

    private static ActionResult Say(TestApp.Booted b, string text, string channel = "speaker")
    {
        var result = b.Services.Actions.Execute(new ShowAction(ShowActionKind.StageMessage, channel, text), ActionOrigin.Desk);
        Dispatcher.UIThread.RunJobs();
        return result;
    }

    /// <summary>A message the link takes: Requested — a failure says the reply's own words.</summary>
    private static void Sent(TestApp.Booted b, string text)
    {
        var result = Say(b, text);
        Assert.True(result.Status == ActionStatus.Requested, $"'{text}' was {result.Status}: {result.Message}");
    }

    private static JsonElement Stage(TestApp.Booted b) => JsonDocument.Parse(new CommandRouter(b.Services).StateJson()).RootElement.GetProperty("stage");

    private static (string From, string Ack, string By, string Pending) Speaker(TestApp.Booted b)
    {
        var stage = Stage(b);
        return (stage.GetProperty("speakerFrom").GetString()!, stage.GetProperty("speakerAck").GetString()!, stage.GetProperty("speakerAckBy").GetString()!, stage.GetProperty("pendingSpeaker").GetString()!);
    }

    private static JsonElement Page(TestApp.Booted b) => JsonDocument.Parse(b.Services.Stage.StatusJson()).RootElement;

    private static IReadOnlyList<ShowLogEntry> Rows(TestApp.Booted b) => b.Services.Journal.Tail(200).Where(e => e.Kind == nameof(ShowActionKind.StageMessage)).ToList();

    private static bool Row(TestApp.Booted b, string words) => Rows(b).Any(e => e.Message.Contains(words, StringComparison.Ordinal));

    private static StageMessage Last(TestApp.Booted b) => b.Services.AirState.Stage.Messages.Last();

    /// <summary>
    /// The last message is on the Stagewatch's stage as this desk reads it: its receipt came and a frame names its signal.
    /// The two travel apart — the answer on the call, the frame on the socket — and a clear asks the Stagewatch only of a
    /// message its frame names, so a test that clears next waits for both.
    /// </summary>
    private static void OnTheirStage(TestApp.Booted b)
        => PumpUntil(() => Last(b) is { SwAck: "sent", SwMessageId: > 0 } m && b.Services.FollowedStagewatch.Message.Id == m.SwMessageId,
            20000, () => $"{Last(b).SwAck} #{Last(b).SwMessageId}, the frame names #{b.Services.FollowedStagewatch.Message.Id}");

    // ---- with a node key -----------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void WithANodeKeyTheSpeakersMessageGoesThroughStagewatchAndItsReceiptAndTheAckComeBack()
    {
        var b = TestApp.Boot();
        using var sw = new SpeakerStagewatch();
        try
        {
            var (services, vm, _) = b;
            Link(b, sw);
            var stage = services.Stage;
            var signature = stage.DeckSignature();

            // Requested, never Done: the reply says where it went and that its receipt comes back.
            var said = Say(b, "Wrap up");
            Assert.Equal(ActionStatus.Requested, said.Status);
            Assert.Equal(StageSpeakerWords.ViaReply(Name, "Wrap up"), said.Message);

            // The Stagewatch saw the contract's route, this desk's key and a 13.12.1 id, once.
            PumpUntil(() => sw.Messages.Count == 1);
            var call = sw.Messages[0];
            Assert.Equal(("POST", StagewatchMessageBytes.MessageRoute("Wrap up")), (call.Method, call.Path));
            Assert.Equal("/api/message/Wrap%20up/info/slow/ack", call.Path);
            Assert.Equal("Bearer " + Key, call.Authorization);
            Assert.Matches(new Regex("^p[0-9]+-[A-Za-z0-9]+$"), call.RequestId);
            Assert.True(call.RequestId.Length <= 32);

            // Its receipt binds the message to the Stagewatch's signal: one journal row, STATE and the page following.
            PumpUntil(() => Row(b, "on the speaker's screen (message 1)"), 20000, () => string.Join(" | ", Rows(b).Select(r => r.Message)));
            Assert.Contains(Rows(b), r => r.Outcome == "applied" && r.Message == $"stage message → Stagewatch {Name}: on the speaker's screen (message 1)");
            PumpUntil(() => Speaker(b) == ("stagewatch", "sent", "", "Wrap up"), 20000, () => Speaker(b).ToString());
            Assert.NotEqual(signature, stage.DeckSignature());
            signature = stage.DeckSignature();
            var message = Last(b);
            Assert.Equal((StageSpeakerWords.Via, 1L, "sent", Name, call.RequestId), (message.Via, message.SwMessageId, message.SwAck, message.SwName, message.SwRequestId));
            PumpUntil(() => Page(b).GetProperty("speakerShown").ValueKind == JsonValueKind.Object && Page(b).GetProperty("speakerShown").GetProperty("id").GetString() == "sw:1");
            var page = Page(b);
            Assert.Equal(("Wrap up", "stagewatch", true, true), (page.GetProperty("speakerShown").GetProperty("text").GetString(), page.GetProperty("speakerShown").GetProperty("from").GetString(),
                page.GetProperty("speakerShown").GetProperty("flash").GetBoolean(), page.GetProperty("speakerShown").GetProperty("ack").GetBoolean()));
            Assert.Equal((1L, "sent", ""), (page.GetProperty("follows").GetProperty("messageId").GetInt64(), page.GetProperty("follows").GetProperty("messageAck").GetString(), page.GetProperty("follows").GetProperty("messageAckBy").GetString()));
            Assert.Equal("Wrap up", page.GetProperty("pendingSpeaker").GetProperty("text").GetString());   // it keeps its meaning: this desk's message waiting for its ACK

            // The Eye's Stagewatch node and the assistant's facts say where it went.
            var app = Assert.Single(services.ShowApps(DateTime.UtcNow), a => a.Linked && a.KindWords == "Stagewatch");
            Assert.Equal($"the speaker's message 'Wrap up' is on Stagewatch {Name}'s speaker screen (message 1), waiting for its ACK", app.Speaker);
            Assert.Contains("  " + app.Speaker, ShowBusView.BriefLines(services.ShowApps(DateTime.UtcNow)));
            services.Eye.Refresh();
            Assert.Contains(services.Eye.Graph.Nodes, n => n.Kind == EyeKind.ShowApp && n.Words.Contains(app.Speaker));
            Assert.DoesNotContain("SPEAKER MESSAGE", services.GlanceWords, StringComparison.Ordinal);

            // The page's ACK on sw:1 is forwarded in this desk's name; a second press sends no second ACK.
            var ack = services.Actions.Execute(new ShowAction(ShowActionKind.StageAck, "", "sw:1"), ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, ack.Status);
            var twice = services.Actions.Execute(new ShowAction(ShowActionKind.StageAck, "", "sw:1"), ActionOrigin.Desk);
            Assert.False(twice.Ok);
            PumpUntil(() => sw.Speaker.Count == 2);
            var forwarded = sw.Speaker[1];
            Assert.Equal(StagewatchMessageBytes.AckRoute(1, StageMessagePath.Name(services.Beacon.MachineName)), forwarded.Path);
            Assert.Equal("Bearer " + Key, forwarded.Authorization);
            Assert.NotEqual(call.RequestId, forwarded.RequestId);

            // The frame says done and by whom — the name and the device's address: the message is seen, nothing pends.
            var by = StagewatchMessageBytes.AnsweredBy(StageMessagePath.Name(services.Beacon.MachineName), "127.0.0.1");
            PumpUntil(() => Speaker(b) == ("stagewatch", "done", by, ""), 20000, () => Speaker(b).ToString());
            Assert.NotEqual(signature, stage.DeckSignature());
            Assert.NotNull(message.AckUtc);
            Assert.Contains(Rows(b), r => r.Outcome == "done" && r.Message.StartsWith($"stage message 1 seen by {by} at ", StringComparison.Ordinal));
            PumpUntil(() => Row(b, $"stage message 1: this desk's ACK reached Stagewatch {Name}"));
            Assert.Equal(JsonValueKind.Null, Page(b).GetProperty("speakerShown").ValueKind);       // answered, it leaves the page
            Assert.Equal(JsonValueKind.Null, Page(b).GetProperty("pendingSpeaker").ValueKind);
            Assert.False(services.Actions.Execute(new ShowAction(ShowActionKind.StageAck, "", "sw:1"), ActionOrigin.Desk).Ok);   // answered: nothing to answer
            ShowBus12AppTests.PumpFor(300);
            Assert.Equal(2, sw.Speaker.Count);

            // 30 s on, the frame's three fields go back to nothing with the message still on stage: done is kept, never "unseen".
            sw.ResetSignalFields();
            ShowBus12AppTests.PumpFor(600);
            Assert.Equal(("stagewatch", "done", by, ""), Speaker(b));
            Assert.Equal(JsonValueKind.Null, Page(b).GetProperty("speakerShown").ValueKind);
            Assert.Equal((1L, "done", by), (Page(b).GetProperty("follows").GetProperty("messageId").GetInt64(), Page(b).GetProperty("follows").GetProperty("messageAck").GetString(), Page(b).GetProperty("follows").GetProperty("messageAckBy").GetString()));

            // The Stagewatch's own message is drawn too — one message in the room — and is never this desk's pending.
            var theirs = sw.OperatorMessage("Stand by for Q&A");
            PumpUntil(() => Page(b).GetProperty("speakerShown").ValueKind == JsonValueKind.Object);
            Assert.Equal(($"sw:{theirs}", "Stand by for Q&A", "stagewatch"), (Page(b).GetProperty("speakerShown").GetProperty("id").GetString(), Page(b).GetProperty("speakerShown").GetProperty("text").GetString(), Page(b).GetProperty("speakerShown").GetProperty("from").GetString()));
            Assert.Equal(("stagewatch", "sent", "", ""), Speaker(b));
            sw.SpeakerAnswers("10.0.0.30");
            PumpUntil(() => Speaker(b) == ("stagewatch", "done", "10.0.0.30", ""), 20000, () => Speaker(b).ToString());

            // The key rides only the link's own calls: in no journal row, no log line, no STATE, no page and no reader's words.
            TestApp.FlushFiles(services);
            var everything = string.Join("\n", services.Journal.Tail(500).Select(e => $"{e.Origin}|{e.Kind}|{e.Target}|{e.Outcome}|{e.Message}"))
                             + File.ReadAllText(Path.Combine(b.Dir, "patterns.log"))
                             + new CommandRouter(services).StateJson() + stage.StatusJson() + services.ContextJson() + services.GlanceWords + vm.StatusMessage
                             + string.Join("\n", ShowBusView.BriefLines(services.ShowApps(DateTime.UtcNow)));
            Assert.DoesNotContain(Key, everything, StringComparison.Ordinal);
            Assert.DoesNotContain("swk_", everything, StringComparison.Ordinal);
        }
        finally
        {
            Unlink(b);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AnAcceptedMessageIsBoundByAFrameThatNamesItsWordsAndNeverByAnotherMessageInTheGap()
    {
        var b = TestApp.Boot();
        using var sw = new SpeakerStagewatch();
        try
        {
            var (services, _, _) = b;
            Link(b, sw);
            sw.Slow = true;

            // A slow show: only accepted, no signal — journalled, and nothing says it is on the screen.
            Sent(b, "Lights in five");
            PumpUntil(() => Speaker(b).Ack == "accepted", 20000, () => Speaker(b).ToString());
            Assert.True(Row(b, $"stage message → Stagewatch {Name}: accepted, not yet applied — its show thread is slow"));
            Assert.False(Row(b, "on the speaker's screen"));
            var ours = Last(b);
            Assert.Equal(0, ours.SwMessageId);

            // In the gap Stagewatch's own operator sends a message of its own: a new signal, other words — not this desk's.
            var theirs = sw.OperatorMessage("House open");
            PumpUntil(() => Page(b).GetProperty("speakerShown").ValueKind == JsonValueKind.Object && Page(b).GetProperty("speakerShown").GetProperty("text").GetString() == "House open");
            ShowBus12AppTests.PumpFor(400);
            Assert.Equal((0L, "accepted"), (ours.SwMessageId, ours.SwAck));
            Assert.Equal($"sw:{theirs}", Page(b).GetProperty("speakerShown").GetProperty("id").GetString());
            // Its answer is its own message's, never shown as the ACK of the message this desk sent.
            sw.SpeakerAnswers("10.0.0.30");
            ShowBus12AppTests.PumpFor(400);
            Assert.Null(ours.AckUtc);
            Assert.Equal("accepted", ours.SwAck);

            // The show runs: the frame names a new signal with this desk's words — bound.
            sw.RunShow();
            PumpUntil(() => ours.SwAck == "sent", 20000, () => ours.SwAck);
            Assert.Equal(sw.MessageId, ours.SwMessageId);
            Assert.True(ours.SwMessageId > theirs);
            Assert.True(Row(b, $"stage message → Stagewatch {Name}: on the speaker's screen (message {ours.SwMessageId}) — its frame names it"));
            Assert.Equal(("stagewatch", "sent", "", "Lights in five"), Speaker(b));
            Assert.Single(sw.Messages);                                                       // never sent again

            // Accepted and never named by a frame: unconfirmed once the wait has passed — on this desk's displays, never "failed".
            services.Stage.BindWithin = TimeSpan.FromMilliseconds(300);
            Sent(b, "Doors in two");
            var second = Last(b);
            PumpUntil(() => second.SwAck == "accepted");
            PumpUntil(() =>
            {
                services.Stage.Follow();
                return second.SwAck == "unconfirmed";
            }, 20000, () => second.SwAck);
            Assert.True(Row(b, $"stage message → Stagewatch {Name}: unconfirmed"));
            Assert.Contains("SPEAKER MESSAGE △ UNCONFIRMED", services.GlanceWords, StringComparison.Ordinal);
            // Stagewatch's stage still shows this desk's first message, unanswered; the unconfirmed one arrived here after it.
            Assert.Equal((second.Id, "desk"), (Page(b).GetProperty("speakerShown").GetProperty("id").GetString(), Page(b).GetProperty("speakerShown").GetProperty("from").GetString()));
            Assert.Equal(("desk", "unconfirmed"), (Speaker(b).From, Speaker(b).Ack));
            // And when the show does run it, the frame binds it after all; the first, replaced on that stage, is off the displays.
            sw.RunShow();
            PumpUntil(() => second.SwAck == "sent", 20000, () => second.SwAck);
            Assert.Equal(sw.MessageId, second.SwMessageId);
            PumpUntil(() => ours.SwAck == StageSpeakerWords.Cleared, 20000, () => ours.SwAck);
            Assert.True(Row(b, $"stage message {ours.SwMessageId} left Stagewatch {Name}'s stage unanswered"));
            Assert.Equal(2, sw.Messages.Count);
        }
        finally
        {
            Unlink(b);
            b.Dispose();
        }
    }

    // ---- 13.12.3's other outcomes --------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void ARefusalARateLimitAndATimeoutEachEndInTheirOwnOutcomeAndNoneSendsTheMessageAgain()
    {
        var b = TestApp.Boot();
        using var sw = new SpeakerStagewatch();
        try
        {
            var (services, _, _) = b;
            var link = services.Kernel.Stagewatch;
            link.SpeakerCallWithin = TimeSpan.FromMilliseconds(600);
            Link(b, sw);

            // 409: the show refused it, in Stagewatch's words — on this desk's displays only, and said so everywhere.
            (sw.SpeakerStatus, sw.SpeakerWords) = (409, "The stage is locked by the showcaller.");
            Sent(b, "Wrap up");
            PumpUntil(() => Speaker(b).Ack == "refused", 20000, () => Speaker(b).ToString());
            var refused = Last(b);
            Assert.Contains(Rows(b), r => r.Outcome == "refused" && r.Message == $"stage message → Stagewatch {Name} refused it (The stage is locked by the showcaller.) — shown on this desk's displays only");
            Assert.Equal(("desk", "refused", "", "Wrap up"), Speaker(b));
            Assert.Equal((refused.Id, "desk", true), (Page(b).GetProperty("speakerShown").GetProperty("id").GetString(), Page(b).GetProperty("speakerShown").GetProperty("from").GetString(), Page(b).GetProperty("speakerShown").GetProperty("ack").GetBoolean()));
            Assert.Contains("SPEAKER MESSAGE △ REFUSED BY STAGEWATCH", services.GlanceWords, StringComparison.Ordinal);
            Assert.Contains("refused the speaker's message 'Wrap up' (The stage is locked by the showcaller.)", services.ShowApps(DateTime.UtcNow).Single(a => a.Linked).Speaker, StringComparison.Ordinal);
            // Its ACK on this desk's display is this desk's own, as ever.
            Assert.True(services.Actions.Execute(new ShowAction(ShowActionKind.StageAck, "", refused.Id), ActionOrigin.Desk).Ok);
            Assert.Equal(("desk", "seen"), (Speaker(b).From, Speaker(b).Ack));
            Assert.DoesNotContain("SPEAKER MESSAGE", services.GlanceWords, StringComparison.Ordinal);
            Assert.Single(sw.Messages);

            // 429: refused with its words; not sent again; and no speaker's call leaves before Retry-After has passed —
            // a message meanwhile stays here and says why.
            (sw.SpeakerStatus, sw.SpeakerWords, sw.RetryAfter) = (429, "slow down", 3);
            Sent(b, "Two minutes");
            PumpUntil(() => Last(b).SwAck == "refused", 20000, () => Last(b).SwAck);
            var limitedAt = sw.Messages[^1].AtMs;
            Assert.Equal("rate limited — again in 3 s", Last(b).SwWords);
            Assert.True(Row(b, $"stage message → Stagewatch {Name} refused it (rate limited — again in 3 s)"));
            (sw.SpeakerStatus, sw.SpeakerWords, sw.RetryAfter) = (0, "", 0);
            var held = Say(b, "One minute");
            Assert.Equal(ActionStatus.Done, held.Status);
            Assert.Contains("on this desk's displays only", held.Message, StringComparison.Ordinal);
            Assert.Contains("its rate limit holds this desk's key", held.Message, StringComparison.Ordinal);
            Assert.Equal(2, sw.Messages.Count);
            PumpUntil(() => link.Route.Can, 20000, () => link.Route.ToString());
            Sent(b, "Thirty seconds");
            PumpUntil(() => sw.Messages.Count == 3);
            Assert.True(sw.Messages[^1].AtMs - limitedAt >= 2900, $"the next call left {sw.Messages[^1].AtMs - limitedAt} ms after the rate limit");
            Assert.DoesNotContain(sw.Messages, m => m.Path.Contains("Two%20minutes", StringComparison.Ordinal) && m.AtMs > limitedAt);
            PumpUntil(() => Last(b).SwAck == "sent");

            // A timeout after the request was written: unconfirmed — it may be on the speaker's screen — never refused, never
            // sent again; and when the Stagewatch does run it, its frame binds it.
            sw.HoldNext();
            Sent(b, "Hold for applause");
            var late = Last(b);
            PumpUntil(() => late.SwAck == "unconfirmed", 20000, () => late.SwAck);
            Assert.Contains(Rows(b), r => r.Outcome == "unconfirmed" && r.Message.StartsWith($"stage message → Stagewatch {Name}: unconfirmed (no answer in 0.6 s) — it may be on the speaker's screen", StringComparison.Ordinal));
            Assert.DoesNotContain("refused", Rows(b).Last(r => r.Outcome == "unconfirmed").Message, StringComparison.Ordinal);
            Assert.Equal(("desk", "unconfirmed"), (Speaker(b).From, Speaker(b).Ack));
            var posts = sw.Messages.Count;
            ShowBus12AppTests.PumpFor(1500);
            Assert.Equal(posts, sw.Messages.Count);                                           // not sent again
            sw.Release();
            PumpUntil(() => late.SwAck == "sent", 20000, () => late.SwAck);
            Assert.True(Row(b, $"(message {late.SwMessageId}) — its frame names it"));
            Assert.Equal(posts, sw.Messages.Count);
        }
        finally
        {
            sw.Release();
            Unlink(b);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AnAnswerLostAfterTheMessageRanIsBoundByTheFrameThatCameWhileItWasAwaited()
    {
        var b = TestApp.Boot();
        using var sw = new SpeakerStagewatch();
        try
        {
            var (services, _, _) = b;
            var link = services.Kernel.Stagewatch;
            var stack = services.CueStack;
            var clock = new DateTime(2026, 11, 11, 19, 0, 0, DateTimeKind.Utc);
            stack.NowUtc = () => clock;
            link.SpeakerCallWithin = TimeSpan.FromMilliseconds(600);
            Link(b, sw);

            // The usual way a message ends unconfirmed: it ran on the Stagewatch, its frame reached this desk, and the HTTP
            // answer was lost. The frame is older than the timeout — and names the message all the same: unconfirmed, then
            // bound, the cue's row Done on the bind, and the speaker's ACK there read from then on. Nothing is sent again.
            sw.HoldNextAnswer();
            Assert.Equal(ActionStatus.Requested, Go(b, "01", "Lost answer").Status);
            var lost = Last(b);
            PumpUntil(() => lost.SwAck == "sent", 20000, () => $"{lost.SwAck} ({lost.SwWords})");
            Assert.Equal(sw.MessageId, lost.SwMessageId);
            var outcomes = Rows(b).Select(r => r.Outcome).ToList();
            Assert.True(outcomes.IndexOf("unconfirmed") >= 0 && outcomes.IndexOf("unconfirmed") < outcomes.LastIndexOf("applied"), "unconfirmed, then bound: " + string.Join(", ", outcomes));
            Assert.Contains(Rows(b), r => r.Outcome == "unconfirmed" && r.Message.StartsWith($"stage message → Stagewatch {Name}: unconfirmed (no answer in 0.6 s)", StringComparison.Ordinal));
            Assert.True(Row(b, $"stage message → Stagewatch {Name}: on the speaker's screen (message {lost.SwMessageId}) — its frame names it"));
            PumpUntil(() => CueRow(b, "01").Outcome == CueOutcome.Done, 20000, () => CueRow(b, "01").OutcomeWords);
            Assert.Equal(("stagewatch", "sent", "", "Lost answer"), Speaker(b));
            Assert.DoesNotContain("SPEAKER MESSAGE", services.GlanceWords, StringComparison.Ordinal);
            sw.SpeakerAnswers("10.0.0.30");
            PumpUntil(() => Speaker(b) == ("stagewatch", "done", "10.0.0.30", ""), 20000, () => Speaker(b).ToString());
            sw.Release();
            ShowBus12AppTests.PumpFor(300);
            Assert.Single(sw.Messages);

            // The same when the link drops with the answer still owed: the Stagewatch's message, kept across the redial with
            // the instant it first arrived, binds this desk's.
            link.SpeakerCallWithin = TimeSpan.FromSeconds(30);
            sw.HoldNextAnswer();
            Sent(b, "Dropped answer");
            var dropped = Last(b);
            PumpUntil(() => link.Sight.Message.Text == "Dropped answer", 20000, () => link.Sight.Message.ToString());
            Assert.Equal("requested", dropped.SwAck);
            sw.DropAll();
            PumpUntil(() => dropped.SwAck == "sent", 40000, () => $"{dropped.SwAck} ({dropped.SwWords}) · {link.Status}");
            Assert.Contains(Rows(b), r => r.Outcome == "unconfirmed" && r.Message.Contains($"before Stagewatch {Name} answered", StringComparison.Ordinal));
            Assert.Equal(sw.MessageId, dropped.SwMessageId);
            Assert.Equal(2, sw.Messages.Count);
        }
        finally
        {
            sw.Release();
            Unlink(b);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AMessageNeverWrittenIsNotSentWithWhyAndANewerOneReplacesOneNotYetSent()
    {
        var b = TestApp.Boot();
        using var sw = new SpeakerStagewatch();
        try
        {
            var (services, _, _) = b;
            var link = services.Kernel.Stagewatch;
            Link(b, sw);

            // Two messages while the first is held: the third replaces the second before it is sent — superseded, journalled,
            // off the displays — and the Stagewatch sees the first and the third only.
            sw.HoldNext();
            Sent(b, "First");
            var first = Last(b);
            PumpUntil(() => sw.Held == 1);
            Sent(b, "Second");
            var second = Last(b);
            Sent(b, "Third");
            var third = Last(b);
            PumpUntil(() => second.SwAck == StageSpeakerWords.Cleared, 20000, () => second.SwAck);
            Assert.NotNull(second.AckUtc);
            Assert.Contains(Rows(b), r => r.Outcome == "not-sent" && r.Message == StageSpeakerWords.SupersededRow);
            sw.Release();
            PumpUntil(() => third.SwAck == "sent", 20000, () => third.SwAck);
            Assert.Equal(new[] { StagewatchMessageBytes.MessageRoute("First"), StagewatchMessageBytes.MessageRoute("Third") }, sw.Messages.Select(m => m.Path));
            PumpUntil(() => first.SwAck == StageSpeakerWords.Cleared, 20000, () => first.SwAck);   // replaced on that stage by the third

            // The show goes stuck before the next request is written: not sent, with why, on this desk's displays.
            sw.HoldNext();
            Sent(b, "In flight");
            var flight = Last(b);
            PumpUntil(() => sw.Held == 1);
            Sent(b, "Behind it");
            var behind = Last(b);
            sw.Stuck = true;
            sw.Push();
            PumpUntil(() => link.Sight.StuckLive, 20000, () => link.Status);
            sw.Release();
            PumpUntil(() => behind.SwAck == "not-sent", 20000, () => behind.SwAck);
            Assert.Equal($"Stagewatch {Name} says its show is stuck", behind.SwWords);
            Assert.Contains(Rows(b), r => r.Outcome == "not-sent" && r.Message == $"stage message → Stagewatch {Name}: not sent (Stagewatch {Name} says its show is stuck) — shown on this desk's displays only");
            Assert.DoesNotContain(sw.Messages, m => m.Path.Contains("Behind", StringComparison.Ordinal));
            Assert.Equal(behind.Id, Page(b).GetProperty("speakerShown").GetProperty("id").GetString());
            // While it is stuck a new message stays here and says so.
            var stuck = Say(b, "While stuck");
            Assert.Equal(ActionStatus.Done, stuck.Status);
            Assert.Equal(StageSpeakerWords.DeskOnlyReply(Name, "While stuck", $"Stagewatch {Name} says its show is stuck"), stuck.Message);
            sw.Stuck = false;
            sw.Push();
            PumpUntil(() => link.Route.Can, 20000, () => link.Route.ToString());
            PumpUntil(() => flight.SwAck != "requested");

            // A hold begun before the request is written (the first call's answer refuses the key): not sent, with why.
            sw.HoldNext();
            Sent(b, "Refused key");
            PumpUntil(() => sw.Held == 1);
            Sent(b, "After the hold");
            var after = Last(b);
            (sw.SpeakerStatus, sw.SpeakerWords) = (401, "unknown or revoked API key");
            sw.Release();
            PumpUntil(() => after.SwAck == "not-sent", 20000, () => after.SwAck);
            // The hold is the reason — or, when the link was already let go for it, that it dropped: never written either way.
            Assert.Contains(after.SwWords, new[] { "this desk's key is refused there", $"the link to Stagewatch {Name} dropped before it was sent" });
            Assert.DoesNotContain(sw.Messages, m => m.Path.Contains("After", StringComparison.Ordinal));
            (sw.SpeakerStatus, sw.SpeakerWords) = (0, "");
            var refusedKey = Say(b, "Key held");
            Assert.Equal(ActionStatus.Done, refusedKey.Status);
            Assert.Contains("on this desk's displays only", refusedKey.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(sw.Messages, m => m.Path.Contains("Key%20held", StringComparison.Ordinal));
        }
        finally
        {
            sw.Release();
            Unlink(b);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void WhenTheLinkGoesWhatWasInFlightIsUnconfirmedWhatWasNeverWrittenIsNotSentAndNothingIsSentLate()
    {
        var b = TestApp.Boot();
        var gone = new SpeakerStagewatch();
        using var sw = new SpeakerStagewatch();
        try
        {
            // The Stagewatch goes away with one message in flight and one behind it: the first may have run — unconfirmed —
            // and the second was never written — not sent, with why. Both are on this desk's displays.
            Link(b, gone);
            gone.HoldNext();
            Sent(b, "In flight");
            var flight = Last(b);
            PumpUntil(() => gone.Held == 1);
            Sent(b, "Never written");
            var never = Last(b);
            gone.GoAway();
            PumpUntil(() => never.SwAck == "not-sent" && flight.SwAck == "unconfirmed", 30000, () => $"{flight.SwAck} ({flight.SwWords}) / {never.SwAck} ({never.SwWords})");
            Assert.Contains(never.SwWords, new[] { $"the link to Stagewatch {Name} dropped before it was sent", $"no connection to Stagewatch {Name} could be made" });
            Assert.Contains(Rows(b), r => r.Outcome == "not-sent" && r.Message.StartsWith($"stage message → Stagewatch {Name}: not sent (", StringComparison.Ordinal) && r.Message.EndsWith("shown on this desk's displays only", StringComparison.Ordinal));
            Assert.Contains(Rows(b), r => r.Outcome == "unconfirmed" && r.Message.Contains("it may be on the speaker's screen", StringComparison.Ordinal));
            Assert.Single(gone.Messages);
            Assert.Equal(never.Id, b.Services.Stage.SpeakerShown()!.Id);                       // the later of the two on this desk's displays
            Unlink(b);

            // The link is turned off with a message behind one in flight, and on again: what was never written is not sent —
            // and is never sent late on the next connection.
            Link(b, sw);
            sw.HoldNext();
            Sent(b, "Held");
            PumpUntil(() => sw.Held == 1);
            Sent(b, "Behind");
            var behind = Last(b);
            Unlink(b);
            PumpUntil(() => behind.SwAck == "not-sent", 20000, () => behind.SwAck);
            Assert.Equal($"the link to Stagewatch {Name} dropped before it was sent", behind.SwWords);
            sw.Release();
            Link(b, sw);
            ShowBus12AppTests.PumpFor(1000);
            Assert.Equal(new[] { StagewatchMessageBytes.MessageRoute("Held") }, sw.Messages.Select(m => m.Path));
            Assert.Equal("not-sent", behind.SwAck);
        }
        finally
        {
            gone.Release();
            sw.Release();
            Unlink(b);
            gone.Dispose();
            b.Dispose();
        }
    }

    // ---- when the link cannot carry it ---------------------------------------------------------------------------------

    [AvaloniaFact]
    public void WhenTheLinkCannotCarryItTheMessageStaysOnThisDeskWithTheReason()
    {
        var b = TestApp.Boot();
        using var sw = new SpeakerStagewatch();
        try
        {
            var (services, vm, _) = b;
            var link = services.Kernel.Stagewatch;

            // No Stagewatch linked: this desk's own message, as ever.
            var alone = Say(b, "Alone");
            Assert.Equal((ActionStatus.Done, "To the speaker: 'Alone' — a receipt comes when the display's ACK is pressed."), (alone.Status, alone.Message));
            Assert.Equal(("desk", "sent", "", "Alone"), Speaker(b));
            Assert.Equal("", services.Stage.SpeakerGlance);

            // Linked and following with no key: Done, on this desk's displays, the reason said; nothing posted. A cue's step
            // lands the same way — a message always lands somewhere.
            Link(b, sw, key: "");
            var keyless = Say(b, "No key");
            Assert.Equal(ActionStatus.Done, keyless.Status);
            Assert.Equal(StageSpeakerWords.DeskOnlyReply(Name, "No key", "the link holds no node key"), keyless.Message);
            Assert.Equal(("", ""), (Last(b).Via, Last(b).SwAck));
            Assert.Equal(("desk", "sent", "", "No key"), Speaker(b));
            Assert.Equal((Last(b).Id, "desk"), (Page(b).GetProperty("speakerShown").GetProperty("id").GetString(), Page(b).GetProperty("speakerShown").GetProperty("from").GetString()));
            var cue = new RunCueConfig { Number = "01", Name = "Wrap" };
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.StageMessage, Target = "speaker", Value = "From a cue" });
            CueStacks.Caller(vm.State).Cues.Add(cue);
            var fired = services.Actions.FireCue(cue, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Done, fired.Status);
            Assert.Equal("From a cue", services.Stage.Pending("speaker")!.Text);
            Assert.Empty(sw.Speaker);
            // The Stagewatch's own message there is the later: the page draws it; this desk's ACK cannot be forwarded without a
            // key, so the page offers none and says where the message is answered.
            var theirs = sw.OperatorMessage("From Stagewatch");
            PumpUntil(() => Page(b).GetProperty("speakerShown").GetProperty("from").GetString() == "stagewatch");
            Assert.Equal((false, StageSpeakerWords.NoAck(Name, "the link holds no node key")),
                (Page(b).GetProperty("speakerShown").GetProperty("ack").GetBoolean(), Page(b).GetProperty("speakerShown").GetProperty("why").GetString()));
            var ack = services.Actions.Execute(new ShowAction(ShowActionKind.StageAck, "", $"sw:{theirs}"), ActionOrigin.Desk);
            Assert.False(ack.Ok);
            Assert.Contains("the link holds no node key", ack.Message, StringComparison.Ordinal);
            Assert.Empty(sw.Speaker);
            sw.OperatorClear();
            Unlink(b);

            // With the key: the crew's channel is this desk's own; FOLLOW off sends as before; a stuck show keeps it here.
            Link(b, sw);
            var crew = Say(b, "Mic 2 is live", "crew");
            Assert.Equal((ActionStatus.Done, StageSpeakerWords.DeskOnlyReply(Name, "Mic 2 is live", StageSpeakerWords.CrewWhy, "crew")), (crew.Status, crew.Message));
            Assert.Equal("", Last(b).Via);
            vm.State.Control.Stagewatch.FollowStage = false;
            Dispatcher.UIThread.RunJobs();
            var unfollowed = Say(b, "Not following");
            Assert.Equal((ActionStatus.Done, StageSpeakerWords.DeskOnlyReply(Name, "Not following", StageSpeakerWords.NotFollowedWhy)), (unfollowed.Status, unfollowed.Message));
            Assert.Equal("", Last(b).Via);
            Assert.Empty(sw.Speaker);
            vm.State.Control.Stagewatch.FollowStage = true;
            Dispatcher.UIThread.RunJobs();
            PumpUntil(() => link.Route.Can, 30000, () => link.Route.ToString());
            // Words the route cannot carry stay here too.
            var tooLong = Say(b, new string('x', 501));
            Assert.Equal(ActionStatus.Done, tooLong.Status);
            Assert.Contains("it is longer than the 500 characters Stagewatch takes", tooLong.Message, StringComparison.Ordinal);
            Assert.Empty(sw.Speaker);

            // A key a 403 has shown may read but not send: the first message is refused in Stagewatch's words, the next stays
            // here without a post.
            (sw.SpeakerStatus, sw.SpeakerWords) = (403, $"This key may read the show; it cannot send messages ({Key}).");
            Sent(b, "First try");
            PumpUntil(() => Last(b).SwAck == "refused", 20000, () => Last(b).SwAck);
            Assert.True(Row(b, $"stage message → Stagewatch {Name} refused it (This key may read the show; it cannot send messages ("));
            // A door that echoes the bearer puts no copy of it anywhere this desk writes: the journal, the log, STATE, the page.
            PumpUntil(() => File.ReadAllText(Path.Combine(b.Dir, "patterns.log")).Contains("refused this desk's key a message to its speaker", StringComparison.Ordinal));
            TestApp.FlushFiles(services);
            var written = string.Join("\n", services.Journal.Tail(500).Select(e => $"{e.Origin}|{e.Kind}|{e.Target}|{e.Outcome}|{e.Message}"))
                          + File.ReadAllText(Path.Combine(b.Dir, "patterns.log")) + new CommandRouter(services).StateJson() + services.Stage.StatusJson()
                          + services.ContextJson() + vm.StatusMessage + Last(b).SwWords;
            Assert.DoesNotContain(Key, written, StringComparison.Ordinal);
            Assert.DoesNotContain("swk_", written, StringComparison.Ordinal);
            var second = Say(b, "Second try");
            Assert.Equal(ActionStatus.Done, second.Status);
            Assert.Equal(StageSpeakerWords.DeskOnlyReply(Name, "Second try", "the key may read but not send messages"), second.Message);
            Assert.Single(sw.Messages);
            // A 403 holds nothing else of the key (13.7.3): the link's key stands as it was.
            Assert.False(link.Key.Fault);
            Assert.Equal(TimeSpan.Zero, link.KeyHeld);
        }
        finally
        {
            Unlink(b);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AQuietStagewatchKeepsTheMessageOnThisDeskAndSaysSo()
    {
        var b = TestApp.Boot();
        using var sw = new SpeakerStagewatch();
        try
        {
            var link = b.Services.Kernel.Stagewatch;
            Link(b, sw);
            // It stops answering on its socket: quiet after 6 s of silence, the socket still open.
            sw.Mute = true;
            PumpUntil(() => link.Route.Why == $"Stagewatch {Name} is quiet", 30000, () => link.Route.ToString());
            var quiet = Say(b, "Anyone there");
            Assert.Equal(ActionStatus.Done, quiet.Status);
            Assert.Equal(StageSpeakerWords.DeskOnlyReply(Name, "Anyone there", $"Stagewatch {Name} is quiet"), quiet.Message);
            Assert.Equal(("", ""), (Last(b).Via, Last(b).SwAck));
            Assert.Empty(sw.Speaker);
        }
        finally
        {
            Unlink(b);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AKeyThatWaitsForTrustKeepsTheMessageOnThisDesk()
    {
        // Off this machine (loopback plays another machine), a certificate pinned at first use carries no key until a person's
        // Trust: the speaker's message stays here and says what it waits for; nothing keyed leaves this desk.
        var b = TestApp.Boot();
        using var cert = ShowBus12AppTests.Cert("Hall A");
        var sw = new ShowBus12AppTests.FakeStagewatch(ShowBus12AppTests.Frame, cert);
        var link = b.Services.Kernel.Stagewatch;
        try
        {
            b.Vm.IsSandboxActive = false;
            link.IsLocal = _ => false;
            var config = b.Vm.State.Control.Stagewatch;
            config.Host = "127.0.0.1:" + sw.Port;
            config.ApiKey = Key;
            config.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();
            PumpUntil(() => link.TrustFingerprint.Length > 0, 40000, () => link.Status);

            var said = Say(b, "Wrap up");
            Assert.Equal(ActionStatus.Done, said.Status);
            Assert.Contains("on this desk's displays only", said.Message, StringComparison.Ordinal);
            Assert.Contains("the key waits for Trust", said.Message, StringComparison.Ordinal);
            Assert.Equal(("desk", "sent"), (Speaker(b).From, Speaker(b).Ack));
            ShowBus12AppTests.PumpFor(300);
            Assert.DoesNotContain(sw.Authorizations, a => a.Contains("swk_", StringComparison.Ordinal));
        }
        finally
        {
            Unlink(b);
            link.IsLocal = IPAddress.IsLoopback;
            sw.Dispose();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AnAckRefusedAtStagewatchsDoorIsShownAndAMessageWithNoSignalSaysNoAcknowledgementFollows()
    {
        var b = TestApp.Boot();
        using var sw = new SpeakerStagewatch();
        try
        {
            var (services, vm, _) = b;
            Link(b, sw);

            // Under crew links required, a show app's forwarded ACK is refused at Stagewatch's door: this desk shows its words,
            // the message stays unanswered, and the ACK may be pressed again.
            var theirs = sw.OperatorMessage("Please wrap up");
            PumpUntil(() => services.Stage.SpeakerShown() is { Ack: true } shown && shown.Id == $"sw:{theirs}");
            (sw.AckStatus, sw.AckWords) = (403, StagewatchMessageBytes.CrewLinks);
            Assert.Equal(ActionStatus.Requested, services.Actions.Execute(new ShowAction(ShowActionKind.StageAck, "", $"sw:{theirs}"), ActionOrigin.Desk).Status);
            PumpUntil(() => Row(b, $"stage message {theirs}: Stagewatch {Name} refused this desk's ACK ({StagewatchMessageBytes.CrewLinks})"), 20000, () => string.Join(" | ", Rows(b).Select(r => r.Message)));
            Assert.Contains(StagewatchMessageBytes.CrewLinks, vm.StatusMessage, StringComparison.Ordinal);
            Assert.Equal(("stagewatch", "sent", "", ""), Speaker(b));
            Assert.False(services.Kernel.Stagewatch.Key.Fault);                                // an ACK's 403 holds nothing of the key (13.7.3)
            Assert.True(services.Kernel.Stagewatch.Route.Can);
            // The display that pressed is told: the button gives way to Stagewatch's words, and no second ACK leaves for that signal.
            var held = services.Stage.SpeakerShown()!;
            Assert.Equal(($"sw:{theirs}", false, StageSpeakerWords.NoAck(Name, StageSpeakerWords.AckRefusedWhy(StagewatchMessageBytes.CrewLinks))), (held.Id, held.Ack, held.Why));
            Assert.Equal(held.Why, Page(b).GetProperty("speakerShown").GetProperty("why").GetString());
            var pressed = sw.Speaker.Count;
            var again = services.Actions.Execute(new ShowAction(ShowActionKind.StageAck, "", $"sw:{theirs}"), ActionOrigin.Desk);
            Assert.False(again.Ok);
            Assert.Contains(StagewatchMessageBytes.CrewLinks, again.Message, StringComparison.Ordinal);
            Assert.Equal(pressed, sw.Speaker.Count);
            // The frame moves — another message on that stage: its ACK is offered and, the door now open, taken.
            (sw.AckStatus, sw.AckWords) = (0, "");
            theirs = sw.OperatorMessage("Please wrap up now");
            PumpUntil(() => services.Stage.SpeakerShown() is { Ack: true } next && next.Id == $"sw:{theirs}", 20000, () => services.Stage.SpeakerShown()?.ToString() ?? "nothing shown");
            Assert.Equal(ActionStatus.Requested, services.Actions.Execute(new ShowAction(ShowActionKind.StageAck, "", $"sw:{theirs}"), ActionOrigin.Desk).Status);
            PumpUntil(() => Speaker(b).Ack == "done", 20000, () => Speaker(b).ToString());
            // Answered already, a late ACK from another display changes nothing and is refused here, unsent.
            var posts = sw.Speaker.Count;
            Assert.False(services.Actions.Execute(new ShowAction(ShowActionKind.StageAck, "", $"sw:{theirs}"), ActionOrigin.Desk).Ok);
            Assert.False(services.Actions.Execute(new ShowAction(ShowActionKind.StageAck, "", "sw:999"), ActionOrigin.Desk).Ok);
            Assert.Equal(posts, sw.Speaker.Count);

            // A Stagewatch with no message signals answers applied with no refs.messageId: on the screen, and no
            // acknowledgement to follow — said so, bound to no signal, and cleared when it leaves that stage.
            sw.NoSignals = true;
            Sent(b, "No signal");
            var plain = Last(b);
            PumpUntil(() => plain.SwAck == "sent", 20000, () => plain.SwAck);
            Assert.Equal(0, plain.SwMessageId);
            Assert.True(Row(b, $"stage message → Stagewatch {Name}: on the speaker's screen — it names no signal, so no acknowledgement follows"));
            PumpUntil(() => services.Stage.SpeakerShown() is { Id: "sw:0", Ack: false, Text: "No signal" });
            sw.OperatorClear();
            PumpUntil(() => plain.SwAck == StageSpeakerWords.Cleared, 20000, () => plain.SwAck);
            Assert.Null(services.Stage.SpeakerShown());
        }
        finally
        {
            Unlink(b);
            b.Dispose();
        }
    }

    // ---- STAGE CLEAR ---------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void StageClearClearsOnlyWhatThisDeskPostedAndSaysWhenStagewatchsOwnStays()
    {
        var b = TestApp.Boot();
        using var sw = new SpeakerStagewatch();
        try
        {
            var (services, _, _) = b;
            Link(b, sw);

            // The Stagewatch's own message is on: no clearmessage is posted, and the words say it stays.
            sw.OperatorMessage("Their own");
            PumpUntil(() => Speaker(b) == ("stagewatch", "sent", "", ""), 20000, () => Speaker(b).ToString());
            var kept = services.Actions.Execute(new ShowAction(ShowActionKind.StageClear, "", ""), ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Done, kept.Status);
            Assert.EndsWith($"Stagewatch {Name}'s own message stays — clear it there.", kept.Message, StringComparison.Ordinal);
            ShowBus12AppTests.PumpFor(300);
            Assert.DoesNotContain(sw.Calls, c => c.Path == "/api/clearmessage");
            Assert.Equal("Their own", Page(b).GetProperty("speakerShown").GetProperty("text").GetString());

            // This desk's message is on: clearmessage is posted with the key and an id, its receipt journalled; the stage is clear.
            Sent(b, "Ours");
            PumpUntil(() => Last(b).SwAck == "sent");
            PumpUntil(() => Page(b).GetProperty("speakerShown").ValueKind == JsonValueKind.Object && Page(b).GetProperty("speakerShown").GetProperty("text").GetString() == "Ours");
            var cleared = services.Actions.Execute(new ShowAction(ShowActionKind.StageClear, "", ""), ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, cleared.Status);
            PumpUntil(() => sw.Calls.Any(c => c.Path == "/api/clearmessage"));
            var call = sw.Calls.Single(c => c.Path == "/api/clearmessage");
            Assert.Equal(("POST", "Bearer " + Key), (call.Method, call.Authorization));
            Assert.Matches(new Regex("^p[0-9]+-[A-Za-z0-9]+$"), call.RequestId);
            PumpUntil(() => Row(b, $"stage clear → Stagewatch {Name}: this desk's message cleared from the speaker's screen"));
            PumpUntil(() => Page(b).GetProperty("speakerShown").ValueKind == JsonValueKind.Null);
            Assert.Equal(("", "", "", ""), Speaker(b));
            Assert.Equal(StageSpeakerWords.Cleared, Last(b).SwAck);

            // Answered, this desk's message is off its own list and still on the Stagewatch's stage — which keeps an answered
            // message until it is cleared: STAGE CLEAR clears it there, and still does once the frame's three fields have gone
            // back to nothing.
            int Clears() => sw.Calls.Count(c => c.Path == "/api/clearmessage");
            foreach (var reset in new[] { false, true })
            {
                Sent(b, reset ? "Answered, fields reset" : "Answered");
                var answered = Last(b);
                PumpUntil(() => answered.SwAck == "sent", 20000, () => answered.SwAck);
                sw.SpeakerAnswers("10.0.0.30");
                PumpUntil(() => answered.SwAck == "done", 20000, () => answered.SwAck);
                if (reset)
                {
                    sw.ResetSignalFields();
                    ShowBus12AppTests.PumpFor(500);
                }
                var before = Clears();
                var there = services.Actions.Execute(new ShowAction(ShowActionKind.StageClear, "", ""), ActionOrigin.Desk);
                Assert.Equal(ActionStatus.Requested, there.Status);
                Assert.Equal($"Nothing on the displays to clear. Stagewatch {Name}'s speaker screen is asked to clear this desk's message — its receipt comes back here.", there.Message);
                PumpUntil(() => Clears() == before + 1, 20000, () => "no clearmessage was posted for an answered message");
                PumpUntil(() => !services.Kernel.Stagewatch.Sight.Message.On, 20000, () => services.Kernel.Stagewatch.Sight.Message.ToString());
            }
            // With nothing of this desk's there, nothing is asked.
            var posted = Clears();
            var nothing = services.Actions.Execute(new ShowAction(ShowActionKind.StageClear, "", ""), ActionOrigin.Desk);
            Assert.Equal((ActionStatus.Done, "Nothing on the displays to clear."), (nothing.Status, nothing.Message));

            // A clear behind a message still on its way is written only if that message gets there. Refused: the clear is
            // dropped unasked, and the Stagewatch's own message — the one on its stage — stays.
            sw.OperatorMessage("Their own again");
            PumpUntil(() => services.Kernel.Stagewatch.Sight.Message.Text == "Their own again");
            (sw.SpeakerStatus, sw.SpeakerWords) = (409, "The stage is locked by the showcaller.");
            sw.HoldNext();
            Sent(b, "Refused on its way");
            PumpUntil(() => sw.Held == 1);
            var behind = services.Actions.Execute(new ShowAction(ShowActionKind.StageClear, "", ""), ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, behind.Status);
            Assert.Contains($"still on its way to Stagewatch {Name}", behind.Message, StringComparison.Ordinal);
            sw.Release();
            PumpUntil(() => Rows(b).Any(r => r.Target == "speaker" && r.Message == $"stage clear → Stagewatch {Name}: not asked — {StageSpeakerWords.NothingToClearWhy}"), 20000,
                () => string.Join(" | ", Rows(b).Select(r => r.Message)));
            ShowBus12AppTests.PumpFor(300);
            Assert.Equal(posted, Clears());
            Assert.Equal("Their own again", services.Kernel.Stagewatch.Sight.Message.Text);
            (sw.SpeakerStatus, sw.SpeakerWords) = (0, "");

            // Applied: the clear goes behind it, in that order, and takes it off.
            sw.HoldNext();
            Sent(b, "Cleared on its way");
            PumpUntil(() => sw.Held == 1);
            Assert.Equal(ActionStatus.Requested, services.Actions.Execute(new ShowAction(ShowActionKind.StageClear, "", ""), ActionOrigin.Desk).Status);
            sw.Release();
            PumpUntil(() => Clears() == posted + 1, 20000, () => "the clear behind an applied message was not posted");
            var order = sw.Speaker.Select(c => c.Path).ToList();
            Assert.True(order.LastIndexOf("/api/clearmessage") > order.IndexOf(StagewatchMessageBytes.MessageRoute("Cleared on its way")), "the clear follows the message it clears");
            PumpUntil(() => !services.Kernel.Stagewatch.Sight.Message.On, 20000, () => services.Kernel.Stagewatch.Sight.Message.ToString());
        }
        finally
        {
            sw.Release();
            Unlink(b);
            b.Dispose();
        }
    }

    // ---- a cue's row ---------------------------------------------------------------------------------------------------

    private static CueExecutionRecord CueRow(TestApp.Booted b, string number) => b.Services.CueStack.History.First(r => r.Number == number);

    private static ActionResult Go(TestApp.Booted b, string number, string words)
    {
        var cue = new RunCueConfig { Number = number, Name = "Message" };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.StageMessage, Target = "speaker", Value = words });
        CueStacks.Caller(b.Vm.State).Cues.Add(cue);
        var stack = b.Services.CueStack;
        if (!stack.Armed) stack.SetArmed(true, ActionOrigin.Desk);
        stack.Standby(cue.Id);
        var result = stack.Go(ActionOrigin.Desk, nowUtc: stack.NowUtc().AddSeconds(1));
        Dispatcher.UIThread.RunJobs();
        return result;
    }

    private static ActionResult GoClear(TestApp.Booted b, string number)
    {
        var cue = new RunCueConfig { Number = number, Name = "Clear" };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.StageClear });
        CueStacks.Caller(b.Vm.State).Cues.Add(cue);
        var stack = b.Services.CueStack;
        if (!stack.Armed) stack.SetArmed(true, ActionOrigin.Desk);
        stack.Standby(cue.Id);
        var result = stack.Go(ActionOrigin.Desk, nowUtc: stack.NowUtc().AddSeconds(1));
        Dispatcher.UIThread.RunJobs();
        return result;
    }

    [AvaloniaFact]
    public void ACuesRowSettlesWhenItsMessageIsClearedHereAndACuesClearOnItsOwnReceipt()
    {
        var b = TestApp.Boot();
        using var sw = new SpeakerStagewatch();
        try
        {
            var (services, _, _) = b;
            var stack = services.CueStack;
            var clock = new DateTime(2026, 11, 11, 19, 0, 0, DateTimeKind.Utc);
            stack.NowUtc = () => clock;
            Link(b, sw);

            // Accepted, then cleared on this desk before any frame names it: the row settles on the clear, in its words — it
            // is not left Requested for good, and the clock had no part in it.
            sw.Slow = true;
            Assert.Equal(ActionStatus.Requested, Go(b, "01", "Accepted then cleared").Status);
            PumpUntil(() => CueRow(b, "01").Waiting.Length > 0, 20000, () => CueRow(b, "01").OutcomeWords);
            var said = services.Actions.Execute(new ShowAction(ShowActionKind.StageClear, "", ""), ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Done, said.Status);
            Assert.Contains("may still reach its speaker's screen", said.Message, StringComparison.Ordinal);
            Assert.Equal((CueOutcome.Done, 0), (CueRow(b, "01").Outcome, CueRow(b, "01").Pending));
            Assert.Contains(StageSpeakerWords.ClearedFirst, CueRow(b, "01").Detail, StringComparison.Ordinal);
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == "CueSettled" && e.Target == "01 Message" && e.Outcome == "Done" && e.Message == StageSpeakerWords.ClearedFirst);
            sw.Slow = false;

            // A cue's STAGE CLEAR of this desk's message on the Stagewatch's stage: Requested, one receipt awaited, never Done
            // by the clock — Done on the clear's receipt.
            Sent(b, "On their stage");
            OnTheirStage(b);
            sw.HoldNext();
            clock = clock.AddSeconds(5);
            var fired = GoClear(b, "02");
            Assert.Equal(ActionStatus.Requested, fired.Status);
            Assert.Equal((CueOutcome.Requested, 1), (CueRow(b, "02").Outcome, CueRow(b, "02").Pending));
            PumpUntil(() => sw.Held == 1);
            clock = clock.AddSeconds(14);
            stack.Poll();
            Assert.Equal(CueOutcome.Requested, CueRow(b, "02").Outcome);                 // past the 12 s window: the clock settles nothing
            sw.Release();
            PumpUntil(() => CueRow(b, "02").Outcome == CueOutcome.Done, 20000, () => CueRow(b, "02").OutcomeWords);
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == "CueSettled" && e.Target == "02 Clear" && e.Outcome == "Done" && e.Message == $"Stagewatch {Name}'s speaker screen is cleared of this desk's message");

            // Refused: FailedLate, with the Stagewatch's words — the message may still be on its screen.
            Sent(b, "On their stage again");
            OnTheirStage(b);
            (sw.SpeakerStatus, sw.SpeakerWords) = (409, "The stage is locked by the showcaller.");
            clock = clock.AddSeconds(5);
            Assert.Equal(ActionStatus.Requested, GoClear(b, "03").Status);
            PumpUntil(() => CueRow(b, "03").Outcome == CueOutcome.FailedLate, 20000, () => CueRow(b, "03").OutcomeWords);
            Assert.Contains("was not cleared (The stage is locked by the showcaller.)", CueRow(b, "03").Detail, StringComparison.Ordinal);
            Assert.True(stack.Armed);
        }
        finally
        {
            sw.Release();
            Unlink(b);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ACuesRowSettlesOnTheMessagesReceiptAndNeverOnTheClock()
    {
        var b = TestApp.Boot();
        using var sw = new SpeakerStagewatch();
        try
        {
            var (services, _, _) = b;
            var stack = services.CueStack;
            var clock = new DateTime(2026, 11, 11, 19, 0, 0, DateTimeKind.Utc);
            stack.NowUtc = () => clock;
            services.Kernel.Stagewatch.SpeakerCallWithin = TimeSpan.FromMilliseconds(600);
            Link(b, sw);

            // Applied: Requested at the GO — one receipt awaited — and Done only once the receipt says so.
            sw.HoldNext();
            var fired = Go(b, "01", "Applied");
            Assert.Equal(ActionStatus.Requested, fired.Status);
            Assert.Contains("1 receipt awaited", fired.Message, StringComparison.Ordinal);
            Assert.Equal((CueOutcome.Requested, 1, "Awaiting 1 receipt"), (CueRow(b, "01").Outcome, CueRow(b, "01").Pending, CueRow(b, "01").OutcomeWords));
            Assert.Contains("cue 01 Message", services.AirState.Stage.Messages.Last().From, StringComparison.Ordinal);
            clock = clock.AddSeconds(14);
            stack.Poll();
            Assert.Equal(CueOutcome.Requested, CueRow(b, "01").Outcome);                 // past the 12 s window: the clock settles nothing
            sw.Release();
            PumpUntil(() => CueRow(b, "01").Outcome == CueOutcome.Done, 20000, () => CueRow(b, "01").OutcomeWords);
            Assert.Equal(0, CueRow(b, "01").Pending);
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == "CueSettled" && e.Target == "01 Message" && e.Outcome == "Done");

            // Refused: FailedLate with Stagewatch's words, journalled as a settlement; the stack is not stopped.
            (sw.SpeakerStatus, sw.SpeakerWords) = (409, "The stage is locked by the showcaller.");
            clock = clock.AddSeconds(5);
            Assert.Equal(ActionStatus.Requested, Go(b, "02", "Refused").Status);
            PumpUntil(() => CueRow(b, "02").Outcome == CueOutcome.FailedLate, 20000, () => CueRow(b, "02").OutcomeWords);
            Assert.Contains($"Stagewatch {Name} refused the message (The stage is locked by the showcaller.) — its displays do not show it", CueRow(b, "02").Detail, StringComparison.Ordinal);
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == "CueSettled" && e.Target == "02 Message" && e.Outcome == "FailedLate");
            Assert.True(stack.Armed);
            (sw.SpeakerStatus, sw.SpeakerWords) = (0, "");

            // Accepted: still Requested past 12 s, in the receipt's own words — then Done when a frame binds it.
            sw.Slow = true;
            clock = clock.AddSeconds(5);
            Assert.Equal(ActionStatus.Requested, Go(b, "03", "Accepted").Status);
            PumpUntil(() => CueRow(b, "03").Waiting.Length > 0, 20000, () => CueRow(b, "03").OutcomeWords);
            clock = clock.AddSeconds(14);
            stack.Poll();
            Assert.Equal((CueOutcome.Requested, $"Stagewatch {Name} accepted the message, not yet applied"), (CueRow(b, "03").Outcome, CueRow(b, "03").OutcomeWords));
            sw.Slow = false;
            sw.RunShow();
            PumpUntil(() => CueRow(b, "03").Outcome == CueOutcome.Done, 20000, () => CueRow(b, "03").OutcomeWords);
            Assert.Equal("Done", CueRow(b, "03").OutcomeWords);

            // Unconfirmed (a timeout): still Requested past 12 s with its words — never Done by the clock, never failed.
            sw.HoldNext();
            clock = clock.AddSeconds(5);
            Assert.Equal(ActionStatus.Requested, Go(b, "04", "Unconfirmed").Status);
            PumpUntil(() => CueRow(b, "04").Waiting.Length > 0, 20000, () => CueRow(b, "04").OutcomeWords);
            clock = clock.AddSeconds(14);
            stack.Poll();
            var unconfirmed = CueRow(b, "04");
            Assert.Equal(CueOutcome.Requested, unconfirmed.Outcome);
            Assert.Equal($"The message to Stagewatch {Name} is unconfirmed — it may be on the speaker's screen", unconfirmed.OutcomeWords);
            Assert.False(unconfirmed.IsFailure);
            sw.Release();
            PumpUntil(() => CueRow(b, "04").Outcome == CueOutcome.Done, 20000, () => CueRow(b, "04").OutcomeWords);   // the frame that names it is the yes

            // Not sent (the show went stuck before the request was written): FailedLate, with why.
            sw.HoldNext();
            Sent(b, "Ahead of the cue");
            PumpUntil(() => sw.Held == 1);
            clock = clock.AddSeconds(5);
            Assert.Equal(ActionStatus.Requested, Go(b, "05", "Not sent").Status);
            sw.Stuck = true;
            sw.Push();
            PumpUntil(() => services.Kernel.Stagewatch.Sight.StuckLive);
            sw.Release();
            PumpUntil(() => CueRow(b, "05").Outcome == CueOutcome.FailedLate, 20000, () => CueRow(b, "05").OutcomeWords);
            Assert.Contains($"the message was not sent to Stagewatch {Name} (Stagewatch {Name} says its show is stuck)", CueRow(b, "05").Detail, StringComparison.Ordinal);
            // Nothing was fired again: one post per cue that reached the Stagewatch.
            Assert.DoesNotContain(sw.Messages, m => m.Path.Contains("Not%20sent", StringComparison.Ordinal));
            Assert.Single(sw.Messages, m => m.Path.Contains("Refused", StringComparison.Ordinal));
        }
        finally
        {
            sw.Release();
            Unlink(b);
            b.Dispose();
        }
    }

    // ---- the desk's stage panel ----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void TheDesksStagePanelSaysEachMessagesRouteAndSeenOnlyForOneSomebodySaw()
    {
        var b = TestApp.Boot();
        using var sw = new SpeakerStagewatch();
        Window? panel = null;
        try
        {
            var (services, vm, _) = b;
            services.Kernel.Stagewatch.SpeakerCallWithin = TimeSpan.FromMilliseconds(600);
            Link(b, sw);

            // One of each: superseded before it was sent, left the Stagewatch's stage unanswered, on its stage, refused,
            // unconfirmed — and a crew message on this desk's displays.
            sw.HoldNext();
            Sent(b, "First");
            var first = Last(b);
            PumpUntil(() => sw.Held == 1);
            Sent(b, "Second");
            var second = Last(b);
            Sent(b, "Third");
            var third = Last(b);
            PumpUntil(() => second.SwAck == StageSpeakerWords.Cleared, 20000, () => second.SwAck);
            sw.Release();
            PumpUntil(() => third.SwAck == "sent" && first.SwAck == StageSpeakerWords.Cleared, 20000, () => $"{first.SwAck} / {third.SwAck}");
            (sw.SpeakerStatus, sw.SpeakerWords) = (409, "The stage is locked by the showcaller.");
            Sent(b, "Refused");
            var refused = Last(b);
            PumpUntil(() => refused.SwAck == "refused", 20000, () => refused.SwAck);
            (sw.SpeakerStatus, sw.SpeakerWords) = (0, "");
            sw.HoldNext();
            Sent(b, "Unconfirmed");
            var unconfirmed = Last(b);
            PumpUntil(() => unconfirmed.SwAck == "unconfirmed", 20000, () => unconfirmed.SwAck);
            Say(b, "Mic 2 is live", "crew");
            var crew = Last(b);

            panel = new Window { DataContext = vm, Width = 900, Height = 2400, Content = new ScrollViewer { Content = new StageSection() } };
            panel.Show();
            Dispatcher.UIThread.RunJobs();
            string Line(StageMessage m) => Assert.Single(panel.GetVisualDescendants().OfType<TextBlock>(), t => ReferenceEquals(t.DataContext, m) && Grid.GetColumn(t) == 2).Text ?? "";

            // The words the timer page's list has — and "seen" for none of them: nobody has seen any.
            Assert.Equal("superseded — never sent", Line(second));
            Assert.Equal("cleared", Line(first));
            Assert.Equal($"on Stagewatch {Name}'s screen, waiting…", Line(third));
            Assert.Equal($"refused by Stagewatch {Name} — on this desk's displays, waiting…", Line(refused));
            Assert.Equal($"unconfirmed by Stagewatch {Name} — on this desk's displays, waiting…", Line(unconfirmed));
            Assert.Equal("waiting…", Line(crew));
            foreach (var m in new[] { first, second, third, refused, unconfirmed, crew }) Assert.DoesNotContain("seen", Line(m), StringComparison.Ordinal);
            var page = Page(b).GetProperty("messages").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("receipt").GetString());
            foreach (var m in new[] { first, second, third, refused, unconfirmed }) Assert.Equal(page[m.Id], Line(m));

            // The lines follow each step as it comes: the refused one's ACK pressed on this desk's display, the third answered
            // on the Stagewatch.
            Assert.True(services.Actions.Execute(new ShowAction(ShowActionKind.StageAck, "", refused.Id), ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith($"refused by Stagewatch {Name} — on this desk's displays, seen ", Line(refused), StringComparison.Ordinal);
            sw.SpeakerAnswers("10.0.0.30");
            PumpUntil(() => third.SwAck == "done", 20000, () => third.SwAck);
            Assert.StartsWith("seen by 10.0.0.30 ", Line(third), StringComparison.Ordinal);
        }
        finally
        {
            panel?.Close();
            sw.Release();
            Unlink(b);
            b.Dispose();
        }
    }

    // ---- the lane ------------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void ASpeakersCallNeverWaitsBehindTheContextsRead()
    {
        var b = TestApp.Boot();
        using var sw = new SpeakerStagewatch();
        try
        {
            Link(b, sw);
            // The Stagewatch takes six seconds over its context; a message asked while that read is in flight arrives while it
            // still is — never behind it — and sooner than a beat (2 s), the side calls' own turn.
            sw.ContextDelayMs = 6000;
            PumpUntil(() => sw.ContextInFlight > 0, 30000);
            var asked = Environment.TickCount64;
            Sent(b, "Now");
            PumpUntil(() => sw.Messages.Count == 1, 5000, () => "the message waited behind the context's read");
            Assert.True(sw.Messages[0].DuringContext, "the context's read was still in flight when the message arrived");
            Assert.True(sw.Messages[0].AtMs - asked < 1900, $"the message left {sw.Messages[0].AtMs - asked} ms after it was asked");
            PumpUntil(() => Last(b).SwAck == "sent");
        }
        finally
        {
            sw.ContextDelayMs = 0;
            Unlink(b);
            b.Dispose();
        }
    }
}
