using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.Core.Services;
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
}
