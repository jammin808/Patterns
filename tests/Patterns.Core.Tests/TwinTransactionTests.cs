using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The handover as a transaction: each shape's order is the order the room needs, a stage cannot
/// be skipped, a stop keeps the trail, and the words for a take-back stopped at the route tell
/// the operator what the room shows and what finishes it.
/// </summary>
public class TwinTransactionTests
{
    private static readonly DateTime At = new(2026, 9, 13, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AcrossMachinesATakeBackPutsThePictureUpPointsTheRoomAtItAndReleasesTheStandbyLast()
    {
        var tx = new TwinTransaction(HandoverKind.TakeBack, HandoverShape.AcrossMachines, hasRoute: true, At);
        Assert.Equal(new[]
        {
            HandoverStage.Preparing, HandoverStage.TargetReady, HandoverStage.RouteRequested, HandoverStage.RouteConfirmed,
            HandoverStage.AuthorityCommitted, HandoverStage.OldOwnerReleased, HandoverStage.Complete,
        }, tx.Plan);
        Assert.Equal(HandoverStage.Preparing, tx.Stage);
        Assert.Equal(HandoverStage.TargetReady, tx.Next);
        foreach (var stage in tx.Plan.Skip(1)) tx.Reached(stage);
        Assert.True(tx.IsComplete);
        Assert.Null(tx.Next);
        Assert.Equal("take back across machines: target ready → route requested → route confirmed → authority committed → old owner released → complete", tx.Trail);
    }

    [Fact]
    public void OnOneMachineTheStandbyLetsGoFirstBecauseItsWindowsAreTheSameDisplays()
    {
        var tx = new TwinTransaction(HandoverKind.TakeBack, HandoverShape.SameMachine, hasRoute: true, At);
        Assert.Equal(new[]
        {
            HandoverStage.Preparing, HandoverStage.AuthorityCommitted, HandoverStage.OldOwnerReleased, HandoverStage.TargetReady, HandoverStage.Complete,
        }, tx.Plan);
        Assert.DoesNotContain(HandoverStage.RouteRequested, tx.Plan);   // one machine has no wall to switch
        Assert.StartsWith("take back on this machine:", tx.Trail);
    }

    [Fact]
    public void WithoutAWallSwitchCueTheRouteStagesAreNotInThePlanAndTheTrailSaysSo()
    {
        var tx = new TwinTransaction(HandoverKind.TakeBack, HandoverShape.AcrossMachines, hasRoute: false, At);
        Assert.Equal(new[]
        {
            HandoverStage.Preparing, HandoverStage.TargetReady, HandoverStage.AuthorityCommitted, HandoverStage.OldOwnerReleased, HandoverStage.Complete,
        }, tx.Plan);
        Assert.Contains("(no wall-switch cue)", tx.Trail);
    }

    [Fact]
    public void ATakeoverRoutesBeforeItsOutputsOpenAndReleasesNobody()
    {
        var tx = new TwinTransaction(HandoverKind.TakeOver, HandoverShape.AcrossMachines, hasRoute: true, At);
        Assert.Equal(new[]
        {
            HandoverStage.Preparing, HandoverStage.RouteRequested, HandoverStage.RouteConfirmed, HandoverStage.AuthorityCommitted, HandoverStage.TargetReady, HandoverStage.Complete,
        }, tx.Plan);
        Assert.DoesNotContain(HandoverStage.OldOwnerReleased, tx.Plan);
        var local = new TwinTransaction(HandoverKind.TakeOver, HandoverShape.SameMachine, hasRoute: true, At);
        Assert.Equal(new[] { HandoverStage.Preparing, HandoverStage.AuthorityCommitted, HandoverStage.TargetReady, HandoverStage.Complete }, local.Plan);
    }

    [Fact]
    public void AStageCannotBeSkippedAndAStopKeepsTheTrail()
    {
        var tx = new TwinTransaction(HandoverKind.TakeBack, HandoverShape.AcrossMachines, hasRoute: true, At);
        Assert.Throws<InvalidOperationException>(() => tx.Reached(HandoverStage.RouteRequested));   // the target first
        tx.Reached(HandoverStage.TargetReady);
        tx.Reached(HandoverStage.RouteRequested);
        tx.Stop("could not fire (No cue 'Wall to main'.)");
        Assert.True(tx.Stopped);
        Assert.False(tx.IsComplete);
        Assert.Equal(HandoverStage.RouteRequested, tx.Stage);
        Assert.Equal("take back across machines: target ready → route requested → stopped: could not fire (No cue 'Wall to main'.)", tx.Trail);
        Assert.Throws<InvalidOperationException>(() => tx.Reached(HandoverStage.RouteConfirmed));   // nothing moves past a stop
        var done = new TwinTransaction(HandoverKind.TakeOver, HandoverShape.SameMachine, hasRoute: false, At);
        done.Reached(HandoverStage.AuthorityCommitted);
        done.Reached(HandoverStage.TargetReady);
        done.Reached(HandoverStage.Complete);
        Assert.Throws<InvalidOperationException>(() => done.Stop("late"));
        Assert.Equal("stopped", Stopped().Reason);

        static TwinTransaction Stopped()
        {
            var t = new TwinTransaction(HandoverKind.TakeBack, HandoverShape.SameMachine, hasRoute: false, At);
            t.Stop("");
            return t;
        }
    }

    [Fact]
    public void TheWordsForARouteThatStoppedTellTheOperatorWhatTheRoomShowsAndWhatFinishesIt()
    {
        var words = TwinTransaction.RouteStoppedWords("Backup desk", "Wall to main", "could not fire (No cue 'Wall to main'.)");
        Assert.Contains("stopped at the wall switch", words);
        Assert.Contains("The room still shows the standby Backup desk, whose picture stays up", words);
        Assert.Contains("switch the wall to this desk by hand, then TAKE BACK again", words);
    }
}
