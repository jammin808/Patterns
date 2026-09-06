using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 15's Run area: "the screen tiles collapsible to a vertical title bar, the cue section
/// about 35 % of the width, a hover pause over a tile pops up a larger view." On a live desk in
/// the Run layout: the stack's share by default, by a drag and by the show; the tiles as bars and
/// back, remembered; the popup on every tile; the desk's own wall untouched by the Run choice.
/// </summary>
public class RunAreaTests
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
        b.Vm.RebuildSwitcherTiles();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static double CueRatio(Grid split)
    {
        var wall = split.ColumnDefinitions[0].ActualWidth;
        var cue = split.ColumnDefinitions[2].ActualWidth;
        return cue / (wall + cue);
    }

    private static List<Border> Tiles(Visual host)
        => host.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("tile") && x.DataContext is SwitcherTile).ToList();

    [AvaloniaFact]
    public void TheStackTakesItsShareTheTilesCollapseToBarsAndAPauseOverATilePopsItUp()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            window.Width = 1400;
            window.Height = 900;
            vm.IsRunLayout = true;
            Settle(window);

            // The stack starts at about a third of the Run area, from the show's own default.
            Assert.Equal(DeskLayoutConfig.DefaultRunCueShare, vm.State.Desk.RunCueShare, 3);
            var run = window.GetVisualDescendants().OfType<RunView>().Single(r => r.IsEffectivelyVisible);
            var split = run.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "RunSplit");
            Assert.True(split.IsEffectivelyVisible);
            Assert.Equal(0.35, run.CueShareApplied, 3);
            Assert.InRange(CueRatio(split), 0.33, 0.37);
            Assert.Single(run.GetVisualDescendants().OfType<GridSplitter>(), s => s.Name == "RunSplitter");

            // A drag of the divider (as the splitter reports it) moves the stack and the show remembers it; the show's limits hold.
            run.SetCueShare(0.5);
            Settle(window);
            Assert.Equal(0.5, vm.State.Desk.RunCueShare, 3);
            Assert.Equal(0.5, run.CueShareApplied, 3);
            Assert.InRange(CueRatio(split), 0.48, 0.52);
            run.SetCueShare(0.95);
            Settle(window);
            Assert.Equal(DeskLayoutConfig.MaxRunCueShare, vm.State.Desk.RunCueShare, 3);
            Assert.Equal(DeskLayoutConfig.MaxRunCueShare, run.CueShareApplied, 3);
            vm.State.Desk.RunCueShare = 0.35;   // the show itself (a load) lays the columns out again
            Settle(window);
            Assert.InRange(CueRatio(split), 0.33, 0.37);

            // The wall's tiles: full tiles with their miniatures, then vertical title bars, then back — remembered in the show.
            var wall = run.GetVisualDescendants().OfType<WallView>().Single();
            Assert.False(wall.Collapsed);
            var toggle = run.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "RunWallToggle");
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.Equal("▸ COLLAPSE TILES", vm.RunWallToggleText);
            var tiles = Tiles(wall);
            Assert.Equal(4, tiles.Count);   // PGM + three screens
            Assert.All(tiles, t => Assert.True(t.Bounds.Width > 150, $"{((SwitcherTile)t.DataContext!).Title} is a full tile ({t.Bounds.Width:0})"));
            Assert.DoesNotContain(wall.GetVisualDescendants().OfType<LayoutTransformControl>(), l => l.IsEffectivelyVisible);

            vm.IsRunWallCollapsed = true;
            Settle(window);
            Assert.True(wall.Collapsed);
            Assert.True(vm.State.Desk.RunWallCollapsed);
            Assert.Equal("◂ EXPAND TILES", vm.RunWallToggleText);
            tiles = Tiles(wall);
            Assert.Equal(4, tiles.Count);
            Assert.All(tiles, t => Assert.True(t.Bounds.Width < 60, $"{((SwitcherTile)t.DataContext!).Title} is a bar ({t.Bounds.Width:0})"));
            Assert.All(tiles, t => Assert.True(t.Bounds.Height >= 100, $"{((SwitcherTile)t.DataContext!).Title} keeps the wall's height ({t.Bounds.Height:0})"));
            var bars = wall.GetVisualDescendants().OfType<LayoutTransformControl>().Where(l => l.IsEffectivelyVisible).ToList();
            Assert.Equal(4, bars.Count);   // the name on its side, one per tile
            Assert.All(bars, l => Assert.IsType<Avalonia.Media.RotateTransform>(l.LayoutTransform));
            Assert.DoesNotContain(wall.GetVisualDescendants().OfType<MonitorTileControl>(), m => m.IsEffectivelyVisible);

            // The desk's own wall (the Build layout) is not collapsed by the Run choice.
            vm.IsRunLayout = false;
            Settle(window);
            var deskWall = window.GetVisualDescendants().OfType<WallView>().Single(w => w.IsEffectivelyVisible);
            Assert.False(deskWall.Collapsed);
            Assert.All(Tiles(deskWall), t => Assert.True(t.Bounds.Width > 150));
            vm.IsRunLayout = true;
            Settle(window);
            Assert.True(wall.Collapsed);

            toggle.IsChecked = false;
            Settle(window);
            Assert.False(vm.IsRunWallCollapsed);
            Assert.False(vm.State.Desk.RunWallCollapsed);
            Assert.All(Tiles(wall), t => Assert.True(t.Bounds.Width > 150));

            // Every tile, full or bar, carries the popup: PGM and PVW large after a pause, never at once.
            foreach (var tile in Tiles(wall))
            {
                Assert.NotNull(ToolTip.GetTip(tile));
                Assert.True(ToolTip.GetShowDelay(tile) >= 500, "a pause, not a flicker");
            }
        }
        finally
        {
            b.Dispose();
        }
    }
}
