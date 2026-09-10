using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// A cue's shape in time. Two rules carry the whole feature: a wait is measured from the step
/// above, so the list order and the running order can never disagree; and the waits belong to the
/// positions, so reordering moves the content and leaves the timing an operator built alone.
/// </summary>
public class CueStepTests
{
    private static List<CueActionConfig> Cue(params (ShowActionKind Kind, double After)[] steps)
        => steps.Select(s => new CueActionConfig { Kind = s.Kind, Target = s.Kind.ToString(), DelaySeconds = s.After }).ToList();

    [Fact]
    public void ACueWithNoWaitsPlansExactlyAsItAlwaysDid()
    {
        var actions = Cue((ShowActionKind.ApplyLook, 0), (ShowActionKind.LowerThirdShow, 0), (ShowActionKind.StreamStart, 0));
        Assert.False(CueSteps.HasDelays(actions));
        Assert.Equal(0, CueSteps.TailSeconds(actions));

        var plan = CueSteps.Plan(actions);
        Assert.Equal(3, plan.Count);
        Assert.All(plan, p => Assert.True(p.IsImmediate));
        Assert.Equal(new[] { 0, 1, 2 }, plan.Select(p => p.Index));
    }

    [Fact]
    public void AWaitIsMeasuredFromTheStepAboveSoTheListOrderIsTheRunningOrder()
    {
        var actions = Cue((ShowActionKind.ApplyLook, 0), (ShowActionKind.LowerThirdShow, 3), (ShowActionKind.StreamStart, 5));
        Assert.True(CueSteps.HasDelays(actions));
        Assert.Equal(8, CueSteps.TailSeconds(actions));

        var plan = CueSteps.Plan(actions);
        Assert.Equal(new[] { 0.0, 3.0, 8.0 }, plan.Select(p => p.AtSeconds));
        Assert.True(plan[0].IsImmediate);
        Assert.False(plan[1].IsImmediate);

        // The seconds only ever climb, whatever anyone types — there is no way to write a cue
        // whose second step runs before its first.
        for (var i = 1; i < plan.Count; i++) Assert.True(plan[i].AtSeconds >= plan[i - 1].AtSeconds);
    }

    [Fact]
    public void MovingAStepKeepsTheCuesShapeAndMovesOnlyTheContent()
    {
        var actions = Cue((ShowActionKind.ApplyLook, 0), (ShowActionKind.LowerThirdShow, 3), (ShowActionKind.StreamStart, 5));

        // The stream goes to the top. The shape — now, then three seconds, then five more — is
        // the shape the operator built, so it stays; the stream is simply the one that goes first.
        Assert.True(CueSteps.Move(actions, 2, 0));
        Assert.Equal(new[] { ShowActionKind.StreamStart, ShowActionKind.ApplyLook, ShowActionKind.LowerThirdShow },
            actions.Select(a => a.Kind));
        Assert.Equal(new[] { 0.0, 3.0, 5.0 }, actions.Select(a => a.DelaySeconds));
        Assert.Equal(new[] { 0.0, 3.0, 8.0 }, CueSteps.Plan(actions).Select(p => p.AtSeconds));

        // A swap of neighbours is the same rule seen close up.
        Assert.True(CueSteps.Move(actions, 1, 2));
        Assert.Equal(new[] { ShowActionKind.StreamStart, ShowActionKind.LowerThirdShow, ShowActionKind.ApplyLook },
            actions.Select(a => a.Kind));
        Assert.Equal(new[] { 0.0, 3.0, 5.0 }, actions.Select(a => a.DelaySeconds));

        Assert.False(CueSteps.Move(actions, 1, 1));
        Assert.False(CueSteps.Move(actions, 0, 9));
        Assert.False(CueSteps.Move(actions, -1, 0));
    }

    [Fact]
    public void AWaitCanNeverBeNegativeOrRunAwayWithTheShow()
    {
        var a = new CueActionConfig();
        Assert.Equal(0, a.DelaySeconds);
        a.DelaySeconds = -5;
        Assert.Equal(0, a.DelaySeconds);
        a.DelaySeconds = double.NaN;
        Assert.Equal(0, a.DelaySeconds);
        a.DelaySeconds = 99999;
        Assert.Equal(CueActionConfig.MaxDelaySeconds, a.DelaySeconds);
        a.DelaySeconds = 2.5;
        Assert.Equal(2.5, a.DelaySeconds);
    }

    [Fact]
    public void TheWordsReadTheWayACallerReadsASheet()
    {
        Assert.Equal("", CueSteps.AtWords(0));
        Assert.Equal("+5 s", CueSteps.AtWords(5));
        Assert.Equal("+2.5 s", CueSteps.AtWords(2.5));
        Assert.Equal("+2 m", CueSteps.AtWords(120));
        Assert.Equal("+1 m 30 s", CueSteps.AtWords(90));
    }

    [Fact]
    public void TheSummaryAndTheSheetBothCarryTheShape()
    {
        var state = new ShowState();
        var look = new LookConfig { Name = "Walk-in" };
        state.LooksAndCues.Looks.Add(look);
        var stack = CueStacks.Caller(state);
        var cue = new RunCueConfig { Number = "01.010", Name = "Doors" };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ApplyLook, Target = look.Id });
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.StreamStart, DelaySeconds = 4 });
        stack.Cues.Add(cue);

        var summary = CueSummary.Describe(state, cue);
        Assert.Contains("after 4 s", summary);
        Assert.Contains("over 4 s", summary);

        // Out through the sheet and back in again with its wait.
        var csv = CueSheet.Export(state, stack);
        Assert.Contains("After", csv);
        var read = CueSheet.Import(CsvTable.Parse(csv), state);
        var back = Assert.Single(read.Cues);
        var streamed = back.Actions.First(a => a.Kind == ShowActionKind.StreamStart);
        Assert.Equal(4, streamed.DelaySeconds);
    }
}
