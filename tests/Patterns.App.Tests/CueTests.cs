using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Cues run through the action layer: order, refusal, blackout as transport, the clicker, the Cues page.</summary>
public class CueTests
{
    private static LookConfig SaveLook(MainViewModel vm, string name, PatternKind kind)
    {
        vm.ActivePattern.Kind = kind;
        vm.NewLookName = name;
        vm.SaveLookCommand.Execute(null);
        return LookService.Find(vm.State, name)!;
    }

    private static RunCueConfig AddCue(CueStackConfig stack, string name, params CueActionConfig[] actions)
    {
        var cue = new RunCueConfig
        {
            Number = CueNumber.Next(stack.Cues.Count > 0 ? stack.Cues[^1].Number : null),
            Name = name,
        };
        foreach (var a in actions) cue.Actions.Add(a);
        stack.Cues.Add(cue);
        return cue;
    }

    private static CueActionConfig Act(CueActionKind kind, string target = "", string value = "")
        => new() { Kind = kind, Target = target, Value = value };

    [AvaloniaFact]
    public void FiringACueRunsItsActionsInOrderThroughTheActionLayer()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.IsSandboxActive = false;
            var walkIn = SaveLook(b.Vm, "Walk-in", PatternKind.ColorBars);
            b.Vm.ActivePattern.Kind = PatternKind.Grid;
            var stack = CueStacks.Caller(b.Vm.State);
            var cue = AddCue(stack, "Doors open",
                Act(CueActionKind.Note),
                Act(CueActionKind.ApplyLook, walkIn.Id, "cut"),
                Act(CueActionKind.MessageOn, "", "Doors are open"),
                Act(CueActionKind.ClockOn),
                Act(CueActionKind.CountdownStart, "", "5"));

