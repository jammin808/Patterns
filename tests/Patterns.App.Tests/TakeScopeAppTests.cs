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
/// the selected groups, or all." Round 81, from the rig: a scoped take lands on its targets alone, as
/// their own pictures — the programme and every other screen untouched, nothing pinned; only ALL
/// ARMED moves the programme, and a group is what a screen is for. On a live desk: the wall's picker,
/// a TAKE to the focused tile alone, ALL ARMED moving the programme with the OWN tile keeping its own
/// until PROGRAM, a CUT to the ticked tiles, GROUPS by kind, the refusals with their reasons, the PGM
/// tile as every armed screen, and ARM / LOCK still counting inside a scope.
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
    public void TheWallPicksWhereCutAndTakeLandAndAScopedTakeTouchesNothingElse()
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
            Assert.Equal(7, picker.ItemCount);                                                   // round 81: the groups by kind joined the picker
            Assert.Same(vm.TakeScopes[0], picker.SelectedItem);
            Assert.True(picker.IsEffectivelyVisible);
            Assert.True(picker.IsEnabled);

            // A send can rebuild the wall (own patterns come and go), so a tile is looked up fresh each time.
            SwitcherTile Tile(string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);

            // FOCUSED (round 81): the bars land on the second screen alone, as its own picture. The programme's air
            // stays the grid and its preview the bars; the other two are untouched — not pinned, not OWN — and EDIT
            // SAFE stays open with the preview kept for the next one.
            vm.SelectTileCommand.Execute(Tile("b"));
            vm.SelectedTakeScope = vm.TakeScopes[1];
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var air = services.Bus.Current;
            Assert.Equal(PatternKind.Grid, air.State.Pattern.Kind);                              // the programme did not move
            Assert.Equal(PatternKind.ColorBars, air.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("c").Kind);
            Assert.False(Pinned(services, "a"));
            Assert.False(Pinned(services, "c"));
            Assert.True(ContentTargets.UsesOwnPattern(services.AirState, "b"));
            Assert.False(ContentTargets.UsesOwnPattern(services.AirState, "a"));
            Assert.True(Tile("b").IsOwn);
            Assert.False(Tile("a").IsOwn);
            Assert.StartsWith("TAKE — the preview fades up on 2 · Right alone, as its own picture; the programme and every other screen stay.", vm.StatusMessage);
            Assert.True(vm.IsSandboxActive, "EDIT SAFE stays open: the preview is kept for the next one");
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);

            // ALL ARMED: the programme moves and a and c follow it; b keeps its own picture (round 30) until PROGRAM on
            // its tile puts it back on the programme.
            vm.SelectedTakeScope = vm.TakeScopes[0];
            vm.State.Pattern.Kind = PatternKind.Focus;
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            air = services.Bus.Current;
            Assert.Equal(PatternKind.Focus, air.State.Pattern.Kind);
            Assert.Equal(PatternKind.Focus, air.PatternFor("a").Kind);
            Assert.Equal(PatternKind.ColorBars, air.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Focus, air.PatternFor("c").Kind);
            Assert.DoesNotContain("kept", vm.StatusMessage);
            Tile("b").ProgramCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Focus, services.Bus.Current.PatternFor("b").Kind);
            Assert.False(Tile("b").IsOwn);

            // TICKED with a CUT: the ticked two take the grid as their own pictures; the third is untouched; the ticks are consumed.
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
            Assert.Equal(PatternKind.Focus, air.State.Pattern.Kind);
            Assert.False(Pinned(services, "b"));
            Assert.False(Tile("b").IsOwn);
            Assert.True(air.CutAtVersion > beforeCut, "the send was a cut: the snapshot after it carries the cut version");
            Assert.False(Tile("a").IsSendTarget);
            Assert.False(Tile("c").IsSendTarget);
            Assert.StartsWith("CUT — the preview is on 1 · Left, 3 · Lobby, as their own pictures; the programme and every other screen stay.", vm.StatusMessage);
            foreach (var id in new[] { "a", "c" })
            {
                Tile(id).ProgramCommand.Execute(null);                                            // back on the programme for the group take below
                Dispatcher.UIThread.RunJobs();
            }

            // Nothing ticked: refused with the reason, the air untouched; GROUPS the same.
            var before = services.Bus.Current.Version;
            vm.CutCommand.Execute(null);
            Assert.Contains("Tick the wall tiles", vm.StatusMessage);
            vm.SelectedTakeScope = vm.TakeScopes[3];
            vm.TakeCommand.Execute(null);
            Assert.Contains("Tick a tile in a group", vm.StatusMessage);                          // round 81: nothing ticked names no group
            Assert.Equal(before, services.Bus.Current.Version);

            // GROUPS (round 81): tick one main screen and every main screen takes the bars as its own; the programme still did not move.
            Tile("a").IsSendTarget = true;
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            air = services.Bus.Current;
            foreach (var id in new[] { "a", "b", "c" })
            {
                Assert.Equal(PatternKind.ColorBars, air.PatternFor(id).Kind);
                Assert.True(Tile(id).IsOwn);
            }
            Assert.Equal(PatternKind.Focus, air.State.Pattern.Kind);
            Assert.False(Tile("a").IsSendTarget);
            Assert.StartsWith("TAKE — the preview fades up on 1 · Left, 2 · Right, 3 · Lobby, as their own pictures", vm.StatusMessage);

            // Once more with nothing new: every main screen already shows the bars as its own — refused with the way out, nothing spent.
            Tile("a").IsSendTarget = true;
            var again = services.Bus.Current.Version;
            vm.TakeCommand.Execute(null);
            Assert.StartsWith("Nothing to take on the main screens", vm.StatusMessage);
            Assert.Contains("SEND", vm.StatusMessage);
            Assert.Equal(again, services.Bus.Current.Version);
            Assert.True(Tile("a").IsSendTarget, "a refused take spends no tick");
            Tile("a").IsSendTarget = false;

            // The PGM tile focused means every armed screen — the rig: every screen is OWN with the bars, so the programme
            // moving to the bars changes nothing the room sees, and the wall says so. PROGRAM on each tile puts them back
            // on the programme; then the programme moves and they follow, nothing pinned.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles.Single(t => t.IsProgramTile));
            vm.SelectedTakeScope = vm.TakeScopes[1];
            vm.TakeCommand.Execute(null);
            Assert.StartsWith("Nothing to take on every armed screen", vm.StatusMessage);
            foreach (var id in new[] { "a", "b", "c" })
            {
                Tile(id).ProgramCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
            }
            vm.SelectTileCommand.Execute(vm.SwitcherTiles.Single(t => t.IsProgramTile));           // a tile's button focuses that tile: back to the PGM tile for the rig
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            air = services.Bus.Current;
            Assert.Equal(PatternKind.Grid, air.State.Pattern.Kind);
            foreach (var id in new[] { "a", "b", "c" })
            {
                Assert.Equal(PatternKind.Grid, air.PatternFor(id).Kind);
                Assert.False(Pinned(services, id));
                Assert.False(Tile(id).IsOwn);
            }

            // ARM off still counts inside a scope: ticked a and b, b un-armed — a alone takes, as its own; b is untouched, not pinned, and named as held.
            Tile("a").IsSendTarget = true;
            Tile("b").IsSendTarget = true;
            Tile("b").IsArmed = false;
            vm.SelectedTakeScope = vm.TakeScopes[2];
            vm.State.Pattern.Kind = PatternKind.Focus;
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            air = services.Bus.Current;
            Assert.Equal(PatternKind.Focus, air.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, air.PatternFor("c").Kind);
            Assert.False(Pinned(services, "b"));
            Assert.False(Tile("b").IsOwn);
            Assert.Contains("on 1 · Left alone", vm.StatusMessage);
            Assert.Contains("held: 2 · Right (not armed)", vm.StatusMessage);
            Tile("b").IsArmed = true;

            // The wire's words for a place mean the same to a take: the action layer takes SCREEN 3 by itself, as its own.
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Assert.True(services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "SCREEN 3").Ok);
            Dispatcher.UIThread.RunJobs();
            air = services.Bus.Current;
            Assert.Equal(PatternKind.ColorBars, air.PatternFor("c").Kind);
            Assert.Equal(PatternKind.Focus, air.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, air.State.Pattern.Kind);
            Assert.False(services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "SCREEN 9").Ok);
            Assert.False(services.Actions.Execute(ShowActionKind.Cut, ActionOrigin.Desk, "sideways").Ok);
        }
        finally
        {
            b.Dispose();
        }
    }
}
