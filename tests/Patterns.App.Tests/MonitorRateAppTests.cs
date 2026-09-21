using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 79.4: the monitors degrade before the outputs. The wall's miniatures, the PROGRAM pane and the RUN
/// surface's monitor present at the desk's monitor rate through the pacer the outputs use (25 fps by default);
/// the preview pane stays at every beat; the setting is the desk's own, in STATE, and a change rebuilds the
/// monitors at once.
/// </summary>
public class MonitorRateAppTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
    };

    private static void Rig(TestApp.Booted b)
    {
        var fakes = ThreeScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.RebuildSwitcherTiles(fakes);
        b.Vm.RefreshRunMonitor();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheDesksMonitorsPaceToTheDeskRateAndThePreviewStaysAtEveryBeat()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            Assert.Equal(DeskLayoutConfig.DefaultMonitorFps, vm.State.Desk.MonitorFps);
            Assert.Equal(25, vm.MonitorFps);
            Assert.Contains(nameof(ShowState.Desk), TwinSync.LocalSections);                       // this desk's own, never the twin's

            // Every monitor viewport carries the rate; the preview none.
            Assert.NotEmpty(vm.SwitcherTiles);
            Assert.All(vm.SwitcherTiles, t =>
            {
                Assert.Equal(25, t.PgmViewport.TargetFps);
                Assert.Equal(25, t.PvwViewport.TargetFps);
                Assert.Equal(SinkKind.Monitor, t.PgmViewport.Kind);
            });
            Assert.NotNull(vm.RunMonitorViewport);
            Assert.Equal(25, vm.RunMonitorViewport!.TargetFps);
            Assert.Equal(0, PipelineViewport.Preview.TargetFps);
            Assert.Equal(0, PipelineViewport.Monitor("b", new SKSizeI(1920, 1080), "b", previewSide: false).TargetFps);   // no rate asked: every beat, as before

            // The pipeline over a monitor presents at the rate before any beat is heard (a pane has no display rate).
            using var pipeline = new RenderPipeline(services.Bus, vm.SwitcherTiles[0].PgmViewport);
            Assert.Equal(25, pipeline.PresentFps);
            using var preview = new RenderPipeline(services.Bus, PipelineViewport.Preview);
            Assert.Equal(0, preview.PresentFps);

            // STATE carries it; a change rebuilds the tiles and the RUN monitor at once.
            Assert.Contains("\"monitorFps\":25", new CommandRouter(services).StateJson());
            vm.MonitorFps = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.All(vm.SwitcherTiles, t => Assert.Equal(0, t.PgmViewport.TargetFps));
            Assert.Equal(0, vm.RunMonitorViewport!.TargetFps);
            Assert.Contains("\"monitorFps\":0", new CommandRouter(services).StateJson());
            vm.MonitorFps = 15;
            Dispatcher.UIThread.RunJobs();
            Assert.All(vm.SwitcherTiles, t => Assert.Equal(15, t.PvwViewport.TargetFps));
            Assert.Contains(FpsOption.Monitor, o => o.Value == 25 && o.Label.Contains("default", StringComparison.Ordinal));
        }
        finally
        {
            b.Dispose();
        }
    }
}
