using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The switcher strip, custom labels, and CUT/TAKE against the live app.</summary>
public class SwitcherTests
{
    private static (AppServices Services, MainViewModel Vm, MainWindow Window) Boot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-switcher-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var services = new AppServices(new SettingsStore(dir));
        AppServices.Instance = services;
        var vm = new MainViewModel(services);
        var window = new MainWindow { DataContext = vm };
        services.AttachMainWindow(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (services, vm, window);
    }

    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new Avalonia.PixelRect(1920, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new Avalonia.PixelRect(4400, 0, 1920, 1080), 1.0, false, 2),
    };

    /// <summary>a+b flush = canvas A; c stands alone.</summary>
    private static void Arrange(MainViewModel vm, List<ScreenInfo> fakes)
    {
        vm.State.Output.Placements.Clear();
        vm.ReconcilePlacements(fakes);
        var a = vm.State.Output.Placements.First(p => p.ScreenId == "a");
        var b = vm.State.Output.Placements.First(p => p.ScreenId == "b");
        var c = vm.State.Output.Placements.First(p => p.ScreenId == "c");
        a.X = 0; a.Y = 0;
        b.X = 1920; b.Y = 0;
        c.X = 6000; c.Y = 0;
        foreach (var p in vm.State.Output.Placements) p.Enabled = true;
    }

    [AvaloniaFact]
    public void StripShowsProgramCanvasAndSinglesWithCustomNames()
    {
        var (services, vm, window) = Boot();
        try
        {
            var fakes = ThreeScreens();
            Arrange(vm, fakes);
            vm.State.Output.CanvasNames.Add(new CanvasNameConfig
            {
                MemberKey = CanvasNameConfig.KeyFor(new[] { "a", "b" }),
                Name = "Main wall",
            });
            vm.State.Output.Placements.First(p => p.ScreenId == "c").CustomLabel = "Lobby TV";

            vm.RebuildSwitcherTiles(fakes);

            Assert.Equal(3, vm.SwitcherTiles.Count);
            Assert.True(vm.SwitcherTiles[0].IsProgramTile);
            Assert.True(vm.SwitcherTiles[0].IsEditTarget); // program is the default target
            Assert.Equal("A · Main wall", vm.SwitcherTiles[1].Title);
            Assert.Equal(new[] { "a", "b" }, vm.SwitcherTiles[1].MemberIds.OrderBy(x => x));
            Assert.Equal("3 · Lobby TV", vm.SwitcherTiles[2].Title);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void TileSwitchTogglesEveryMemberAndPinsThem()
    {
        var (services, vm, window) = Boot();
        try
        {
            var fakes = ThreeScreens();
            Arrange(vm, fakes);
            vm.RebuildSwitcherTiles(fakes);

            var canvasTile = vm.SwitcherTiles[1];
            canvasTile.Enabled = false;

            var a = vm.State.Output.Placements.First(p => p.ScreenId == "a");
            var b = vm.State.Output.Placements.First(p => p.ScreenId == "b");
            var c = vm.State.Output.Placements.First(p => p.ScreenId == "c");
            Assert.False(a.Enabled);
            Assert.False(b.Enabled);
            Assert.True(c.Enabled);
            Assert.True(a.UserPinned);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void CutSendsInstantlyAndPreservesTheTransitionSetting()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Transition.Enabled = true;
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.ColorBars;

            vm.CutCommand.Execute(null);

            Assert.True(vm.IsSandboxActive); // EDIT SAFE re-armed after the send
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.State.Pattern.Kind);
            Assert.True(vm.State.Transition.Enabled); // CUT bypassed, not disabled
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void TakeWithoutASandboxJustExplains()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.TakeCommand.Execute(null);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);
            Assert.Contains("sandbox", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void SendToTickedTilesTargetsAllCanvasMembers()
    {
        var (services, vm, window) = Boot();
        try
        {
            var fakes = ThreeScreens();
            Arrange(vm, fakes);
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.RebuildSwitcherTiles(fakes);

            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.Focus;
            vm.SwitcherTiles[1].IsSendTarget = true; // the whole canvas A

            vm.SandboxSendSelectedCommand.Execute(null);

            Assert.True(vm.IsSandboxActive); // EDIT SAFE re-armed after the send
            Assert.False(vm.SwitcherTiles[1].IsSendTarget); // a send consumes its targets
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind); // program restored
            foreach (var id in new[] { "a", "b" })
            {
                Assert.True(vm.State.Output.Placements.First(p => p.ScreenId == id).UseCustomPattern);
                Assert.Equal(PatternKind.Focus, vm.State.Independent.First(x => x.ScreenId == id).Pattern.Kind);
            }
            Assert.False(vm.State.Output.Placements.First(p => p.ScreenId == "c").UseCustomPattern);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void BannerNamesTheEditTarget()
    {
        var (services, vm, window) = Boot();
        try
        {
            Assert.StartsWith("EDITING: PROGRAM", vm.EditTargetBanner);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [Fact]
    public void CanvasKeyIsStableAcrossMemberOrder()
    {
        Assert.Equal(CanvasNameConfig.KeyFor(new[] { "b", "a" }), CanvasNameConfig.KeyFor(new[] { "a", "b" }));
        Assert.NotEqual(CanvasNameConfig.KeyFor(new[] { "a" }), CanvasNameConfig.KeyFor(new[] { "a", "b" }));
    }
}
