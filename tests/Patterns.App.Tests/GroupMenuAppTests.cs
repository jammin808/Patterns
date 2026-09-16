using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 67.7: the group in the tile's menu on a live desk — THIS TILE → Group names the screen's group and
/// changes it with the same verb the Screens page and the wire use; the wall's badge and foot line, the lock,
/// the take plan, the Screens page and the menu read one truth at once; a canvas sets every screen in it and
/// reads mixed when they differ; a screen inside a canvas gets the menu from the Screens page.
/// </summary>
public class GroupMenuAppTests
{
    /// <summary>a and b flush (canvas a+b), c standing alone.</summary>
    private static string InstallRig(AppServices services, MainViewModel vm)
    {
        var fakes = new List<ScreenInfo>
        {
            new("a", "Left", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
            new("b", "Right", new Avalonia.PixelRect(1920, 0, 1920, 1080), 1.0, false, 1),
            new("c", "Lobby", new Avalonia.PixelRect(4400, 0, 1920, 1080), 1.0, false, 2),
        };
        services.Screens.All.Clear();
        foreach (var s in fakes) services.Screens.All.Add(s);
        vm.State.Output.Placements.Clear();
        vm.ReconcilePlacements(fakes);
        vm.State.Output.Placements.First(p => p.ScreenId == "a").X = 0;
        vm.State.Output.Placements.First(p => p.ScreenId == "b").X = 1920;
        vm.State.Output.Placements.First(p => p.ScreenId == "c").X = 6000;
        foreach (var p in vm.State.Output.Placements) p.Enabled = true;
        vm.RebuildSwitcherTiles();
        Dispatcher.UIThread.RunJobs();
        return CanvasNameConfig.KeyFor(new[] { "a", "b" });
    }

    private static void Choose(DeskMenuVm menu, string id)
    {
        var entry = menu.Find(id) ?? throw new InvalidOperationException($"no entry '{id}' in the {menu.Menu.Kind} menu");
        Assert.True(entry.IsEnabled, $"{id}: {entry.Because}");
        entry.ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    [AvaloniaFact]
    public void TheTileMenuChangesTheGroupAndTheWallTheScreensPageThePlanAndTheWireAgree()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var canvasKey = InstallRig(services, vm);
            var router = new CommandRouter(services);
            ScreenPlacement Placement(string id) => vm.State.Output.Placements.Single(p => p.ScreenId == id);
            SwitcherTile Tile(string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);

            // The lobby screen is a main screen: the drawer says so, the others carry the wire's line, a repeater needs a source.
            var menu = vm.MenuFor("tile", Tile("c"))!;
            Assert.Equal("Group — Main", menu.Find("tile.group")!.Text);
            Assert.True(menu.Find("tile.group:main")!.IsOn);
            Assert.Equal("SCREEN 3 GROUP confidence", menu.Find("tile.group:confidence")!.Wire);
            Assert.False(menu.Find("tile.group:repeater")!.IsEnabled);
            Assert.Contains("Mirror of", menu.Find("tile.group:repeater")!.Because);

            // Confidence: the role, the lock the role picks, the badge and foot line, the plan and the re-read menu — at once.
            Choose(menu, "tile.group:confidence");
            Assert.Equal(ScreenRole.Confidence, Placement("c").Role);
            Assert.False(Placement("c").FollowsCues);
            Assert.True(ScreenRoles.IsLocked(vm.State, "c"));
            Assert.Contains("a confidence screen", vm.StatusMessage);
            Assert.Contains("locked", vm.StatusMessage);
            var c = Tile("c");                                                   // the wall was rebuilt under the menu
            Assert.Equal("CONF", c.RoleBadge);
            Assert.Equal("CONF", c.KindWord);
            Assert.True(c.IsLocked);
            Assert.Contains(services.Actions.PlanTake(FadeScope.Everything).Held, h => h.Id == "c" && h.Reason.Contains("locked"));
            var again = vm.MenuFor("tile", c)!;
            Assert.Equal("Group — Confidence", again.Find("tile.group")!.Text);
            Assert.True(again.Find("tile.group:confidence")!.IsOn);
            Assert.False(again.Find("tile.group:main")!.IsOn);
            vm.Screens.SelectedPlacement = Placement("c");
            Assert.Equal(ScreenRole.Confidence, vm.Screens.SelectedRole);

            // The Screens page's arrangement gives the same menu for the placement; Main again unlocks and follows.
            var page = vm.MenuFor("screen", Placement("c"))!;
            Assert.Equal("screen", page.Menu.Kind);
            Assert.True(page.Find("tile.group:confidence")!.IsOn);
            Choose(page, "tile.group:main");
            Assert.Equal(ScreenRole.Main, Placement("c").Role);
            Assert.True(Placement("c").FollowsCues);
            Assert.False(ScreenRoles.IsLocked(vm.State, "c"));
            Assert.Equal("", Tile("c").RoleBadge);
            Assert.Equal("MAIN", Tile("c").KindWord);
            Assert.Equal(ScreenRole.Main, vm.Screens.SelectedRole);

            // The canvas tile sets every screen in it; it rides the action, not a wire number; a canvas never repeats.
            var canvas = vm.MenuFor("tile", Tile(canvasKey))!;
            Assert.Equal("Group — Main", canvas.Find("tile.group")!.Text);
            Assert.Equal("", canvas.Find("tile.group:info")!.Wire);
            Assert.False(canvas.Find("tile.group:repeater")!.IsEnabled);
            Choose(canvas, "tile.group:info");
            Assert.Equal(ScreenRole.Info, Placement("a").Role);
            Assert.Equal(ScreenRole.Info, Placement("b").Role);
            Assert.Contains("every screen is an info screen", vm.StatusMessage);
            Assert.Equal("INFO", Tile(canvasKey).KindWord);

            // The wire moves one screen of the canvas back: the canvas reads mixed with nothing on; the screen
            // inside the canvas — no wall tile of its own — still gets its menu from the Screens page.
            Assert.StartsWith("OK", Send(router, "SCREEN 1 GROUP main"));
            Assert.Equal(ScreenRole.Main, Placement("a").Role);
            var mixed = vm.MenuFor("tile", Tile(canvasKey))!.Find("tile.group")!;
            Assert.Contains("mixed", mixed.Text);
            Assert.All(mixed.Children, e => Assert.False(e.IsOn));
            Assert.Equal("MIXED", Tile(canvasKey).KindWord);
            var inner = vm.MenuFor("screen", Placement("b"))!;
            Assert.Equal("Group — Info", inner.Find("tile.group")!.Text);
            Assert.Equal("SCREEN 2 GROUP main", inner.Find("tile.group:main")!.Wire);
            Assert.StartsWith("ERR", Send(router, "SCREEN 1 GROUP sideways"));
            Assert.Equal(ScreenRole.Main, Placement("a").Role);
        }
        finally
        {
            b.Dispose();
        }
    }
}
