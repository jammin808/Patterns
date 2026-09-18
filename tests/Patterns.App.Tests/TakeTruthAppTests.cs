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
/// Round 78: attempts are not facts on the wall. A tile's PVW holds one picture — staged or edited and
/// not yet seen, its own while the editors are on it, else the programme's preview — and a CUT / TAKE on
/// the tile lands exactly that: a second take lands the new preview rather than copying the tile's own
/// picture over itself, a take that would change nothing is refused with the way out, and every take says
/// when the outputs are off, so the desk never reports a fade-up nobody could see. The field pressed TAKE
/// eleven times with the outputs off and read "fades up" eleven times.
/// </summary>
public class TakeTruthAppTests
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

    /// <summary>Three screens, the grid on air everywhere, EDIT SAFE open with the preview still the grid.</summary>
    private static (AppServices Services, MainViewModel Vm) Open(TestApp.Booted b)
    {
        var (services, vm, _) = b;
        Rig(b);
        vm.IsSandboxActive = false;
        vm.State.Pattern.Kind = PatternKind.Grid;
        Dispatcher.UIThread.RunJobs();
        vm.IsSandboxActive = true;
        Dispatcher.UIThread.RunJobs();
        return (services, vm);
    }

    [AvaloniaFact]
    public void ASecondTakeOnATileLandsTheNewPreviewNotTheTilesOldPicture()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            Tile(vm, "b").TakeHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("b").Kind);
            Assert.True(Tile(vm, "b").IsOwn);
            Assert.False(services.Sandbox.IsStaged("b"));                                   // on air: nothing pending on the tile
            Assert.False(Tile(vm, "b").IsStaged);

            // The programme's preview moves on. The editors are on the programme, so the tile's PVW follows the
            // preview — the miniature, the big pane pointed at the tile, and the take — where it used to hold the
            // tile's own picture and the take copied that over itself, saying "fades up".
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(services.EditingTargetId);
            Assert.Equal("b", services.PreviewScreenId);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Sandbox!.PatternFor("b").Kind);
            Assert.Equal(PatternKind.ColorBars, vm.PreviewPattern.Kind);
            Assert.Equal(SandboxService.TakeEffect.Picture, services.Sandbox.EffectOf("b"));

            var again = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.True(again.Ok);
            Assert.Contains("the preview fades up on", again.Message);
            Assert.Contains("alone", again.Message);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);        // the programme and every other screen stay
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("c").Kind);
            Assert.Equal(PatternKind.Grid, services.AirState.Pattern.Kind);
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);                       // the preview keeps its picture for the next one
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATakeThatWouldChangeNothingIsRefusedWithTheWayOutAndSpendsNoOneShot()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b").Ok);
            Dispatcher.UIThread.RunJobs();
            var version = services.Bus.Current.Version;

            // A one-shot armed, then the same press again: the screen already shows this picture as its own.
            Assert.True(services.Actions.Execute(ControlProtocol.Parse("TAKE NEXT CUT").Action, ActionOrigin.Desk).Ok);
            Assert.NotNull(services.NextTake.Pending);
            Assert.Equal(SandboxService.TakeEffect.Nothing, services.Sandbox.EffectOf("b"));
            var refused = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.Equal(ActionStatus.Refused, refused.Status);
            Assert.Contains("already shows this picture", refused.Message);
            Assert.Contains("nothing to take", refused.Message);
            Assert.Contains("SEND", refused.Message);
            Assert.NotNull(services.NextTake.Pending);                                         // a press that fails spends nothing
            Assert.Equal(version, services.Bus.Current.Version);                                // and publishes nothing
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("b").Kind);

            // The same on the wire, a CUT: refused with the same words.
            var cut = services.Actions.Execute(ControlProtocol.Parse("SCREEN 2 CUT").Action, ActionOrigin.Desk);
            Assert.False(cut.Ok);
            Assert.Contains("nothing to take", cut.Message);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void WhileTheEditorsAreOnATileItsPvwIsItsOwnPictureAndAnEditIsPendingUntilTaken()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            Tile(vm, "b").TakeHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            // The editors go to the tile: its PVW is the picture being worked on — its own, already on air — and a
            // take with nothing edited is refused with the way to the programme's preview.
            vm.SelectTileCommand.Execute(Tile(vm, "b"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("b", services.EditingTargetId);
            Assert.Equal(PatternKind.LedWall, services.Bus.Sandbox!.PatternFor("b").Kind);
            Assert.Equal(PatternKind.LedWall, vm.PreviewPattern.Kind);
            var idle = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.Equal(ActionStatus.Refused, idle.Status);
            Assert.Contains("already shows its own picture", idle.Message);
            Assert.Contains("PROGRAM", idle.Message);

            // One edit: pending. The miniature shows it, the programme's preview never moved, the take lands it.
            vm.ActivePattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Sandbox.IsStaged("b"));
            Assert.Equal(PatternKind.Focus, services.Bus.Sandbox!.PatternFor("b").Kind);
            Assert.Equal(PatternKind.LedWall, vm.State.Pattern.Kind);
            Assert.Equal(PatternKind.LedWall, services.Bus.Sandbox!.PatternFor("a").Kind);      // the other tiles follow the programme's preview
            var landed = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.True(landed.Ok);
            Assert.Contains("PVW", landed.Message);
            Assert.Equal(PatternKind.Focus, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);
            Assert.False(services.Sandbox.IsStaged("b"));

            // Back on the programme: the tile's PVW follows the preview again, and the pane pointed at it agrees.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles[0]);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(services.EditingTargetId);
            Assert.Equal(PatternKind.LedWall, services.Bus.Sandbox!.PatternFor("b").Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStagedPictureLightsPvwOnTheTileAndStateAndTheEyeSayItIsNotTakenYet()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            vm.State.Pattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();
            Tile(vm, "b").SendHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(services.Sandbox.IsStaged("b"));
            Assert.True(Tile(vm, "b").IsStaged);
            Assert.False(Tile(vm, "a").IsStaged);
            Assert.Contains("\"pending\":[\"b\"]", new CommandRouter(services).StateJson());
            services.Eye.Refresh();
            Assert.Contains(services.Eye.Graph.Find("screen:b")!.Words, w => w.Contains("not taken yet", StringComparison.Ordinal));
            Assert.DoesNotContain(services.Eye.Graph.Find("screen:a")!.Words, w => w.Contains("not taken yet", StringComparison.Ordinal));

            // A picture staged the same as what is on air is a take that lights OWN and moves nothing visible — and says so.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles[0]);
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            Tile(vm, "c").SendHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SandboxService.TakeEffect.OwnOnly, services.Sandbox.EffectOf("c"));
            var ownOnly = services.Actions.Execute(ShowActionKind.ScreenCut, ActionOrigin.Desk, "c");
            Assert.True(ownOnly.Ok);
            Assert.Contains("already showed this picture", ownOnly.Message);
            Assert.Contains("OWN", ownOnly.Message);
            Assert.True(ContentTargets.UsesOwnPattern(services.AirState, "c"));

            // TAKE on the staged tile puts it up: pending gone, the badge off.
            Tile(vm, "b").TakeHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Focus, services.Bus.Current.PatternFor("b").Kind);
            Assert.False(Tile(vm, "b").IsStaged);
            Assert.Contains("\"pending\":[]", new CommandRouter(services).StateJson());
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void EveryTakeSaysWhenTheOutputsAreOffAndTheJournalCarriesIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            Assert.False(services.Outputs.IsLive);
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();

            // The tile's take lands in the model — the screens show it the moment the outputs open — and says so.
            var one = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.True(one.Ok);
            Assert.Contains("outputs are off", one.Message);
            Assert.Contains("OUTPUTS ON", one.Message);
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("b").Kind);
            var row = services.Journal.Tail(5).Last(e => e.Kind == "ScreenTake");
            Assert.Contains("OUTPUTS ON", row.Message);
            Assert.Contains("\"outputsLive\":false", new CommandRouter(services).StateJson());

            // The wall's take too — and with nothing changed in the preview it says every screen already showed its picture.
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("OUTPUTS ON", vm.StatusMessage);
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("already showed", vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }
}
