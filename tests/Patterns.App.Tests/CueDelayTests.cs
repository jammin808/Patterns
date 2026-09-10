using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A cue with a shape in time, on a live desk. The rules that matter are the safety ones: a step
/// left over from a cue the show has moved past must never land on the audience.
/// </summary>
public class CueDelayTests
{
    private static (CueStackConfig Stack, RunCueConfig Cue) Build(MainViewModel vm, params (ShowActionKind Kind, double After)[] steps)
    {
        var stack = CueStacks.Caller(vm.State);
        var cue = new RunCueConfig { Number = "01.010", Name = "Doors" };
        foreach (var (kind, after) in steps)
        {
            cue.Actions.Add(new CueActionConfig { Kind = kind, DelaySeconds = after });
        }
        stack.Cues.Add(cue);
        return (stack, cue);
    }

    /// <summary>A tail whose clock the test holds, so nothing waits on the wall clock.</summary>
    private static CueTail Held(AppServices services, out Action<double> setClock)
    {
        var now = 0.0;
        setClock = v => now = v;
        var tail = new CueTail(() => now);
        var run = services.Tail.Run;
        tail.Run = step => run?.Invoke(step);
        return tail;
    }

    [AvaloniaFact]
    public void ACueWithNoWaitsIsStillOneChangeOnTheScreens()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var (stack, cue) = Build(vm, (ShowActionKind.BlackoutOn, 0), (ShowActionKind.ClockOn, 0));
            Dispatcher.UIThread.RunJobs();

