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
/// Round 63: CUT and TAKE on a screen tile put the preview on that screen alone — as its own
/// picture, so OWN lights up by itself — while the programme and every other screen stay exactly
/// as they were and the preview keeps the picture for the next one. The look tally reads the
/// result: the screen has gone its own way inside the look on air, and the desk says so.
/// </summary>
public class TileTakeTests
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
    public void TakeOnATilePutsThePreviewOnThatScreenAloneAsItsOwnPictureAndTheRestStay()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();

            // EDIT SAFE off: nothing to take — the tile's key is refused with the words, and nothing moves.
            var refused = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.False(refused.Ok);
            Assert.Contains("EDIT SAFE", refused.Message);

            // Build a picture in the preview, then TAKE it to the right screen alone.
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            Assert.False(Tile(vm, "b").IsOwn);
            Tile(vm, "b").TakeHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("b").Kind);   // on air, there alone
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);      // the programme, untouched
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("c").Kind);
            Assert.Equal(PatternKind.Grid, services.AirState.Pattern.Kind);
            Assert.True(ContentTargets.UsesOwnPattern(services.AirState, "b"));              // OWN, set by the take
            Assert.True(Tile(vm, "b").IsOwn);
            Assert.False(Tile(vm, "a").IsOwn);
            Assert.True(services.Sandbox.Active);                                             // the preview keeps its picture
            Assert.Equal(PatternKind.LedWall, vm.State.Pattern.Kind);
            Assert.Equal("b", vm.SelectedTargetId);                                          // the hand chose this tile: FOCUSED means it
            Assert.Contains("alone", vm.StatusMessage);

            // The wire's own words reach the same verb: the third screen, cut.
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            var line = ControlProtocol.Parse("SCREEN 3 CUT");
            Assert.True(services.Actions.Execute(line.Action, ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("c").Kind);
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(services.Bus.Current.Version, services.Bus.Current.CutAtVersion);   // a cut, not a fade
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATakenScreenHasGoneItsOwnWayInsideTheLookOnAirAndTheDeskSaysSo()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            var look = new LookConfig { Name = "Walk-in", Json = LookService.Capture(vm.State) };
            vm.State.LooksAndCues.Looks.Add(look);
            Assert.True(services.Actions.Execute(ShowActionKind.ApplyLook, ActionOrigin.Desk, look.Id).Ok);
            Dispatcher.UIThread.RunJobs();
            vm.RefreshTallies();
            Assert.False(services.LookTally.AirEdited());
            Assert.Equal("PROGRAM", look.TallyText);

            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.Motion;
            Dispatcher.UIThread.RunJobs();
            Tile(vm, "b").CutHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            // The look is still the one on air, but this screen has gone its own way — and every
            // reader of the tally agrees: the Looks page, the tile's menu, the wire's STATE.
            Assert.True(services.LookTally.AirEdited());
            Assert.True(services.LookTally.IsOffLook("b"));
            Assert.False(services.LookTally.IsOffLook("a"));
            Assert.Contains("EDITED", look.TallyText);
            var facts = DeskMenuFacts.Screen(services, "b", true);
            Assert.True(facts.OffLook);
            Assert.True(facts.Own);
            var menu = vm.MenuFor("tile", Tile(vm, "b"));
            Assert.NotNull(menu);
            Assert.Contains("off the look", menu!.Menu.Subtitle);   // the header says it, beside OWN
        }
        finally
        {
            b.Dispose();
        }
    }
}
