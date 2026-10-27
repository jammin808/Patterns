using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 65: explicit trust on the control network. A pairing token set on the Remote page makes
/// every mutating verb wait for it — AUTH on the wire, X-Patterns-Token on the web — and (round 91,
/// ADR-016) the wire's state too, scope A's reads alone open before pairing, while the pages and this
/// machine's own browsers never need it; NEW TOKEN cuts the paired remotes off, a wire connection told
/// why and closed; the admin passcode rides in a header and never in a URL; the control ports bind
/// where the page says. Real sockets against the live app, from loopback with the exemption off.
/// </summary>
public class RemoteTrustTests
{
    private const string Token = "K7QM-3XWD-P9RA";

    private static (AppServices Services, MainViewModel Vm) Boot([System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerMemberName] string member = "")
    {
        var b = TestApp.Boot(callerFile: file, callerMember: member);
        return (b.Services, b.Vm);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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

    /// <summary>
    /// A connection to the wire with its first line read — ADR-016: the greeting STATE when the connection is paired from the
    /// start (an open desk, this machine trusted), a NONCE and no STATE when it has the token to prove. Send a line,
    /// ReadResponse the next OK/ERR (STATE pushes skipped), NextLine the next line whatever it is.
    /// </summary>
    private sealed class Wire : IDisposable
    {
        private readonly TcpClient _client = new();
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;

        /// <summary>The nonce the desk said first to an unpaired connection; "" for one greeted with STATE.</summary>
        public string Nonce { get; } = "";

        public Wire(int port, bool paired = true)
        {
            Pump(_client.ConnectAsync(IPAddress.Loopback, port));
            _stream = _client.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8);
            var first = Pump(_reader.ReadLineAsync());
            if (paired)
            {
                Assert.StartsWith("STATE {", first);
            }
            else
            {
                Assert.StartsWith("NONCE ", first);                       // ADR-016: the nonce first, and no STATE before it
                Nonce = first!["NONCE ".Length..];
            }
        }

        public void Send(string line) => _stream.Write(Encoding.UTF8.GetBytes(line + "\n"));

        /// <summary>The next line the desk says, STATE and NONCE included.</summary>
        public string NextLine() => Pump(_reader.ReadLineAsync()) ?? throw new IOException("the desk closed the connection");

        /// <summary>A line sent and the next line the desk says: its answer, with nothing — no STATE push — before it.</summary>
        public string Ask(string line)
        {
            Send(line);
            return NextLine();
        }

        /// <summary>A line sent and its answer, the pushes a paired connection hears meanwhile skipped.</summary>
        public string Reply(string line)
        {
            Send(line);
            return ReadResponse();
        }

        /// <summary>A line sent and the next line that is not a STATE push — a NONCE included, so a test sees one that came.</summary>
        public string ReplyOrNonce(string line)
        {
            Send(line);
            while (true)
            {
                var next = NextLine();
                if (!next.StartsWith("STATE ", StringComparison.Ordinal)) return next;
            }
        }

        public string ReadResponse()
        {
            while (true)
            {
                var line = Pump(_reader.ReadLineAsync());
                Assert.NotNull(line);
                if (!line!.StartsWith("STATE ") && !line.StartsWith("NONCE ")) return line;   // round 87: a paired desk's nonce is not an answer
            }
        }

        /// <summary>Null once the desk has closed the connection.</summary>
        public string? ReadLineOrEnd()
        {
            while (true)
            {
                var line = Pump(_reader.ReadLineAsync());
                if (line is null || (!line.StartsWith("STATE ") && !line.StartsWith("NONCE "))) return line;
            }
        }

        public void Dispose() => _client.Dispose();
    }

