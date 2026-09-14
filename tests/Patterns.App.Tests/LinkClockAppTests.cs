using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// One clock across the machines: a stage timer node linked to a desk whose clock runs thirty
/// seconds ahead measures the offset on the beats, its room clock follows the desk's, both sides
/// say the clocks are apart with the fix named, and alone again the clock is the node's own.
/// </summary>
public class LinkClockAppTests
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

    private static void Settings(string dir, Action<ShowState> edit)
    {
        var state = SettingsStore.Fresh();
        edit(state);
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(state));
    }

    private static (NodeHost Host, string Dir) BootNode(NodeKind kind, Action<ShowState> edit)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"patterns-tests-clock-{NodeKinds.Wire(kind)}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Settings(dir, s =>
        {
            s.Control.Enabled = true;
            s.Control.HttpPort = FreePort();
            s.Control.TcpPort = FreePort();
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            edit(s);
        });
        var host = NodeHost.Build(kind, new SettingsStore(dir));
        host.Start();
        return (host, dir);
    }

    [AvaloniaFact]
    public void AFollowerMeasuresTheDesksClockOnTheLinkAndItsRoomClockFollowsIt()
    {
        var twinPort = FreePort();
        var desk = TestApp.Boot("patterns-tests-desk-", dir => Settings(dir, s =>
        {
            s.Name = "Gala";
            s.Twin.Port = twinPort;
            s.Twin.Key = "hunter2";
            s.Control.HttpPort = FreePort();
            s.Control.TcpPort = FreePort();
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
        }));
        NodeHost? node = null;
        var dir = "";
        try
        {
            var d = desk.Services;
            PumpUntil(() => d.Twin.LinkPort == twinPort);
            // The desk's wall clock runs thirty seconds ahead of this machine's, as far as the link can tell.
            var ahead = TimeSpan.FromSeconds(30);
            d.Twin.Clock = () => DateTime.UtcNow + ahead;
            (node, dir) = BootNode(NodeKind.Timer, s => s.Twin.Key = "hunter2");
            var t = node;
            Assert.Equal(TimeSpan.Zero, t.Kernel.Clock.Offset);
            Assert.Null(t.Twin!.ClockOffset);
            var card = new NodeCard("d1", NodeKind.Desk, "FOH-PC", IPAddress.Loopback, "Gala", "", twinPort, 0, TimeSpan.Zero, false);
            t.Twin.LinkTo(card);
            PumpUntil(() => t.Twin.Phase == TwinPhase.InStep);

            // Within a few beats the offset is known on both sides, and the timer's room clock follows the desk's.
            PumpUntil(() => t.Twin.ClockOffset is { } o && Math.Abs((o - ahead).TotalSeconds) < 0.5, 20000);
            PumpUntil(() => Math.Abs((t.Kernel.Clock.Offset - ahead).TotalSeconds) < 0.5, 5000);
            Assert.InRange((t.Kernel.Clock.UtcNow - DateTime.UtcNow - ahead).TotalSeconds, -0.5, 0.5);
            Assert.InRange((t.Stage.UtcNow() - DateTime.UtcNow - ahead).TotalSeconds, -0.5, 0.5);
            Assert.InRange((t.CueStack.NowUtc() - DateTime.UtcNow - ahead).TotalSeconds, -0.5, 0.5);
            Assert.Contains("the desk's clock 30.0 s ahead", t.Twin.Status);
            Assert.StartsWith("CLOCKS 30.0 s APART — ", t.Twin.HealthWords);                          // the desk by the name its WELCOME gave
            Assert.EndsWith("'s clock is ahead; set both machines to one time server", t.Twin.HealthWords);
            Assert.Contains("· the desk's clock 30.0 s ahead", t.Twin.GlanceWords);
            var status = TestApp.Pump(t.NewRouter().ExecuteAsync(ControlProtocol.Parse("TWIN STATUS")));
            Assert.Contains("\"apart\":true", status);
            Assert.Contains("\"followed\":true", status);

            // The desk sees the timer's clock thirty seconds behind, says so on its line and its health words, and TWIN STATUS carries it.
            PumpUntil(() => d.Twin.PeerClocks.Any(c => Math.Abs((c.Offset + ahead).TotalSeconds) < 0.5), 20000);
            Assert.Contains("clock 30.0 s behind", d.Twin.Status);
            Assert.Contains("CLOCKS 30.0 s APART — timer", d.Twin.HealthWords);
            Assert.Contains("· CLOCKS APART", d.Twin.GlanceWords);
            Assert.Contains("\"apart\":true", TestApp.Pump(new CommandRouter(d).ExecuteAsync(ControlProtocol.Parse("TWIN STATUS"))));
            Assert.Equal(TimeSpan.Zero, d.Kernel.Clock.Offset);                                   // the desk is the frame

            // The clocks brought back in step: the words clear and the room clock follows.
            d.Twin.Clock = () => DateTime.UtcNow;
            PumpUntil(() => t.Twin.ClockOffset is { } o2 && Math.Abs(o2.TotalSeconds) < 0.5, 25000);
            PumpUntil(() => Math.Abs(t.Kernel.Clock.Offset.TotalSeconds) < 0.5, 5000);
            PumpUntil(() => d.Twin.PeerClocks.All(c => Math.Abs(c.Offset.TotalSeconds) < 0.5), 25000);
            Assert.Equal("", t.Twin.HealthWords);
            Assert.Equal("", d.Twin.HealthWords);
            Assert.DoesNotContain("s ahead", t.Twin.Status);
            Assert.DoesNotContain("APART", d.Twin.Status);

            // UNLINK: alone, the clock is this node's own again.
            Assert.StartsWith("Unlinked", t.Twin.Unlink());
            PumpUntil(() => t.Twin.Phase == TwinPhase.Off);
            Assert.Equal(TimeSpan.Zero, t.Kernel.Clock.Offset);
            Assert.Null(t.Twin.ClockOffset);
        }
        finally
        {
            node?.Dispose();
            desk.Dispose();
            if (dir.Length > 0) { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
        }
    }
}
