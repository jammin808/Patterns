using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 26: the switcher's round trip — pull a picture into the preview, change it, stage it back
/// on the tile it came from, take it there and nowhere else.
///
/// Every piece of that existed except the joins. → PVW could not name the programme, so "put what
/// is on air back into the preview" had no button; it then cleared the focus, so a FOCUSED take
/// after it silently widened to the whole rig; and SEND punched straight to air, so there was no
/// way to hold a picture on one tile's preview and decide.
/// </summary>
public class SwitcherRoundTripTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new Avalonia.PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new Avalonia.PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
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

    private static SwitcherTile Tile(MainViewModel vm, string? id) => vm.SwitcherTiles.Single(t => t.TargetId == id);

    [AvaloniaFact]
    public void TheProgramComesBackIntoThePreviewFromItsOwnTile()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();

            // The PGM tile is a tile like any other now.
            var pgm = Tile(vm, null);
            Assert.True(pgm.IsProgramTile);
            pgm.ToPreviewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsSandboxActive);                                      // it opened EDIT SAFE first
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);           // what the room is watching, in the preview
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.State.Pattern.Kind);
            Assert.Contains("program", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

            // And it is a copy: changing it leaves the air alone until a take.
            vm.State.Pattern.Kind = PatternKind.Geometry;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.State.Pattern.Kind);

            // The programme is the whole rig, so the focus is cleared for it — a FOCUSED take
            // means every armed screen, exactly as it always has.
            Assert.Null(vm.SelectedTargetId);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AFocusedTakeAfterAStagedSendTouchesThatTileAndNothingElse()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();

            // Pull screen b's picture in, change it, stage it back on b.
            Tile(vm, "b").ToPreviewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("b", vm.SelectedTargetId);
            vm.State.Pattern.Kind = PatternKind.Focus;
            Tile(vm, "b").SendHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            // Staged: b's PVW holds it, the room still sees the grid everywhere.
            Assert.True(services.Sandbox.IsStaged("b"));
            Assert.Equal(PatternKind.Focus, services.Bus.Sandbox!.PatternFor("b").Kind);
            foreach (var id in new[] { "a", "b", "c" })
            {
                Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor(id).Kind);
            }

            // FOCUSED take: b alone changes.
            Assert.Equal("b", vm.SelectedTargetId);
            Assert.True(services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "focused").Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Focus, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("c").Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATakeThatLeavesATileAloneDoesNotThrowAwayWhatIsStagedOnIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            vm.IsSandboxActive = true;

            // Stage a picture on c, then take somewhere else entirely.
            vm.State.Pattern.Kind = PatternKind.Focus;
            Tile(vm, "c").SendHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Sandbox.IsStaged("c"));

            vm.State.Pattern.Kind = PatternKind.ColorBars;
            vm.SelectTileCommand.Execute(Tile(vm, "a"));
            Assert.True(services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "focused").Ok);
            Dispatcher.UIThread.RunJobs();

            // a took the new picture. c was left alone, so the audience's picture on it does not
            // move — and the staging, which was not taken, leaves no residue: c follows the show
            // again rather than quietly becoming a tile with a picture of its own.
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("c").Kind);
            Assert.False(services.Sandbox.IsStaged("c"));
            Assert.True(services.State.Independent.First(x => x.ScreenId == "c").PinnedByTake);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void StagingIsNotATakeSoNothingCrossfadesUntilItGoesUp()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();

            // Every snapshot a gesture publishes, so a later unrelated publish cannot flatter it.
            var takes = new List<bool>();
            services.Bus.Changed += () => takes.Add(services.Bus.Current.IsTake);

            Tile(vm, "b").SendHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            // Moving a picture into a preview is not moving it on air: one operator gesture must
            // not cost two dissolves — one as it is staged and another as it goes up.
            Assert.NotEmpty(takes);
            Assert.DoesNotContain(true, takes);

            takes.Clear();
            Assert.True(services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk, "focused").Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(true, takes);
        }
        finally
        {
            b.Dispose();
        }
    }
}
