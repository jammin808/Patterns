using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 81, from the rig: "when a screen tile is selected by drop-down, or when the tile title is clicked —
/// when a screen tile is highlighted — it is the focus of any editing or changes." The EDITING TARGET picker,
/// the tile's title and the wall's highlight are one selection: whichever is used, the same tile is highlighted,
/// under the editors, and what FOCUSED means to CUT / TAKE — and STATE's take row, the Eye's desk node and the
/// plan under the picker say which. The PGM tile is the programme; the picker on PROGRAM keeps a following tile
/// highlighted (the programme's edits reach it) and lets go of an OWN one (they do not). The take row also names
/// the ticked tiles' groups, which is what TICKED GROUPS takes to.
/// </summary>
public class FocusAppTests
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
        b.Vm.State.Output.Placements.First(p => p.ScreenId == "c").Role = ScreenRole.Confidence;
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

    [AvaloniaFact]
    public void ThePickerTheTitleAndTheHighlightAreOneSelectionAndFocusedMeansIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Settle();

            SwitcherTile Tile(string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);
            string StateJson() => new CommandRouter(services).StateJson();

            // The picker: choosing a tile highlights it, puts the editors on it, and makes it what FOCUSED means — on the
            // plan, and on STATE's take row for a deck.
            vm.EditTarget = vm.EditTargets.Single(t => t.ScreenId == "b");
            Dispatcher.UIThread.RunJobs();
            Assert.True(Tile("b").IsSelected);
            Assert.False(Tile("a").IsSelected);
            Assert.Equal("b", services.EditingTargetId);
            Assert.Equal("b", vm.SelectedTargetId);
            Assert.Equal("on 2 · Right alone", services.Actions.PlanTake(FadeScope.Focused).Where);
            Assert.Contains("\"focused\":\"b\",\"focusedLabel\":\"2 ", StateJson());

            // The title: clicking another tile moves all three at once, and the picker follows; the plan under the picker
            // and the Eye's desk node name the tile.
            vm.SelectTileCommand.Execute(Tile("c"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("c", vm.EditTarget.ScreenId);
            Assert.True(Tile("c").IsSelected);
            Assert.False(Tile("b").IsSelected);
            Assert.Equal("c", services.EditingTargetId);
            Assert.Contains("\"focused\":\"c\"", StateJson());
            vm.SelectedTakeScope = vm.TakeScopes[1];
            Assert.Contains("3 · Lobby", vm.TakePlanText);
            services.Eye.Refresh();
            Assert.Contains(services.Eye.Graph.Find("desk")!.Words, w => w.Contains("3 · Lobby", StringComparison.Ordinal));

            // The PGM tile is the programme: nothing highlighted, the editors on the programme, FOCUSED every armed screen.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles.Single(t => t.IsProgramTile));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(vm.EditTarget.ScreenId);
            Assert.Null(services.EditingTargetId);
            Assert.False(Tile("c").IsSelected);
            Assert.Equal("on every armed screen", services.Actions.PlanTake(FadeScope.Focused).Where);
            Assert.Contains("\"focused\":\"\",\"focusedLabel\":\"\"", StateJson());

            // The picker back on PROGRAM keeps a following tile highlighted — the programme's edits reach it, so FOCUSED still
            // means it — and lets go of an OWN tile, whose picture the editors no longer touch.
            vm.EditTarget = vm.EditTargets.Single(t => t.ScreenId == "a");
            Dispatcher.UIThread.RunJobs();
            vm.EditTarget = vm.EditTargets[0];
            Dispatcher.UIThread.RunJobs();
            Assert.True(Tile("a").IsSelected);
            Assert.Null(services.EditingTargetId);
            Assert.Contains("\"focused\":\"a\"", StateJson());
            vm.EditTarget = vm.EditTargets.Single(t => t.ScreenId == "a");
            Dispatcher.UIThread.RunJobs();
            vm.ActivePattern.Kind = PatternKind.Focus;                                                // own on edit (round 67.5)
            Dispatcher.UIThread.RunJobs();
            Assert.True(Tile("a").IsOwn);
            vm.EditTarget = vm.EditTargets[0];
            Dispatcher.UIThread.RunJobs();
            Assert.False(Tile("a").IsSelected);
            Assert.Contains("\"focused\":\"\"", StateJson());

            // The ticks: the take row names the ticked tiles' groups — what TICKED GROUPS takes to — in wall order.
            Tile("a").IsSendTarget = true;
            Tile("c").IsSendTarget = true;
            Assert.Contains("\"groups\":[\"main\",\"confidence\"]", StateJson());
            Assert.Contains("main and confidence", services.Actions.PlanTake(FadeScope.Groups).Where);
            Tile("c").IsSendTarget = false;
            Assert.Contains("\"groups\":[\"main\"]", StateJson());
            Tile("a").IsSendTarget = false;
            Assert.Contains("\"groups\":[]", StateJson());
        }
        finally
        {
            b.Dispose();
        }
    }
}
