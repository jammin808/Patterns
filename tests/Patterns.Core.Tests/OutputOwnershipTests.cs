using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 20: "If Patterns crashes and restarts, the render engines keep playing to keep content
/// alive on screens — this is correct and good. However, the old render windows don't get re-owned
/// on restart and continue playing without any way to stop them." The rules for reading the
/// ownership record: whose screens these are, whether that owner is alive, hung or long gone, and
/// what the desk says about it. Pure — the doing is the App's.
/// </summary>
public class OutputOwnershipTests
{
    private const string Me = "DESK-1";
    private static readonly DateTime Now = new(2026, 9, 9, 19, 30, 0, DateTimeKind.Utc);

    private static OutputOwner Owner(int pid = 4242, long started = 1000, double beatSecondsAgo = 0, string machine = Me)
        => new(pid, started, Now.AddSeconds(-beatSecondsAgo), Now.AddMinutes(-40),
            new[] { "Main wall", "Stage left" }, machine, @"C:\Patterns\Patterns.exe");

    private static Func<int, long?> Running(int pid, long started) => id => id == pid ? started : null;

    private static readonly Func<int, long?> NothingRunning = _ => null;

    [Fact]
    public void NoRecordAndOurOwnRecordAreBothNothingToTake()
    {
        Assert.Equal(OutputClaim.Free, OutputOwnership.Read(null, 7, Me, Now, NothingRunning));
        Assert.Equal(OutputClaim.Ours, OutputOwnership.Read(Owner(pid: 7), 7, Me, Now, Running(7, 1000)));
        Assert.False(OutputOwnership.ShouldTakeOver(OutputClaim.Free));
        Assert.False(OutputOwnership.ShouldTakeOver(OutputClaim.Ours));
    }

    [Fact]
    public void AnOwnerThatDiedTookItsWindowsWithIt()
    {
        // Nothing with that id: the crash ended the process, so the screens went dark with it.
        Assert.Equal(OutputClaim.Stale, OutputOwnership.Read(Owner(), 7, Me, Now, NothingRunning));
        Assert.False(OutputOwnership.ShouldTakeOver(OutputClaim.Stale));
    }

    [Fact]
    public void APidHandedOutAgainIsNotTheOwner()
    {
        // Same number, different process: Windows reuses ids, so the start time is what decides.
        Assert.Equal(OutputClaim.Stale, OutputOwnership.Read(Owner(started: 1000), 7, Me, Now, Running(4242, 9999)));
    }

    [Fact]
    public void ALiveBeatingOwnerIsASecondDeskAndItsScreensAreAskedFor()
    {
        var claim = OutputOwnership.Read(Owner(beatSecondsAgo: 1), 7, Me, Now, Running(4242, 1000));
        Assert.Equal(OutputClaim.HeldByLiveDesk, claim);
        Assert.True(OutputOwnership.ShouldTakeOver(claim));
        Assert.Contains("pid 4242", OutputOwnership.Words(claim, Owner()));
    }

    [Fact]
    public void AProcessThatStoppedBeatingIsHungAndItsScreensAreTakenBack()
    {
        // The whole point: the process is up and its render windows are playing, but the desk in it
        // has stopped answering — nothing on that machine can stop the picture.
        var silent = OutputOwnership.HeartbeatSilent.TotalSeconds + 1;
        var claim = OutputOwnership.Read(Owner(beatSecondsAgo: silent), 7, Me, Now, Running(4242, 1000));
        Assert.Equal(OutputClaim.HeldByHungDesk, claim);
        Assert.True(OutputOwnership.ShouldTakeOver(claim));
        Assert.Contains("stopped answering", OutputOwnership.Words(claim, Owner()));

        // A beat inside the window is a desk that is simply busy, not one to end.
        var busy = OutputOwnership.HeartbeatSilent.TotalSeconds - 1;
        Assert.Equal(OutputClaim.HeldByLiveDesk, OutputOwnership.Read(Owner(beatSecondsAgo: busy), 7, Me, Now, Running(4242, 1000)));
    }

