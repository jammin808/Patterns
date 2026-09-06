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
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 17's wall tiles. On the rig: "screen tiles (other than PGM) have the name label
/// vertically when maximised, and the OWN / MON / ARM bar floats in the vertical middle when it
/// should be top justified (like PGM)". The tiles decided their two branches — the body, the
/// vertical title bar — by a binding up the tree that could resolve late on a tile rebuilt after
/// the wall, and the body sat in a panel with no top alignment. Now the branches are classes on
/// the tree itself and everything sits at the top; and a tile collapses on its own (▸ / ▾),
/// remembered by the show.
/// </summary>
public class TileLayoutTests
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

    private static void Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static WallView DeskWall(Window window)
        => window.GetVisualDescendants().OfType<WallView>().Single(w => w.IsEffectivelyVisible);

    private static List<Border> Tiles(Visual host)
        => host.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("tile") && x.DataContext is SwitcherTile).ToList();

    private static T Named<T>(Visual tile, string name) where T : Control
        => tile.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static SwitcherTile Model(Border tile) => (SwitcherTile)tile.DataContext!;

    /// <summary>Where a part sits inside its tile: the top edge in the tile's own coordinates.</summary>
    private static double TopIn(Visual part, Visual tile) => part.TranslatePoint(new Point(0, 0), tile)!.Value.Y;

    [AvaloniaFact]
    public void EveryTileShowsOneBranchWithItsButtonsAtTheTopHoweverTallTheRowIs()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            window.Width = 1600;
            window.Height = 1000;
            Settle();

            var wall = DeskWall(window);
            Assert.False(wall.Collapsed);
            var tiles = Tiles(wall);
            Assert.Equal(4, tiles.Count);   // PGM + three screens, every one rebuilt after the wall existed
            foreach (var tile in tiles)
            {
                var title = Model(tile).Title;
                Assert.False(Named<StackPanel>(tile, "TileBar").IsEffectivelyVisible, $"{title}: the bar shows beside the body");
                Assert.True(Named<StackPanel>(tile, "TileBody").IsEffectivelyVisible, $"{title}: no body");
                Assert.DoesNotContain(tile.GetVisualDescendants().OfType<LayoutTransformControl>(), l => l.IsEffectivelyVisible);
                Assert.False(Named<Button>(tile, "TileExpand").IsEffectivelyVisible, $"{title}: ▾ on a tile that is open");
                Assert.True(Named<Button>(tile, "TileCollapse").IsEffectivelyVisible, $"{title}: no ▸");
                Assert.True(tile.Bounds.Width > 150, $"{title} is a full tile ({tile.Bounds.Width:0})");
            }

            // The row is as tall as its tallest tile; a tile made much taller than its content still keeps
            // its title row and its buttons at the top — where PGM keeps them — not floating mid-height.
            var screen = tiles.Single(t => Model(t).TargetId == "b");
            var natural = screen.Bounds.Height;
            screen.Height = natural + 160;
            Settle();
            Assert.InRange(screen.Bounds.Height, natural + 159, natural + 161);
            var body = Named<StackPanel>(screen, "TileBody");
            var buttons = Named<Grid>(screen, "TileButtons");
            Assert.InRange(TopIn(body, screen), 0, 8);
            Assert.True(TopIn(buttons, screen) < natural, $"the buttons sit at {TopIn(buttons, screen):0} in a {screen.Bounds.Height:0} tile");
            var pgm = tiles.Single(t => Model(t).IsProgramTile);
            var pgmButtons = TopIn(Named<Grid>(pgm, "TileButtons"), pgm);
            Assert.True(Math.Abs(TopIn(buttons, screen) - pgmButtons) <= 4,
                $"level with PGM's: the screen's buttons sit at {TopIn(buttons, screen):0}, PGM's at {pgmButtons:0}");

            // MON off takes the miniatures away and the buttons move up under the title row — still at the top.
            Model(screen).IsMonitored = false;
            Settle();
            Assert.True(TopIn(buttons, screen) < 60, $"with MON off the buttons sit at {TopIn(buttons, screen):0}");
            Model(screen).IsMonitored = true;
            screen.Height = double.NaN;
            Settle();
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATileCollapsesOnItsOwnOpensAgainAndTheShowRemembersIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            window.Width = 1600;
            window.Height = 1000;
            Settle();

            var wall = DeskWall(window);
            var tiles = Tiles(wall);
            var screen = tiles.Single(t => Model(t).TargetId == "b");
            var others = tiles.Where(t => t != screen).ToList();
            Assert.Empty(vm.State.Desk.CollapsedTiles);

            // ▸ on the title row: this tile alone is a bar; the others stay full; the show has the choice.
            var collapse = Named<Button>(screen, "TileCollapse");
            Assert.True(collapse.Command!.CanExecute(null));
            collapse.Command.Execute(null);
            Settle();
            Assert.True(Model(screen).IsCollapsed);
            Assert.Equal(new[] { "b" }, vm.State.Desk.CollapsedTiles);
            Assert.Contains("collapsed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("tileCollapsed", screen.Classes);
            Assert.True(Named<StackPanel>(screen, "TileBar").IsEffectivelyVisible);
            Assert.False(Named<StackPanel>(screen, "TileBody").IsEffectivelyVisible);
            Assert.True(Named<Button>(screen, "TileExpand").IsEffectivelyVisible, "▾ on the bar");
            Assert.Single(screen.GetVisualDescendants().OfType<LayoutTransformControl>(), l => l.IsEffectivelyVisible);
            Assert.True(screen.Bounds.Width < 60, $"a bar ({screen.Bounds.Width:0})");
            Assert.All(others, t => Assert.True(t.Bounds.Width > 150, $"{Model(t).Title} stays a full tile"));
            Assert.All(others, t => Assert.False(Named<StackPanel>(t, "TileBar").IsEffectivelyVisible));
            Assert.NotNull(ToolTip.GetTip(screen));   // the pause still pops it up large

            // The wall rebuilt (a display plugged in, a show loaded) keeps the choice: the tile comes back as a bar.
            vm.RebuildSwitcherTiles();
            Settle();
            tiles = Tiles(DeskWall(window));
            screen = tiles.Single(t => Model(t).TargetId == "b");
            Assert.True(Model(screen).IsCollapsed);
            Assert.True(screen.Bounds.Width < 60);
            Assert.Equal(3, tiles.Count(t => t.Bounds.Width > 150));

            // The choice travels with the show file; an older file without it opens every tile full.
            var json = JsonUtil.Serialize(vm.State);
            Assert.Equal(new[] { "b" }, JsonUtil.Deserialize<ShowState>(json)!.Desk.CollapsedTiles);
            Assert.Empty(JsonUtil.Deserialize<ShowState>("{}")!.Desk.CollapsedTiles);

            // ▾ on the bar: open again, and the show forgets it.
            Named<Button>(screen, "TileExpand").Command!.Execute(null);
            Settle();
            Assert.False(Model(screen).IsCollapsed);
            Assert.Empty(vm.State.Desk.CollapsedTiles);
            Assert.DoesNotContain("tileCollapsed", screen.Classes);
            Assert.True(screen.Bounds.Width > 150);
            Assert.True(Named<StackPanel>(screen, "TileBody").IsEffectivelyVisible);
            Assert.False(Named<StackPanel>(screen, "TileBar").IsEffectivelyVisible);

            // PGM collapses too, under its own key.
            var pgm = tiles.Single(t => Model(t).IsProgramTile);
            Model(pgm).CollapseCommand.Execute(null);
            Settle();
            Assert.Equal(new[] { "" }, vm.State.Desk.CollapsedTiles);
            Assert.True(pgm.Bounds.Width < 60);
            Model(pgm).ExpandCommand.Execute(null);
            Settle();
            Assert.Empty(vm.State.Desk.CollapsedTiles);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheRunAreasCollapseTilesCoversEveryTileAndLeavesTheTilesOwnChoicesUnderIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            window.Width = 1400;
            window.Height = 900;
            vm.IsRunLayout = true;
            Settle();

            var run = window.GetVisualDescendants().OfType<RunView>().Single(r => r.IsEffectivelyVisible);
            var wall = run.GetVisualDescendants().OfType<WallView>().Single();
            var tiles = Tiles(wall);
            var screen = tiles.Single(t => Model(t).TargetId == "c");
            Model(screen).CollapseCommand.Execute(null);
            Settle();
            Assert.True(screen.Bounds.Width < 60);
            Assert.Equal(3, tiles.Count(t => t.Bounds.Width > 150));

            // The whole wall as bars: every tile, and no ▾ on any of them — COLLAPSE TILES is the way back.
            vm.IsRunWallCollapsed = true;
            Settle();
            Assert.Contains(WallView.CollapsedClass, wall.Classes);
            foreach (var tile in Tiles(wall))
            {
                Assert.True(tile.Bounds.Width < 60, $"{Model(tile).Title} is a bar");
                Assert.True(Named<StackPanel>(tile, "TileBar").IsEffectivelyVisible);
                Assert.False(Named<StackPanel>(tile, "TileBody").IsEffectivelyVisible);
                Assert.False(Named<Button>(tile, "TileExpand").IsEffectivelyVisible);
            }
            Assert.Equal(new[] { "c" }, vm.State.Desk.CollapsedTiles);   // the tile's own choice is kept, not overwritten

            // Back to full tiles: the one collapsed on its own is still a bar, with its ▾.
            vm.IsRunWallCollapsed = false;
            Settle();
            Assert.DoesNotContain(WallView.CollapsedClass, wall.Classes);
            tiles = Tiles(wall);
            screen = tiles.Single(t => Model(t).TargetId == "c");
            Assert.True(screen.Bounds.Width < 60);
            Assert.True(Named<Button>(screen, "TileExpand").IsEffectivelyVisible);
            Assert.Equal(3, tiles.Count(t => t.Bounds.Width > 150));
        }
        finally
        {
            b.Dispose();
        }
    }

    [Fact]
    public void TheHelpSaysHowATileCollapsesAndHowAScreenJoinsAGroup()
    {
        Assert.Contains("▾ on the bar", HelpBodies.Switcher);
        Assert.Contains("▸ at the end of the row", HelpBodies.Switcher);
        Assert.Contains("a group is a joined canvas", HelpBodies.Switcher);
        Assert.Contains("SETUP → Screens", HelpBodies.Switcher);
        Assert.Contains("▸ at the end of a tile's title row", HelpBodies.Modes);
    }
}
