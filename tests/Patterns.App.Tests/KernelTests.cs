using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The kernel: what every role stands on, built before the desk and without it. Its services
/// are written against the kernel and cannot reach an output; what they need of the desk they
/// take as a capability the desk fills in, and a test fills in just as well.
/// </summary>
public class KernelTests
{
    private sealed class FakeAir : IAirReport
    {
        public string AirLabel { get; set; } = "Walk-in";
        public bool OutputsLive { get; set; } = true;
        public bool Armed { get; set; } = true;
        public string StandbyWords { get; set; } = "03.020 Five-minute call";
        public string LastCueNumber { get; set; } = "03.010";
        public double Fps { get; set; } = 59.9;
        public int Windows { get; set; } = 2;
    }

    private sealed class FakeLink : ILinkReport
    {
        public int LinkPort => 9699;
        public int CallerCount => 2;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-kernel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [AvaloniaFact]
    public void TheKernelBuildsAloneWithNothingOnAirAndTakesWhatTheDeskWouldFillIn()
    {
        var dir = TempDir();
        try
        {
            using var kernel = ServiceKernel.Build(NodeKind.Caller, new SettingsStore(dir));
            Assert.Equal(NodeKind.Caller, kernel.Profile);
            Assert.False(kernel.IsDesk);
            Assert.NotNull(kernel.State);
            Assert.NotNull(kernel.Bus.Current);
            Assert.NotNull(kernel.Journal);
            Assert.Null(kernel.LastCrash);
            Assert.False(kernel.SafeRun);
            Assert.Equal("", kernel.StandDownNote);
            Assert.False(kernel.State.Blackout);
            Assert.False(kernel.State.Tone.Enabled);

            // Nothing on air, no link, the log for lines, a bare brief: the kernel's own answers.
            var packet = kernel.Beacon.Build();
            Assert.Equal("caller", packet.Kind);
            Assert.False(packet.Live);
            Assert.Equal("—", packet.Program);
            Assert.Equal("", packet.Standby);
            Assert.Equal(0, packet.Link);
            Assert.Equal(0, kernel.Nodes.Linked);
            Assert.False(kernel.Assistant.Gather().OutputsLive);
            Assert.Equal("", kernel.Assistant.Gather().AirLabel);

            // The desk fills the slots; so can a test, with no desk at all.
            kernel.Air = new FakeAir();
            kernel.Link = new FakeLink();
            var lines = new List<string>();
            kernel.Notifier = lines.Add;
            kernel.Facts = () => new ShowFacts { OutputsLive = true, AirLabel = "Walk-in" };
            packet = kernel.Beacon.Build();
            Assert.True(packet.Live);
            Assert.Equal("Walk-in", packet.Program);
            Assert.True(packet.Armed);
            Assert.Equal("03.020 Five-minute call", packet.Standby);
            Assert.Equal("03.010", packet.Last);
            Assert.Equal(59.9, packet.Fps);
            Assert.Equal(2, packet.Windows);
            Assert.Equal(9699, packet.Link);
            Assert.Equal(2, kernel.Nodes.Linked);
            kernel.Notify("a line");
            Assert.Equal(new[] { "a line" }, lines);
            Assert.True(kernel.Assistant.Gather().OutputsLive);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TheKernelHasNoOutputsAndItsServicesNeverTakeTheDesk()
    {
        // The boundary, as a test: nothing of the desk is reachable from the kernel's type, and
        // the services the kernel builds take the kernel — never the desk — in their constructors.
        var deskOnly = new[] { typeof(OutputWindowManager), typeof(SandboxService), typeof(VideoEngine), typeof(StreamService), typeof(CueStackService), typeof(StingerService), typeof(ScreenService), typeof(AppServices) };
        foreach (var p in typeof(ServiceKernel).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.DoesNotContain(p.PropertyType, deskOnly);
        }
        foreach (var kernelService in new[] { typeof(AssistantService), typeof(BeaconService), typeof(NodesService), typeof(ArcadeService) })   // the arcade is built on the kernel by a role, never by the kernel
        {
            foreach (var ctor in kernelService.GetConstructors())
            {
                Assert.Contains(ctor.GetParameters(), q => q.ParameterType == typeof(ServiceKernel));
                Assert.DoesNotContain(ctor.GetParameters(), q => q.ParameterType == typeof(AppServices));
            }
        }

        // The desk-facing services say what they need of the desk, as a contract — never the desk itself.
        foreach (var (service, contract) in new[] { (typeof(TwinService), typeof(ITwinHost)), (typeof(ControlService), typeof(IWireHost)), (typeof(StageService), typeof(IStageHost)), (typeof(PlayService), typeof(IPlayHost)), (typeof(CueStackService), typeof(ICueHost)), (typeof(UpdateService), typeof(IMachineHost)), (typeof(ManagementService), typeof(IMachineHost)) })
        {
            foreach (var ctor in service.GetConstructors())
            {
                Assert.Contains(ctor.GetParameters(), q => q.ParameterType == contract);
                Assert.DoesNotContain(ctor.GetParameters(), q => q.ParameterType == typeof(AppServices));
            }
        }
        // And the desk provides every one of them.
        Assert.True(typeof(IAirReport).IsAssignableFrom(typeof(AppServices)));
        Assert.True(typeof(ITwinHost).IsAssignableFrom(typeof(AppServices)));
        Assert.True(typeof(IWireHost).IsAssignableFrom(typeof(AppServices)));
        Assert.True(typeof(IStageHost).IsAssignableFrom(typeof(AppServices)));
        Assert.True(typeof(IPlayHost).IsAssignableFrom(typeof(AppServices)));
        Assert.True(typeof(ICueHost).IsAssignableFrom(typeof(AppServices)));
        Assert.True(typeof(IRunHost).IsAssignableFrom(typeof(AppServices)));
        Assert.True(typeof(IMachineHost).IsAssignableFrom(typeof(AppServices)));
        // The arcade is nobody's kernel: the roles that want a game build it.
        Assert.DoesNotContain(typeof(ServiceKernel).GetProperties(BindingFlags.Public | BindingFlags.Instance), p => p.PropertyType == typeof(ArcadeService));
        Assert.True(typeof(ILinkReport).IsAssignableFrom(typeof(TwinService)));
    }

    [AvaloniaFact]
    public void TheDeskIsBuiltOnTheKernelAndFillsItsSlots()
    {
        var b = TestApp.Boot();
        try
        {
            var services = b.Services;
            var kernel = services.Kernel;
            Assert.Same(kernel.State, services.State);
            Assert.Same(kernel.Bus, services.Bus);
            Assert.Same(kernel.Store, services.Store);
            Assert.Same(kernel.Journal, services.Journal);
            Assert.Same(kernel.Beacon, services.Beacon);
            Assert.Same(kernel.Nodes, services.Nodes);
            Assert.Same(kernel.Assistant, services.Assistant);
            Assert.Same(kernel.Cues, services.Cues);
            Assert.True(kernel.IsDesk);
            // The slots: the desk's air, the twin's link, the status strip, the desk's brief.
            Assert.Same(services, kernel.Air);
            Assert.Same(services.Twin, kernel.Link);
            kernel.Notify("Kernel: a line for the strip");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Kernel: a line for the strip", b.Vm.StatusMessage);
            Assert.Equal(services.AirLabel, services.Beacon.Build().Program);
            Assert.False(kernel.Facts().OutputsLive);
            Assert.False(services.Assistant.Gather().EditSafeOpen);
        }
        finally
        {
            b.Dispose();
        }
    }
}