    [Fact]
    public void AnotherMachinesRecordIsNeverThisDesksToTake()
    {
        // A show folder on a share: the pid over there means nothing here, and ending it is not ours to do.
        var claim = OutputOwnership.Read(Owner(machine: "DESK-2"), 7, Me, Now, Running(4242, 1000));
        Assert.Equal(OutputClaim.AnotherMachine, claim);
        Assert.False(OutputOwnership.ShouldTakeOver(claim));
        Assert.Contains("DESK-2", OutputOwnership.Words(claim, Owner(machine: "DESK-2")));
    }

    [Fact]
    public void ARecordFromLastMonthIsHistory()
    {
        var old = Owner(beatSecondsAgo: OutputOwnership.Forgotten.TotalSeconds + 60);
        Assert.Equal(OutputClaim.Stale, OutputOwnership.Read(old, 7, Me, Now, Running(4242, 1000)));
    }

    [Fact]
    public void TheGraceIsGivenAndThenExpires()
    {
        Assert.False(OutputOwnership.GraceExpired(Now, Now));
        Assert.False(OutputOwnership.GraceExpired(Now, Now + OutputOwnership.HandoverGrace - TimeSpan.FromMilliseconds(1)));
        Assert.True(OutputOwnership.GraceExpired(Now, Now + OutputOwnership.HandoverGrace));
    }

    [Fact]
    public void AnAskIsObeyedOnceAndNeverByTheAskerItself()
    {
        Assert.True(OutputOwnership.ShouldStandDown(new HandoverRequest(9, Now), 4242, Now));
        Assert.False(OutputOwnership.ShouldStandDown(new HandoverRequest(4242, Now), 4242, Now));
        Assert.False(OutputOwnership.ShouldStandDown(null, 4242, Now));
        // An ask from a claimant that itself died is not acted on for ever.
        Assert.False(OutputOwnership.ShouldStandDown(new HandoverRequest(9, Now - OutputOwnership.Forgotten - TimeSpan.FromMinutes(1)), 4242, Now));
    }

    [Fact]
    public void TheWordsNameTheScreens()
    {
        Assert.Equal("the screens", OutputOwnership.TargetWords(null));
        Assert.Equal("the screens", OutputOwnership.TargetWords(Array.Empty<string>()));
        Assert.Equal("1 screen (Main wall)", OutputOwnership.TargetWords(new[] { "Main wall" }));
        Assert.Equal("2 screens (Main wall, Foyer)", OutputOwnership.TargetWords(new[] { "Main wall", "Foyer" }));
        Assert.Equal("6 screens (A, B, C, D and 2 more)", OutputOwnership.TargetWords(new[] { "A", "B", "C", "D", "E", "F" }));

        Assert.Contains("stood down", OutputOwnership.TakenWords(Owner(), ended: false));
        Assert.Contains("ended", OutputOwnership.TakenWords(Owner(), ended: true));
        Assert.Contains("2 screens", OutputOwnership.TakenWords(Owner(), ended: true));
    }

    [Fact]
    public void TheRecordAndTheAskSurviveTheDiskRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-own-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new OutputOwnerStore(dir);
            Assert.Null(store.Read());
            Assert.Null(store.ReadRequest());

            var owner = Owner();
            store.Write(owner);
            var back = store.Read();
            Assert.NotNull(back);
            Assert.Equal(owner.Pid, back!.Pid);
            Assert.Equal(owner.StartedAtUtcTicks, back.StartedAtUtcTicks);
            Assert.Equal(owner.Machine, back.Machine);
            Assert.Equal(owner.Targets, back.Targets);

            store.Ask(new HandoverRequest(9, Now));
            Assert.Equal(9, store.ReadRequest()!.Pid);
            store.ClearRequest();
            Assert.Null(store.ReadRequest());

            store.Clear();
            Assert.Null(store.Read());

            // Junk on disk is not a crash — a torn write must never make a live desk look like an orphan.
            File.WriteAllText(Path.Combine(dir, "patterns.outputs.json"), "{ not json");
            Assert.Null(store.Read());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* the temp folder can wait */ }
        }
    }
}
