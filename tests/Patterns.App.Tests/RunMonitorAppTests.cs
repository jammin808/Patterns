using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 62: the RUN surface's monitor — one screen drawn large between the wall and the history,
/// the main screen by default, any screen or canvas or the programme by right-click or by RUN
/// MONITOR on the wire, hidden by RUN MONITOR OFF; the show remembers. The desk's own eye: nothing
/// here changes the air.
/// </summary>
public class RunMonitorAppTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new PixelRect(6000, 0, 3840, 2160), 1.0, false, 2),
    };

    private static void Rig(TestApp.Booted b)
    {
        var fakes = ThreeScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        // Main is every screen's role until the operator says otherwise: the left screen is a confidence
        // monitor here, so the first screen whose role is Main is the right one.
        b.Vm.State.Output.Placements.First(p => p.ScreenId == "a").Role = ScreenRole.Confidence;
        b.Vm.State.Output.Placements.First(p => p.ScreenId == "b").Role = ScreenRole.Main;
        b.Vm.RebuildSwitcherTiles(fakes);
        Dispatcher.UIThread.RunJobs();
    }

    private static ActionResult Wire(AppServices services, string line)
        => services.Actions.Execute(ControlProtocol.Parse(line).Action, ActionOrigin.Desk);

    [AvaloniaFact]
    public void TheMonitorShowsTheMainScreenByDefaultAndFollowsTheWireTheMenuAndTheShow()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            var airBefore = JsonUtil.SerializeIdentity(services.AirState.Pattern);

            // The main screen — the one whose role is Main — by default, at its own shape.
            Assert.True(vm.HasRunMonitor);
            Assert.Equal("b", vm.RunMonitorViewport!.ScreenId);
            Assert.Contains("main screen", vm.RunMonitorTitle);
            Assert.Equal("MAIN", DeskMenuFacts.MonitorWord(services));
            Assert.Equal(16.0 / 9.0, vm.RunMonitorRatio, 3);

            // The wire: a screen by its number (the lobby is 4K — the monitor takes its shape), the programme, off, main.
            Assert.True(Wire(services, "RUN MONITOR 3").Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("c", vm.RunMonitorViewport!.ScreenId);
            Assert.Equal("c", vm.State.Desk.RunMonitor);
            Assert.Equal("3", DeskMenuFacts.MonitorWord(services));
            Assert.Equal(16.0 / 9.0, vm.RunMonitorRatio, 3);

            Assert.True(Wire(services, "RUN MONITOR PGM").Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.HasRunMonitor);
            Assert.Null(vm.RunMonitorViewport!.ScreenId);
            Assert.Equal("PGM", DeskMenuFacts.MonitorWord(services));

            Assert.True(Wire(services, "RUN MONITOR OFF").Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.HasRunMonitor);
            Assert.Equal("OFF", vm.State.Desk.RunMonitor);
            Assert.Equal("OFF", DeskMenuFacts.MonitorWord(services));

            Assert.True(Wire(services, "RUN MONITOR MAIN").Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("b", vm.RunMonitorViewport!.ScreenId);
            Assert.Equal("", vm.State.Desk.RunMonitor);

            // A screen the rig has not got is refused; nothing moves.
            var refused = Wire(services, "RUN MONITOR 9");
            Assert.False(refused.Ok);
            Assert.Equal("b", vm.RunMonitorViewport!.ScreenId);

            // The right-click menu: every screen, the programme, hide — with the current choice ticked — and a choice moves the monitor.
            var menu = vm.MenuFor("monitor", vm)!;
            Assert.Equal("monitor", menu.Menu.Kind);
            Assert.True(menu.Find("monitor.main")!.IsOn);
            Assert.NotNull(menu.Find("monitor.a"));
            Assert.NotNull(menu.Find("monitor.c"));
            Assert.Equal("RUN MONITOR 1", menu.Find("monitor.a")!.Wire);
            menu.Find("monitor.a")!.ChooseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("a", vm.State.Desk.RunMonitor);
            Assert.Equal("a", vm.RunMonitorViewport!.ScreenId);
            Assert.Equal("1", DeskMenuFacts.MonitorWord(services));

            // The wire's MENU answers the same menu, and it never touched the air.
            var answered = MenuQuery.Build(services, "MONITOR", out _);
            Assert.NotNull(answered);
            Assert.Equal("monitor", answered!.Kind);
            Assert.True(answered.Find("monitor.a")!.IsOn);
            Assert.Equal(airBefore, JsonUtil.SerializeIdentity(services.AirState.Pattern));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AMainScreenJoinedIntoACanvasShowsTheWholeCanvasAndNoRigShowsNothing()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            // Arrange a and b flush: they become one canvas, and the main screen (b) is part of it, so the monitor draws the canvas.
            var pa = vm.State.Output.Placements.First(p => p.ScreenId == "a");
            var pb = vm.State.Output.Placements.First(p => p.ScreenId == "b");
            pa.X = 0; pa.Y = 0; pb.X = 1920; pb.Y = 0;
            vm.RebuildSwitcherTiles(ThreeScreens());
            Dispatcher.UIThread.RunJobs();
            var canvas = vm.SwitcherTiles.First(t => t.TargetId is { } id && ContentTargets.IsCanvasKey(id)).TargetId!;
            Assert.Equal(canvas, DeskMenuFacts.MainTarget(services));
            Assert.Equal(canvas, vm.RunMonitorViewport!.ScreenId);
            Assert.Contains(DeskMenuFacts.Monitor(services).Choices, c => c.TargetId == canvas && c.Number.Length == 0);
            Assert.Equal("MAIN", DeskMenuFacts.MonitorWord(services));

            // No rig: nothing to draw, and the menu's main entry says why.
            services.Screens.All.Clear();
            vm.State.Output.Placements.Clear();
            vm.RebuildSwitcherTiles(new List<ScreenInfo>());
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.HasRunMonitor);
            Assert.False(vm.MenuFor("monitor", vm)!.Find("monitor.main")!.IsEnabled);
        }
        finally
        {
            b.Dispose();
        }
    }
}
