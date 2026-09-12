using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 28: "When I save a Pattern as a Preset in the Build Rail, how do I recall it? It needs an
/// easy recall feature that also can be quickly used to send to a screen from the show panel."
///
/// Saving worked and recall existed — on a different page, with nothing where you saved it saying
/// so — and there was no way at all to put a saved pattern on one screen from the panel. A preset
/// is a PATTERN, not a look: no overlays, no countdown, no per-screen arrangement, which is exactly
/// what makes it safe to drop onto one screen without disturbing the rest of the show.
/// </summary>
public class PresetRecallTests
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

    private static void Save(MainViewModel vm, string name, PatternKind kind)
    {
        vm.ActivePattern.Kind = kind;
        vm.NewPresetName = name;
        vm.SavePresetCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void SavingOneSaysWhereItWentAndPutsItOnePressAway()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, _) = b;
            vm.SelectPage(Shell.IndexOf("Pattern"));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.PresetChips);
            Assert.Contains("No presets yet", vm.PresetHint);

            Save(vm, "Walk-in", PatternKind.ColorBars);

            // Recall lives where saving does. It used to say only "saved", and the answer to "so
            // how do I get it back?" was a page nothing here mentioned.
            Assert.Contains("recall", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Single(vm.PresetChips, c => c.Name == "Walk-in");
            Assert.Contains("Press one to recall", vm.PresetHint);

            // Change the picture, press the chip, and it is back.
            vm.ActivePattern.Kind = PatternKind.Motion;
            Dispatcher.UIThread.RunJobs();
            vm.PresetChips.Single().RecallCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);

            // ✕ forgets it — the file is the preset.
            vm.PresetChips.Single().DeleteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.PresetChips);
            Assert.Empty(b.Services.Store.PresetNames());
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void OnePickerOffersBothAndSendsAPresetToOneScreenAlone()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();

            Save(vm, "Sponsor", PatternKind.LedWall);
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.State.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in", Json = LookService.Capture(vm.State) });
            vm.RefreshSendChoices();
            Dispatcher.UIThread.RunJobs();

            // One picker, two groups: the operator's question is "what do I want on this screen",
            // not "which of the desk's two kinds of saved thing am I reaching for".
            Assert.Contains(vm.SendChoices, c => c.Name == "Walk-in" && c.Group == "Looks" && !c.IsPreset);
            Assert.Contains(vm.SendChoices, c => c.Name == "Sponsor" && c.Group == "Presets" && c.IsPreset);

            var tile = vm.SwitcherTiles.Single(t => t.TargetId == "b");
            tile.PendingSend = vm.SendChoices.Single(c => c.IsPreset && c.Name == "Sponsor");
            Assert.True(tile.HasPendingSend);
            tile.SendChoiceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            // On that screen alone, live — every other screen stays exactly as it was.
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("c").Kind);
            Assert.True(ContentTargets.UsesOwnPattern(services.State, "b"));

            // PROGRAM puts it back, as it always did.
            tile.ProgramCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ARecallWaitsForTheTakeWhileASendToOneScreenIsLive()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            Save(vm, "Bars", PatternKind.ColorBars);
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();

            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();

            // A RECALL is a recall: with EDIT SAFE open it lands in the preview and waits, which is
            // what an operator building a show expects. A recall that jumped to air would be a
            // different verb, and a dangerous one.
            Assert.True(services.Actions.Execute(ShowActionKind.PatternPreset, ActionOrigin.Desk, "", "Bars").Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
            Assert.Equal(PatternKind.Grid, services.AirState.Pattern.Kind);

            // A SEND to one screen is live, exactly as a per-screen look send has always been.
            Assert.True(services.Actions.Execute(ShowActionKind.ScreenPreset, ActionOrigin.Desk, "c", "Bars").Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.PatternFor("c").Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void APresetThatDidNotTravelSaysWhatThisMachineActuallyHas()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);

            // Presets are files beside the show rather than part of it, so a show carried to
            // another machine can name one that is not there. "No preset 'X'" alone would leave an
            // operator hunting, so the desk says what it has.
            var empty = services.Actions.Execute(ShowActionKind.PatternPreset, ActionOrigin.Desk, "", "Nobody");
            Assert.False(empty.Ok);
            Assert.Contains("presets folder is empty", empty.Message);

            Save(vm, "Walk-in", PatternKind.Focus);
            var missing = services.Actions.Execute(ShowActionKind.PatternPreset, ActionOrigin.Desk, "", "Nobody");
            Assert.False(missing.Ok);
            Assert.Contains("Walk-in", missing.Message);

            // And the name is matched the way an operator typed it.
            Assert.True(services.Actions.Execute(ShowActionKind.PatternPreset, ActionOrigin.Desk, "", "walk-IN").Ok);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ACueAndTheWireRecallAPresetThroughTheSameOneAction()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            Save(vm, "Sponsor", PatternKind.Checkerboard);
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();

            // The wire, both forms.
            var recall = ControlProtocol.Parse("PRESET Sponsor");
            Assert.True(recall.IsAction);
            Assert.Equal(ShowActionKind.PatternPreset, recall.Action.Kind);
            Assert.Equal("Sponsor", recall.Action.Value);
            var onScreen = ControlProtocol.Parse("SCREEN 2 PRESET Sponsor");
            Assert.True(onScreen.IsAction);
            Assert.Equal(ShowActionKind.ScreenPreset, onScreen.Action.Kind);
            Assert.Equal("2", onScreen.Action.Target);
            Assert.Equal("Sponsor", onScreen.Action.Value);
            Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("PRESET").Kind);
            Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN 2 PRESET").Kind);

            // A cue reads and says it.
            var cue = new RunCueConfig { Number = "1", Name = "Walk-in" };
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.PatternPreset, Value = "Sponsor" });
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ScreenPreset, Target = "2", Value = "Sponsor" });
            Assert.Contains("Sponsor", CueSummary.DescribeAction(vm.State, cue.Actions[0]));
            Assert.Contains("Sponsor", CueSummary.DescribeAction(vm.State, cue.Actions[1]));

            // The checks know what this machine has, and a preset it has not got is worth a look
            // rather than a refusal — the folder may simply not have travelled yet.
            var stack = new CueStackConfig();
            stack.Cues.Add(cue);
            var report = CueValidator.Validate(vm.State, stack, services.ValidationContext);
            Assert.False(report.IsBroken(cue.Id));

            cue.Actions[0].Value = "Nowhere";
            report = CueValidator.Validate(vm.State, stack, services.ValidationContext);
            Assert.False(report.IsBroken(cue.Id));
            Assert.Contains(report.Issues, i => i.Text.Contains("Nowhere") && i.Text.Contains("presets folder"));

            // And with no preset named at all, the cue cannot run as written.
            cue.Actions[0].Value = "";
            Assert.True(CueValidator.Validate(vm.State, stack, services.ValidationContext).IsBroken(cue.Id));
        }
        finally
        {
            b.Dispose();
        }
    }
}