    /// <summary>
    /// One HTTP request on the control port, as a phone or a curl would send it: the status and the body. Its Host is "test"
    /// unless a header names one; a Host header with no value sends none at all.
    /// </summary>
    private static (string Status, string Body) Http(int port, string method, string path, string body = "", params (string Name, string Value)[] headers)
    {
        using var client = new TcpClient();
        Pump(client.ConnectAsync(IPAddress.Loopback, port));
        using var stream = client.GetStream();
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var ownHost = headers.Any(h => h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase));
        var head = new StringBuilder($"{method} {path} HTTP/1.1\r\n{(ownHost ? "" : "Host: test\r\n")}Content-Length: {bodyBytes.Length}\r\n");
        foreach (var (name, value) in headers.Where(h => h.Value.Length > 0)) head.Append($"{name}: {value}\r\n");
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
        var statusLine = text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
        return (statusLine["HTTP/1.1 ".Length..], cut < 0 ? "" : text[(cut + 4)..]);
    }

    [AvaloniaFact]
    public void OnTheWireAVerbAndAReadWaitForTheTokenAndNewTokenCutsThePairedOffFromBoth()
    {
        var (services, vm) = Boot();
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            Assert.EndsWith("· paired.", services.Control.Status);

            using var wire = new Wire(vm.State.Control.TcpPort, paired: false);
            wire.Send("PING");
            Assert.Equal("OK PONG", wire.ReadResponse());
            wire.Send("STATUS");
            Assert.Equal(ControlProtocol.Err(ControlProtocol.StateNotPaired), wire.ReadResponse());   // ADR-016: reading needs the token too

            wire.Send("BLACKOUT ON");
            var refused = wire.ReadResponse();
            Assert.StartsWith("ERR not paired", refused);
            Assert.Contains("AUTH <token>", refused);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);                          // nothing ran

            wire.Send("AUTH K7QM-3XWD-P9RB");
            Assert.StartsWith("ERR wrong token", wire.ReadResponse());
            wire.Send("auth k7qm3xwd p9ra");                                 // dashes, spaces and case do not matter
            Assert.Equal("OK paired", wire.ReadResponse());
            Assert.StartsWith("STATE {", wire.NextLine());                  // the greeting follows the pairing
            wire.Send("BLACKOUT ON");
            Assert.Equal("OK", wire.ReadResponse());
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);

            // NEW TOKEN: the show's token changes, and the connection paired with the old one is cut off from the verbs and,
            // ADR-016, from reading — told why in its last line and closed, so it never sits on its last STATE as if it were
            // live. A connection that proves the new token pairs and is greeted.
            vm.NewPairingTokenCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Matches(new Regex("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$"), vm.State.Control.Token);
            Assert.NotEqual(Token, vm.State.Control.Token);
            Assert.Equal(ControlProtocol.Err(ControlProtocol.TokenChanged), wire.ReadLineOrEnd());
            Assert.Null(wire.ReadLineOrEnd());                              // closed after its last word
            using var again = new Wire(vm.State.Control.TcpPort, paired: false);
            again.Send("BLACKOUT OFF");
            Assert.StartsWith("ERR not paired", again.ReadResponse());
            again.Send("STATUS");
            Assert.Equal(ControlProtocol.Err(ControlProtocol.StateNotPaired), again.ReadResponse());
            again.Send("AUTH " + Token);
            Assert.StartsWith("ERR wrong token", again.ReadResponse());       // the old token pairs nothing now
            again.Send("AUTH " + vm.State.Control.Token);
            Assert.Equal("OK paired", again.ReadResponse());
            Assert.StartsWith("STATE {", again.NextLine());
            again.Send("STATUS");
            Assert.StartsWith("OK {", again.ReadResponse());
            again.Send("BLACKOUT OFF");
            Assert.Equal("OK", again.ReadResponse());
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);

            // An open desk answers AUTH too, so a module with a stale token is told rather than refused; the token cleared
            // pairs every connection, so none is closed for it.
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            again.Send("AUTH anything");
            Assert.StartsWith("OK open", again.ReadResponse());
            again.Send("BLACKOUT ON");
            Assert.Equal("OK", again.ReadResponse());
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Pumps the dispatcher for a while — long enough for the push timer (200 ms, trailing) to have fired.</summary>
    private static void PumpFor(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// ADR-016 (round 91, scope A): the wire shows its state only to a paired connection. On a token desk, a connection that
    /// has not paired is greeted with NONCE and no STATE; STATUS, EYE and CONTEXT — and every read the allow-list does not
    /// name — are answered with the not-paired words (they answered OK {json} before), each kind in turn; no push reaches
    /// it; the handshake and scope A's five open reads answer; a verb waits as before. The refusal is counted on the Super
    /// Check's REMOTE row and logged at most once for the connection, at its first refused read, which names its address
    /// once in the process's life, for at most <see cref="ControlProtocol.UnpairedReadsNamedMost"/> addresses before one
    /// line says no more are named. The proof pairs it, the greeting follows the OK, the reads answer and the pushes
    /// arrive; NEW TOKEN stops the pushes again.
    /// </summary>
    [AvaloniaFact]
    public void AConnectionThatHasNotPairedHearsNonceFirstAndNoStateAndEveryReadButTheOpenOnesIsRefusedWithWords()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;                                   // loopback plays another machine
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            var log = Path.Combine(b.Dir, "patterns.log");
            int Said() => File.ReadAllLines(log).Count(l => l.Contains("before pairing — refused", StringComparison.Ordinal));
            var saidBefore = Said();
            var refusedBefore = services.Control.UnpairedReadsRefused;
            var notPaired = ControlProtocol.Err(ControlProtocol.StateNotPaired);

            using var wire = new Wire(vm.State.Control.TcpPort, paired: false);
            Assert.Matches("^[0-9a-f]{32}$", wire.Nonce);
            Assert.Equal("OK PONG", wire.Ask("PING"));                          // the next line is the answer: no STATE came first

            // Every read the allow-list does not name, refused with the words — STATUS, EYE and CONTEXT first; nothing between.
            var refused = new[]
            {
                "STATUS", "EYE", "EYE STATUS", "EYE GRAPH", "EYE AT 12:00", "CONTEXT", "CONTEXT STATUS", "TWIN STATUS", "SHOWLOCK STATUS",
                "CALIBRATE STATUS", "NODES", "STAGE STATUS", "RIGDAY STATUS", "ALIGN STATUS", "SCREEN 1 SIGNAL", "SCREEN 1 EDID",
                "RIG STATUS", "COMMISSION", "MIDI",
            };
            foreach (var line in refused)
            {
                Assert.True(ControlProtocol.IsQuery(ControlProtocol.Parse(line)), line);
                Assert.False(ControlProtocol.OpenBeforePairing(ControlProtocol.Parse(line)), line);
                Assert.Equal(notPaired, wire.Ask(line));
            }
            Assert.Equal(refusedBefore + refused.Length, services.Control.UnpairedReadsRefused);
            Assert.Equal("MIDI from 127.0.0.1", services.Control.LastUnpairedRead);

            // A verb waits as before, in its own words; an unknown line keeps its own refusal.
            Assert.Equal(ControlProtocol.Err(ControlProtocol.NotPaired), wire.Ask("BLACKOUT ON"));
            Assert.Equal(ControlProtocol.Err(ControlProtocol.NotPaired), wire.Ask("RECORD ON"));
            Assert.StartsWith("ERR unknown command", wire.Ask("FROBNICATE"));

            // The handshake and scope A's five open reads answer an unpaired connection — none of them with the not-paired words.
            Assert.Equal("OK", wire.Ask("HELLO FOH deck"));
            Assert.Matches("^OK [0-9a-f]{32}$", wire.Ask("NONCE"));
            foreach (var line in new[] { "NAV", "NAV PAGES", "MENU PGM", "CUE LIST", "ARCADE STATUS", "PLAY STATUS", "AUDIENCE STATUS", "ASSISTANT ASK what is on air" })
            {
                Assert.True(ControlProtocol.OpenBeforePairing(ControlProtocol.Parse(line)), line);
                Assert.DoesNotContain("not paired", wire.Ask(line), StringComparison.Ordinal);
            }
            Assert.StartsWith("OK {", wire.Ask("NAV"));
            Assert.StartsWith("OK {\"name\":\"Cue stack\"", wire.Ask("CUE LIST"));
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);

            // No push reaches it: the show moves, the push timer fires, and the next line is still the answer to a PING.
            services.Actions.Execute(ShowActionKind.BlackoutOn, ActionOrigin.Desk);
            PumpFor(400);
            Assert.Equal("OK PONG", wire.Ask("PING"));

            // The log: one line for the connection's first refused read, naming the verb and the address, never the line.
            Assert.Equal(saidBefore + 1, Said());
            var said = File.ReadAllLines(log).Last(l => l.Contains("before pairing — refused", StringComparison.Ordinal));
            Assert.Contains("Wire: 127.0.0.1 asked STATUS before pairing — refused", said, StringComparison.Ordinal);
            Assert.Contains("ADR-016", said, StringComparison.Ordinal);
            using (var second = new Wire(vm.State.Control.TcpPort, paired: false))
            {
                Assert.Equal(notPaired, second.Ask("EYE"));                     // another connection from the address within the minute
            }
            Assert.Equal(saidBefore + 1, Said());                               // counted, not written again

            // The Super Check's REMOTE row: amber with the count and the last one, the rule and what scope A leaves open.
            var row = Assert.Single(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Section == "REMOTE" && r.Item == "Wire reads");
            Assert.Equal(CheckLight.Amber, row.Light);
            Assert.Equal($"paired connections only · {services.Control.UnpairedReadsRefused} unpaired reads refused", row.Value);
            Assert.Contains("the last: EYE from 127.0.0.1", row.Note, StringComparison.Ordinal);
            Assert.Contains(ControlProtocol.OpenBeforePairingWords, row.Note, StringComparison.Ordinal);

            // The proof pairs it: the greeting is the line after OK paired, the reads answer, and the pushes arrive.
            wire.Send("AUTH-PROOF " + WireProof.Proof(Token, wire.Ask("NONCE")["OK ".Length..], "FOH deck"));
            Assert.Equal("OK paired", wire.NextLine());
            Assert.StartsWith("STATE {", wire.NextLine());                      // the greeting, the line after the OK
            Assert.StartsWith("OK {", wire.Reply("STATUS"));
            Assert.StartsWith("OK {", wire.Reply("EYE"));
            Assert.StartsWith("OK {", wire.Reply("CONTEXT"));
            services.Actions.Execute(ShowActionKind.BlackoutOff, ActionOrigin.Desk);
            PumpFor(400);
            Assert.StartsWith("STATE {", wire.NextLine());                      // a push reaches it now
            Assert.Equal("OK PONG", wire.Reply("PING"));

            // The deck said HELLO before it paired: paired now, on the Remote page and in the Eye.
            Assert.True(Assert.Single(services.Control.Decks).Paired);
            services.Eye.Refresh();
            Assert.Equal(("paired", CheckLight.Green), (services.Eye.Graph.Find("deck:FOH deck@127.0.0.1")!.Sub, services.Eye.Graph.Find("deck:FOH deck@127.0.0.1")!.Light));

            // NEW TOKEN: the connection paired with the old token hears why in its last line and is closed — open and silent,
            // it would keep its last STATE as if it were live. No reader says it is paired from the moment the token changed,
            // the show moving meanwhile reaches it no more, and once it is gone no deck is listed. One log line says so.
            vm.NewPairingTokenCommand.Execute(null);
            Assert.DoesNotContain(services.Control.Decks, d => d.Paired);
            services.Eye.Refresh();
            Assert.NotEqual(CheckLight.Green, services.Eye.Graph.Find("deck:FOH deck@127.0.0.1")?.Light ?? CheckLight.Amber);
            services.Actions.Execute(ShowActionKind.BlackoutOn, ActionOrigin.Desk);
            Assert.Equal(ControlProtocol.Err(ControlProtocol.TokenChanged), wire.ReadLineOrEnd());
            Assert.Null(wire.ReadLineOrEnd());
            var gone = Environment.TickCount64 + 8000;
            while (services.Control.Decks.Count > 0 && Environment.TickCount64 < gone) PumpFor(20);
            Assert.Empty(services.Control.Decks);
            services.Eye.Refresh();
            Assert.Null(services.Eye.Graph.Find("deck:FOH deck@127.0.0.1"));
            Assert.Contains("Wire: the pairing token changed — 1 connection that had paired before it told so and closed", File.ReadAllText(log), StringComparison.Ordinal);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            services.State.Blackout = false;
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    /// <summary>
    /// ADR-016: an open desk and this machine's own connection, while the desk trusts it, are paired from the start — greeted
    /// with STATE as before, and no NONCE; the Super Check's row says the rule only on a desk with a token.
    /// </summary>
    [AvaloniaFact]
    public void AnOpenDeskAndATrustedLoopbackAreGreetedWithStateAsBefore()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var trusted = ControlService.TrustLoopback;
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            ControlService.TrustLoopback = true;
            Dispatcher.UIThread.RunJobs();
            using (var own = new Wire(vm.State.Control.TcpPort))                // this machine's own: STATE first
            {
                Assert.Equal("OK PONG", own.ReplyOrNonce("PING"));              // and no NONCE after it
                Assert.StartsWith("OK {", own.Reply("STATUS"));
            }
            var row = Assert.Single(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Section == "REMOTE" && r.Item == "Wire reads");
            Assert.Equal(CheckLight.Green, row.Light);
            Assert.Equal("paired connections only", row.Value);

            ControlService.TrustLoopback = false;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            using (var open = new Wire(vm.State.Control.TcpPort))               // an open desk: STATE first, as before
            {
                Assert.Equal("OK PONG", open.ReplyOrNonce("PING"));
                Assert.StartsWith("OK {", open.Reply("STATUS"));
            }
            Assert.DoesNotContain(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Item == "Wire reads");

            // A token set on an open desk: a connection that read the open desk is told why in its last line and closed, as
            // NEW TOKEN closes one paired with the old token. One that heard only NONCE is greeted once the token is cleared.
            using var reader = new Wire(vm.State.Control.TcpPort);
            vm.State.Control.Token = Token;
            Assert.Equal(ControlProtocol.Err(ControlProtocol.TokenChanged), reader.ReadLineOrEnd());
            Assert.Null(reader.ReadLineOrEnd());
            using var waiting = new Wire(vm.State.Control.TcpPort, paired: false);
            vm.State.Control.Token = "";
            Assert.StartsWith("STATE {", waiting.NextLine());                  // the greeting, the desk open now
            Assert.Equal("OK PONG", waiting.Reply("PING"));
            Assert.StartsWith("OK {", waiting.Reply("STATUS"));
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void FiveWrongTokensCloseTheConnection()
    {
        var (services, vm) = Boot();
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            using var wire = new Wire(vm.State.Control.TcpPort, paired: false);
            for (var i = 1; i < ControlService.WrongTokensBeforeClose; i++)
            {
                wire.Send("AUTH nope");
                Assert.Equal(ControlProtocol.Err(ControlProtocol.WrongToken), wire.ReadResponse());
            }
            wire.Send("AUTH nope");
            Assert.EndsWith("— closed", wire.ReadResponse());
            Assert.Null(wire.ReadLineOrEnd());                                 // the desk hung up
            var deadline = Environment.TickCount64 + 5000;
            while (services.Control.WireConnections > 0 && Environment.TickCount64 < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
            Assert.Equal(0, services.Control.WireConnections);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void OnTheWebAMutatingPostWantsTheTokenInItsHeaderAndThisMachinesOwnBrowserIsExempt()
    {
        var (services, vm) = Boot();
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;

            var refused = Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("X-Patterns-Client", "phone"));
            Assert.Equal("403 Forbidden", refused.Status);
            Assert.Contains("\"ok\":false", refused.Body);
            Assert.Contains("not paired", refused.Body);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);

            // Round 85 (P1-02): a query wants the token too — STATUS answers the JSON /api/state has asked it for since round 83 — and so does the cue list.
            var query = Http(port, "POST", "/api/cmd", "STATUS");
            Assert.Equal("403 Forbidden", query.Status);
            Assert.Contains("not paired", query.Body);
            var queryPaired = Http(port, "POST", "/api/cmd", "STATUS", ("X-Patterns-Token", Token));
            Assert.Equal("200 OK", queryPaired.Status);
            Assert.Contains("\"ok\":true", queryPaired.Body);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/api/cues").Status);
            Assert.Equal("200 OK", Http(port, "GET", "/api/cues", "", ("Host", $"localhost:{port}"), ("Cookie", "patterns-token=" + Token)).Status);   // the cookie, under the desk's own name (round 89)

            var wrong = Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("X-Patterns-Token", "K7QM-3XWD-P9RB"));
            Assert.Equal("403 Forbidden", wrong.Status);

            var paired = Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("X-Patterns-Token", "k7qm-3xwd-p9ra"));
            Assert.Equal("200 OK", paired.Status);
            Assert.Contains("\"ok\":true", paired.Body);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);

            var ack = Http(port, "POST", "/api/stage/ack", "some-id");
            Assert.Equal("403 Forbidden", ack.Status);
            Assert.Contains("not paired", ack.Body);
            var pad = Http(port, "POST", "/api/arcade/key", "1 A TAP");
            Assert.Equal("403 Forbidden", pad.Status);

            // The pages themselves are open; the state and the pictures are the show's too (round 83): the header, or the pages' cookie for their img tags.
            Assert.Equal("200 OK", Http(port, "GET", "/").Status);
            var stateRefused = Http(port, "GET", "/api/state");
            Assert.Equal("403 Forbidden", stateRefused.Status);
            Assert.Contains("not paired", stateRefused.Body);
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("X-Patterns-Token", Token)).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", $"localhost:{port}"), ("Cookie", "patterns-token=" + Token)).Status);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/pgm.jpg").Status);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/mv.jpg?w=320").Status);
            Assert.Equal("200 OK", Http(port, "GET", "/pgm.jpg", "", ("Host", $"localhost:{port}"), ("Cookie", "patterns-token=" + Token)).Status);

            // A token in the URL is not a token: nothing reads query strings for credentials, and the route is not even there.
            Assert.NotEqual("200 OK", Http(port, "POST", "/api/cmd?token=" + Token, "BLACKOUT OFF").Status);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);

            // This machine's own browser — the desk's pages opened on the desk, by a name that is the desk's — never needs the token.
            ControlService.TrustLoopback = true;
            Assert.Equal("200 OK", Http(port, "POST", "/api/cmd", "BLACKOUT OFF", ("Host", $"localhost:{port}")).Status);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ARequestOverLoopbackUnderANameThatIsNotThisDesksIsAnsweredAsAnotherMachinesWithWordsAndOneLogLine()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = true;                                        // the show machine's own browser is trusted — by its own names only
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;
            var rebound = $"evil.example:{port}";
            var log = Path.Combine(b.Dir, "patterns.log");
            int Said() => File.ReadAllLines(log).Count(l => l.Contains("named this desk", StringComparison.Ordinal));
            var saidBefore = Said();

            // This desk by its own names, over loopback: trusted as before.
            foreach (var host in new[] { $"localhost:{port}", $"127.0.0.1:{port}", $"[::1]:{port}", $"{Environment.MachineName.ToLowerInvariant()}:{port}" })
            {
                Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", host)).Status);
            }
            Assert.Equal("200 OK", Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("Host", $"localhost:{port}")).Status);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);
            Assert.Equal(saidBefore, Said());

            // A page whose name was rebound to 127.0.0.1: same-origin to itself, so round 83's rule lets its POST through — and its
            // Host is still its own name, so it is another machine's request: the token or nothing, and it hears why.
            var post = Http(port, "POST", "/api/cmd", "BLACKOUT OFF", ("Host", rebound), ("Origin", "http://" + rebound), ("Sec-Fetch-Site", "same-origin"));
            Assert.Equal("403 Forbidden", post.Status);
            Assert.Contains("\"ok\":false", post.Body);
            Assert.Contains("not paired", post.Body);
            Assert.Contains("a name that is not its own", post.Body);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);                                   // nothing ran
            var read = Http(port, "GET", "/api/state", "", ("Host", rebound));
            Assert.Equal("403 Forbidden", read.Status);
            Assert.Contains("a name that is not its own", read.Body);
            Assert.DoesNotContain("\"blackout\"", read.Body);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/api/context", "", ("Host", rebound)).Status);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/pgm.jpg", "", ("Host", rebound)).Status);
            Assert.Contains("a name that is not its own", Http(port, "POST", "/api/stage/ack", "some-id", ("Host", rebound)).Body);
            Assert.Equal("200 OK", Http(port, "GET", "/", "", ("Host", rebound)).Status);     // the pages themselves are open to any machine

            // A forwarder's port, a scheme's port, a trailing dot, no Host at all: none names this desk.
            foreach (var host in new[] { $"localhost:{port + 1}", "localhost", $"localhost.:{port}", "" })
            {
                Assert.Equal("403 Forbidden", Http(port, "GET", "/api/state", "", ("Host", host)).Status);
            }

            // The pages' cookie wants this desk's name too; the token in its header pairs any machine, whatever its name.
            ControlService.TrustLoopback = false;
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", $"localhost:{port}"), ("Cookie", "patterns-token=" + Token)).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/pgm.jpg", "", ("Host", $"127.0.0.1:{port}"), ("Cookie", "patterns-token=" + Token)).Status);
            var cookie = Http(port, "GET", "/api/state", "", ("Host", rebound), ("Cookie", "patterns-token=" + Token));
            Assert.Equal("403 Forbidden", cookie.Status);
            Assert.Contains("a name that is not its own", cookie.Body);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/pgm.jpg", "", ("Host", rebound), ("Cookie", "patterns-token=" + Token)).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", rebound), ("X-Patterns-Token", Token)).Status);
            var paired = Http(port, "POST", "/api/cmd", "BLACKOUT OFF", ("Host", rebound), ("X-Patterns-Token", Token));
            Assert.Equal("200 OK", paired.Status);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);
            // A machine with no token, under any name, hears the plain words: its name changed nothing.
            var plain = Http(port, "GET", "/api/state", "", ("Host", rebound));
            Assert.Equal("403 Forbidden", plain.Status);
            Assert.Contains("not paired", plain.Body);
            Assert.DoesNotContain("a name that is not its own", plain.Body);

            // One log line for the address (loopback), naming the Host it used, however many requests it sent.
            Assert.Equal(saidBefore + 1, Said());
            var line = File.ReadAllLines(log).Last(l => l.Contains("named this desk", StringComparison.Ordinal));
            Assert.Contains("from 127.0.0.1", line);
            Assert.Contains($"'{rebound}'", line);
            Assert.DoesNotContain(Token, File.ReadAllText(log));

            // An open desk (no token) answers every machine, under any name, as it always has — a curl, a device, a Companion.
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", rebound)).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", rebound), ("X-Patterns-Client", "tool")).Status);
            // A browser's page under a name that is not the desk's is no machine of the network's: it is a page whose name was
            // rebound to this desk, from any site a browser on the network has open. The show's routes refuse it with words that
            // say what to do and never ask for a token (there is none to give); the page itself still loads.
            var openPost = Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("Host", rebound), ("Origin", "http://" + rebound), ("Sec-Fetch-Site", "same-origin"));
            Assert.Equal("403 Forbidden", openPost.Status);
            Assert.Contains("a browser named the desk by a name that is not its own", openPost.Body);   // (the JSON escapes its apostrophes and dashes)
            Assert.DoesNotContain("not paired", openPost.Body);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);                                  // nothing ran
            var openRead = Http(port, "GET", "/api/state", "", ("Host", rebound), ("Sec-Fetch-Site", "same-origin"));
            Assert.Equal("403 Forbidden", openRead.Status);
            Assert.DoesNotContain("\"blackout\"", openRead.Body);
            foreach (var path in new[] { "/api/context", "/api/cues", "/pgm.jpg", "/mv.jpg?w=320" })
                Assert.Equal("403 Forbidden", Http(port, "GET", path, "", ("Host", rebound), ("Sec-Fetch-Site", "same-origin")).Status);
            Assert.Contains("a browser named the desk by a name that is not its own", Http(port, "POST", "/api/stage/ack", "some-id", ("Host", rebound), ("Sec-Fetch-Site", "same-origin")).Body);
            Assert.Equal("200 OK", Http(port, "GET", "/", "", ("Host", rebound), ("Sec-Fetch-Site", "none")).Status);
            // Under the desk's own names a browser is answered on an open desk, from this machine or another.
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", $"localhost:{port}"), ("Sec-Fetch-Site", "same-origin")).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", $"{Environment.MachineName.ToLowerInvariant()}:{port}"), ("Sec-Fetch-Site", "same-origin")).Status);
            // One more log line for the address, of its own kind: the browser under another name on an open desk.
            Assert.Equal(saidBefore + 2, Said());
            var open = File.ReadAllLines(log).Last(l => l.Contains("named this desk", StringComparison.Ordinal));
            Assert.Contains("with no pairing token set", open);
            Assert.Contains($"'{rebound}'", open);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void APageFromAnotherOriginCannotPostToTheDeskWhileACurlAndTheDesksOwnPageCan()
    {
        var (services, vm) = Boot();
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = true;                                        // the show machine's own browser: trusted, and still not a way in for another site's page
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;
            var desk = ("Host", $"localhost:{port}");                               // this machine's browser and curl, by the desk's own name (round 89)

            var evil = Http(port, "POST", "/api/cmd", "BLACKOUT ON", desk, ("Origin", "http://evil.example"));
            Assert.Equal("403 Forbidden", evil.Status);
            Assert.Contains("another origin", evil.Body);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/cmd", "BLACKOUT ON", desk, ("Sec-Fetch-Site", "same-site")).Status);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/cmd", "BLACKOUT ON", desk, ("Origin", "null")).Status);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/cmd", "STATUS", desk, ("Origin", "http://evil.example")).Status);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/stage/ack", "some-id", desk, ("Origin", "http://evil.example")).Status);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);

            var own = Http(port, "POST", "/api/cmd", "BLACKOUT ON", desk, ("Origin", $"http://localhost:{port}"), ("Sec-Fetch-Site", "same-origin"));
            Assert.Equal("200 OK", own.Status);                                     // the desk's own page: same origin as the Host it was served from
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);

            var curl = Http(port, "POST", "/api/cmd", "BLACKOUT OFF", desk);
            Assert.Equal("200 OK", curl.Status);                                    // a curl, a device, a script: no Origin, not touched
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);

            // A deliberate client sends X-Patterns-Client and passes round 83's rule whatever its origin: a browser cannot send that
            // header cross-origin without a CORS grant the desk never gives. An Origin that is not the desk's is no page of this
            // machine's own, though (round 89, PeerLink profile 13.3.4): on this paired desk it presents the token, as any client of yours does.
            var foreign = Http(port, "POST", "/api/cmd", "BLACKOUT ON", desk, ("Origin", "http://tool.example"), ("X-Patterns-Client", "tool"));
            Assert.Equal("403 Forbidden", foreign.Status);
            Assert.Contains("not paired", foreign.Body);
            Assert.Contains("a page from another origin is not this desk", foreign.Body);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);
            var client = Http(port, "POST", "/api/cmd", "BLACKOUT ON", desk, ("Origin", "http://tool.example"), ("X-Patterns-Client", "tool"), ("X-Patterns-Token", vm.State.Control.Token));
            Assert.Equal("200 OK", client.Status);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);
            Assert.Equal("200 OK", Http(port, "POST", "/api/cmd", "BLACKOUT OFF", desk).Status);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ASiteOpenInThisMachinesBrowserGetsNoTrustForThisMachineEvenUnderTheDesksOwnName()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = true;                                        // the show machine's own browser is trusted — its own pages only
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;
            var desk = ("Host", $"localhost:{port}");
            var log = Path.Combine(b.Dir, "patterns.log");
            int Said() => File.ReadAllLines(log).Count(l => l.Contains("a page from another origin", StringComparison.Ordinal));
            var saidBefore = Said();

            // The desk's own page (same-origin) and a typed address (none): this machine's, trusted as before.
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", desk, ("Sec-Fetch-Site", "same-origin")).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/pgm.jpg", "", desk, ("Sec-Fetch-Site", "none")).Status);

            // <img src="http://localhost:port/mv.jpg?w=1920"> on any site this machine's browser has open: a page of another
            // origin is no page of this machine's own, so it gets no trust — no picture rendered on its demand, no state long-poll
            // whose timing says when the show moves.
            var img = Http(port, "GET", "/mv.jpg?w=1920", "", desk, ("Sec-Fetch-Site", "cross-site"));
            Assert.Equal("403 Forbidden", img.Status);
            Assert.Contains("a page from another origin", img.Body);
            var poll = Http(port, "GET", "/api/state?since=0", "", desk, ("Sec-Fetch-Site", "cross-site"));
            Assert.Equal("403 Forbidden", poll.Status);
            Assert.DoesNotContain("\"blackout\"", poll.Body);
            // Another app on this host (same-site, another port) carries the pages' cookie with it: the cookie is the desk's own pages' only.
            Assert.Equal("403 Forbidden", Http(port, "GET", "/api/state", "", desk, ("Sec-Fetch-Site", "same-site"), ("Cookie", "patterns-token=" + Token)).Status);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/api/cues", "", desk, ("Origin", "http://localhost:3000")).Status);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/stage/ack", "some-id", desk, ("Origin", "http://localhost:3000"), ("X-Patterns-Client", "tool")).Status);
            // The token in its header pairs it — any client of yours, whatever its origin.
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", desk, ("Sec-Fetch-Site", "cross-site"), ("X-Patterns-Token", Token)).Status);

            // One log line for the address, naming what the page said it was; never the token.
            Assert.Equal(saidBefore + 1, Said());
            var line = File.ReadAllLines(log).Last(l => l.Contains("a page from another origin", StringComparison.Ordinal));
            Assert.Contains("from 127.0.0.1", line);
            Assert.Contains("'cross-site'", line);
            Assert.DoesNotContain(Token, File.ReadAllText(log));
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TrafficForwardedInOverLoopbackIsNotThisMachinesForRequireHttpsOrForTheTokenSeenInClear()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = true;                                        // this machine may stay plain — by the desk's own names
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            vm.State.Control.RequireHttps = true;
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;

            // This machine's own browser, by the desk's name: plain is allowed, and its token crossed no network.
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", $"localhost:{port}"), ("X-Patterns-Token", Token)).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/tls", "", ("Host", $"127.0.0.1:{port}")).Status);
            Assert.Empty(services.Kernel.TokenSeenInClear.For(Token));

            // A port proxy or a tunnel carrying another machine's plain traffic in over loopback: its Host is the name and port
            // that machine typed. It is not this machine's — Require HTTPS refuses it, and the token it carried crossed the
            // network in clear, so it is marked (rotate it: NEW TOKEN).
            var forwarded = Http(port, "GET", "/api/state", "", ("Host", "show-gateway:8080"), ("X-Patterns-Token", Token));
            Assert.Equal("403 Forbidden", forwarded.Status);
            Assert.Contains("requires HTTPS", forwarded.Body, StringComparison.Ordinal);
            var seen = Assert.Single(services.Kernel.TokenSeenInClear.For(Token));
            Assert.Equal(("127.0.0.1", "web"), (seen.Address, seen.Via));
            Assert.Contains("requires HTTPS", Http(port, "GET", "/tls", "", ("Host", $"localhost:{port + 1}")).Body, StringComparison.Ordinal);   // another port: a forwarder's
            Assert.Contains("requires HTTPS", Http(port, "GET", "/tls", "", ("Host", $"localhost:{port}"), ("Sec-Fetch-Site", "cross-site")).Body, StringComparison.Ordinal);   // another site's page: no grant
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
    public void ABrowsersPostToTheWirePortIsClosedOnItsRequestLineBeforeItsBodyIsReadAsALine()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = true;                                        // the harm needs this machine's trust: the body would run as its line
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            var tcp = vm.State.Control.TcpPort;
            var log = Path.Combine(b.Dir, "patterns.log");
            int Said() => File.ReadAllLines(log).Count(l => l.Contains("sent an HTTP request to the Companion port", StringComparison.Ordinal));
            var saidBefore = Said();

            // A no-cors POST from any page in this machine's browser to http://127.0.0.1:<wire>/ with a text/plain body.
            using (var page = new Wire(tcp))
            {
                page.Send($"POST / HTTP/1.1\r\nHost: 127.0.0.1:{tcp}\r\nOrigin: http://evil.example\r\nSec-Fetch-Site: cross-site\r\nContent-Type: text/plain\r\nContent-Length: 12\r\n\r\nBLACKOUT ON");
                Assert.Equal(ControlProtocol.Err(ControlProtocol.HttpOnWire), page.ReadLineOrEnd());
                string? end;
                try
                {
                    end = page.ReadLineOrEnd();
                }
                catch (IOException)
                {
                    end = null;                                                     // a reset is the door closed too: the page's bytes were left unread
                }
                Assert.Null(end);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);                                  // the body never ran
            Assert.Equal(saidBefore + 1, Said());

            // The same words sent as a line by this machine's own client run: the door, not the words, was what the page lacked.
            using var own = new Wire(tcp);
            own.Send("BLACKOUT ON");
            Assert.StartsWith("OK", own.ReadResponse());
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);
            own.Send("BLACKOUT OFF");
            Assert.StartsWith("OK", own.ReadResponse());
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheAdminPasscodeRidesInAHeaderAndNeverInTheUrl()
    {
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;                                   // loopback plays another machine
        var (services, vm) = Boot();
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Install.AdminPasscode = "open-sesame";
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;

            var inUrl = Http(port, "GET", "/api/admin/log?pass=open-sesame");
            Assert.Equal("403 Forbidden", inUrl.Status);                       // the URL is not where a passcode goes
            var inHeader = Http(port, "GET", "/api/admin/log", "", ("X-Patterns-Pass", "open-sesame"));
            Assert.Equal("200 OK", inHeader.Status);
            var bundleInUrl = Http(port, "GET", "/support-bundle.zip?pass=open-sesame");
            Assert.Equal("403 Forbidden", bundleInUrl.Status);
            var bundle = Http(port, "GET", "/support-bundle.zip", "", ("X-Patterns-Pass", "open-sesame"));
            Assert.Equal("200 OK", bundle.Status);
            Assert.StartsWith("PK", bundle.Body);                              // a zip

            // The admin page's own script sends the header and never builds a ?pass= URL.
            var page = Http(port, "GET", "/admin").Body;
            Assert.Contains("'X-Patterns-Pass': pass", page);
            Assert.DoesNotContain("?pass=", page);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Install.AdminPasscode = "";
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// L65: the ADMIN page's log tail masks what the support bundle masks, in the bundle's order: every private page
    /// address said in public, then every secret value masked. A line an older build wrote with the twin's key in it —
    /// "this desk was given the key …", said through a notice that fell back to the log before the window existed —
    /// leaves the machine with the mask where the key was while it is still the show's key, and so does another of the
    /// show's secret values; a line written before the log said addresses in public, a page's address with its pass that
    /// no book keeps, leaves as its public form; the rest of each line reads as written. Asked as another machine would
    /// ask (loopback not trusted). A refused passcode is answered with the gate's words and no line of the log.
    /// </summary>
    [AvaloniaFact]
    public void TheAdminPagesLogTailMasksTheShowsSecretsAsTheBundleDoes()
    {
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;                                   // loopback plays another machine
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Install.AdminPasscode = "open-sesame";
            vm.State.Twin.Key = "xpbe-4kq2-m7rd-9tzc";
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;
            // As an older build wrote them: the made key in the twin's notice, the passcode in another line — and, written
            // past today's log (which says every address in public), a page's address with its pass, kept in no book.
            Log.Info("Twin: this desk was given the key xpbe-4kq2-m7rd-9tzc — a caller node that links needs the same key (Machine page, TWIN).");
            Log.Info("Remote admin: RESTART open-sesame");
            File.AppendAllText(Path.Combine(b.Dir, "patterns.log"), "2026-03-02 21:14:05.118 [INFO] Web page failed: https://hall-b.local/feed?pin=k2m4q7: net::ERR_CONNECTION_REFUSED" + Environment.NewLine);

            var refused = Http(port, "GET", "/api/admin/log", "", ("X-Patterns-Pass", "not-the-passcode"));
            Assert.Equal("403 Forbidden", refused.Status);
            Assert.DoesNotContain("Twin:", refused.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("xpbe-4kq2-m7rd-9tzc", refused.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("hall-b.local", refused.Body, StringComparison.Ordinal);

            var tail = Http(port, "GET", "/api/admin/log", "", ("X-Patterns-Pass", "open-sesame"));
            Assert.Equal("200 OK", tail.Status);
            Assert.Contains($"Twin: this desk was given the key {Secrets.Mask} — a caller node that links needs the same key (Machine page, TWIN).", tail.Body, StringComparison.Ordinal);
            Assert.Contains($"Remote admin: RESTART {Secrets.Mask}", tail.Body, StringComparison.Ordinal);
            Assert.Contains($"[INFO] Web page failed: https://hall-b.local{WindowAddresses.PrivateNote}: net::ERR_CONNECTION_REFUSED", tail.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("xpbe-4kq2-m7rd-9tzc", tail.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("open-sesame", tail.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("k2m4q7", tail.Body, StringComparison.Ordinal);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            b.Vm.State.Install.AdminPasscode = "";
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheControlPortsBindWhereTheRemotePageSaysAndThePagesCarryTheToken()
    {
        var (services, vm) = Boot();
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Bind = "127.0.0.1";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("at 127.0.0.1 only", services.Control.Status);
            services.Control.ForgetRemoteUrls();
            Assert.Equal(new[] { $"http://127.0.0.1:{vm.State.Control.HttpPort}/" }, services.Control.RemoteUrls());
            using var wire = new Wire(vm.State.Control.TcpPort);
            wire.Send("PING");
            Assert.Equal("OK PONG", wire.ReadResponse());

            // Every page that runs a verb sends the token it keeps and asks for it on a 403; round 85: the pages that read the
            // state (the remote, the admin) send it on those reads too, and the pages with a picture set it as the cookie.
            foreach (var path in new[] { "/", "/run", "/stage", "/pad", "/admin" })
            {
                var page = Http(vm.State.Control.HttpPort, "GET", path);
                Assert.Equal("200 OK", page.Status);
                Assert.Contains("X-Patterns-Token", page.Body);
                Assert.Contains("patterns.token", page.Body);
                Assert.Contains("403", page.Body);
            }
            foreach (var path in new[] { "/", "/admin" })
            {
                Assert.Contains("fetch('/api/state', { headers: hdr() })", Http(vm.State.Control.HttpPort, "GET", path).Body);
            }
            foreach (var path in new[] { "/", "/run", "/multiview" })
            {
                Assert.Contains("patterns-token=", Http(vm.State.Control.HttpPort, "GET", path).Body);
            }

            // Round 85 (P1-06): a bind that is not an address opens nothing — never every interface — and says why.
            vm.State.Control.Bind = "10.0.0";
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("Remote control closed — '10.0.0' is not an address", services.Control.Status);
            Assert.StartsWith("'10.0.0' is not an address", services.Control.BindProblem);
            Assert.False(services.Control.StartFailed);                                            // not a failure to retry: the setting is wrong until it is changed
            using (var probe = new TcpClient())
            {
                Assert.ThrowsAny<SocketException>(() => Pump(probe.ConnectAsync(IPAddress.Loopback, vm.State.Control.TcpPort)));
            }
            using (var probe = new TcpClient())
            {
                Assert.ThrowsAny<SocketException>(() => Pump(probe.ConnectAsync(IPAddress.Loopback, vm.State.Control.HttpPort)));
            }
            var closedRow = Assert.Single(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Section == "REMOTE" && r.Item == "Remote control");
            Assert.Equal(CheckLight.Red, closedRow.Light);
            Assert.Equal("closed — the bind is not an address", closedRow.Value);
            Assert.Contains("'10.0.0' is not an address", closedRow.Note);

            // The audience listener fails closed the same way, on its own bind, with the control ports untouched.
            vm.State.Control.Bind = "";
            vm.State.Control.AudienceEnabled = true;
            vm.State.Control.AudiencePort = FreePort();
            vm.State.Control.AudienceBind = "phones";
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Control.AudienceListening);
            Assert.Contains("Audience closed — 'phones' is not an address", services.Control.Status);
            Assert.StartsWith("Web remote on port", services.Control.Status);
            Assert.Empty(services.Control.AudienceUrls());
            using (var stillOpen = new Wire(vm.State.Control.TcpPort))
            {
                stillOpen.Send("PING");
                Assert.Equal("OK PONG", stillOpen.ReadResponse());
            }
            var audienceRow = Assert.Single(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Section == "REMOTE" && r.Item == "Audience");
            Assert.Equal(CheckLight.Red, audienceRow.Light);
            Assert.Contains("'phones' is not an address", audienceRow.Note);
            vm.State.Control.AudienceBind = "";
            Dispatcher.UIThread.RunJobs();
            for (var i = 0; i < 400 && !services.Control.AudienceListening; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
            Assert.True(services.Control.AudienceListening);
            Assert.NotEmpty(services.Control.AudienceUrls());
            Assert.DoesNotContain(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Section == "REMOTE" && r.Item == "Audience");
            vm.State.Control.AudienceEnabled = false;

            vm.State.Control.Bind = "";
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("only", services.Control.Status);
            Assert.DoesNotContain("closed", services.Control.Status);
            services.Control.ForgetRemoteUrls();
            Assert.Contains($"http://localhost:{vm.State.Control.HttpPort}/", services.Control.RemoteUrls());
        }
        finally
        {
            vm.State.Control.Bind = "";
            vm.State.Control.AudienceBind = "";
            vm.State.Control.AudienceEnabled = false;
            Dispatcher.UIThread.RunJobs();
        }
    }
}
