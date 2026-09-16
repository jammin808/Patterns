using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 62: the show caller calls up lower thirds from the Run surface. The strip is the run
/// page's — the desk's window and a caller node bind the same members: the show's designs as
/// chips, a press to air (or to the preview with PVW FIRST on the desk), HIDE, TAKE. On a node the
/// press goes to the desk as the node's other verbs do.
/// </summary>
public class CallerLowerThirdsTests
{
    [AvaloniaFact]
    public void OnTheDeskAChipPutsTheDesignOnAirAndTheStripFollowsTheShow()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            IRunPage page = vm;
            Assert.False(page.HasLowerThirds);

            var design = vm.NewLowerThird("Clean");
            Dispatcher.UIThread.RunJobs();
            Assert.True(page.HasLowerThirds);
            Assert.Same(vm.State.LowerThirds, page.LowerThirds);
            Assert.Contains(design, page.LowerThirds.Designs);

            // A chip: the design on air, through the action layer.
            page.ChipLowerThirdCommand.Execute(design);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.AirState.LowerThirds.IsShowing);
            Assert.Equal(design.Id, services.AirState.LowerThirds.Active?.Id);

            // HIDE: the design leaves the way it was designed to.
            page.HideLowerThirdCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual("", vm.StatusMessage);

            // PVW FIRST on the desk: the chip goes to the preview instead, and TAKE puts it up.
            vm.IsSandboxActive = true;
            page.LowerThirdChipsToPreview = true;
            page.ChipLowerThirdCommand.Execute(design);
            Dispatcher.UIThread.RunJobs();
            Assert.True(page.HasLowerThirdInPreview);
            page.TakeLowerThirdCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(page.HasLowerThirdInPreview);
            Assert.True(services.AirState.LowerThirds.IsShowing);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void OnACallerNodeTheChipsAreTheMirroredDesignsAndAPressGoesThroughTheNodesVerbs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-caller-lt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var s = SettingsStore.Fresh();
        s.Name = "Caller";
        s.Control.Enabled = false;
        s.Watchdog.BeaconListenPort = FreePort();
        s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        NodeHost? host = null;
        try
        {
            host = NodeHost.Build(NodeKind.Caller, new SettingsStore(dir));
            var vm = new NodeViewModel(host);
            IRunPage page = vm;
            Assert.False(page.HasRunMonitor);
            Assert.False(page.HasLowerThirds);

            var design = new LowerThirdDesign { Name = "Neon" };
            host.State.LowerThirds.Designs.Add(design);
            Assert.True(page.HasLowerThirds);
            Assert.Same(host.State.LowerThirds, page.LowerThirds);

            // A node has no preview: the switch stays off whatever is asked; a press answers with words (alone, the desk's verb is refused; linked, it goes there).
            page.LowerThirdChipsToPreview = true;
            Assert.False(page.LowerThirdChipsToPreview);
            Assert.False(page.HasLowerThirdInPreview);
            page.ChipLowerThirdCommand.Execute(design);
            Assert.NotEqual("", page.LowerThirdStatus);
            page.HideLowerThirdCommand.Execute(null);
            Assert.NotEqual("", page.LowerThirdStatus);
        }
        finally
        {
            host?.Dispose();
            try { Directory.Delete(dir, true); } catch { /* a temp folder left behind is not a failed test */ }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
