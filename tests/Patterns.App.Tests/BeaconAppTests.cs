using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The beacon end to end: this machine's heartbeat going out, a main machine heard, its own broadcast ignored, the supervisor's last word, the switch off.</summary>
public class BeaconAppTests
{
    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

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

    /// <summary>The next beacon on a socket that satisfies the test, pumping the UI thread meanwhile (the sender's timer lives there).</summary>
    private static Beacon Receive(UdpClient socket, Func<Beacon, bool> wanted, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            while (socket.Available > 0)
            {
                IPEndPoint? from = null;
                var packet = socket.Receive(ref from!);
                var beacon = Beacon.Parse(packet);
                if (beacon is not null && wanted(beacon)) return beacon;
            }
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        throw new TimeoutException("the beacon never came");
    }

    [AvaloniaFact]
    public void TheBeaconGoesOutAndAMainMachineIsHeard()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var cfg = vm.State.Watchdog;
            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var listenPort = FreeUdpPort();
            cfg.BeaconHost = "127.0.0.1";
            cfg.BeaconPort = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
            cfg.BeaconName = "MAIN-DESK";
            cfg.BeaconListenPort = listenPort;
            cfg.BeaconEnabled = true;
            cfg.BeaconListen = true;
            Dispatcher.UIThread.RunJobs(); // publish → the sender and the listener open
            Assert.True(services.Beacon.Sending);
            Assert.True(services.Beacon.Listening);
            Assert.Contains($"beacon to 127.0.0.1:{cfg.BeaconPort} as MAIN-DESK", services.Beacon.Status);
            Assert.Contains($"listening on port {listenPort}", services.Beacon.Status);
            Assert.StartsWith("Listening for the main machine", services.Beacon.WatchText);

            // The heartbeat arrives and says who and how.
            var beat = Receive(receiver, x => x.Machine == "MAIN-DESK");
            Assert.Equal(services.Beacon.Instance, beat.Instance);
            Assert.False(beat.Live);
            Assert.StartsWith("Up ", beat.Health);
            Assert.True(beat.Seq >= 1);
            Assert.Equal("", beat.Event);
            Assert.True(services.Beacon.Sent >= 1);

            // A main machine on the listen port is heard; this machine's own beacon (the same instance) is not.
            using var main = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var target = new IPEndPoint(IPAddress.Loopback, listenPort);
            main.Send(new Beacon { Machine = "MAIN-DESK", Instance = services.Beacon.Instance, Seq = 99, Utc = DateTime.UtcNow, Live = true }.ToBytes(), target);
            main.Send(new Beacon { Machine = "MAIN-PC", Instance = "abcd1234", Seq = 5, Utc = DateTime.UtcNow, Live = true, Program = "Walk-in", Armed = true, Standby = "01.020 Welcome" }.ToBytes(), target);
            PumpUntil(() => services.Beacon.LastBeacon is not null);
            Assert.Equal("MAIN-PC", services.Beacon.LastBeacon!.Machine);
            Assert.Equal(1, services.Beacon.Heard);
            Assert.StartsWith("Main machine MAIN-PC seen", services.Beacon.WatchText);
            Assert.Contains("live · Walk-in · armed · standby 01.020 Welcome", services.Beacon.WatchText);
            var router = new CommandRouter(services);
            Assert.Contains("\"beacon\":{\"sending\":true,\"listening\":true,\"main\":\"Main machine MAIN-PC seen", router.StateJson());
            Assert.Contains("Main machine MAIN-PC seen", services.Metrics.GatherFacts().BeaconWatch);

            // The supervisor's last word: its watchdog gave up.
            BeaconService.SendEvent(new WatchdogConfig { BeaconHost = "127.0.0.1", BeaconPort = listenPort, BeaconName = "MAIN-PC" }, "gave-up");
            PumpUntil(() => services.Beacon.LastBeacon?.Event == "gave-up");
            Assert.Contains("MAIN MACHINE MAIN-PC: its watchdog gave up", services.Beacon.WatchText);
            Assert.EndsWith("Take over?", services.Beacon.WatchText);

            // Off: nothing goes out, nothing is heard.
            cfg.BeaconEnabled = false;
            cfg.BeaconListen = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Beacon off.", services.Beacon.Status);
            Assert.False(services.Beacon.Sending);
            Assert.False(services.Beacon.Listening);
            Assert.Equal("", services.Beacon.WatchText);
            Assert.Null(services.Beacon.LastBeacon);
        }
        finally
        {
            b.Dispose();
        }
    }
}
