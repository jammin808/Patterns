using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Play;
using Patterns.Arcade;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A node's Machine tab: what it is, where the desk's key goes, its ports, what RESTART and an
/// update need — and the link's words sending the operator there, not to a page a node never
/// had. And the audience's network profile: a room behind one address turned away on a flat
/// network, named on the line with the fix, and seated under the venue NAT profile.
/// </summary>
public class NodeMachineAppTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static (NodeHost Host, string Dir) BootNode(NodeKind kind, Action<ShowState>? edit = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"patterns-tests-machine-{NodeKinds.Wire(kind)}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var s = SettingsStore.Fresh();
        s.Control.Enabled = true;
        s.Control.HttpPort = FreePort();
        s.Control.TcpPort = FreePort();
        s.Watchdog.BeaconListenPort = FreePort();
        s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
        edit?.Invoke(s);
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        var host = NodeHost.Build(kind, new SettingsStore(dir));
        host.Start();
        return (host, dir);
    }

    private static void Clean(NodeHost host, string dir)
    {
        host.Dispose();
        try { Directory.Delete(dir, true); } catch { /* a log still open */ }
    }

    [AvaloniaFact]
    public void ACallerNodesMachineTabSaysWhatItIsWhereTheKeyGoesAndWhatARestartNeeds()
    {
        var (host, dir) = BootNode(NodeKind.Caller);
        try
        {
            var vm = new NodeViewModel(host);
            Assert.StartsWith("Caller node · ", vm.MachineIdentity);
            Assert.Contains($"build {UpdateService.RunningVersion}", vm.MachineIdentity);
            Assert.Contains(dir, vm.MachineIdentity);
            Assert.Contains($"HTTP {host.State.Control.HttpPort}", vm.MachinePorts);
            Assert.Contains($"Companion (TCP) {host.State.Control.TcpPort}", vm.MachinePorts);
            Assert.Contains("audience port off", vm.MachinePorts);
            Assert.StartsWith("Web remote on port", vm.MachineWireStatus);
            Assert.StartsWith("http://", vm.MachineFrontDoor);
            Assert.True(vm.HasLink);
            Assert.StartsWith("No key yet", vm.MachineKeyWords);
            host.BulkEdit(() => host.State.Twin.Key = "abc123");
            Assert.StartsWith("The key is set", vm.MachineKeyWords);
            // Without the watchdog a restart and an update say what they need; nothing exits.
            Assert.StartsWith("Without the watchdog", vm.MachineWatchdog);
            vm.RestartCommand.Execute(null);
            Assert.Contains("needs the watchdog", vm.StatusMessage);
            vm.ApplyUpdateCommand.Execute(null);
            Assert.StartsWith("Nothing to apply", vm.StatusMessage);
            Assert.StartsWith("Nothing staged", vm.UpdateStatus);
            Assert.Contains("no outputs, ever", vm.MachineHelp);
            // The link's words send the operator to this tab.
            var card = new NodeCard("d1", NodeKind.Desk, "FOH-PC", IPAddress.Loopback, "Gala", "", FreePort(), 9696, TimeSpan.Zero, false);
            var words = host.Twin!.LinkTo(card);
            Assert.StartsWith("Linking to FOH-PC", words);
            Assert.Contains("this node's Machine tab", words);
            host.Twin.Unlink();
            // The window shows the tab, and the poll keeps its words.
            var window = new NodeWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            vm.SelectedTab = NodeViewModel.MachineTab;
            Dispatcher.UIThread.RunJobs();
            Assert.NotEmpty(window.GetVisualDescendants().OfType<NodeMachineSection>());
            vm.Poll();
            window.Close();
            // Remote control off: the ports line says the desk cannot reach it, and the front door is closed.
            host.BulkEdit(() => host.State.Control.Enabled = false);
            Assert.StartsWith("Remote control off", vm.MachinePorts);
            Assert.Equal("", vm.MachineFrontDoor);
            vm.OpenFrontDoorCommand.Execute(null);
            Assert.StartsWith("Remote control is off", vm.StatusMessage);
        }
        finally
        {
            Clean(host, dir);
        }
    }

    [AvaloniaFact]
    public void TheArcadeNodeHasNoKeyAndItsWordsSayWhatItsPortsCarry()
    {
        var (host, dir) = BootNode(NodeKind.Arcade, s =>
        {
            s.Control.AudienceEnabled = true;
            s.Control.AudiencePort = FreePort();
            s.Control.AudienceMaxPlayers = 250;
        });
        try
        {
            var vm = new NodeViewModel(host);
            Assert.False(vm.HasLink);
            Assert.StartsWith("Arcade node · ", vm.MachineIdentity);
            Assert.Contains("phone pad", vm.MachinePorts);
            Assert.Contains($"audience {host.State.Control.AudiencePort} — the phones' play pages, 250 seats", vm.MachinePorts);
            Assert.Contains("no key", vm.MachineHelp);
        }
        finally
        {
            Clean(host, dir);
        }
    }

    [AvaloniaFact]
    public void ARoomBehindOneAddressIsTurnedAwayOnAFlatNetworkNamedOnTheLineAndSeatedUnderTheVenueNatProfile()
    {
        var (host, dir) = BootNode(NodeKind.Arcade, s =>
        {
            s.Control.AudienceEnabled = true;
            s.Control.AudiencePort = FreePort();
            s.Control.AudienceMaxPlayers = 300;
        });
        try
        {
            var play = host.Play;
            Assert.Equal(20, play.Effective.JoinsPerAddressPerMinute);
            for (var i = 0; i < 20; i++) Assert.Contains("\"ok\":true", play.JoinJson($"{{\"nick\":\"Phone {i}\"}}", "10.0.0.1"));
            Assert.Contains("Too many joins", play.JoinJson("{\"nick\":\"Phone 21\"}", "10.0.0.1"));
            Assert.Contains("\"ok\":true", play.JoinJson("{\"nick\":\"Elsewhere\"}", "10.0.0.2"));      // another address is not the flood
            Assert.Contains("1 join refused this minute (all from 10.0.0.1) — phones behind one address? Remote page, AUDIENCE: Network → venue NAT", play.Words);
            var json = play.StatusJson("audience");
            Assert.Contains("\"network\":\"flat\"", json);
            Assert.Contains("\"joinsRefused\":1", json);
            Assert.Contains("\"refusedFrom\":\"10.0.0.1\"", json);
            Assert.Contains("\"JoinsPerAddressPerMinute\":20", json);
            // The profile: the per-address ceiling opens to the room, the per-phone ones stand, and the next phone from that address is seated.
            var flat = play.Effective;
            host.BulkEdit(() => host.State.Control.AudienceNetwork = AudienceNetwork.VenueNat);
            var nat = play.Effective;
            Assert.Equal(620, nat.JoinsPerAddressPerMinute);
            Assert.Equal(nat.MaxConnections, nat.MaxConnectionsPerAddress);
            Assert.Equal(flat.AnswersPerTokenPerMinute, nat.AnswersPerTokenPerMinute);
            Assert.Contains("\"ok\":true", play.JoinJson("{\"nick\":\"Phone 22\"}", "10.0.0.1"));
            Assert.Contains("\"network\":\"venue-nat\"", play.StatusJson("audience"));
            Assert.Contains("\"JoinsPerAddressPerMinute\":620", play.StatusJson("audience"));
            Assert.Contains("the room's own joins-per-minute reached", play.Words);           // the minute's refusal is still on the line, in the profile's words
            Assert.Equal(22, play.Room.PlayerCount);
        }
        finally
        {
            Clean(host, dir);
        }
    }
}
