using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Arcade;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

public class ArcadeAppTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static bool CanConnect(int port)
    {
        try
        {
            using var probe = new TcpClient();
            probe.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
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
        var s = SettingsStore.Fresh();
        edit(s);
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
    }

    [AvaloniaFact]
    public void AnArcadeNodeRunsTheGameFromTheWireTheKeyboardAndThePhonePad()
    {
        var http = FreePort();
        var b = TestApp.Boot("patterns-tests-arcade-", dir => Settings(dir, s =>
        {
            s.Name = "Gala";
            s.Control.Enabled = true;
            s.Control.HttpPort = http;
            s.Control.TcpPort = FreePort();
            s.Twin.AcceptCallers = false;
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
        }), NodeKind.Arcade);
        try
        {
            var (services, vm, _) = b;
            Assert.True(vm.IsArcadeNode);
            Assert.Equal("arcade", services.Beacon.Build().Kind);
            Assert.Equal(new[] { "Arcade", "Nodes", "Machine", "Help" }, Shell.PagesFor(NodeKind.Arcade).Select(p => p.Header));
            Assert.Equal(Shell.IndexOf("Arcade"), Shell.HomePage(NodeKind.Arcade));    // the node opens on the game
            Assert.Contains("arcade node", services.OutputsHeldBy);
            Assert.True(services.Arcade.IsRunning);                                   // the loop from the first frame
            PumpUntil(() => services.Arcade.Frames > 5);
            Assert.Equal(ArcadePhase.Idle, services.Arcade.Phase);
            Assert.StartsWith("Idle — no game", vm.ArcadeWords);

            var router = new CommandRouter(services);
            string Wire(string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

            // A match from the wire: two people, the picture at a size, a key tapped and released by the loop.
            Assert.StartsWith("OK", Wire("ARCADE START pong 2"));
            Assert.Equal(ArcadePhase.Playing, services.Arcade.Phase);
            var snap = services.Arcade.Snapshot();
            Assert.Equal("pong", snap.GameId);
            Assert.Equal(new[] { true, true, false, false }, snap.Humans);
            Assert.StartsWith("OK", Wire("ARCADE SIZE 640x360"));
            Assert.Equal((640, 360), (services.Arcade.Width, services.Arcade.Height));
            Assert.StartsWith("OK", Wire("ARCADE KEY 1 UP TAP"));
            Assert.True(services.Arcade.Pressed(0).HasFlag(PadButtons.Up));
            PumpUntil(() => services.Arcade.Pressed(0) == PadButtons.None);         // the tap's release, a tenth of a second of steps later
            Assert.StartsWith("ERR", Wire("ARCADE KEY 1 SELECT"));
            Assert.StartsWith("ERR", Wire("ARCADE START tetris"));
            var status = Wire("ARCADE STATUS");
            Assert.StartsWith("OK {", status);
            Assert.Contains("\"phase\":\"playing\"", status);
            Assert.Contains("\"game\":\"pong\"", status);
            Assert.Contains("\"width\":640", status);
            Assert.Contains("\"id\":\"snake\"", Wire("ARCADE GAMES"));
            Assert.Contains("\"game\":\"pong\"", Wire("ARCADE SCORES"));
            Assert.StartsWith("ERR", Wire("ARCADE NAME ABC"));                        // no score yet

            // The keyboard as pads, and the pages: the pad is served, a press lands, the status answers.
            services.Arcade.Key(1, PadButtons.Down, true);
            Assert.True(services.Arcade.Pressed(1).HasFlag(PadButtons.Down));
            services.Arcade.Key(1, PadButtons.Down, false);
            Assert.Equal(PadButtons.None, services.Arcade.Pressed(1));
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{http}/") };
            Assert.Contains("Patterns Arcade pad", TestApp.Pump(client.GetStringAsync("pad")));
            Assert.Contains("/pad", TestApp.Pump(client.GetStringAsync("/")));
            var pressed = TestApp.Pump(client.PostAsync("api/arcade/key", new StringContent("2 START DOWN")).ContinueWith(t => t.Result.Content.ReadAsStringAsync().Result));
            Assert.Contains("\"ok\":true", pressed);
            Assert.True(services.Arcade.Pressed(1).HasFlag(PadButtons.Start));
            Assert.Contains("\"phase\":\"paused\"", TestApp.Pump(client.GetStringAsync("api/arcade")));   // START mid-match pauses
            TestApp.Pump(client.PostAsync("api/arcade/key", new StringContent("2 START UP")));

            // Pause and resume by the verbs, the house, and the title card; NDI asks for a runtime this machine may not have — a status, never a fault.
            Assert.StartsWith("OK", Wire("ARCADE RESUME"));
            Assert.Equal(ArcadePhase.Playing, services.Arcade.Phase);
            Assert.StartsWith("OK", Wire("ARCADE PAUSE"));
            Assert.StartsWith("ERR", Wire("ARCADE PAUSE"));
            Assert.StartsWith("OK", Wire("ARCADE ATTRACT breakout"));
            Assert.Equal(ArcadePhase.Attract, services.Arcade.Phase);
            Assert.Equal("breakout", services.Arcade.Snapshot().GameId);
            Assert.StartsWith("OK", Wire("ARCADE NDI ON"));
            Assert.True(services.Arcade.NdiOn);
            PumpUntil(() => services.Arcade.Status.Contains("NDI") && !services.Arcade.Status.Contains("starting"));
            Assert.StartsWith("OK", Wire("ARCADE NDI OFF"));
            Assert.False(services.Arcade.NdiOn);
            Assert.StartsWith("OK", Wire("ARCADE STOP"));
            Assert.Equal(ArcadePhase.Idle, services.Arcade.Phase);
            services.Arcade.PickGame(2);
            Assert.Equal("snake", services.Arcade.Snapshot().GameId);
            Assert.Equal(ArcadePhase.Attract, services.Arcade.Phase);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ADeskSendsArcadeVerbsToTheArcadeNodesItHearsAndAsksThemForStatus()
    {
        var desk = TestApp.Boot("patterns-tests-desk-arc-", dir => Settings(dir, s =>
        {
            s.Name = "Gala";
            s.Control.Enabled = false;
            s.Twin.AcceptCallers = false;
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
        }));
        TestApp.Booted? node = null;
        try
        {
            var d = desk.Services;
            // No arcade heard: the desk runs the game itself, on its own Arcade page.
            Assert.False(d.Arcade.IsRunning);
            var local = d.Actions.Execute(new ShowAction(ShowActionKind.ArcadeStart, "", "pong"), ActionOrigin.Desk);
            Assert.True(local.Ok, local.Message);
            Assert.Equal(ArcadePhase.Playing, d.Arcade.Phase);
            Assert.True(d.Arcade.IsRunning);
            Assert.StartsWith("OK {", TestApp.Pump(new CommandRouter(d).ExecuteAsync(ControlProtocol.Parse("ARCADE STATUS"))));
            Assert.True(d.Actions.Execute(new ShowAction(ShowActionKind.ArcadeStop), ActionOrigin.Desk).Ok);
            Assert.Equal(ArcadePhase.Idle, d.Arcade.Phase);

            var wire = FreePort();
            node = TestApp.Boot("patterns-tests-arcade2-", dir => Settings(dir, s =>
            {
                s.Name = "Gala";
                s.Control.Enabled = true;
                s.Control.HttpPort = FreePort();
                s.Control.TcpPort = wire;
                s.Twin.AcceptCallers = false;
                s.Watchdog.BeaconListenPort = FreePort();
                s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            }), NodeKind.Arcade);
            var n = node.Services;
            PumpUntil(() => CanConnect(wire));

            // The desk hears the node's beacon (as the network would carry it) and its card has the wire.
            d.Beacon.Hear(n.Beacon.Build() with { Machine = "HUB-PC" }, new IPEndPoint(IPAddress.Loopback, 9700));
            d.Nodes.Poll();
            var card = Assert.Single(d.Nodes.Arcades());
            Assert.Equal(wire, card.WirePort);
            Assert.Equal("HUB-PC", card.Name);

            var sent = d.Actions.Execute(new ShowAction(ShowActionKind.ArcadeStart, "", "snake 1"), ActionOrigin.Desk);
            Assert.True(sent.Ok, sent.Message);
            Assert.Contains("1 arcade node: HUB-PC", sent.Message);
            PumpUntil(() => n.Arcade.Phase == ArcadePhase.Playing);
            Assert.Equal("snake", n.Arcade.Snapshot().GameId);
            Assert.Equal(ArcadePhase.Idle, d.Arcade.Phase);                            // the desk's own stayed idle: the node has it

            var status = TestApp.Pump(new CommandRouter(d).ExecuteAsync(ControlProtocol.Parse("ARCADE STATUS")));
            Assert.StartsWith("OK [", status);
            Assert.Contains("\"node\":\"HUB-PC\"", status);
            Assert.Contains("\"phase\":\"playing\"", status);

            // A cue on the desk carries the verb too.
            var stack = CueStacks.Caller(desk.Vm.State);
            var cue = new RunCueConfig { Number = "01", Name = "Games" };
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ArcadeStop });
            stack.Cues.Add(cue);
            Assert.True(d.Actions.FireCue(cue, ActionOrigin.Desk).Ok);
            PumpUntil(() => n.Arcade.Phase == ArcadePhase.Idle);
        }
        finally
        {
            node?.Dispose();
            desk.Dispose();
        }
    }
}