            var before = services.Bus.Current.Version;
            var result = services.Actions.FireCue(cue, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();

            Assert.True(result.Ok, result.Message);
            Assert.True(services.State.Blackout);
            Assert.True(services.State.Overlays.Clock.Enabled);
            Assert.Equal(0, services.Tail.Count);
            // One publish for the whole cue, exactly as before delays existed.
            Assert.Equal(before + 1, services.Bus.Current.Version);
            Assert.DoesNotContain("to come", result.Message);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStepWithAWaitRunsLaterAndSaysSoWhenTheCueLands()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var (stack, cue) = Build(vm, (ShowActionKind.BlackoutOn, 0), (ShowActionKind.ClockOn, 4));
            Dispatcher.UIThread.RunJobs();

            var result = services.Actions.FireCue(cue, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();

            Assert.True(result.Ok, result.Message);
            Assert.True(services.State.Blackout);            // the step that goes with the GO
            Assert.False(services.State.Overlays.Clock.Enabled); // the one that waits has not
            Assert.Equal(1, services.Tail.Count);
            Assert.Contains("1 step to come", result.Message);
            Assert.Contains("to come", services.Tail.Words);

            // A poll before the wait is up leaves it exactly where it is.
            services.Tail.Poll();
            Assert.Equal(1, services.Tail.Count);
            Assert.False(services.State.Overlays.Clock.Enabled);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheTailRunsOnItsSecondAndGoesThroughTheActionLayer()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var tail = Held(services, out var setClock);
            try
            {
                var cue = new RunCueConfig { Number = "01.010", Name = "Doors" };
                cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ClockOn, DelaySeconds = 4 });
                var plan = CueSteps.Plan(cue.Actions);

                setClock(100);
                tail.Schedule("caller", cue, "01.010 Doors", plan, 100);
                Assert.Equal(1, tail.Count);
                Assert.Equal(4, tail.NextInSeconds!.Value, 3);

                setClock(103);
                tail.Poll();
                Assert.Equal(1, tail.Count);                     // a second early is still early
                Assert.False(services.State.Overlays.Clock.Enabled);

                setClock(104);
                tail.Poll();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, tail.Count);
                Assert.True(services.State.Overlays.Clock.Enabled);
                Assert.Equal("", tail.Words);
            }
            finally
            {
                tail.Dispose();
            }
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheNextGoOnAListTakesItOverAndStopAllDropsEverything()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var (stack, first) = Build(vm, (ShowActionKind.BlackoutOn, 0), (ShowActionKind.ClockOn, 30));
            var second = new RunCueConfig { Number = "01.020", Name = "Welcome" };
            second.Actions.Add(new CueActionConfig { Kind = ShowActionKind.BlackoutOff });
            stack.Cues.Add(second);
            Dispatcher.UIThread.RunJobs();

            services.Actions.FireCue(first, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, services.Tail.Count);

            // The next cue on the same list takes it over: a step from the cue before it can never
            // land on the audience half a minute after the show moved on.
            services.Actions.FireCue(second, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, services.Tail.Count);
            Assert.False(services.State.Overlays.Clock.Enabled);

            // And STOP ALL drops every list's tail, and says how many.
            services.Actions.FireCue(first, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, services.Tail.Count);
            var stop = services.Actions.Execute(ShowActionKind.StopAll, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, services.Tail.Count);
            Assert.Contains("waiting cue step", stop.Message);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void DisarmingAListAndResettingItBothDropWhatItLeftWaiting()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var (stack, cue) = Build(vm, (ShowActionKind.BlackoutOn, 0), (ShowActionKind.ClockOn, 30));
            Dispatcher.UIThread.RunJobs();

            services.Actions.FireCue(cue, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, services.Tail.Count);

            // Standing the list down means nothing more comes from it.
            services.Actions.Execute(new ShowAction(ShowActionKind.ListDisarm, stack.Id), ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, services.Tail.Count);

            services.Actions.FireCue(cue, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, services.Tail.Count);
            services.Actions.Execute(new ShowAction(ShowActionKind.ListReset, stack.Id), ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, services.Tail.Count);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheEditorMovesAStepAndLeavesTheCuesTimingWhereItWas()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            window.Width = 1500;
            window.Height = 950;
            var (stack, cue) = Build(vm, (ShowActionKind.ApplyLook, 0), (ShowActionKind.LowerThirdShow, 3), (ShowActionKind.StreamStart, 5));
            vm.SelectPage(Shell.IndexOf("Cues"));
            vm.Cues.SelectedStack = stack;
            vm.Cues.SelectedCue = cue;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(3, vm.Cues.ActionRows.Count);
            Assert.Equal("", vm.Cues.ActionRows[0].AtWords);
            Assert.Equal("+3 s", vm.Cues.ActionRows[1].AtWords);
            Assert.Equal("+8 s", vm.Cues.ActionRows[2].AtWords);

            // The stream goes to the top; the shape stays and only the content moves.
            vm.Cues.MoveActionTo(vm.Cues.ActionRows[2], 0);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { ShowActionKind.StreamStart, ShowActionKind.ApplyLook, ShowActionKind.LowerThirdShow },
                cue.Actions.Select(a => a.Kind));
            Assert.Equal(new[] { 0.0, 3.0, 5.0 }, cue.Actions.Select(a => a.DelaySeconds));
            // The rows moved rather than being rebuilt, so a drag keeps hold of what it is dragging.
            Assert.Equal(ShowActionKind.StreamStart, vm.Cues.ActionRows[0].Action.Kind);
            Assert.Equal("+8 s", vm.Cues.ActionRows[2].AtWords);

            // Typing a wait re-times what is under it, at once.
            vm.Cues.ActionRows[1].Delay = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("+1 s", vm.Cues.ActionRows[1].AtWords);
            Assert.Equal("+6 s", vm.Cues.ActionRows[2].AtWords);
            Assert.Contains("over 6 s", vm.Cues.CueShapeWords);

            // And the grip is on the row for the pointer to take hold of.
            var panel = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "ActionGrip").ToList();
            Assert.Equal(3, panel.Count);
            Assert.All(panel, g => Assert.True(Views.Controls.DragReorder.GetGrip(g)));
        }
        finally
        {
            b.Dispose();
        }
    }
}
