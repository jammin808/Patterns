using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 80.2: an armed target whose own picture is on the air and unchanged in the preview is carried by a wall
/// take and left as it is (round 30's rule). The plan said "→ 2 · Lobby" for it and the take then reported that
/// nothing moved there; now the plan says before the press what the take will do — the target stays taken (the
/// send carries it), and the words say it keeps its own picture.
/// </summary>
public class TakePlanOwnWordsTests
{
    [Fact]
    public void AnArmedSettledOwnTargetIsSaidToKeepItsPictureBeforeThePress()
    {
        var rig = new[]
        {
            new TakeTarget("s1", "1 · Main"),
            new TakeTarget("s2", "2 · Lobby", KeepsOwn: true),
            new TakeTarget("s3", "3 · Info", Locked: true),
        };
        var plan = TakePlan.Resolve(rig, FadeScope.Everything, focused: null);
        Assert.False(plan.IsRefused);
        Assert.Equal(new[] { "s1", "s2" }, plan.Taken);                                   // still carried by the send
        Assert.Equal(new[] { "1 · Main", "2 · Lobby" }, plan.TakenLabels);                // the ticket's promise is unchanged
        Assert.Equal(new[] { "2 · Lobby" }, plan.KeepsOwnLabels);
        Assert.Equal("→ 1 · Main · 2 · Lobby keeps its own picture (OWN) · held: 3 · Info (locked)", plan.Words);
    }

    [Fact]
    public void TwoSettledOwnTargetsAndNothingElseReadAsNoScreenChanging()
    {
        var rig = new[]
        {
            new TakeTarget("s1", "1 · Main", KeepsOwn: true),
            new TakeTarget("s2", "2 · Lobby", KeepsOwn: true),
        };
        var plan = TakePlan.Resolve(rig, FadeScope.Everything, focused: null);
        Assert.False(plan.IsRefused);                                                    // the plan moves nothing; the executor's own rule (round 79) answers the press
        Assert.Equal("→ no screen changes its picture · 1 · Main, 2 · Lobby keep their own picture (OWN)", plan.Words);
    }

    [Fact]
    public void AHeldTargetIsNeverSaidToKeepItsOwnPictureTwice()
    {
        var rig = new[]
        {
            new TakeTarget("s1", "1 · Main"),
            new TakeTarget("s2", "2 · Lobby", Armed: false, KeepsOwn: true),               // not armed: held, and said once as held
            new TakeTarget("s3", "3 · Rear", IsMirror: true, KeepsOwn: true),              // a repeater never keeps a picture of its own
        };
        var plan = TakePlan.Resolve(rig, FadeScope.Everything, focused: null);
        Assert.Empty(plan.KeepsOwnLabels);
        Assert.Equal("→ 1 · Main · held: 2 · Lobby (not armed), 3 · Rear (a repeater)", plan.Words.Replace(TakeHeld.NotArmedReason, "not armed").Replace(TakeHeld.RepeaterReason, "a repeater"));
    }
}
