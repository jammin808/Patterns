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
/// Round 67.3 and 67.5: the tile clicked is the editing target — both ways with the BUILD → PATTERN
/// picker — and its first edit makes it its own picture at once, distinct from the programme and every
/// other screen; a target left unedited leaves nothing behind; OWN off hands the editors back to the
/// programme with the tile still selected.
/// </summary>
public class TileIsolationAppTests
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
        Dispatcher.UIThread.RunJobs();
    }

    private static SwitcherTile Tile(MainViewModel vm, string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);

    [AvaloniaFact]
    public void TheTileClickedIsTheEditingTargetAndItsFirstEditMakesItItsOwnAtOnce()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.Grid;
            services.Sandbox.SendAll();                                  // Grid everywhere is the programme
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();

            // Every screen is in the picker; nothing selected means the programme.
            Assert.True(vm.ShowEditTargets);
            Assert.Equal(new[] { null, "a", "b", "c" }, vm.EditTargets.Select(t => t.ScreenId));
            Assert.Null(vm.EditTarget.ScreenId);

            // A click on the right tile: the editors, the panes and the banner mean that tile — still following the programme.
            vm.SelectTileCommand.Execute(Tile(vm, "b"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("b", vm.EditTarget.ScreenId);
            Assert.Equal("b", vm.SelectedTargetId);
            Assert.Contains("SCREEN 2", vm.EditTargetBanner);
            Assert.Contains("follows the programme", vm.EditTargetBanner);
            Assert.False(ContentTargets.UsesOwnPattern(vm.State, "b"));
            Assert.False(Tile(vm, "b").IsOwn);
            Assert.NotSame(vm.State.Pattern, vm.ActivePattern);          // the editors write into the tile's own copy
            Assert.Equal(PatternKind.Grid, vm.ActivePattern.Kind);        // a copy of the programme — nothing jumps
            Assert.Equal(PatternKind.Grid, services.Bus.Sandbox!.PatternFor("b").Kind);

            // The first edit: OWN at once, the preview of that tile shows it, the programme and the rest do not move.
            vm.ActivePattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            Assert.True(ContentTargets.UsesOwnPattern(vm.State, "b"));
            Assert.True(Tile(vm, "b").IsOwn);
            Assert.Contains("its own", vm.EditTargetBanner);
            Assert.Contains("its own picture now", vm.StatusMessage);
            Assert.Equal(PatternKind.LedWall, services.Bus.Sandbox!.PatternFor("b").Kind);   // the tile's PVW
            Assert.Equal(PatternKind.Grid, services.Bus.Sandbox!.PatternFor("a").Kind);      // the others' PVW
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);                           // the programme's preview untouched
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);       // the air untouched until a take
            Assert.False(Tile(vm, "a").IsOwn);
            Assert.False(Tile(vm, "c").IsOwn);
            // The Eye says so on the same press.
            Assert.Contains(services.Eye.Graph.Find("screen:b")!.Words, w => w.StartsWith("its own picture in the preview", StringComparison.Ordinal));
            Assert.DoesNotContain(services.Eye.Graph.Find("screen:a")!.Words, w => w.StartsWith("its own picture", StringComparison.Ordinal));

            // The tile's own TAKE puts exactly that picture up, on that screen alone.
            Tile(vm, "b").TakeHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("c").Kind);

            // The picker the other way: choosing the lobby selects its tile; leaving the right screen keeps its own picture.
            vm.EditTarget = vm.EditTargets.First(t => t.ScreenId == "c");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("c", vm.SelectedTargetId);
            Assert.True(Tile(vm, "c").IsSelected);
            Assert.False(Tile(vm, "b").IsSelected);
            Assert.True(ContentTargets.UsesOwnPattern(vm.State, "b"));
            Assert.Contains(vm.State.Independent, x => x.ScreenId == "c");                    // the lobby's inert copy, ready for an edit

            // Leaving the lobby unedited leaves nothing behind.
            vm.EditTarget = vm.EditTargets[0];
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(vm.State.Independent, x => x.ScreenId == "c");
            Assert.False(ContentTargets.UsesOwnPattern(vm.State, "c"));
            Assert.Same(vm.State.Pattern, vm.ActivePattern);

            // OWN off on the right screen: it follows the programme again, the editors go with it, the tile stays selected.
            vm.SelectTileCommand.Execute(Tile(vm, "b"));
            Tile(vm, "b").IsOwn = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(vm.EditTarget.ScreenId);
            Assert.Equal("b", vm.SelectedTargetId);
            Assert.False(ContentTargets.UsesOwnPattern(vm.State, "b"));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ARightClickEditOnTheTileTouchesThatTileAloneAndTheProgrammeTileMeansTheProgramme()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.Grid;
            services.Sandbox.SendAll();
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();

            // The preview pane's menu, with the lobby selected: the SOURCE edit lands on the lobby's copy — its own from that press.
            vm.SelectTileCommand.Execute(Tile(vm, "c"));
            var menu = vm.MenuFor("preview", null);
            Assert.NotNull(menu);
            var source = menu!.Menu.Flatten().First(e => e.Edit.StartsWith("media.source:", StringComparison.Ordinal) && e.IsEnabled);
            var entry = menu.Find(source.Id)!;
            entry.ChooseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(ContentTargets.UsesOwnPattern(vm.State, "c"));
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);                            // the programme untouched
            Assert.Equal(PatternKind.Media, services.Bus.Sandbox!.PatternFor("c").Kind);      // the lobby's own picture
            Assert.Equal(PatternKind.Grid, services.Bus.Sandbox!.PatternFor("a").Kind);

            // The PGM tile: the programme is what is edited, and FOCUSED means every armed screen.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles.Single(t => t.IsProgramTile));
            Assert.Null(vm.EditTarget.ScreenId);
            Assert.Same(vm.State.Pattern, vm.ActivePattern);
            vm.SelectedTakeScope = vm.TakeScopes[1];
            Assert.Equal("on every armed screen", vm.CurrentTakePlan.Where);
        }
        finally
        {
            b.Dispose();
        }
    }
}
