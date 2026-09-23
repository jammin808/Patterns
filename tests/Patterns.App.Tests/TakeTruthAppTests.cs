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
/// Round 78: attempts are not facts on the wall. A tile's PVW holds one picture — its own while it is OWN,
/// else the programme's preview (round 81: an OWN tile is its own until SEND or PROGRAM says otherwise) —
/// and a CUT / TAKE on the tile lands exactly that: a second take on an OWN tile needs SEND to bring the
/// new preview first, a take that would change nothing is refused with the way out, and every take says
/// when the outputs are off, so the desk never reports a fade-up nobody could see. The field pressed TAKE
/// eleven times with the outputs off and read "fades up" eleven times.
///
/// Round 79.1: the facts as fields. Every result is stamped with whether the room could see it land (the outputs,
/// the blackout) and every take with what it did to the air (measured before and after, at the executor); the
/// journal row, STATE's take row and the Eye's desk node carry them. A hand's wall TAKE over an air that already
/// is the preview is refused like the tile's; the show's own automation is not, and its row says Nothing changed.
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

    /// <summary>The outputs open on the fake screens (headless windows), so a take can be seen.</summary>
    private static void GoLive(TestApp.Booted b)
    {
        var on = b.Services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
        Assert.True(on.Ok, on.Message);
        Dispatcher.UIThread.RunJobs();
        Assert.True(b.Services.Outputs.IsLive);
    }

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
    public void ASecondTakeOnAnOwnTileNeedsSendFirstBecauseItsPvwIsItsOwn()
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

            // The programme's preview moves on. The tile is OWN now, so its PVW keeps its own picture (round 81) — the
            // miniature, the big pane pointed at the tile, and the take — and the programme's preview reaches it only
            // when SEND copies it there: the same press again is refused with that way out, and spends nothing.
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(services.EditingTargetId);
            Assert.Equal("b", services.PreviewScreenId);
            Assert.Equal(PatternKind.LedWall, services.Bus.Sandbox!.PatternFor("b").Kind);
            Assert.Equal(PatternKind.LedWall, vm.PreviewPattern.Kind);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Sandbox!.PatternFor("a").Kind);   // a follows the programme: its PVW is the preview
            Assert.Equal(SandboxService.TakeEffect.Nothing, services.Sandbox.EffectOf("b"));
            var same = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.Equal(ActionStatus.Refused, same.Status);
            Assert.Contains("already shows its own picture", same.Message);
            Assert.Contains("SEND", same.Message);
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("b").Kind);

            // SEND copies the programme's preview onto the tile's PVW — pending now — and the take lands it.
            Tile(vm, "b").SendHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Sandbox.IsStaged("b"));
            Assert.Equal(PatternKind.ColorBars, services.Bus.Sandbox!.PatternFor("b").Kind);
            Assert.Equal(SandboxService.TakeEffect.Picture, services.Sandbox.EffectOf("b"));
            var again = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.True(again.Ok);
            Assert.Contains("PVW", again.Message);
            Assert.Contains("fades up", again.Message);
            Assert.Contains("alone", again.Message);
            Assert.False(services.Sandbox.IsStaged("b"));
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
            Assert.Contains("already shows its own picture", refused.Message);
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
            Assert.Contains("SEND", idle.Message);

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

            // Back on the programme: the tile is OWN, so its PVW stays its own picture (round 81) — the programme's
            // preview reaches it only through SEND — while the tiles that follow show the preview.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles[0]);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(services.EditingTargetId);
            Assert.Equal(PatternKind.Focus, services.Bus.Sandbox!.PatternFor("b").Kind);
            Assert.Equal(PatternKind.LedWall, services.Bus.Sandbox!.PatternFor("a").Kind);
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
            Assert.Equal(ActionEffect.OwnOnly, ownOnly.Effect);                                   // round 79: measured, not believed
            Assert.Equal(ActionVisibility.OutputsOff, ownOnly.Visibility);
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
            Assert.Equal("OutputsOff", row.Visibility);                                           // round 79: the fact as a field, not only in the words
            Assert.Equal("Changed", row.Effect);
            Assert.Equal(ActionVisibility.OutputsOff, one.Visibility);
            Assert.Equal(ActionEffect.Changed, one.Effect);
            Assert.Contains("\"outputsLive\":false", new CommandRouter(services).StateJson());

            // The wall's take too — and with nothing changed in the preview the next press is refused (round 79), where
            // it used to be reported done with "every screen already showed its picture".
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("OUTPUTS ON", vm.StatusMessage);
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Nothing to take", vm.StatusMessage);
            Assert.Contains("\"outcome\":\"Refused\",\"effect\":\"Nothing\",\"visibility\":\"OutputsOff\"", new CommandRouter(services).StateJson());
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATakeWithAnOutputOpenIsAFactRowAndASecondPressIsRefusedAsNothing()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            GoLive(b);
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();

            // The tile's take: seen, and it changed the picture — on the result, in the journal, in STATE, in the Eye.
            var one = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.True(one.Ok, one.Message);
            Assert.Equal(ActionVisibility.OutputsLive, one.Visibility);
            Assert.Equal(ActionEffect.Changed, one.Effect);
            Assert.DoesNotContain("outputs are off", one.Message);
            var row = services.Journal.Tail(5).Last(e => e.Kind == "ScreenTake");
            Assert.Equal("Done", row.Outcome);
            Assert.Equal("OutputsLive", row.Visibility);
            Assert.Equal("Changed", row.Effect);
            Assert.Contains("\"last\":{\"kind\":\"ScreenTake\",\"outcome\":\"Done\",\"effect\":\"Changed\",\"visibility\":\"OutputsLive\"", new CommandRouter(services).StateJson());
            Assert.NotNull(services.Actions.LastTake);
            services.Eye.Refresh();
            Assert.Contains(services.Eye.Graph.Find(EyeGraph.DeskId)!.Words,
                w => w.StartsWith("Last take: TAKE ", StringComparison.Ordinal) && w.Contains("the pictures changed", StringComparison.Ordinal) && w.Contains("outputs live", StringComparison.Ordinal));

            // The same press again: refused, and the refusal is a fact row too — nothing changed, nothing published.
            var version = services.Bus.Current.Version;
            var again = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.Equal(ActionStatus.Refused, again.Status);
            Assert.Equal(ActionEffect.Nothing, again.Effect);
            Assert.Equal(ActionVisibility.OutputsLive, again.Visibility);
            Assert.Equal(version, services.Bus.Current.Version);
            var refused = services.Journal.Tail(5).Last(e => e.Kind == "ScreenTake");
            Assert.Equal("Refused", refused.Outcome);
            Assert.Equal("Nothing", refused.Effect);
            Assert.Contains("\"last\":{\"kind\":\"ScreenTake\",\"outcome\":\"Refused\",\"effect\":\"Nothing\"", new CommandRouter(services).StateJson());
            services.Eye.Refresh();
            Assert.Contains(services.Eye.Graph.Find(EyeGraph.DeskId)!.Words,
                w => w.StartsWith("Last take: TAKE ", StringComparison.Ordinal) && w.Contains("refused: ", StringComparison.Ordinal) && w.Contains("nothing to take", StringComparison.Ordinal));

            // The wall: the programme's preview is the LED wall and a and c still show the grid, so the take changes them.
            // The next press over an air that already is the preview is refused with the way out, spends no one-shot and
            // publishes nothing — the field's eleven presses, answered on the wall as round 78 answered them on the tile.
            var wall = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk);
            Assert.True(wall.Ok, wall.Message);
            Assert.Equal(ActionEffect.Changed, wall.Effect);
            Assert.Equal(ActionVisibility.OutputsLive, wall.Visibility);
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("a").Kind);
            Assert.True(services.Actions.Execute(ControlProtocol.Parse("TAKE NEXT CUT").Action, ActionOrigin.Desk).Ok);
            version = services.Bus.Current.Version;
            var nothing = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Refused, nothing.Status);
            Assert.StartsWith("Nothing to take", nothing.Message, StringComparison.Ordinal);
            Assert.Contains("the air is already the preview", nothing.Message);
            Assert.Contains("SEND", nothing.Message);
            Assert.Equal(ActionEffect.Nothing, nothing.Effect);
            Assert.NotNull(services.NextTake.Pending);
            Assert.Equal(version, services.Bus.Current.Version);
            var cut = services.Actions.Execute(ShowActionKind.Cut, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Refused, cut.Status);
            Assert.Contains("Nothing to take", cut.Message);
            Assert.Equal("Nothing", services.Journal.Tail(5).Last(e => e.Kind == "Cut").Effect);

            // The blackout up: the take lands in the model, and its stamp says the screens stayed black.
            Assert.True(services.Actions.Execute(ShowActionKind.BlackoutOn, ActionOrigin.Desk).Ok);
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            var dark = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk);
            Assert.True(dark.Ok, dark.Message);
            Assert.Equal(ActionVisibility.Blackout, dark.Visibility);
            Assert.Equal(ActionEffect.Changed, dark.Effect);
            Assert.Contains("Blackout is on", dark.Message);
            var darkRow = services.Journal.Tail(5).Last(e => e.Kind == "Take");
            Assert.Equal("Blackout", darkRow.Visibility);
            Assert.Equal("Changed", darkRow.Effect);
            services.Eye.Refresh();
            Assert.Contains(services.Eye.Graph.Find(EyeGraph.DeskId)!.Words, w => w.StartsWith("Last take: TAKE ", StringComparison.Ordinal) && w.Contains("blackout — unseen", StringComparison.Ordinal));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheShowsOwnAutomationIsNotRefusedWhenItsEndStateHoldsAndItsRowSaysNothingChanged()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            var cue = new ActionOrigin(OriginKind.Cue, "Q3");

            // Nothing edited: a hand is refused; the cue's take runs, changes nothing, and its row says exactly that.
            Assert.Equal(ActionStatus.Refused, services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk).Status);
            var byCue = services.Actions.Execute(ShowActionKind.Take, cue);
            Assert.True(byCue.Ok, byCue.Message);
            Assert.Equal(ActionEffect.Nothing, byCue.Effect);
            Assert.Contains("every screen taken already showed its picture", byCue.Message);
            var row = services.Journal.Tail(5).Last(e => e.Kind == "Take");
            Assert.Equal("cue Q3", row.Origin);
            Assert.Equal("Done", row.Outcome);
            Assert.Equal("Nothing", row.Effect);
            Assert.Equal("OutputsOff", row.Visibility);

            // The tile the same way: the first cue take lands, the second is a no-op said as one, a hand's is refused.
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            var first = services.Actions.Execute(ShowActionKind.ScreenTake, cue, "b");
            Assert.True(first.Ok, first.Message);
            Assert.Equal(ActionEffect.Changed, first.Effect);
            var second = services.Actions.Execute(ShowActionKind.ScreenTake, cue, "b");
            Assert.True(second.Ok, second.Message);
            Assert.Equal(ActionEffect.Nothing, second.Effect);
            Assert.Contains("nothing changed", second.Message);
            Assert.Equal(ActionStatus.Refused, services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b").Status);
            Assert.Equal(ActionStatus.Refused, services.Actions.Execute(ShowActionKind.ScreenTake, new ActionOrigin(OriginKind.Companion, "FOH deck"), "b").Status);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void EveryResultIsStampedAndEveryDoneTakeFromAHandChangedSomething()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            var seen = new List<(ShowAction Action, ActionOrigin Origin, ActionResult Result)>();
            services.Actions.Performed += (a, o, r) => seen.Add((a, o, r));

            void Press(ShowActionKind kind, string target = "") => services.Actions.Execute(kind, ActionOrigin.Desk, target);
            Press(ShowActionKind.BlackoutOn);
            Press(ShowActionKind.BlackoutOff);
            Press(ShowActionKind.ScreenCut, "c");                                                 // own only: c already shows the preview (the grid) and becomes its own
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            Press(ShowActionKind.ScreenTake, "b");                                                // changed
            Press(ShowActionKind.ScreenTake, "b");                                                // refused: nothing
            Press(ShowActionKind.Take);                                                           // changed: a
            Press(ShowActionKind.Take);                                                           // refused: nothing
            vm.State.Pattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();
            Press(ShowActionKind.Cut);                                                            // changed: a again
            Press(ShowActionKind.Cut);                                                            // refused
            services.Actions.Execute(ShowActionKind.Take, new ActionOrigin(OriginKind.Cue, "Q1"));   // automation: done, nothing

            Assert.Equal(10, seen.Count);                                                        // every press above, once each
            foreach (var (action, origin, result) in seen)
            {
                Assert.NotEqual(ActionVisibility.Unknown, result.Visibility);
                if (!ShowActions.IsTake(action.Kind))
                {
                    Assert.Equal(ActionEffect.NotMeasured, result.Effect);
                    continue;
                }
                Assert.NotEqual(ActionEffect.NotMeasured, result.Effect);
                if (result.Status == ActionStatus.Done && !origin.IsAutomation) Assert.NotEqual(ActionEffect.Nothing, result.Effect);
                if (result.Status == ActionStatus.Refused) Assert.Equal(ActionEffect.Nothing, result.Effect);
            }
            Assert.Contains(seen, x => x.Result.Effect == ActionEffect.OwnOnly && x.Action.Kind == ShowActionKind.ScreenCut);
            Assert.Contains(seen, x => x.Origin.IsAutomation && x.Result.Status == ActionStatus.Done && x.Result.Effect == ActionEffect.Nothing);
            Assert.Equal(3, seen.Count(x => x.Result.Status == ActionStatus.Done && !x.Origin.IsAutomation && x.Result.Effect == ActionEffect.Changed));

            // The take family is exactly the four verbs.
            foreach (var kind in Enum.GetValues<ShowActionKind>())
            {
                Assert.Equal(kind is ShowActionKind.Take or ShowActionKind.Cut or ShowActionKind.ScreenTake or ShowActionKind.ScreenCut, ShowActions.IsTake(kind));
            }
        }
        finally
        {
            b.Dispose();
        }
    }
}
