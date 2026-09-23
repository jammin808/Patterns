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
/// Round 81, from the rig: editing a screen tile concentrates on the selected tile alone; screens not set
/// to their own follow anything done to the programme's preview; a tile set to OWN is untouched by the
/// programme's preview unless SEND is clicked in that tile, which copies the preview from PGM onto that
/// tile's preview. On a live desk: a following tile's PVW is the programme's preview; the first edit with
/// the tile selected makes it its own (round 67.5) and stages the edit; editing the programme never reaches
/// the OWN tile; its CUT puts its PVW up on it alone; settled, its own TAKE and a FOCUSED take have nothing
/// to put up and name SEND as the way; SEND copies the programme's preview onto its PVW and the take lands
/// it; PROGRAM puts the tile back on the programme.
/// </summary>
public class OwnPreviewAppTests
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

    [AvaloniaFact]
    public void AnOwnTilesPreviewIsItsOwnUntilSendOrProgramSaysOtherwise()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;          // the programme: a grid everywhere
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.ColorBars;     // the preview: bars
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Settle();

            SwitcherTile Tile(string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);
            ShowSnapshot Pvw() => services.Bus.Sandbox!;

            // Every tile follows the programme: their PVWs are the programme's preview.
            Assert.Equal(PatternKind.ColorBars, Pvw().PatternFor("a").Kind);
            Assert.Equal(PatternKind.ColorBars, Pvw().PatternFor("b").Kind);
            Assert.False(Tile("b").IsOwn);

            // Select the second tile: selecting edits nothing. The first edit makes it its own and stages the edit — its PVW
            // is the edit, the programme's preview and the other tiles are untouched, and the air has not moved.
            vm.SelectTileCommand.Execute(Tile("b"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("b", services.EditingTargetId);
            Assert.False(Tile("b").IsOwn);
            vm.ActivePattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Tile("b").IsOwn);
            Assert.True(services.Sandbox.IsStaged("b"));
            Assert.Equal(PatternKind.Focus, Pvw().PatternFor("b").Kind);
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
            Assert.Equal(PatternKind.ColorBars, Pvw().PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);

            // Editing the programme never reaches the OWN tile: its PVW keeps the edit while the tiles that follow move.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles.Single(t => t.IsProgramTile));
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Focus, Pvw().PatternFor("b").Kind);
            Assert.Equal(PatternKind.LedWall, Pvw().PatternFor("a").Kind);
            Assert.Equal(PatternKind.LedWall, Pvw().PatternFor("c").Kind);

            // The tile's CUT puts its PVW up on it alone; the programme and every other screen stay; its PVW is still its own.
            Tile("b").CutHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Focus, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);
            Assert.False(services.Sandbox.IsStaged("b"));
            Assert.Equal(PatternKind.Focus, Pvw().PatternFor("b").Kind);

            // Settled: the tile's own TAKE and a FOCUSED take have nothing new to put up; both say so and name SEND as the way.
            vm.SelectedTakeScope = vm.TakeScopes[1];
            var version = services.Bus.Current.Version;
            Tile("b").TakeHereCommand.Execute(null);
            Assert.StartsWith("2 · Right already shows its own picture — nothing to take.", vm.StatusMessage);
            Assert.Contains("SEND", vm.StatusMessage);
            vm.TakeCommand.Execute(null);
            Assert.StartsWith("Nothing to take on 2 · Right alone — it already shows its own picture.", vm.StatusMessage);
            Assert.Contains("SEND", vm.StatusMessage);
            Assert.Equal(version, services.Bus.Current.Version);

            // SEND copies the programme's preview onto its PVW — pending — and the FOCUSED take lands it; the programme stays.
            Tile("b").SendHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Sandbox.IsStaged("b"));
            Assert.Equal(PatternKind.LedWall, Pvw().PatternFor("b").Kind);
            Assert.StartsWith("Staged on 2", vm.StatusMessage);
            Assert.Contains("the programme's preview is on its PVW", vm.StatusMessage);
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);
            Assert.StartsWith("TAKE — the picture on its PVW fades up on 2 · Right alone, as its own picture; the programme and every other screen stay.", vm.StatusMessage);
            Assert.False(services.Sandbox.IsStaged("b"));

            // PROGRAM puts it back on the programme: it follows again, and its PVW is the programme's preview.
            Tile("b").ProgramCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(Tile("b").IsOwn);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal(PatternKind.LedWall, Pvw().PatternFor("b").Kind);
        }
        finally
        {
            b.Dispose();
        }
    }
}
