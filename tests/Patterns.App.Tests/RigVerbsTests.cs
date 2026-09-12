using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>A screen's role and its name are verbs: the Screens page, a cue, the wire and the journal share them; the rig's edits live in a service.</summary>
public class RigVerbsTests
{
    [AvaloniaFact]
    public void ARoleIsAVerbThatLocksAConfidenceScreenAndTheDeskUsesIt()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var placement = vm.State.Output.Placements[0];
            var actions = b.Services.Actions;

            var conf = actions.Execute(ShowActionKind.ScreenRole, ActionOrigin.Desk, "1", "confidence");
            Assert.True(conf.Ok, conf.Message);
            Assert.Equal(ScreenRole.Confidence, placement.Role);
            Assert.False(placement.FollowsCues);                                     // a confidence screen keeps its picture
            Assert.Contains("locked", conf.Message);
            Assert.Equal("ScreenRole", b.Services.Journal.Tail(1).Single().Kind);

            var main = actions.Execute(ShowActionKind.ScreenRole, ActionOrigin.Desk, placement.ScreenId, "main");
            Assert.True(main.Ok, main.Message);
            Assert.Equal(ScreenRole.Main, placement.Role);
            Assert.True(placement.FollowsCues);

            Assert.False(actions.Execute(ShowActionKind.ScreenRole, ActionOrigin.Desk, "1", "sideways").Ok);
            Assert.False(actions.Execute(ShowActionKind.ScreenRole, ActionOrigin.Desk, "9", "info").Ok);

            // The Screens page's picker is the same verb.
            vm.Screens.SelectedPlacement = placement;
            vm.Screens.SelectedRole = ScreenRole.Info;
            Assert.Equal(ScreenRole.Info, placement.Role);
            Assert.False(vm.Screens.SelectedFollowsCues);
            Assert.Equal("ScreenRole", b.Services.Journal.Tail(1).Single().Kind);

            // A cue may carry it; the sheet reads it back by its words.
            Assert.Contains(ShowActionKind.ScreenRole, ActionSpec.CueKinds);
            Assert.Equal(ShowActionKind.ScreenRole, CueSheet.ParseKind("Screen role (main, confidence, info, repeater)"));
            Assert.Contains("role → info", CueSummary.DescribeAction(vm.State, new CueActionConfig { Kind = ShowActionKind.ScreenRole, Target = placement.ScreenId, Value = "info" }));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ALabelIsAVerbForAScreenOrACanvasAndTheRigEditorKeepsTheCanvasEntry()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var placement = vm.State.Output.Placements[0];
            var actions = b.Services.Actions;

            var named = actions.Execute(ShowActionKind.ScreenLabel, ActionOrigin.Desk, "1", "Stage left");
            Assert.True(named.Ok, named.Message);
            Assert.Equal("Stage left", placement.CustomLabel);
            Assert.Contains("Stage left", Rig.Geometry(vm.State, b.Services.Screens.All).LabelFor(vm.State, placement.ScreenId));
            vm.Screens.SelectedPlacement = placement;
            Assert.Equal("Stage left", vm.Screens.SelectedScreenLabel);

            Assert.True(actions.Execute(ShowActionKind.ScreenLabel, ActionOrigin.Desk, "1", "").Ok);
            Assert.Equal("", placement.CustomLabel);
            Assert.False(actions.Execute(ShowActionKind.ScreenLabel, ActionOrigin.Desk, "no-such-screen", "x").Ok);
            Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.ScreenLabel));            // the rig's own naming, never a running order's step

            // A canvas's entry is made on demand and named by its key.
            var state = new ShowState();
            Assert.Null(RigEditor.CanvasConfigFor(state, "canvas:a+b", create: false));
            var entry = RigEditor.CanvasConfigFor(state, "canvas:a+b", create: true);
            Assert.NotNull(entry);
            Assert.Equal("canvas:a+b", entry!.MemberKey);
            Assert.Same(entry, RigEditor.CanvasConfigFor(state, "canvas:a+b", create: false));

            // The rig editor plans, places and removes screens; the view model keeps its lists in step.
            var planned = vm.Screens.AddPlannedScreen(1920, 1080, "Side");
            Assert.True(planned.IsPlannedDisplay);
            Assert.Contains(planned, vm.State.Output.Placements);
            vm.Screens.RemovePlannedScreen(planned);
            Assert.DoesNotContain(planned, vm.State.Output.Placements);
        }
        finally
        {
            b.Dispose();
        }
    }
}