            var result = b.Services.Actions.FireCue(cue, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();

            Assert.True(result.Ok, result.Message);
            var air = b.Services.Bus.Current.State;
            Assert.Equal(PatternKind.ColorBars, air.Pattern.Kind);
            Assert.True(air.Overlays.Message.Enabled);
            Assert.Equal("Doors are open", air.Overlays.Message.Text);
            Assert.True(air.Overlays.Clock.Enabled);
            Assert.True(air.Countdown.Enabled);
            Assert.Equal(CountdownTargetKind.Duration, air.Countdown.TargetKind);
            Assert.Equal(5, air.Countdown.DurationMinutes);
            Assert.StartsWith("01.010 Doors open", result.Message);

            var entry = b.Services.Journal.Tail(1).Single();
            Assert.Equal("CueFire", entry.Kind);
            Assert.Equal("desk", entry.Origin);
            Assert.Equal("Done", entry.Outcome);
            Assert.Equal(cue.Id, b.Services.Cues.For(stack).LastCueId);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ABrokenCueIsRefusedWithTheReasonAndTheProgramIsUntouched()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.IsSandboxActive = false;
            b.Vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            var before = b.Services.Bus.Current.Version;
            var cue = AddCue(CueStacks.Caller(b.Vm.State), "Gone", Act(CueActionKind.ApplyLook, "Deleted look"), Act(CueActionKind.ClockOn));

            var result = b.Services.Actions.FireCue(cue, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ActionStatus.Refused, result.Status);
            Assert.Contains("not found", result.Message);
            Assert.False(b.Services.Bus.Current.State.Overlays.Clock.Enabled); // nothing ran, not even the good action
            Assert.Equal(PatternKind.Grid, b.Services.Bus.Current.State.Pattern.Kind);
            Assert.Equal("Refused", b.Services.Journal.Tail(1).Single().Outcome);
            _ = before;
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ACueStopsAtTheFirstFailureAndEarlierActionsStand()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.IsSandboxActive = false;
            // A file that exists but is neither a sound nor a clip: the stinger service refuses it at
            // fire time, which the validator cannot know — the honest mid-cue failure.
            var odd = Path.Combine(b.Dir, "not-a-clip.txt");
            File.WriteAllText(odd, "x");
            b.Vm.State.Stingers.Items.Add(new StingerItemConfig { Name = "Odd", Path = odd });
            var cue = AddCue(CueStacks.Caller(b.Vm.State), "Mixed",
                Act(CueActionKind.ClockOn),
                Act(CueActionKind.StingerFire, "Odd"),
                Act(CueActionKind.MessageOn, "", "never"));

            var result = b.Services.Actions.FireCue(cue, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ActionStatus.Failed, result.Status);
            Assert.Contains("failed at action 2 of 3", result.Message);
            var air = b.Services.Bus.Current.State;
            Assert.True(air.Overlays.Clock.Enabled);      // action 1 stood
            Assert.False(air.Overlays.Message.Enabled);   // action 3 never ran
            Assert.Equal("Failed", b.Services.Cues.For(CueStacks.Caller(b.Vm.State)).LastOutcome);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void BlackoutIsTransportAcrossACueUnlessTheCueSwitchesIt()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.IsSandboxActive = false;
            var look = SaveLook(b.Vm, "Holding", PatternKind.Focus); // saved with blackout off
            var stack = CueStacks.Caller(b.Vm.State);
            var recall = AddCue(stack, "Holding", Act(CueActionKind.ApplyLook, look.Id));
            var lift = AddCue(stack, "Lift", Act(CueActionKind.BlackoutOff), Act(CueActionKind.ApplyLook, look.Id));

            b.Services.Actions.Execute(ShowActionKind.BlackoutOn, ActionOrigin.Desk);
            Assert.True(b.Services.Actions.FireCue(recall, ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Vm.State.Blackout);                // an emergency blackout survives a look recall
            Assert.True(b.Services.Bus.Current.State.Blackout);

            Assert.True(b.Services.Actions.FireCue(lift, ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Vm.State.Blackout);               // the cue said so
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheClickerListAnswersThePageKeysOnlyWhileArmedAndTheRemoteSeesIt()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.IsSandboxActive = false;
            var one = SaveLook(b.Vm, "One", PatternKind.ColorBars);
            var two = SaveLook(b.Vm, "Two", PatternKind.Focus);
            b.Vm.ActivePattern.Kind = PatternKind.Grid;
            var clicker = CueStacks.Clicker(b.Vm.State);
            AddCue(clicker, "Opening", Act(CueActionKind.ApplyLook, one.Id));
            AddCue(clicker, "Sponsors", Act(CueActionKind.ApplyLook, two.Id));
            Assert.False(b.Vm.ClickerArmed); // always off at launch

            b.Window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            b.Window.KeyRelease(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Grid, b.Vm.State.Pattern.Kind); // disarmed: nothing

            b.Vm.ClickerArmed = true;
            Assert.True(b.Services.Cues.For(clicker).Armed);
            b.Window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            b.Window.KeyRelease(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, b.Vm.State.Pattern.Kind);
            Assert.Equal(0, b.Services.Cues.For(clicker).CurrentIndex);
            Assert.StartsWith("Cue 1 of 2: Opening", b.Vm.PresenterStepText);

            var state = new CommandRouter(b.Services).StateJson(); // what every remote reads
            Assert.Contains("\"count\":2", state.Replace(" ", ""));
            Assert.Contains("\"index\":0", state.Replace(" ", ""));
            Assert.Contains("Opening", state);

            b.Vm.PresenterResetCommand.Execute(null);
            Assert.Equal(-1, b.Services.Cues.For(clicker).CurrentIndex);
            b.Services.Cues.Reset();
            Assert.False(b.Vm.ClickerArmed);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheCuesPageAddsMovesRenumbersAndShowsWhatIsBroken()
    {
        var b = TestApp.Boot();
        try
        {
            var editor = b.Vm.Cues;
            Assert.Same(CueStacks.Caller(b.Vm.State), editor.SelectedStack);
            Assert.Empty(editor.Rows);

            var first = editor.AddCue();
            var second = editor.AddCue();
            var third = editor.AddCue();
            Assert.Equal(new[] { "01.010", "01.020", "01.030" }, editor.SelectedStack!.Cues.Select(c => c.Number));
            Assert.Same(third, editor.SelectedCue);

            editor.SelectedCue = first;
            var between = editor.AddCue();
            Assert.Equal("01.015", between.Number);
            Assert.Equal(new[] { first, between, second, third }, editor.SelectedStack.Cues.ToArray());

            editor.MoveCue(+1);
            Assert.Equal(new[] { first, second, between, third }, editor.SelectedStack.Cues.ToArray());
            editor.RenumberCommand.Execute(null);
            Assert.Equal(new[] { "01.010", "01.020", "01.030", "01.040" }, editor.SelectedStack.Cues.Select(c => c.Number));

            // An action with a missing target marks the cue broken as you build; fixing it clears it.
            editor.SelectedCue = second;
            editor.AddActionCommand.Execute(null);
            var row = editor.ActionRows.Single();
            Assert.Equal("ApplyLook", row.SelectedKind.Id);
            Assert.True(row.HasTarget);
            Assert.True(row.HasValue);
            row.Action.Target = "nope";
            editor.Refresh();
            var cueRow = editor.Rows.Single(r => ReferenceEquals(r.Cue, second));
            Assert.True(cueRow.IsBroken);
            Assert.Contains("not found", cueRow.Problem);
            Assert.Contains("1 of 4", editor.ValidationSummary);

            var look = SaveLook(b.Vm, "Walk-in", PatternKind.ColorBars);
            row.RefreshChoices();
            row.SelectedTarget = row.TargetChoices.Single(t => t.Id == look.Id);
            editor.Refresh();
            Assert.False(editor.Rows.Single(r => ReferenceEquals(r.Cue, second)).IsBroken);
            Assert.Equal("Apply 'Walk-in'", editor.Rows.Single(r => ReferenceEquals(r.Cue, second)).Summary);
            Assert.StartsWith("All 4 cues", editor.ValidationSummary);

            // Kinds without a target hide the picker; the value box follows the spec.
            row.SelectedKind = CueEditor.KindChoices.Single(k => k.Id == "ClockOn");
            Assert.False(row.HasTarget);
            Assert.False(row.HasValue);
            Assert.Equal("", row.Action.Target);

            editor.RemoveSelectedCommand.Execute(null);
            Assert.Equal(3, editor.SelectedStack.Cues.Count);
            Assert.DoesNotContain(second, editor.SelectedStack.Cues);

            editor.SelectedStack = CueStacks.Clicker(b.Vm.State);
            Assert.True(editor.IsClickerSelected);
            Assert.Empty(editor.Rows);
        }
        finally
        {
            b.Dispose();
        }
    }
}
