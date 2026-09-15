using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 60: the staged verbs on the desk. SCREEN n PVW … and PVW … put a picture on a target's
/// PVW in the sandboxed preview and nowhere else: EDIT SAFE opens by itself, the frozen program is
/// never written, and CUT or TAKE is what puts the picture up. The right-click menus stand on this
/// promise, so it is proved here on the real services, not on the menu.
/// </summary>
public class StagedVerbsAppTests
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

    private static LookConfig SaveLook(MainViewModel vm, string name, PatternKind kind)
    {
        vm.ActivePattern.Kind = kind;
        vm.Show.NewLookName = name;
        vm.Show.SaveLookCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        return LookService.Find(vm.State, name) ?? throw new InvalidOperationException($"look '{name}' was not saved");
    }

    private static PatternKind? OwnKind(ShowState state, string screenId)
        => state.Independent.FirstOrDefault(a => a.ScreenId == screenId)?.Pattern.Kind;

    private static TestApp.Booted BootLive()
    {
        var b = TestApp.Boot();
        b.Vm.IsSandboxActive = false;
        b.Vm.State.Transition.Enabled = false;
        b.Vm.State.Switcher.EditSafeByDefault = false;
        Rig(b);
        return b;
    }

    [AvaloniaFact]
    public void AStagedLookOpensEditSafeLandsOnTheTilesPvwAndLeavesTheAirAlone()
    {
        var b = BootLive();
        try
        {
            var (services, vm, _) = b;
            var sponsor = SaveLook(vm, "Sponsor", PatternKind.ColorBars);
            var walkIn = SaveLook(vm, "Walk-in", PatternKind.Grid);
            Assert.True(services.Actions.ApplyLook(walkIn, ActionOrigin.Desk, cut: true).Ok);
            Dispatcher.UIThread.RunJobs();
            var airBefore = LookService.Fingerprint(services.AirState);
            Assert.False(services.Sandbox.Active);

            var result = services.Actions.Execute(new ShowAction(ShowActionKind.ScreenStageLook, "2", "Sponsor"), ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();

            Assert.True(result.Ok, result.Message);
            Assert.True(services.Sandbox.Active);                         // EDIT SAFE opened by itself
            Assert.Contains("EDIT SAFE opened", result.Message);
            Assert.Contains("staged", result.Message);
            Assert.True(services.Sandbox.IsStaged("b"));                  // the tile's PVW holds it
            Assert.Equal(PatternKind.ColorBars, OwnKind(services.State, "b"));
            Assert.Equal(airBefore, LookService.Fingerprint(services.AirState)); // the audience saw nothing
            Assert.False(ContentTargets.UsesOwnPattern(services.AirState, "b"));
            Assert.Null(OwnKind(services.State, "a"));                      // every other screen stays

            // TAKE is what puts it up — and only then.
            Assert.True(services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.True(ContentTargets.UsesOwnPattern(services.AirState, "b"));
            Assert.Equal(PatternKind.ColorBars, OwnKind(services.AirState, "b"));
            Assert.Equal(PatternKind.Grid, services.AirState.Pattern.Kind);

            // The wire says the same thing, and PROGRAM stages the programme back in the preview only.
            var router = new CommandRouter(services);
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("SCREEN 2 PVW PROGRAM"))));
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Sandbox.Active);
            Assert.False(ContentTargets.UsesOwnPattern(services.State, "b"));   // the preview: b follows again
            Assert.True(ContentTargets.UsesOwnPattern(services.AirState, "b")); // the air: still its own, until TAKE
            _ = sponsor;
        }
        finally
        {
            b.Window.Close();
        }
    }

    [AvaloniaFact]
    public void ResetPutsTheLooksOwnPictureBackOnTheTilesPvwAndRefusesWithNoLookOnAir()
    {
        var b = BootLive();
        try
        {
            var (services, vm, _) = b;

            // Nothing on air was ever a look: RESET refuses and leaves the desk exactly as it was.
            var refused = services.Actions.Execute(new ShowAction(ShowActionKind.ScreenStageReset, "2"), ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Refused, refused.Status);
            Assert.Contains("No look is on air", refused.Message);
            Assert.False(services.Sandbox.Active);

            var walkIn = SaveLook(vm, "Walk-in", PatternKind.Grid);
            Assert.True(services.Actions.ApplyLook(walkIn, ActionOrigin.Desk, cut: true).Ok);
            // Screen 2 goes its own way, live: the look reads as off on that screen.
            Assert.True(services.Actions.Execute(new ShowAction(ShowActionKind.ScreenPattern, "2", "ColorBars"), ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, OwnKind(services.AirState, "b"));
            Assert.True(services.LookTally.IsOffLook("b"));

            var result = services.Actions.Execute(new ShowAction(ShowActionKind.ScreenStageReset, "2"), ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(result.Ok, result.Message);
            Assert.Contains("as it was", result.Message);
            Assert.True(services.Sandbox.Active);
            Assert.Equal(PatternKind.Grid, OwnKind(services.State, "b"));      // the PVW: the look's own picture for it
            Assert.Equal(PatternKind.ColorBars, OwnKind(services.AirState, "b")); // the air: untouched
            Assert.True(services.LookTally.IsOffLook("b"));

            Assert.True(services.Actions.Execute(ShowActionKind.Take, ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Grid, OwnKind(services.AirState, "b"));
        }
        finally
        {
            b.Window.Close();
        }
    }

    [AvaloniaFact]
    public void TheProgrammeStagesAKindAPresetAndTheWholeLookBackInThePreview()
    {
        var b = BootLive();
        try
        {
            var (services, vm, _) = b;
            var walkIn = SaveLook(vm, "Walk-in", PatternKind.Grid);
            Assert.True(services.Actions.ApplyLook(walkIn, ActionOrigin.Desk, cut: true).Ok);
            Dispatcher.UIThread.RunJobs();

            var kind = services.Actions.Execute(new ShowAction(ShowActionKind.ScreenStagePattern, "", "Colour bars"), ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(kind.Ok, kind.Message);
            Assert.Contains("in the preview", kind.Message);
            Assert.True(services.Sandbox.Active);
            Assert.Equal(PatternKind.ColorBars, services.State.Pattern.Kind);   // the preview
            Assert.Equal(PatternKind.Grid, services.AirState.Pattern.Kind);     // the air
            Assert.Equal("", services.PreviewLookId);                           // an edit, not a look

            // A preset saved from the preview, then the whole look back, then the preset on the programme.
            vm.ActivePattern.Kind = PatternKind.Motion;
            vm.NewPresetName = "Moving";
            vm.SavePresetCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Moving", services.Store.PresetNames());

            var reset = services.Actions.Execute(new ShowAction(ShowActionKind.ScreenStageReset, "PGM"), ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(reset.Ok, reset.Message);
            Assert.Equal(PatternKind.Grid, services.State.Pattern.Kind);
            Assert.Equal(walkIn.Id, services.PreviewLookId);                    // the whole look names itself

            var preset = services.Actions.Execute(new ShowAction(ShowActionKind.ScreenStagePreset, "", "Moving"), ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(preset.Ok, preset.Message);
            Assert.Equal(PatternKind.Motion, services.State.Pattern.Kind);
            Assert.Equal(PatternKind.Grid, services.AirState.Pattern.Kind);

            // A stranger is refused with the kinds, and a missing preset with what to do.
            Assert.Equal(ActionStatus.Refused, services.Actions.Execute(new ShowAction(ShowActionKind.ScreenStagePattern, "", "Bogus"), ActionOrigin.Desk).Status);
            Assert.Equal(ActionStatus.Refused, services.Actions.Execute(new ShowAction(ShowActionKind.ScreenStagePreset, "2", "Nope"), ActionOrigin.Desk).Status);
            Assert.Equal(ActionStatus.Refused, services.Actions.Execute(new ShowAction(ShowActionKind.ScreenStageLook, "9", "Walk-in"), ActionOrigin.Desk).Status);
        }
        finally
        {
            b.Window.Close();
        }
    }

    [AvaloniaFact]
    public void ScreenPatternIsLiveOnOneScreenAlone()
    {
        var b = BootLive();
        try
        {
            var (services, _, _) = b;
            var result = services.Actions.Execute(new ShowAction(ShowActionKind.ScreenPattern, "2", "LED wall"), ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(result.Ok, result.Message);
            Assert.False(services.Sandbox.Active);                              // live: no sandbox opened
            Assert.True(ContentTargets.UsesOwnPattern(services.AirState, "b"));
            Assert.Equal(PatternKind.LedWall, OwnKind(services.AirState, "b"));
            Assert.False(ContentTargets.UsesOwnPattern(services.AirState, "a"));
            Assert.False(ContentTargets.UsesOwnPattern(services.AirState, "c"));
            Assert.Equal(ActionStatus.Refused, services.Actions.Execute(new ShowAction(ShowActionKind.ScreenPattern, "2", "Bogus"), ActionOrigin.Desk).Status);
        }
        finally
        {
            b.Window.Close();
        }
    }
}
