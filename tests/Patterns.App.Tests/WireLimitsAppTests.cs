using Patterns.Devices;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The wire's ceilings: so many Companion connections in all and from one address, a line that
/// started given so long to end, the web remote's port under the same two ceilings — each refusal
/// said once in the wire's own words and the door closed, an idle client never cut, the node untouched.
/// </summary>
public class WireLimitsAppTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the condition never held");
        }
    }

    private static void Wait(Task task) => TestApp.Pump(task.ContinueWith(t => { if (t.IsFaulted) throw t.Exception!; return 0; }));

    /// <summary>An arcade node in a fresh folder with its own ports, started: the cheapest host with a wire.</summary>
    private static (NodeHost Host, string Dir) BootNode()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-tests-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var s = SettingsStore.Fresh();
        s.Control.Enabled = true;
        s.Control.HttpPort = FreePort();
        s.Control.TcpPort = FreePort();
        s.Watchdog.BeaconListenPort = FreePort();
        s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        var host = NodeHost.Build(NodeKind.Arcade, new SettingsStore(dir));
        host.Start();
        return (host, dir);
    }

    private static void Clean(NodeHost host, string dir)
    {
        host.Dispose();
        try { Directory.Delete(dir, true); } catch { /* a log still open */ }
    }

    /// <summary>One Companion: a socket and a line reader on it.</summary>
    private sealed class Wire : IDisposable
    {
        public Wire(TcpClient client)
        {
            Client = client;
            Reader = new BoundedLineReader(client.GetStream(), 1 << 20);
        }

        public TcpClient Client { get; }
        public BoundedLineReader Reader { get; }

        public Task<string?> Line(int timeoutMs = 6000) => Reader.ReadLineAsync(new CancellationTokenSource(timeoutMs).Token);

        public Task Send(string text) => Client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

        public void Dispose() => Client.Dispose();
    }

    private static Wire Dial(int port)
    {
        var client = new TcpClient();
        Wait(client.ConnectAsync(IPAddress.Loopback, port));
        return new Wire(client);
    }

    [AvaloniaFact]
    public void TheWireKeepsSoManyConnectionsAndSaysBusyToTheNextInItsOwnWords()
    {
        var (host, dir) = BootNode();
        try
        {
            host.Control.WireLimits = new WireLimits(MaxClients: 2, MaxClientsPerAddress: 2);
            var port = host.State.Control.TcpPort;
            using var a = Dial(port);
            using var b = Dial(port);
            Assert.StartsWith("STATE ", TestApp.Pump(a.Line()));
            Assert.StartsWith("STATE ", TestApp.Pump(b.Line()));
            Assert.Equal(2, host.Control.WireConnections);
            // The third reads which ceiling, and the door closes on it; the two already in still answer.
            using var c = Dial(port);
            Assert.Equal("ERR busy — 2 connections already open from this address; close one first", TestApp.Pump(c.Line()));
            Assert.Null(TestApp.Pump(c.Line()));
            Wait(a.Send("PING\n"));
            Assert.Equal("OK PONG", TestApp.Pump(a.Line()));
            Assert.Equal(2, host.Control.WireConnections);
            // One closed, the next is admitted.
            a.Dispose();
            PumpUntil(() => host.Control.WireConnections == 1);
            using var d = Dial(port);
            Assert.StartsWith("STATE ", TestApp.Pump(d.Line()));
            Assert.Equal(2, host.Control.WireConnections);
        }
        finally
        {
            Clean(host, dir);
        }
    }

    [AvaloniaFact]
    public void AHalfLineIsCutWhenItsSecondsAreUpAndAnIdleClientNever()
    {
        var (host, dir) = BootNode();
        try
        {
            host.Control.WireLimits = new WireLimits(LineSeconds: 0.5);
            var port = host.State.Control.TcpPort;
            using var idle = Dial(port);
            using var half = Dial(port);
            Assert.StartsWith("STATE ", TestApp.Pump(idle.Line()));
            Assert.StartsWith("STATE ", TestApp.Pump(half.Line()));
            var clock = Stopwatch.StartNew();
            Wait(half.Send("PIN"));                                                  // a line that never ends
            Assert.Equal("ERR a line that did not end within 0.5 s — the wire's lines are commands, and this one was not; closed", TestApp.Pump(half.Line()));
            Assert.InRange(clock.ElapsedMilliseconds, 400, 4000);
            Assert.Null(TestApp.Pump(half.Line()));
            // The idle one sat through more than a line's seconds without a byte, and still answers.
            Thread.Sleep(800);
            Wait(idle.Send("PING\n"));
            Assert.Equal("OK PONG", TestApp.Pump(idle.Line()));
            PumpUntil(() => host.Control.WireConnections == 1);
        }
        finally
        {
            Clean(host, dir);
        }
    }

    [AvaloniaFact]
    public void TheWebRemoteKeepsSoManyConnectionsFromOneAddressAndSaysBusyWithA503()
    {
        var (host, dir) = BootNode();
        try
        {
            host.Control.WireLimits = new WireLimits(MaxHttpClientsPerAddress: 1);
            host.Control.ControlLimits = HttpLimits.Control with { HeadSeconds = 30 };
            var port = host.State.Control.HttpPort;
            // One connection holds the address's one slot with half a request head.
            using var holder = Dial(port);
            Wait(holder.Send("GET / HTTP/1.1\r\nHost: node\r\n"));
            PumpUntil(() => host.Control.HttpConnections == 1);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
            var busy = TestApp.Pump(http.GetAsync("/"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.Equal("busy", TestApp.Pump(busy.Content.ReadAsStringAsync()));
            Assert.Equal(1, host.Control.HttpConnections);
            // The holder gone, the next request is answered.
            holder.Dispose();
            PumpUntil(() => host.Control.HttpConnections == 0);
            var ok = TestApp.Pump(http.GetAsync("/"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Contains("Arcade node", TestApp.Pump(ok.Content.ReadAsStringAsync()));
        }
        finally
        {
            Clean(host, dir);
        }
    }
}
