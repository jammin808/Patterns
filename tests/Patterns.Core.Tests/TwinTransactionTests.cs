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

    [Fact]
    public void EveryHandoverHasAnIdOfItsOwnAndOneGivenIsKept()
    {
        var a = new TwinTransaction(HandoverKind.TakeBack, HandoverShape.SameMachine, hasRoute: false, At);
        var b = new TwinTransaction(HandoverKind.TakeBack, HandoverShape.SameMachine, hasRoute: false, At);
        Assert.Equal(12, a.Id.Length);
        Assert.Matches("^[0-9a-f]{12}$", a.Id);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal("abc123def456", new TwinTransaction(HandoverKind.TakeOver, HandoverShape.AcrossMachines, hasRoute: true, At, "abc123def456").Id);
        Assert.Equal(12, TwinTransaction.NewId().Length);
    }

    [Fact]
    public void AStoppedHandoverResumesWhenTheFactArrivesAndTheTrailKeepsBoth()
    {
        // A take-back across machines with no cue: the show is up here, the room is switched by hand, the second press releases.
        var tx = new TwinTransaction(HandoverKind.TakeBack, HandoverShape.AcrossMachines, hasRoute: false, At);
        tx.Reached(HandoverStage.TargetReady);
        tx.Stop("the route is the operator's own");
        Assert.Throws<InvalidOperationException>(() => tx.Reached(HandoverStage.AuthorityCommitted));
        tx.Resume("switched by hand — the operator's word");
        Assert.False(tx.Stopped);
        Assert.Equal("", tx.Reason);
        tx.Reached(HandoverStage.AuthorityCommitted);
        tx.Note("Backup desk said it let go");
        tx.Reached(HandoverStage.OldOwnerReleased);
        tx.Reached(HandoverStage.Complete);
        Assert.Equal("take back across machines (no wall-switch cue): target ready (stopped: the route is the operator's own → resumed: switched by hand — the operator's word) → authority committed (Backup desk said it let go) → old owner released → complete", tx.Trail);
        Assert.Throws<InvalidOperationException>(() => tx.Resume("again"));      // only a stopped handover resumes

        // Stopped twice — the standby never answered, told again, still nothing: every stop and every resumption stays in the trail.
        var twice = new TwinTransaction(HandoverKind.TakeBack, HandoverShape.SameMachine, hasRoute: false, At);
        twice.Reached(HandoverStage.AuthorityCommitted);
        twice.Stop("the standby did not say it let go");
        twice.Resume("told again");
        twice.Stop("the standby did not say it let go");
        Assert.Equal("take back on this machine: authority committed (stopped: the standby did not say it let go → resumed: told again) → stopped: the standby did not say it let go", twice.Trail);
        twice.Resume("");
        Assert.Contains("resumed: the fact arrived", twice.Trail);

        // A note at the first stage, before anything was reached, is in the trail too.
        var early = new TwinTransaction(HandoverKind.TakeOver, HandoverShape.SameMachine, hasRoute: false, At);
        early.Note("the marker written");
        Assert.Equal("take over on this machine: preparing (the marker written)", early.Trail);
        early.Note("");
        Assert.Equal("take over on this machine: preparing (the marker written)", early.Trail);
    }

    [Fact]
    public void TheWordsForTheHandBackSayWhatIsAwaitedWhatStoppedAndWhatTheRoomShows()
    {
        Assert.Contains("switch the room to this desk by hand, then TAKE BACK again releases the standby Backup desk", TwinTransaction.SwitchByHandWords("Backup desk", "No take-back cue is set"));
        Assert.StartsWith("TAKE BACK: the show is on here, on displays the room is not yet looking at. No take-back cue is set —", TwinTransaction.SwitchByHandWords("Backup desk", "No take-back cue is set"));
        Assert.Contains("the picture goes up here the moment it says it has", TwinTransaction.ReleaseAwaitedWords("Backup desk", sameMachine: true));
        Assert.Contains("the room shows this desk; the standby Backup desk was told to let go and its answer is awaited", TwinTransaction.ReleaseAwaitedWords("Backup desk", sameMachine: false));
        var same = TwinTransaction.ReleaseStoppedWords("Backup desk", sameMachine: true);
        Assert.Contains("did not say it let go", same);
        Assert.Contains("this desk's outputs stay held", same);
        Assert.Contains("TAKE BACK again", same);
        var across = TwinTransaction.ReleaseStoppedWords("Backup desk", sameMachine: false);
        Assert.Contains("the room shows this desk", across);
        Assert.Contains("its picture may still be up on its own input", across);
    }
}
