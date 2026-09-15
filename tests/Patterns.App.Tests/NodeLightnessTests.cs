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
            // The invariant that holds in any order: a timer node's boot never pulls the NDI runtime in. Measured alone, the
            // node also loads the arcade and the room (every node builds them, the charter's next cut) and never NDI.
            if (!before.Contains("Patterns.Ndi")) Assert.DoesNotContain("Patterns.Ndi", loaded);
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
}
