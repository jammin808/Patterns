using Patterns.App.Views;
using Patterns.App.ViewModels;
using Avalonia.Threading;
using Avalonia.Controls;
using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Patterns.App.Tests;

/// <summary>
/// What a role actually loads: the modules map read after a node's boot. The suite's process is
/// shared, so a module another test loaded earlier stays loaded — the assertions here are the
/// ones that hold in any order (the kernel's modules are always there), and the whole set is
/// written to the test's output so a run of this test alone measures a role's real footprint;
/// every start of the app logs the same words, which is the record a support ticket reads.
/// </summary>
public class NodeLightnessTests
{
    private readonly ITestOutputHelper _out;

    public NodeLightnessTests(ITestOutputHelper output) => _out = output;

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [AvaloniaFact]
    public void ATimerNodeBootsOnTheKernelsModulesAndSaysWhatItLoaded()
    {
        var before = Modules.LoadedAssemblies();
        var dir = Path.Combine(Path.GetTempPath(), "patterns-tests-light-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var s = SettingsStore.Fresh();
        s.Control.Enabled = false;
        s.Control.HttpPort = FreePort();
        s.Control.TcpPort = FreePort();
        s.Watchdog.BeaconListenPort = FreePort();
        s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        var host = NodeHost.Build(NodeKind.Timer, new SettingsStore(dir));
        try
        {
            host.Start();
            var loaded = Modules.LoadedAssemblies();
            _out.WriteLine("before the node: " + string.Join(", ", before.OrderBy(n => n, StringComparer.Ordinal)));
            _out.WriteLine("timer node loaded: " + string.Join(", ", loaded.OrderBy(n => n, StringComparer.Ordinal)));
            _out.WriteLine(Modules.Words());
            Assert.Contains("Patterns.Core", loaded);
            Assert.Contains("Patterns.Devices", loaded);                                         // the beacon and DNS-SD are the kernel's
            Assert.Contains("Patterns.Assistant", loaded);                                       // the assistant client is the kernel's
            Assert.Contains("Patterns", loaded);
            Assert.StartsWith("Modules loaded: Core", Modules.Words());
            // The invariants that hold in any order: a timer node's boot never pulls the NDI runtime in, and since round 64
            // neither the room's assembly nor the arcade's — a timer builds neither (NodeKinds.RunsRoom / RunsArcade).
            if (!before.Contains("Patterns.Ndi")) Assert.DoesNotContain("Patterns.Ndi", loaded);
            if (!before.Contains("Patterns.Audience")) Assert.DoesNotContain("Patterns.Audience", loaded);
            if (!before.Contains("Patterns.Arcade")) Assert.DoesNotContain("Patterns.Arcade", loaded);
            var rows = Modules.Rows();
            Assert.All(rows.Where(r => r.Loaded), r => Assert.NotEqual("", r.Version));
            Assert.All(rows.Where(r => !r.Loaded), r => Assert.Equal("", r.Version));
        }
        finally
        {
            host.Dispose();
            try { Directory.Delete(dir, true); } catch { /* a log still open */ }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertTypedReadersAnswer(NodeHost host)
    {
        Assert.NotNull(host.Play);
        Assert.NotNull(host.Arcade);
        Assert.NotEqual("", host.Play!.Code);
    }

    private static string FreshDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-tests-role-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var s = SettingsStore.Fresh();
        s.Control.Enabled = false;
        s.Control.HttpPort = FreePort();
        s.Control.TcpPort = FreePort();
        s.Watchdog.BeaconListenPort = FreePort();
        s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        return dir;
    }

    /// <summary>
    /// Round 64: a role builds only its modules. A timer and a caller have no room and no arcade —
    /// their verbs are refused with "not on this node", their status words are empty — and the
    /// arcade node has both. The timer and the caller boot first, so their claim about the
    /// assemblies is measured before the arcade node loads them; with PATTERNS_FOOTPRINT_STRICT=1
    /// (CI's own process for this class) the claim is absolute.
    /// </summary>
    [AvaloniaFact]
    public void ARoleBuildsOnlyItsModules()
    {
        var strict = Environment.GetEnvironmentVariable("PATTERNS_FOOTPRINT_STRICT") == "1";
        var before = Modules.LoadedAssemblies();
        _out.WriteLine("before the roles: " + string.Join(", ", before.OrderBy(n => n, StringComparer.Ordinal)));
        foreach (var (kind, room, arcade) in new[] { (NodeKind.Timer, false, false), (NodeKind.Caller, false, false), (NodeKind.Arcade, true, true) })
        {
            var dir = FreshDir();
            var host = NodeHost.Build(kind, new SettingsStore(dir));
            try
            {
                host.Start();
                // Naming the room's or the arcade's type here would load its assembly when this method is compiled —
                // before the timer's claim is measured — so the roles are asked, and the typed readers only for the arcade.
                Assert.Equal(room, host.HasRoom);
                Assert.Equal(arcade, host.HasArcade);
                Assert.Equal(NodeKinds.RunsRoom(kind), room);
                if (room) AssertTypedReadersAnswer(host);
                // The window and its view model, as the real process has them (round 65): the tabs the role shows, a
                // poll of the words, the arcade's section built only on an arcade node.
                var vm = new NodeViewModel(host);
                var window = new NodeWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                vm.Poll();
                Assert.Equal(arcade, window.FindControl<TabItem>("ArcadeTab")?.Content is not null);
                window.Close();
                Dispatcher.UIThread.RunJobs();
                var loaded = Modules.LoadedAssemblies();
                _out.WriteLine($"{kind} node loaded: " + string.Join(", ", loaded.OrderBy(n => n, StringComparer.Ordinal)));
                if (!room)
                {
                    if (strict || !before.Contains("Patterns.Audience")) Assert.DoesNotContain("Patterns.Audience", loaded);
                    if (strict || !before.Contains("Patterns.Arcade")) Assert.DoesNotContain("Patterns.Arcade", loaded);
                    var refused = host.Actions.Execute(new ShowAction(ShowActionKind.ArcadeStart, "", "pong"), ActionOrigin.Desk);
                    Assert.False(refused.Ok);
                    var play = host.Actions.Execute(new ShowAction(ShowActionKind.AudienceOn), ActionOrigin.Desk);
                    Assert.False(play.Ok);
                }
            }
            finally
            {
                host.Dispose();
                try { Directory.Delete(dir, true); } catch { /* a log still open */ }
            }
        }
    }
}
