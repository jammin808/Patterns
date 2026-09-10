using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 24: the transition an operator chooses on the page and the one a cue names for itself —
/// the pickers following the choice on the keystroke rather than on the next poll, and a look
/// carrying its own arrival all the way to the snapshot the engine draws from.
/// </summary>
public class TransitionDeskTests
{
    [AvaloniaFact]
    public void TheTransitionPickersFollowTheChoiceOnTheKeystroke()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

            // The desk starts on what it has always done, and offers nothing it does not need.
            Assert.Equal(TransitionKind.Dissolve, vm.State.Transition.Kind);
            Assert.False(vm.TransitionHasDirection);
            Assert.False(vm.TransitionIsReactive);
            Assert.False(vm.TransitionIsDip);
            Assert.False(vm.TransitionHasSoftness);
            Assert.Contains("fades away", vm.TransitionNote);

            // A wipe travels and has an edge — and both rows appear on the change itself, with no
            // poll in between: this is the difference between a desk that feels instant and one
            // that feels like a form.
            raised.Clear();
            vm.State.Transition.Kind = TransitionKind.Wipe;
            Assert.True(vm.TransitionHasDirection);
            Assert.True(vm.TransitionHasSoftness);
            Assert.False(vm.TransitionIsReactive);
            Assert.Contains(nameof(vm.TransitionHasDirection), raised);
            Assert.Contains(nameof(vm.TransitionHasSoftness), raised);
            Assert.Contains(nameof(vm.TransitionNote), raised);
            Assert.Contains("soft edge", vm.TransitionNote);

            // A reactive wipe asks which scene, and still has an edge to soften.
            raised.Clear();
            vm.State.Transition.Kind = TransitionKind.Reactive;
            Assert.True(vm.TransitionIsReactive);
            Assert.True(vm.TransitionHasSoftness);
            Assert.False(vm.TransitionHasDirection);
            Assert.Contains(nameof(vm.TransitionIsReactive), raised);

            // A dip asks for its colour; the stinger asks for nothing at all.
            vm.State.Transition.Kind = TransitionKind.Dip;
            Assert.True(vm.TransitionIsDip);
            Assert.False(vm.TransitionHasDirection);
            Assert.False(vm.TransitionHasSoftness);

            vm.State.Transition.Kind = TransitionKind.BrandStinger;
            Assert.False(vm.TransitionIsDip);
            Assert.False(vm.TransitionHasDirection);
            Assert.False(vm.TransitionIsReactive);
            Assert.Contains("brand", vm.TransitionNote, StringComparison.OrdinalIgnoreCase);

            // Something else on the page changing does not make the pickers redraw themselves.
            raised.Clear();
            vm.State.Transition.DurationMs = 700;
            Assert.DoesNotContain(nameof(vm.TransitionHasDirection), raised);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AShowReadFromAFileArrivesWithItsPickersAlreadyRight()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var loaded = new ShowState();
            loaded.Transition.Kind = TransitionKind.Push;
            loaded.Transition.Direction = TransitionDirection.Up;

            // The same copy a show load does, and then the same re-hook after it.
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
            services.BulkEdit(() => ModelCopier.Copy(loaded, vm.State));
            vm.HookTransition();

            Assert.Equal(TransitionKind.Push, vm.State.Transition.Kind);
            Assert.True(vm.TransitionHasDirection);
            Assert.Contains(nameof(vm.TransitionHasDirection), raised);

            // And the new show's transition is the one being watched from here on.
            raised.Clear();
            vm.State.Transition.Kind = TransitionKind.Dip;
            Assert.True(vm.TransitionIsDip);
            Assert.Contains(nameof(vm.TransitionIsDip), raised);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ALookCanNameTheTransitionItArrivesOn()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;               // straight to air, so the snapshot is the one the wall draws
            vm.State.Transition.Enabled = false;      // the show itself does not fade at all
            vm.State.Transition.Kind = TransitionKind.Dissolve;
            vm.State.Pattern.Kind = PatternKind.Grid;
            var look = new LookConfig { Name = "Reveal", Json = LookService.Capture(vm.State) };
            vm.State.LooksAndCues.Looks.Add(look);
            vm.State.Pattern.Kind = PatternKind.ColorBars; // something for the look to change back

            var done = services.Actions.Execute(ShowActionKind.ApplyLook, ActionOrigin.Desk, look.Id, "wipe left 600");
            Assert.True(done.Ok);

            var snap = services.Bus.Current;
            // The recall's own arrival rides the snapshot the recall published — and it is still
            // the current one. The desk repaints its tally chips straight after every action, and
            // those writes used to publish three more snapshots and leave the look's wipe stranded
            // on a version no sink would ever draw.
            Assert.Equal(snap.Version, snap.TransitionOverrideVersion);
            Assert.Equal(snap.Version, snap.FadeOverrideVersion);
            Assert.True(snap.FadesEnabled); // naming a transition is asking for one
            Assert.Equal(TransitionKind.Wipe, snap.TransitionKindFor(snap.Version));
            Assert.Equal(TransitionDirection.Left, snap.TransitionDirectionFor(snap.Version));
            Assert.Equal(0.6, snap.FadeSecondsFor(snap.Version), 3);

            // And it belongs to that recall alone: the next change is the show's own again.
            services.Bus.Publish(vm.State);
            var next = services.Bus.Current;
            Assert.False(next.FadesEnabled);
            Assert.Equal(TransitionKind.Dissolve, next.TransitionKindFor(next.Version));

            // A stinger by name, on a show that names no scene of its own.
            vm.State.Pattern.Kind = PatternKind.Grid;
            Assert.True(services.Actions.Execute(ShowActionKind.ApplyLook, ActionOrigin.Desk, look.Id, "reactive vortex 900").Ok);
            var sting = services.Bus.Current;
            Assert.Equal(TransitionKind.Reactive, sting.TransitionKindFor(sting.Version));
            Assert.Equal(ReactiveScene.Vortex, sting.TransitionSceneFor(sting.Version));

            // A word the desk cannot read changes the picture and leaves the transition alone,
            // rather than guessing at something in front of the room.
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Assert.True(services.Actions.Execute(ShowActionKind.ApplyLook, ActionOrigin.Desk, look.Id, "wype").Ok);
            var junk = services.Bus.Current;
            Assert.False(junk.FadesEnabled);
            Assert.Equal(TransitionKind.Dissolve, junk.TransitionKindFor(junk.Version));
        }
        finally
        {
            b.Dispose();
        }
    }
}
