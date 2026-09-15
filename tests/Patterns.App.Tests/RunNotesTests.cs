using Patterns.Assistant;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The show caller's pad and the in-show notes: the pad and each cue's note live in the show file
/// and reach the assistant's brief; the row's own menu — the same verbs as right-click and the ✎
/// chip — puts a cue on standby, fires it now, opens its note, skips it and opens it in the editor.
/// </summary>
public class RunNotesTests
{
    private static RunCueConfig Cue(CueStackConfig stack, string number, string name)
    {
        var cue = new RunCueConfig { Number = number, Name = name };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ClockOn });
        stack.Cues.Add(cue);
        return cue;
    }

    [AvaloniaFact]
    public void ThePadAndTheNotesLiveInTheShowAndReachTheBriefAndTheRowsMenuDoesTheCallersVerbs()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var run = vm.Run;
            var stack = CueStacks.Caller(vm.State);
            var walkIn = Cue(stack, "01.010", "Walk-in");
            var keynote = Cue(stack, "01.020", "Keynote");
            Dispatcher.UIThread.RunJobs();
            run.Refresh();
            var rows = run.Rows;
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Same(run, r.Owner));

            // The pad: closed until it is opened, its words the show's own.
            Assert.False(run.IsPadOpen);
            Assert.Equal("▸ PAD", run.PadToggleText);
            run.TogglePadCommand.Execute(null);
            Assert.True(run.IsPadOpen);
            Assert.True(vm.State.Desk.RunPadOpen);
            run.Scratchpad = "Doors 18:30 · Amira speaks first, NOT the chair · walk-in music off at 18:58";
            Assert.Equal("Doors 18:30 · Amira speaks first, NOT the chair · walk-in music off at 18:58", stack.Scratchpad);

            // A note on a cue: opened from the menu (or the ✎), typed on the cue itself, shown on the row and the standby card.
            var keynoteRow = rows.Single(r => ReferenceEquals(r.Cue, keynote));
            Assert.False(keynoteRow.HasLiveNotes);
            run.EditNotesCommand.Execute(keynoteRow);
            Assert.True(keynoteRow.IsEditingNotes);
            Assert.False(rows[0].IsEditingNotes);
            keynote.LiveNotes = "Amira wants the lower third held until she sits";
            run.CloseNotesCommand.Execute(keynoteRow);
            Assert.False(keynoteRow.IsEditingNotes);
            Assert.True(keynoteRow.HasLiveNotes);
            Assert.Equal("Amira wants the lower third held until she sits", keynoteRow.LiveNotes);
            run.SelectRowCommand.Execute(keynoteRow);
            Dispatcher.UIThread.RunJobs();
            run.Refresh();
            Assert.Equal("Amira wants the lower third held until she sits", run.StandbyLiveNotes);
            run.EditNotesCommand.Execute(keynoteRow);
            run.EditNotesCommand.Execute(keynoteRow);                    // the same row again closes it
            Assert.False(keynoteRow.IsEditingNotes);

            // The brief carries both: the assistant reads the caller's words before the plan.
            var brief = ShowBrief.Summarise(vm.State);
            Assert.Contains("the caller's note: Amira wants the lower third held until she sits", brief);
            Assert.Contains("The caller's pad: Doors 18:30", brief);

            // The row's verbs: skip and back, fire now, and the editor.
            run.ToggleSkipCommand.Execute(keynoteRow);
            Assert.False(keynote.Enabled);
            Assert.Equal("UNSKIP — BACK IN THE RUN", keynoteRow.SkipLabel);
            Assert.Contains("skipped", vm.StatusMessage);
            run.ToggleSkipCommand.Execute(keynoteRow);
            Assert.True(keynote.Enabled);
            Assert.Equal("SKIP THIS CUE", keynoteRow.SkipLabel);

            var walkInRow = rows.Single(r => ReferenceEquals(r.Cue, walkIn));
            vm.IsSandboxActive = false;
            vm.State.Overlays.Clock.Enabled = false;
            run.FireRowCommand.Execute(walkInRow);                    // GO THIS CUE NOW: the cue's own action lands, whatever is on standby
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Overlays.Clock.Enabled);

            run.OpenInEditorCommand.Execute(keynoteRow);
            Assert.Same(keynote, vm.Cues.SelectedCue);
            Assert.Equal(Shell.IndexOf("Cues"), vm.SelectedPageIndex);

            // Both travel with the show file.
            var json = JsonUtil.SerializeCompact(vm.State);
            var back = JsonUtil.Deserialize<ShowState>(json)!;
            var stackBack = CueStacks.Caller(back);
            Assert.Equal(stack.Scratchpad, stackBack.Scratchpad);
            Assert.Equal("Amira wants the lower third held until she sits", stackBack.Cues.Single(c => c.Name == "Keynote").LiveNotes);
            Assert.True(back.Desk.RunPadOpen);
        }
        finally
        {
            b.Dispose();
        }
    }
}
