using System.Text.Json;
using Avalonia;
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
/// Re-review reproducers (kept under docs/rereview-repro, outside the test projects). Each test asserts the behaviour the finding describes,
/// so a PASS confirms the finding at the head under test and a FAIL means the head behaves otherwise.
/// Findings: TF-1..TF-4, TF-6, TF-7 (truth as fields, round 79) and SW-1..SW-3 (the switcher, round 81).
/// </summary>
public class RereviewTakeRepro
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
    };

    /// <summary>Three stand-alone main screens (TakeTruthAppTests' rig); with <paramref name="confidenceC"/>, c is a confidence screen (ScopedTakeAppTests' rig).</summary>
    private static void Rig(TestApp.Booted b, bool confidenceC = false)
    {
        var fakes = ThreeScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        if (confidenceC) b.Vm.State.Output.Placements.First(p => p.ScreenId == "c").Role = ScreenRole.Confidence;
        b.Vm.RebuildSwitcherTiles(fakes);
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

    private static SwitcherTile Tile(MainViewModel vm, string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);

    private static void GoLive(TestApp.Booted b)
    {
        var on = b.Services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
        Assert.True(on.Ok, on.Message);
        Dispatcher.UIThread.RunJobs();
        Assert.True(b.Services.Outputs.IsLive);
    }

    /// <summary>Three screens, the grid on air everywhere, EDIT SAFE open with the preview still the grid.</summary>
    private static (AppServices Services, MainViewModel Vm) Open(TestApp.Booted b, bool confidenceC = false)
    {
        var (services, vm, _) = b;
        Rig(b, confidenceC);
        vm.IsSandboxActive = false;
        vm.State.Pattern.Kind = PatternKind.Grid;
        Dispatcher.UIThread.RunJobs();
        vm.IsSandboxActive = true;
        Dispatcher.UIThread.RunJobs();
        return (services, vm);
    }

    // ---------------------------------------------------------------- TF-1: the wall refusal compares less than a take carries

    [AvaloniaFact]
    public void TF1a_AWallTakeCarryingOnlyANewBrandKitIsRefusedForAHand()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            vm.State.Brand.PrimaryColor = "#FFAA00";
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual("#FFAA00", services.Bus.Current.State.Brand.PrimaryColor);          // the programme keeps its own kit until the take
            var take = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Refused, take.Status);                                     // FINDING: refused
            Assert.Contains("the air is already the preview", take.Message);
            Assert.NotEqual("#FFAA00", services.Bus.Current.State.Brand.PrimaryColor);          // the new kit never reaches the air by TAKE
        }
        finally { b.Dispose(); }
    }

    [AvaloniaFact]
    public void TF1b_AnAutomationTakeLandsTheNewBrandKitAndMeasuresNothing()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            vm.State.Brand.PrimaryColor = "#00AAFF";
            Dispatcher.UIThread.RunJobs();
            var take = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Schedule);
            Assert.True(take.Ok, take.Message);
            Assert.Equal("#00AAFF", services.Bus.Current.State.Brand.PrimaryColor);             // the kit landed on the air
            Assert.Equal(ActionEffect.Nothing, take.Effect);                                      // FINDING: measured as Nothing
        }
        finally { b.Dispose(); }
    }

    [AvaloniaFact]
    public void TF1c_AWallTakeCarryingOnlyALowerThirdInThePreviewIsRefusedForAHand()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            var design = vm.NewLowerThird("Clean");
            design.InMs = 0;
            design.OutMs = 0;
            Dispatcher.UIThread.RunJobs();
            var toPreview = services.Actions.Execute(ShowActionKind.LowerThirdPreview, ActionOrigin.Desk, design.Id);
            Assert.True(toPreview.Ok, toPreview.Message);
            Assert.Contains("TAKE puts it on air", toPreview.Message);                             // the desk's own promise
            var take = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Refused, take.Status);                                     // FINDING: refused
            Assert.False(services.AirState.LowerThirds.IsShowing);
        }
        finally { b.Dispose(); }
    }

    // ---------------------------------------------------------------- TF-2: an ownership-only wall take is refused

    [AvaloniaFact]
    public void TF2_AWallTakeWhoseOnlyEffectIsOwnershipIsRefusedWithAdviceToSend()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            Tile(vm, "c").SendHereCommand.Execute(null);                                          // SEND the preview (= the air) to tile 3
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Sandbox.IsStaged("c"));                                          // the PVW badge lights: something to take
            var take = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Refused, take.Status);                                     // FINDING: refused
            Assert.Contains("SEND a picture to a tile first", take.Message);                      // ... telling the operator to do what they just did
            Assert.False(ContentTargets.UsesOwnPattern(services.AirState, "c"));                 // tile 3 still follows the programme
        }
        finally { b.Dispose(); }
    }

    // ---------------------------------------------------------------- TF-3: Visibility is rig-wide, not the taken target's

    [AvaloniaFact]
    public void TF3a_ATakeUnderFreezeIsStampedOutputsLive()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            GoLive(b);
            Assert.True(services.Actions.Execute(ShowActionKind.FreezeOn, ActionOrigin.Desk).Ok);
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            var take = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.True(take.Ok, take.Message);
            Assert.Equal(ActionVisibility.OutputsLive, take.Visibility);                          // FINDING: every output is frozen, yet "live"
        }
        finally { b.Dispose(); }
    }

    [AvaloniaFact]
    public void TF3b_ATakeOnAScreenFadedToBlackOnItsOwnIsStampedOutputsLive()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            GoLive(b);
            var fade = services.Actions.Execute(ShowActionKind.FadeToBlack, ActionOrigin.Desk, "ID c", "0");
            Assert.True(fade.Ok, fade.Message);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("c", services.Bus.BlackTargets);
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            var take = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "c");
            Assert.True(take.Ok, take.Message);
            Assert.Equal(ActionVisibility.OutputsLive, take.Visibility);                          // FINDING: c is drawn black
        }
        finally { b.Dispose(); }
    }

    [AvaloniaFact]
    public void TF3c_ATakeOnAScreenWithNoOutputWindowIsStampedOutputsLive()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            GoLive(b);
            Tile(vm, "a").Enabled = false;                                                        // OUT off on tile 1
            Dispatcher.UIThread.RunJobs();
            services.Outputs.Apply();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(services.Outputs.Windows, w => w.TargetScreenId == "a");
            Assert.True(services.Outputs.IsLive);                                                 // b's and c's windows are open
            vm.State.Pattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();
            var take = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "a");
            Assert.True(take.Ok, take.Message);
            Assert.Equal(ActionVisibility.OutputsLive, take.Visibility);                          // FINDING: a has no window
        }
        finally { b.Dispose(); }
    }

    // ---------------------------------------------------------------- TF-4 / SW-3: SEND TO TICKED and the per-screen verbs pass LOCK

    [AvaloniaFact]
    public void TF4_SendToTickedChangesALockedTileOnAirAndWritesNoJournalRow()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            var locked = services.Actions.Execute(ShowActionKind.ScreenLock, ActionOrigin.Desk, "b");
            Assert.True(locked.Ok, locked.Message);
            Dispatcher.UIThread.RunJobs();
            Assert.True(ScreenRoles.IsLocked(services.State, "b"));
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            Tile(vm, "b").IsSendTarget = true;
            Dispatcher.UIThread.RunJobs();
            var rowsBefore = services.Journal.Tail(1000).Count;
            vm.SandboxSendSelectedCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("b").Kind);      // FINDING: the locked screen changed on air
            Assert.Equal(rowsBefore, services.Journal.Tail(1000).Count);                          // FINDING: and the journal has no row for it
        }
        finally { b.Dispose(); }
    }

    [AvaloniaFact]
    public void SW3b_APerScreenPatternStepFromACueChangesALockedScreenOnAir()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenLock, ActionOrigin.Desk, "c").Ok);
            Dispatcher.UIThread.RunJobs();
            var step = services.Actions.Execute(ShowActionKind.ScreenPattern, new ActionOrigin(OriginKind.Cue, "Q7"), "3", "Focus");
            Assert.True(step.Ok, step.Message);                                                   // FINDING: not refused
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Focus, services.Bus.Current.PatternFor("c").Kind);           // FINDING: the locked screen shows Focus
        }
        finally { b.Dispose(); }
    }

    // ---------------------------------------------------------------- TF-6: no reader knows the wall refusal before the press

    [AvaloniaFact]
    public void TF6_StatePublishesNoRefusalForAWallTakeThatWillBeRefused()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, _) = Open(b);
            using var doc = JsonDocument.Parse(new CommandRouter(services).StateJson());
            var refusal = doc.RootElement.GetProperty("take").GetProperty("refusal").GetString();
            var take = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Refused, take.Status);
            Assert.Equal("", refusal);                                                           // FINDING: STATE said nothing before the press
        }
        finally { b.Dispose(); }
    }

    // ---------------------------------------------------------------- TF-7: "follow the programme" on an OWN tile

    [AvaloniaFact]
    public void TF7_FollowTheProgrammeOnAnOwnTileIsNotPendingAndIsSaidToKeepItsPictureWhileTakeMovesIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm) = Open(b);
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b").Ok);   // b OWN on air with LedWall
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            var follow = services.Actions.Execute(ShowActionKind.ScreenStageProgram, ActionOrigin.Desk, "b");
            Assert.True(follow.Ok, follow.Message);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Sandbox.IsStaged("b"));                                        // FINDING: the PVW badge stays dark
            var plan = services.Actions.PlanTake(FadeScope.Everything);
            Assert.Contains(plan.KeepsOwnLabels, l => l.Contains("Right"));                      // FINDING: "2 · Right keeps its own picture (OWN)"
            var take = services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk);
            Assert.True(take.Ok, take.Message);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("b").Kind);      // ... and the take moved b anyway
        }
        finally { b.Dispose(); }
    }

    // ---------------------------------------------------------------- SW-1: the second group take of a new picture

    [AvaloniaFact]
    public void SW1_ASecondGroupTakeOfANewPictureIsRefusedAndAllArmedThenSaysTheAirIsThePreview()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b, confidenceC: true);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Settle();
            vm.SelectedTakeScope = vm.TakeScopes[4];                                              // MAIN SCREENS
            Assert.Contains("MAIN", vm.SelectedTakeScope.Words.ToUpperInvariant());
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            vm.SelectedTakeScope = vm.TakeScopes[5];                                              // CONFIDENCE SCREENS
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("c").Kind);

            vm.State.Pattern.Kind = PatternKind.Focus;                                           // the next picture, built in the programme's preview
            Dispatcher.UIThread.RunJobs();
            vm.SelectedTakeScope = vm.TakeScopes[4];                                              // MAIN SCREENS again
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("a").Kind);      // FINDING: the new picture did not land
            Assert.StartsWith("Nothing to take on the main screens", vm.StatusMessage);

            vm.SelectedTakeScope = vm.TakeScopes[0];                                              // ALL ARMED, every screen OWN
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("the air is already the preview", vm.StatusMessage);                  // FINDING: false words (preview Focus, air Grid/Bars)
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("a").Kind);
        }
        finally { b.Dispose(); }
    }

    // ---------------------------------------------------------------- SW-2: the highlight and the editors part

    [AvaloniaFact]
    public void SW2_AfterATilesTakeTheHighlightIsTheTileButTheEditorsAndThePaneAreNot()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.LedWall;
            Settle();
            Tile(vm, "b").TakeHereCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("b", vm.SelectedTargetId);                                               // highlighted: FOCUSED and the pane mean b
            Assert.Null(vm.EditTarget.ScreenId);                                                  // FINDING: the editors stay on PROGRAM
            vm.ActivePattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.LedWall, vm.PreviewPattern.Kind);                            // FINDING: the big pane does not show the edit
        }
        finally { b.Dispose(); }
    }
}
