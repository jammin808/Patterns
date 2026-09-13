using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The arcade node built from the kernel alone: no desk, no outputs, no engines — the kernel,
/// the arcade, the room, the wire, a window's worth of view model. Its own verbs answer; the
/// desk's are refused with the reason; the desk finds it on the beacon.
/// </summary>
public class NodeHostTests
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

    /// <summary>A line on the wire: the greeting, then the reply — off the UI thread, pumped.</summary>
    private static (string Greeting, string Reply) Wire(int port, string line)
        => TestApp.Pump(Task.Run(() =>
        {
            using var tcp = new TcpClient();
            tcp.Connect(IPAddress.Loopback, port);
            tcp.ReceiveTimeout = 8000;
            using var s = tcp.GetStream();
            var reader = new StreamReader(s, Encoding.UTF8);
            var greeting = reader.ReadLine() ?? "";
            s.Write(Encoding.UTF8.GetBytes(line + "\n"));
            return (greeting, reader.ReadLine() ?? "");
        }));

    private static string Post(HttpClient http, string path, string body)
        => TestApp.Pump(http.PostAsync(path, new StringContent(body)).ContinueWith(t => t.Result.Content.ReadAsStringAsync().Result));

    [AvaloniaFact]
    public void AnArcadeNodeBootsFromTheKernelAloneAnswersItsVerbsAndRefusesTheDesks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var wire = FreePort();
        var http = FreePort();
        var audience = FreePort();
        var s = SettingsStore.Fresh();
        s.Name = "Foyer games";
        s.Control.Enabled = true;
        s.Control.TcpPort = wire;
        s.Control.HttpPort = http;
        s.Control.AudienceEnabled = true;
        s.Control.AudiencePort = audience;
        s.Twin.AcceptCallers = false;
        s.Watchdog.BeaconListenPort = FreePort();
        s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        NodeHost? host = null;
        try
        {
            host = NodeHost.Build(NodeKind.Arcade, new SettingsStore(dir));
            Assert.Equal(NodeKind.Arcade, host.Kind);
            Assert.False(host.Kernel.IsDesk);
            Assert.Equal("Foyer games", host.State.Name);
            host.Start();
            PumpUntil(() => CanConnect(wire) && host.Control.AudienceListening);

            // The wire greets with the node's own state, and answers the node's verbs.
            var (greeting, pong) = Wire(wire, "PING");
            Assert.StartsWith("STATE {", greeting);
            Assert.Contains("\"node\":true", greeting);
            Assert.Contains("\"kind\":\"arcade\"", greeting);
            Assert.Contains("\"show\":\"Foyer games\"", greeting);
            Assert.Equal("OK PONG", pong);
            Assert.StartsWith("OK", Wire(wire, "ARCADE START pong 1").Reply);
            PumpUntil(() => host.Arcade.Snapshot().GameId == "pong");
            Assert.StartsWith("OK {", Wire(wire, "ARCADE STATUS").Reply);
            Assert.StartsWith("OK", Wire(wire, "PLAY ADD choice Which hall? | A | B").Reply);
            Assert.StartsWith("OK {", Wire(wire, "PLAY STATUS").Reply);
            Assert.StartsWith("OK {", Wire(wire, "NODES STATUS").Reply);

            // The desk's verbs and queries are refused with the reason — the same vocabulary, whose it is said.
            var blackout = Wire(wire, "BLACKOUT ON").Reply;
            Assert.StartsWith("ERR", blackout);
            Assert.Contains("is the desk's — not on this arcade node", blackout);
            Assert.Contains("not on this arcade node", Wire(wire, "TWIN STATUS").Reply);
            Assert.Contains("not on this arcade node", Wire(wire, "CUE LIST").Reply);
            Assert.Contains(host.Kernel.Journal.Tail(6), e => e.Kind == "BlackoutOn" && e.Outcome == "Refused" && e.Message.Contains("desk's"));   // journaled as refused, on the node

            // The room is on the audience port: a phone joins; the node's front door is its own, not the desk's remote.
            using var phone = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{audience}/"), Timeout = TimeSpan.FromSeconds(8) };
            var joined = Post(phone, "api/play/join", $"{{\"nick\":\"Ada\",\"room\":\"{host.Play.Code}\"}}");
            Assert.Contains("\"ok\":true", joined);
            Assert.Equal(1, host.Play.Room.PlayerCount);
            using var browser = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{http}/"), Timeout = TimeSpan.FromSeconds(8) };
            var front = TestApp.Pump(browser.GetStringAsync("/"));
            Assert.Contains("Arcade node", front);
            Assert.Contains("/pad", front);
            Assert.DoesNotContain("BLACKOUT", front);
            Assert.StartsWith("<!doctype", TestApp.Pump(browser.GetStringAsync("/pad")), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.NotFound, TestApp.Pump(browser.GetAsync("/api/stage")).StatusCode);      // the stage pages are a caller's or a timer's, never the arcade's

            // The machine's own services run on a node as on a desk: the updates folder is read, the site checks in when a URL is typed.
            host.Updates.Scan();
            Assert.Contains("Nothing staged", host.Updates.Status);
            Assert.StartsWith("No management URL", host.Management.Status);

            // The beacon says what it is, with nothing on air; the view model reads the same words the desk's page would.
            var packet = host.Kernel.Beacon.Build();
            Assert.Equal("arcade", packet.Kind);
            Assert.False(packet.Live);
            Assert.Equal(wire, packet.Wire);
            var vm = new NodeViewModel(host);
            vm.Poll();
            Assert.Equal("Patterns — Arcade node", vm.WindowTitle);
            Assert.Equal(host.Play.Code, vm.PlayCode);
            Assert.Contains("1 joined", vm.PlayWords);
            Assert.Contains("pong", vm.ArcadeWords, StringComparison.OrdinalIgnoreCase);
            Assert.False(vm.IsDesk);
            vm.PlayQuestionLine = "words One word for today";
            vm.PlayAddCommand.Execute(null);
            Assert.Equal("", vm.PlayQuestionLine);
            host.Notify("a line for the strip");
            Assert.Equal("a line for the strip", vm.StatusMessage);
            Assert.Contains("desk", vm.NodesLinkWords);

            // A setting written on the node moves its state (the tracker fires) and lands in its own folder.
            var published = 0;
            host.SnapshotPublished += () => published++;
            host.State.Control.AudienceMaxPlayers = 77;
            Assert.Equal(1, published);
            host.SaveNow();
            using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "patterns.settings.json"))))
            {
                Assert.Equal(77, doc.RootElement.GetProperty("Control").GetProperty("AudienceMaxPlayers").GetInt32());
            }

            // Shut down: the ports close.
            host.Shutdown();
            PumpUntil(() => !CanConnect(wire), 8000);
        }
        finally
        {
            host?.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TheNodeIsBuiltWithoutTheDesk()
    {
        // The boundary as a test: nothing of the desk is reachable from the node host's type, and
        // its pieces take the kernel, the host or the room — never AppServices.
        var deskOnly = new[] { typeof(AppServices), typeof(OutputWindowManager), typeof(SandboxService), typeof(VideoEngine), typeof(ShowActions), typeof(CommandRouter), typeof(StingerService), typeof(StreamService) };
        foreach (var p in typeof(NodeHost).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.DoesNotContain(p.PropertyType, deskOnly);
        }
        foreach (var t in new[] { typeof(NodeActions), typeof(NodeRouter), typeof(NodeViewModel) })
        {
            foreach (var ctor in t.GetConstructors())
            {
                Assert.DoesNotContain(ctor.GetParameters(), q => q.ParameterType == typeof(AppServices));
            }
        }
        Assert.True(typeof(IWireHost).IsAssignableFrom(typeof(NodeHost)));
        Assert.True(typeof(IPlayHost).IsAssignableFrom(typeof(NodeHost)));
        Assert.True(typeof(ITwinHost).IsAssignableFrom(typeof(NodeHost)));
        Assert.True(typeof(IStageHost).IsAssignableFrom(typeof(NodeHost)));
        Assert.True(typeof(ICueHost).IsAssignableFrom(typeof(NodeHost)));
        Assert.True(typeof(IRunHost).IsAssignableFrom(typeof(NodeHost)));
        Assert.True(typeof(IMachineHost).IsAssignableFrom(typeof(NodeHost)));
        Assert.True(typeof(IActionLayer).IsAssignableFrom(typeof(NodeActions)));
        Assert.True(typeof(IRouter).IsAssignableFrom(typeof(NodeRouter)));
        // And the pages bind to the interfaces both view models implement.
        Assert.True(typeof(IArcadePage).IsAssignableFrom(typeof(MainViewModel)));
        Assert.True(typeof(INodesPage).IsAssignableFrom(typeof(MainViewModel)));
        Assert.True(typeof(IArcadePage).IsAssignableFrom(typeof(NodeViewModel)));
        Assert.True(typeof(INodesPage).IsAssignableFrom(typeof(NodeViewModel)));
        foreach (var page in new[] { typeof(IRunPage), typeof(ICuesPage), typeof(IStagePage) })
        {
            Assert.True(page.IsAssignableFrom(typeof(MainViewModel)), $"the desk shows {page.Name}");
            Assert.True(page.IsAssignableFrom(typeof(NodeViewModel)), $"a node shows {page.Name}");
        }
        // The Run surface and the cue editor take the host contract, never the desk.
        foreach (var t in new[] { typeof(RunViewModel), typeof(CueEditor) })
        {
            foreach (var ctor in t.GetConstructors())
            {
                Assert.DoesNotContain(ctor.GetParameters(), q => q.ParameterType == typeof(AppServices));
                Assert.Contains(ctor.GetParameters(), q => q.ParameterType == typeof(IRunHost));
            }
        }
    }
}
