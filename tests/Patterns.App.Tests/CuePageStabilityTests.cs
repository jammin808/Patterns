using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The Cues page under the pointer. The desk re-runs the checks after every snapshot it publishes,
/// which is often — so anything that page rebuilds on a revalidate is something the operator sees
/// blink, or loses hold of: the row under the pointer flashing its hover colour, a picker closing
/// as they reach for a row, a box forgetting what is being typed into it.
///
/// So the rule these pin: a revalidate that finds nothing changed must change nothing.
/// </summary>
public class CuePageStabilityTests
{
    private static (CueStackConfig Stack, RunCueConfig Cue) Build(MainViewModel vm, int cues = 3)
    {
        var stack = CueStacks.Caller(vm.State);
        for (var i = 0; i < cues; i++)
        {
            var cue = new RunCueConfig { Number = $"01.{(i + 1) * 10:000}", Name = $"Cue {i + 1}" };
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ApplyLook });
            stack.Cues.Add(cue);
        }
        return (stack, stack.Cues[0]);
    }

    private static int CountChanges(INotifyCollectionChanged collection, Action act)
    {
        var seen = 0;
        void Handler(object? _, NotifyCollectionChangedEventArgs __) => seen++;
        collection.CollectionChanged += Handler;
        try { act(); }
        finally { collection.CollectionChanged -= Handler; }
        return seen;
    }

    [AvaloniaFact]
    public void ARevalidateThatFindsNothingChangedLeavesEveryRowExactlyWhereItWas()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var (stack, first) = Build(vm);
            vm.Cues.SelectedStack = stack;
            vm.Cues.SelectedCue = first;
            vm.Cues.Refresh();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(3, vm.Cues.Rows.Count);
            var rows = vm.Cues.Rows.ToArray();

            // Ten revalidates — what a minute of a live desk publishing looks like — and the list
            // is untouched. A single Add or Remove here is a row the pointer was over vanishing.
            var moved = CountChanges(vm.Cues.Rows, () =>
            {
                for (var i = 0; i < 10; i++) vm.Cues.Refresh();
            });
            Assert.Equal(0, moved);
            Assert.Equal(rows, vm.Cues.Rows.ToArray());

            // What the checks say still lands, in place, on the row that is already there.
            stack.Cues[1].Name = "Renamed";
            stack.Cues[1].Actions[0].Target = "nothing-by-that-name";
            vm.Cues.Refresh();
            Assert.Same(rows[1], vm.Cues.Rows[1]);
            Assert.True(vm.Cues.Rows[1].IsBroken);
            Assert.Equal("Renamed", vm.Cues.Rows[1].Name);

            // A cue that really is new, gone or somewhere else moves the list — and only it.
            var added = new RunCueConfig { Number = "01.005", Name = "Opener" };
            stack.Cues.Insert(0, added);
            vm.Cues.Refresh();
            Assert.Equal(4, vm.Cues.Rows.Count);
            Assert.Same(added, vm.Cues.Rows[0].Cue);
            Assert.Same(rows[0], vm.Cues.Rows[1]);   // the rows that were there are the rows that stay

            stack.Cues.Move(0, 3);
            vm.Cues.Refresh();
            Assert.Same(added, vm.Cues.Rows[3].Cue);
            Assert.Same(rows[0], vm.Cues.Rows[0]);

            stack.Cues.Remove(added);
            vm.Cues.Refresh();
            Assert.Equal(3, vm.Cues.Rows.Count);
            Assert.Equal(rows, vm.Cues.Rows.ToArray());
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThePickersInTheSettingsColumnAreNotEmptiedUnderTheOperatorsHand()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.State.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in", Json = LookService.Capture(vm.State) });
            var (stack, cue) = Build(vm, 1);
            vm.Cues.SelectedStack = stack;
            vm.Cues.SelectedCue = cue;
            vm.Cues.Refresh();
            Dispatcher.UIThread.RunJobs();

            var row = Assert.Single(vm.Cues.ActionRows);
            Assert.Contains(row.TargetChoices, t => t.Label == "Walk-in");

            // A revalidate is not a reason to close a dropdown. Clearing an ItemsSource does
            // exactly that, so nothing at all may move while the choices are the same choices.
            var churn = CountChanges(row.TargetChoices, () =>
            {
                for (var i = 0; i < 10; i++) vm.Cues.Refresh();
            });
            Assert.Equal(0, churn);
            Assert.Equal(0, CountChanges(vm.Cues.QuickLooks, () => vm.Cues.Refresh()));

            // A look that really is new does reach the picker.
            vm.State.LooksAndCues.Looks.Add(new LookConfig { Name = "Keynote", Json = LookService.Capture(vm.State) });
            vm.Cues.Refresh();
            Assert.Contains(row.TargetChoices, t => t.Label == "Keynote");
            Assert.Contains(vm.Cues.QuickLooks, l => l.Label == "Keynote");
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheWaitBeingTypedIsNeverWrittenOverByTheDeskRetimingWhatIsUnderIt()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "01.010", Name = "Doors" };
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.BlackoutOn });
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ClockOn });
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.MessageOn });
            stack.Cues.Add(cue);
            vm.Cues.SelectedStack = stack;
            vm.Cues.SelectedCue = cue;
            vm.Cues.Refresh();
            Dispatcher.UIThread.RunJobs();

            var rows = vm.Cues.ActionRows.ToArray();
            var raised = new List<string>();
            foreach (var r in rows)
            {
                var name = r;
                r.PropertyChanged += (_, e) => raised.Add($"{Array.IndexOf(rows, name)}.{e.PropertyName}");
            }

            // Typing 4 into the first row's box re-times the rows under it — and tells them so
            // by their moment in the cue, never by pushing a wait back into their own box.
            rows[0].Delay = 4;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("+4 s", rows[1].AtWords);
            Assert.Contains("1.AtWords", raised);
            Assert.Contains("1.IsDelayed", raised);
            Assert.DoesNotContain("1.Delay", raised);
            Assert.DoesNotContain("2.Delay", raised);
            Assert.Equal(1, raised.Count(r => r == "0.Delay")); // once, from the box the operator used

            // A reorder is the other case: the waits stay with the positions, so a row really does
            // take a different number and its box has to be told.
            raised.Clear();
            vm.Cues.MoveActionTo(rows[2], 0);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("2.Delay", raised);
            Assert.Contains("0.Delay", raised);
            Assert.Equal(new[] { 4.0, 0.0, 0.0 }, cue.Actions.Select(a => a.DelaySeconds));
        }
        finally
        {
            b.Dispose();
        }
    }
}
