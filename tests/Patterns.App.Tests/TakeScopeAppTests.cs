using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 15: "Cut / Take could be scoped to four areas: The focused screen, the selected screens,
/// the selected groups, or all." On a live desk: the wall's picker, a TAKE to the focused tile
/// alone with the rest pinned to their picture, the next full TAKE lifting the pins, a CUT to the
/// ticked tiles, the refusals with their reasons, the PGM tile as every armed screen, and ARM / LOCK
/// still counting inside a scope.
/// </summary>
public class TakeScopeAppTests
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

    private static bool Pinned(AppServices services, string target)
        => services.AirState.Independent.FirstOrDefault(x => x.ScreenId == target) is { PinnedByTake: true };

    [AvaloniaFact]
    public void TheWallPicksWhereCutAndTakeLandAndTheRestKeepsItsPicture()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;          // the program: a grid everywhere
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.ColorBars;     // the preview: bars
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Settle(window);

            // The picker sits on the wall beside CUT and TAKE and starts on every armed screen.
            var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "TakeScopePicker");
            Assert.Equal(4, picker.ItemCount);
            Assert.Same(vm.TakeScopes[0], picker.SelectedItem);
            Assert.True(picker.IsEffectivelyVisible);
            Assert.True(picker.IsEnabled);

            // A send can rebuild the wall (own patterns come and go), so a tile is looked up fresh each time.
            SwitcherTile Tile(string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);

            // THE FOCUSED SCREEN: the bars land on the second screen alone; the others keep the grid, pinned.
            vm.SelectTileCommand.Execute(Tile("b"));
            vm.SelectedTakeScope = vm.TakeScopes[1];
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var air = services.Bus.Current;
            Assert.Equal(PatternKind.ColorBars, air.State.Pattern.Kind);
            Assert.Equal(PatternKind.ColorBars, air.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("c").Kind);
            Assert.True(Pinned(services, "a"));
            Assert.True(Pinned(services, "c"));
            Assert.False(Pinned(services, "b"));
            Assert.Contains("2 kept their picture", vm.StatusMessage);
            Assert.True(vm.IsSandboxActive, "EDIT SAFE re-armed after the send");

            // EVERY ARMED SCREEN: the next full TAKE lifts the pins — everything follows the new program.
            vm.SelectedTakeScope = vm.TakeScopes[0];
            vm.State.Pattern.Kind = PatternKind.Focus;
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            air = services.Bus.Current;
            Assert.Equal(PatternKind.Focus, air.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Focus, air.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Focus, air.PatternFor("c").Kind);
            Assert.False(Pinned(services, "a"));
            Assert.False(Pinned(services, "c"));
            Assert.DoesNotContain("kept", vm.StatusMessage);

            // THE TICKED SCREENS with a CUT: the ticked two take the grid, the third keeps the focus pattern; the ticks are consumed.
            Tile("a").IsSendTarget = true;
            Tile("c").IsSendTarget = true;
            vm.SelectedTakeScope = vm.TakeScopes[2];
            vm.State.Pattern.Kind = PatternKind.Grid;
            var beforeCut = services.Bus.Current.Version;
            vm.CutCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            air = services.Bus.Current;
            Assert.Equal(PatternKind.Grid, air.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("c").Kind);
            Assert.Equal(PatternKind.Focus, air.PatternFor("b").Kind);
            Assert.True(Pinned(services, "b"));
            Assert.True(air.CutAtVersion > beforeCut, "the send was a cut: the snapshot after it carries the cut version");
            Assert.False(Tile("a").IsSendTarget);
            Assert.False(Tile("c").IsSendTarget);
            Assert.StartsWith("CUT", vm.StatusMessage);

            // Nothing ticked, no group on the wall: refused with the reason, the air untouched.
            var before = services.Bus.Current.Version;
            vm.CutCommand.Execute(null);
            Assert.Contains("Tick the wall tiles", vm.StatusMessage);
            vm.SelectedTakeScope = vm.TakeScopes[3];
            Tile("a").IsSendTarget = true;
            vm.TakeCommand.Execute(null);
            Assert.Contains("Tick a group", vm.StatusMessage);
            Assert.Equal(before, services.Bus.Current.Version);
            Tile("a").IsSendTarget = false;

            // The PGM tile focused means every armed screen: the pin on the second screen lifts.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles.Single(t => t.IsProgramTile));
            vm.SelectedTakeScope = vm.TakeScopes[1];
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            air = services.Bus.Current;
            Assert.Equal(PatternKind.ColorBars, air.PatternFor("b").Kind);
            Assert.False(Pinned(services, "b"));

            // ARM off still counts inside a scope: ticked a and b, b un-armed — a alone takes.
            Tile("a").IsSendTarget = true;
            Tile("b").IsSendTarget = true;
            Tile("b").IsArmed = false;
            vm.SelectedTakeScope = vm.TakeScopes[2];
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            air = services.Bus.Current;
            Assert.Equal(PatternKind.Grid, air.PatternFor("a").Kind);
            Assert.Equal(PatternKind.ColorBars, air.PatternFor("b").Kind);
            Assert.Equal(PatternKind.ColorBars, air.PatternFor("c").Kind);
            Assert.Contains("2 kept their picture", vm.StatusMessage);

            // The wire's words for a place mean the same to a take: the action layer takes SCREEN 3 by itself.
            vm.State.Pattern.Kind = PatternKind.Focus;
            Assert.True(services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "SCREEN 3").Ok);
            Dispatcher.UIThread.RunJobs();
            air = services.Bus.Current;
            Assert.Equal(PatternKind.Focus, air.PatternFor("c").Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("a").Kind);
            Assert.StartsWith("ERR", services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "SCREEN 9").Ok ? "OK" : "ERR");
            Assert.False(services.Actions.Execute(ShowActionKind.Cut, ActionOrigin.Desk, "sideways").Ok);
        }
        finally
        {
            b.Dispose();
        }
    }
}
