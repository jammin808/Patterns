using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Services;
using Patterns.Devices;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>OSC end to end: real datagrams against the live app — a command in, the answer and the feedback out, a bundle, the switch off.</summary>
public class OscAppTests
{
    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    /// <summary>Pumps the UI thread until the condition holds.</summary>
    private static void PumpUntil(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the condition never held");
        }
    }

    /// <summary>The next message on a socket that satisfies the test, pumping the UI thread meanwhile.</summary>
    private static OscMessage Receive(UdpClient socket, Func<OscMessage, bool> wanted, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            while (socket.Available > 0)
            {
                IPEndPoint? from = null;
                var packet = socket.Receive(ref from!);
                foreach (var m in OscCodec.Decode(packet))
                {
                    if (wanted(m)) return m;
                }
            }
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        throw new TimeoutException("the message never came");
    }

    /// <summary>The next line from the wire that is not a STATE push.</summary>
    private static string NextLine(StreamReader reader, int timeoutMs = 8000)
    {
        while (true)
        {
            var read = reader.ReadLineAsync();
            PumpUntil(() => read.IsCompleted, timeoutMs);
            var line = read.Result ?? throw new IOException("the wire closed");
            if (!line.StartsWith("STATE ", StringComparison.Ordinal)) return line;
        }
    }

    /// <summary>One request to the web remote, answered with its status line.</summary>
    private static string HttpStatus(int port, string method, string path, params (string Name, string Value)[] headers)
    {
        using var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback, port);
        PumpUntil(() => connect.IsCompleted);
        using var stream = client.GetStream();
        var head = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: localhost:{port}\r\nContent-Length: 0\r\n");
        foreach (var (name, value) in headers) head.Append($"{name}: {value}\r\n");
        stream.Write(Encoding.UTF8.GetBytes(head.Append("\r\n").ToString()));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var first = reader.ReadLineAsync();
        PumpUntil(() => first.IsCompleted);
        var status = first.Result ?? "";
        return status[(status.IndexOf(' ', StringComparison.Ordinal) + 1)..];
    }

    [AvaloniaFact]
    public void OscDrivesTheShowAnswersTheSenderAndFeedsBack()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var cfg = vm.State.Control;
            cfg.HttpPort = FreeTcpPort();
            cfg.TcpPort = FreeTcpPort();
            using var feedback = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var oscPort = FreeUdpPort();
            cfg.OscPort = oscPort;
            cfg.OscFeedbackHost = "127.0.0.1";
            cfg.OscFeedbackPort = ((IPEndPoint)feedback.Client.LocalEndPoint!).Port;
            cfg.OscEnabled = true;
            Dispatcher.UIThread.RunJobs(); // publish → the port opens
            Assert.StartsWith($"OSC in on port {oscPort} · feedback to 127.0.0.1:", services.Osc.Status);
            Assert.NotNull(services.Osc.FeedbackEndpoint);

            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var target = new IPEndPoint(IPAddress.Loopback, oscPort);
            void Send(OscMessage m) => sender.Send(OscCodec.Encode(m), target);

            // A command in: the show changes, the feedback bundle says so.
            Send(OscMessage.Of("/patterns/blackout", 1));
            PumpUntil(() => services.State.Blackout);
            var fed = Receive(feedback, m => m.Address == "/patterns/state/blackout" && Equals(m.Args[0], 1));
            Assert.Equal(1, fed.Args[0]);
            Assert.Contains("/patterns/blackout 1 → BLACKOUT ON → OK", services.Osc.LastLine);
            Assert.True(services.Osc.Received >= 1);

            // Answers to the sender: pong, an error for a refused command, an error for an address that is not Patterns'.
            Send(OscMessage.Of("/patterns/ping"));
            Receive(sender, m => m.Address == "/patterns/pong");
            Send(OscMessage.Of("/patterns/look", 5));
            var refused = Receive(sender, m => m.Address == "/patterns/error");
            Assert.StartsWith("ERR", refused.Text());
            Send(OscMessage.Of("/patterns/nonsense", 1));
            var unknown = Receive(sender, m => m.Address == "/patterns/error" && (m.Text() ?? "").Contains("unknown address"));
            Assert.Contains("/patterns/nonsense", unknown.Text());
            Send(OscMessage.Of("/patterns/status"));
            var status = Receive(sender, m => m.Address == "/patterns/status");
            Assert.Contains("\"blackout\":true", status.Text());

            // A bundle: two commands in one datagram, in order.
            sender.Send(OscCodec.EncodeBundle(new[] { OscMessage.Of("/patterns/blackout", "off"), OscMessage.Of("/patterns/duck/on") }), target);
            PumpUntil(() => !services.State.Blackout && services.State.Stingers.DuckActive);
            Receive(feedback, m => m.Address == "/patterns/state/duck" && Equals(m.Args[0], 1));
            Assert.True(services.Osc.Sent >= 4);

            // The page's line; the switch off closes the port and a datagram goes nowhere.
            Assert.Contains($"OSC in on port {oscPort}", services.Osc.StatusLine);
            Assert.Contains(" in, ", services.Osc.StatusLine);
            Assert.Contains("Last:", services.Osc.StatusLine);
            cfg.OscEnabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("OSC off.", services.Osc.Status);
            Assert.Null(services.Osc.FeedbackEndpoint);
            Send(OscMessage.Of("/patterns/blackout", 1));
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(50);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>Round 79: the desk's bind address holds for OSC too — the socket opens on that address alone and the status says so.</summary>
    [AvaloniaFact]
    public void OscListensOnTheDesksBindAddressAlone()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var cfg = vm.State.Control;
            cfg.HttpPort = FreeTcpPort();
            cfg.TcpPort = FreeTcpPort();
            var oscPort = FreeUdpPort();
            cfg.OscPort = oscPort;
            cfg.Bind = "127.0.0.1";
            cfg.OscEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith($"OSC in on port {oscPort} on 127.0.0.1 only", services.Osc.Status);

            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var target = new IPEndPoint(IPAddress.Loopback, oscPort);
            sender.Send(OscCodec.Encode(OscMessage.Of("/patterns/blackout", 1)), target);
            PumpUntil(() => services.State.Blackout);

            // An unknown switch word is answered as an error and moves nothing (round 79: the map fails closed).
            sender.Send(OscCodec.Encode(OscMessage.Of("/patterns/blackout/offf")), target);
            var error = Receive(sender, m => m.Address == "/patterns/error" && (m.Text() ?? "").Contains("unknown address"));
            Assert.Contains("/patterns/blackout/offf", error.Text());
            Assert.True(services.State.Blackout);

            // Round 85 (P1-06): a bind that is not an address closes the port — never every interface — and the status says why.
            cfg.Bind = "10.0.0";
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("OSC closed — '10.0.0' is not an address", services.Osc.Status);
            using (var again = new UdpClient(new IPEndPoint(IPAddress.Loopback, oscPort)))
            {
                Assert.NotNull(again);                                                             // the port is free: nothing is listening on it
            }

            // The bind cleared: the port re-opens on every interface, and the status says so no more.
            cfg.Bind = "";
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith($"OSC in on port {oscPort} · ", services.Osc.Status);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// A proof grants nothing to an address (ADR-017). A machine that proved the token on the wire (AUTH-PROOF) and on the
    /// web (the token header) is still refused over OSC from that address: the sender hears why, nothing runs, the Remote
    /// page's line says "not on OSC ALLOW" and the log names the address once. Its ping stays open, and its STATUS — the
    /// show's state — answers the addresses its verbs come from (ADR-016), refused with its own words and log line. The operator's
    /// list lets it in; this machine moves the show when the desk trusts its own. The Super Check's OSC row and the Eye
    /// say the rule and what the list names.
    /// </summary>
    [AvaloniaFact]
    public void AProofOnTheWireOrTheWebGrantsItsAddressNothingOverOsc()
    {
        const string token = "K7QM-3XWD-P9RA";
        var b = TestApp.Boot();
        var (services, cfg) = (b.Services, b.Vm.State.Control);
        var (trusted, plain, oscTrusted) = (ControlService.TrustLoopback, ControlService.PlainAuthOnLoopback, OscService.ControlTrustsLoopback);
        ControlService.TrustLoopback = false;                                     // loopback plays another machine on every door
        ControlService.PlainAuthOnLoopback = false;
        OscService.ControlTrustsLoopback = false;
        try
        {
            cfg.HttpPort = FreeTcpPort();
            cfg.TcpPort = FreeTcpPort();
            var oscPort = FreeUdpPort();
            cfg.OscPort = oscPort;
            cfg.OscEnabled = true;
            cfg.Token = token;
            Dispatcher.UIThread.RunJobs();

            // The wire: AUTH-PROOF against this connection's nonce — paired.
            using var wire = new TcpClient();
            var connect = wire.ConnectAsync(IPAddress.Loopback, cfg.TcpPort);
            PumpUntil(() => connect.IsCompleted);
            using var stream = wire.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var nonce = NextLine(reader);
            Assert.StartsWith("NONCE ", nonce);
            stream.Write(Encoding.UTF8.GetBytes("AUTH-PROOF " + WireProof.Proof(token, nonce["NONCE ".Length..], "") + "\n"));
            Assert.Equal("OK paired", NextLine(reader));

            // The web: the token in its header — answered as paired.
            Assert.Equal("200 OK", HttpStatus(cfg.HttpPort, "GET", "/api/state", ("X-Patterns-Token", token)));

            // OSC from the same address: refused, with the words; nothing ran; the line and the log say so, the log once.
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var target = new IPEndPoint(IPAddress.Loopback, oscPort);
            void Send(OscMessage m) => sender.Send(OscCodec.Encode(m), target);
            var log = Path.Combine(b.Dir, "patterns.log");
            int Said() => File.ReadAllLines(log).Count(l => l.Contains("OSC: a verb from 127.0.0.1 refused", StringComparison.Ordinal));
            int SaidRead() => File.ReadAllLines(log).Count(l => l.Contains("OSC: STATUS from 127.0.0.1 refused", StringComparison.Ordinal));
            var saidBefore = Said();
            var saidReadBefore = SaidRead();
            var refusedBefore = services.Osc.Refused;

            Send(OscMessage.Of("/patterns/blackout", 1));
            var refused = Receive(sender, m => m.Address == "/patterns/error");
            Assert.Equal(OscGate.NotListed, refused.Text());
            Assert.False(services.State.Blackout);
            Assert.Equal(refusedBefore + 1, services.Osc.Refused);
            Assert.Contains("refused: not on OSC ALLOW", services.Osc.LastLine);
            Send(OscMessage.Of("/patterns/blackout", 1));
            Receive(sender, m => m.Address == "/patterns/error");
            Assert.Equal(refusedBefore + 2, services.Osc.Refused);
            Assert.Contains($" {refusedBefore + 2} refused.", services.Osc.StatusLine, StringComparison.Ordinal);   // the Remote page's line counts them
            Assert.False(services.State.Blackout);
            Assert.Equal(saidBefore + 1, Said());                                   // one line for the address, however often

            // Its ping stays open, as the wire's does; its STATUS answers only whom its verbs come from (ADR-016) — refused here
            // with the words, its own log line once for the address.
            Send(OscMessage.Of("/patterns/ping"));
            Receive(sender, m => m.Address == "/patterns/pong");
            Send(OscMessage.Of("/patterns/status"));
            Assert.Equal(OscGate.ReadNotListed, Receive(sender, m => m.Address == "/patterns/error").Text());
            Send(OscMessage.Of("/patterns/status"));
            Receive(sender, m => m.Address == "/patterns/error");
            Assert.Equal(saidReadBefore + 1, SaidRead());
            Assert.Equal(saidBefore + 1, Said());                                   // the verbs' line stands on its own

            // The Super Check and the Eye: the paired desk's rule, and that the list names no one yet.
            var row = Assert.Single(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Section == "REMOTE" && r.Item == "OSC");
            Assert.Equal(CheckLight.Green, row.Light);
            Assert.Equal($"port {oscPort} · verbs from this machine and OSC ALLOW only", row.Value);
            Assert.Contains(OscGate.PairedRule, row.Note);
            Assert.Contains(OscGate.Listed(0), row.Note);
            services.Eye.Refresh();
            var eye = services.Eye.Graph.Find("osc");
            Assert.NotNull(eye);
            Assert.Contains(eye!.Words, w => w.Contains(OscGate.PairedRule, StringComparison.Ordinal) && w.Contains(OscGate.Listed(0), StringComparison.Ordinal));

            // An entry that is not one whole address lets no one in: named on the Remote page's line, amber on the Super
            // Check and the Eye, never counted as listed.
            cfg.OscAllow = "127.0.0";
            Dispatcher.UIThread.RunJobs();
            Send(OscMessage.Of("/patterns/blackout", 1));
            Receive(sender, m => m.Address == "/patterns/error");
            Assert.False(services.State.Blackout);
            Assert.Contains("OSC ALLOW: '127.0.0' is not an address — left out.", services.Osc.StatusLine, StringComparison.Ordinal);
            var typo = Assert.Single(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Item == "OSC");
            Assert.Equal(CheckLight.Amber, typo.Light);
            Assert.Contains(OscGate.Listed(0, new[] { "127.0.0" }), typo.Note);
            services.Eye.Refresh();
            Assert.Equal(CheckLight.Amber, services.Eye.Graph.Find("osc")!.Light);

            // The operator's list lets the address in, and — the port open on every interface — says a listed address is not a proof.
            cfg.OscAllow = "127.0.0.1";
            Dispatcher.UIThread.RunJobs();
            Send(OscMessage.Of("/patterns/blackout", 1));
            PumpUntil(() => services.State.Blackout);
            Send(OscMessage.Of("/patterns/status"));
            Assert.Contains("\"blackout\":true", Receive(sender, m => m.Address == "/patterns/status").Text());   // listed: its STATUS answers too
            var listed = Assert.Single(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Item == "OSC");
            Assert.Equal(CheckLight.Green, listed.Light);
            Assert.Contains(OscGate.Listed(1), listed.Note);
            Assert.Contains("A listed address is not a proof", listed.Note);
            Assert.DoesNotContain("left out", services.Osc.StatusLine, StringComparison.Ordinal);

            // Off the list again, it is refused again; this machine, trusted as its own, moves the show unlisted.
            cfg.OscAllow = "";
            Dispatcher.UIThread.RunJobs();
            Send(OscMessage.Of("/patterns/blackout", 0));
            Receive(sender, m => m.Address == "/patterns/error");
            Assert.True(services.State.Blackout);
            OscService.ControlTrustsLoopback = true;
            Send(OscMessage.Of("/patterns/blackout", 0));
            PumpUntil(() => !services.State.Blackout);
            Assert.Equal(saidBefore + 1, Said());                                   // still one line for the address
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            ControlService.PlainAuthOnLoopback = plain;
            OscService.ControlTrustsLoopback = oscTrusted;
            cfg.Token = "";
            services.State.Blackout = false;
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }
}
