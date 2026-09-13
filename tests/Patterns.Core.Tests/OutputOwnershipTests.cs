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
        Assert.Equal(OutputClaim.Stale, OutputOwnership.Read(Owner(started: 1000), 7, Me, Now, _ => ProcessSight.Alive(9999)));
    }

    [Fact]
    public void AnOwnerThatCannotBeReadIsAFenceNotAnAbsence()
    {
        // Up, and not this process's to read (another user's, elevated): "could not see it" is never
        // "it is gone". It reads as an owner — the heartbeat alone says whether its desk answers —
        // and never as free screens.
        Func<int, ProcessSight> unreadable = pid => pid == 4242 ? ProcessSight.Unreadable() : ProcessSight.Gone;
        var live = OutputOwnership.Read(Owner(beatSecondsAgo: 1), 7, Me, Now, unreadable);
        Assert.Equal(OutputClaim.HeldByLiveDesk, live);
        var hung = OutputOwnership.Read(Owner(beatSecondsAgo: 30), 7, Me, Now, unreadable);
        Assert.Equal(OutputClaim.HeldByHungDesk, hung);
        Assert.True(OutputOwnership.ShouldTakeOver(hung));
        // Gone is still gone, and the two-answer read still means what it meant.
        Assert.Equal(OutputClaim.Stale, OutputOwnership.Read(Owner(), 7, Me, Now, _ => ProcessSight.Gone));
        Assert.Equal(OutputClaim.HeldByLiveDesk, OutputOwnership.Read(Owner(beatSecondsAgo: 1), 7, Me, Now, ProcessSight.From(Running(4242, 1000))));
        Assert.Equal(OutputClaim.Stale, OutputOwnership.Read(Owner(), 7, Me, Now, ProcessSight.From(NothingRunning)));
    }

    [Fact]
    public void ALookAnswersThreeWaysAndTheFencesReadEachOne()
    {
        var gone = ProcessSight.Gone;
        var mine = ProcessSight.Alive(1000, @"C:\Patterns\Patterns.exe");
        var other = ProcessSight.Alive(2000);
        var shut = ProcessSight.Unreadable();
        Assert.False(gone.Exists);
        Assert.True(mine.IsTheOne(1000));
        Assert.False(other.IsTheOne(1000));
        Assert.False(shut.IsTheOne(1000));                                          // it may be, but nobody can say so
        Assert.True(gone.IsGoneOrReused(1000));
        Assert.True(other.IsGoneOrReused(1000));
        Assert.False(mine.IsGoneOrReused(1000));
        Assert.False(shut.IsGoneOrReused(1000));                                    // and it may not be gone either: the fence
        Assert.True(shut.IsUnreadable);
        Assert.False(mine.IsUnreadable);
        Assert.False(gone.IsUnreadable);
        Assert.Equal("", shut.ExePath);
        Assert.Equal(@"C:\Patterns\Patterns.exe", mine.ExePath);
        var from = ProcessSight.From(pid => pid == 1 ? 1000 : null);
        Assert.True(from(1).IsTheOne(1000));
        Assert.Same(ProcessSight.Gone, from(2));
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
            Assert.True(store.Read().IsMissing);
            Assert.True(store.ReadRequest().IsMissing);

            var owner = Owner();
            Assert.True(store.Write(owner).Committed);
            var read = store.Read();
            Assert.True(read.IsValid);
            var back = read.Value!;
            Assert.Equal(owner.Pid, back.Pid);
            Assert.Equal(owner.StartedAtUtcTicks, back.StartedAtUtcTicks);
            Assert.Equal(owner.Machine, back.Machine);
            Assert.Equal(owner.Targets, back.Targets);

            Assert.True(store.Ask(new HandoverRequest(9, Now)).Committed);
            Assert.Equal(9, store.ReadRequest().Value!.Pid);
            Assert.True(store.ClearRequest().Committed);
            Assert.True(store.ReadRequest().IsMissing);
            Assert.True(store.ClearRequest().Committed);           // clearing nothing is not a failure

            Assert.True(store.Clear().Committed);
            Assert.True(store.Read().IsMissing);

            // Junk on disk is not a crash, and it is not "free" either: a torn write must never make
            // a live desk look like an orphan, nor a locked record look like nobody's screens.
            File.WriteAllText(Path.Combine(dir, "patterns.outputs.json"), "{ not json");
            var junk = store.Read();
            Assert.True(junk.IsUnreadable);
            Assert.Null(junk.Value);
            Assert.NotEqual("", junk.Problem);
            Assert.Equal(OutputClaim.Unknown, OutputOwnership.Read(junk, 1, Me, Now, ProcessSight.From(NothingRunning)));
            File.WriteAllText(Path.Combine(dir, "patterns.outputs.json"), "null");
            Assert.True(store.Read().IsUnreadable);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* the temp folder can wait */ }
        }
    }

    /// <summary>The files as a night can find them: a folder that will not take a write, a file that is locked, a rename that fails, a share that is gone, junk.</summary>
    private sealed class FaultyFiles : ISidecarFiles
    {
        public readonly Dictionary<string, string> Disk = new(StringComparer.Ordinal);
        public bool FolderGone;
        public bool WriteThrows;
        public bool MoveThrows;
        public string? ReadThrowsFor;     // one path only
        public string? DeleteThrowsFor;

        public bool DirectoryExists(string path) => !FolderGone;

        public bool Exists(string path) => Disk.ContainsKey(path);

        public string ReadAllText(string path)
        {
            if (ReadThrowsFor == path) throw new IOException("The process cannot access the file because it is being used by another process.");
            return Disk[path];
        }

        public void WriteAllText(string path, string text)
        {
            if (WriteThrows) throw new UnauthorizedAccessException("Access to the path is denied.");
            Disk[path] = text;
        }

        public void Move(string from, string to)
        {
            if (MoveThrows) throw new IOException("The file cannot be moved: it is in use.");
            Disk[to] = Disk[from];
            Disk.Remove(from);
        }

        public void Delete(string path)
        {
            if (DeleteThrowsFor == path) throw new IOException("The file is locked.");
            Disk.Remove(path);
        }
    }

    private static readonly Func<int, ProcessSight> Nobody = ProcessSight.From(NothingRunning);

    [Fact]
    public void AReadOnlyFolderCommitsNoWriteAndTheReadStaysHonest()
    {
        var files = new FaultyFiles { WriteThrows = true };
        var store = new OutputOwnerStore("/show", files);
        var wrote = store.Write(Owner());
        Assert.False(wrote.Committed);
        Assert.Contains("denied", wrote.Problem);
        Assert.True(store.Read().IsMissing);                    // nothing landed, and nothing pretends it did
        var asked = store.Ask(new HandoverRequest(9, Now));
        Assert.False(asked.Committed);
        Assert.True(store.ReadRequest().IsMissing);
        Assert.Empty(files.Disk);                               // the half-written .tmp was not left behind either
    }

    [Fact]
    public void ALockedRecordOrAskIsUnreadableAndReadsAsAFence()
    {
        var files = new FaultyFiles();
        var store = new OutputOwnerStore("/show", files);
        Assert.True(store.Write(Owner()).Committed);
        Assert.True(store.Ask(new HandoverRequest(9, Now)).Committed);

        files.ReadThrowsFor = store.OwnerPath;
        var read = store.Read();
        Assert.True(read.IsUnreadable);
        Assert.Contains("another process", read.Problem);
        Assert.Equal(OutputClaim.Unknown, OutputOwnership.Read(read, 1, Me, Now, Nobody));
        Assert.True(store.ReadRequest().IsValid);              // the other file is its own question

        files.ReadThrowsFor = store.RequestPath;
        Assert.True(store.Read().IsValid);
        Assert.True(store.ReadRequest().IsUnreadable);
    }

    [Fact]
    public void JunkInEitherSidecarIsUnreadableNotAbsent()
    {
        var files = new FaultyFiles();
        var store = new OutputOwnerStore("/show", files);
        files.Disk[store.OwnerPath] = "{ \"Pid\": ";
        files.Disk[store.RequestPath] = "<html>";
        Assert.True(store.Read().IsUnreadable);
        Assert.True(store.ReadRequest().IsUnreadable);
        Assert.Equal(OutputClaim.Unknown, OutputOwnership.Read(store.Read(), 1, Me, Now, Nobody));
    }

    [Fact]
    public void AFailedRenameIsAFailedWriteAndLeavesNoHalfRecord()
    {
        var files = new FaultyFiles { MoveThrows = true };
        var store = new OutputOwnerStore("/show", files);
        var wrote = store.Write(Owner());
        Assert.False(wrote.Committed);
        Assert.Contains("in use", wrote.Problem);
        Assert.True(store.Read().IsMissing);
        Assert.DoesNotContain(files.Disk.Keys, k => k.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void AFolderThatIsGoneIsUnreadableNeverFree()
    {
        // A show on a share that dropped, or on a stick that was pulled: File.Exists answers false
        // there, which is the one answer that must not become "nobody has the screens".
        var files = new FaultyFiles { FolderGone = true };
        var store = new OutputOwnerStore("/show", files);
        var read = store.Read();
        Assert.True(read.IsUnreadable);
        Assert.Contains("not there", read.Problem);
        Assert.Equal(OutputClaim.Unknown, OutputOwnership.Read(read, 1, Me, Now, Nobody));
        Assert.False(store.Write(Owner()).Committed == false && files.Disk.Count > 0);   // and a write there is not believed either way
    }

    [Fact]
    public void AClearThatFailsSaysSoAndOneOfNothingIsFine()
    {
        var files = new FaultyFiles();
        var store = new OutputOwnerStore("/show", files);
        Assert.True(store.Clear().Committed);                   // nothing to clear
        Assert.True(store.Write(Owner()).Committed);
        files.DeleteThrowsFor = store.OwnerPath;
        var cleared = store.Clear();
        Assert.False(cleared.Committed);
        Assert.Contains("locked", cleared.Problem);
        Assert.True(store.Read().IsValid);                      // still there, and said so
        files.DeleteThrowsFor = null;
        Assert.True(store.Clear().Committed);
        Assert.True(store.Read().IsMissing);
    }

    [Fact]
    public void TheStoreRecoversWhenTheFolderTakesWritesAgain()
    {
        var files = new FaultyFiles { WriteThrows = true };
        var store = new OutputOwnerStore("/show", files);
        Assert.False(store.Write(Owner()).Committed);
        Assert.False(store.Write(Owner()).Committed);
        files.WriteThrows = false;
        Assert.True(store.Write(Owner()).Committed);
        Assert.True(store.Read().IsValid);
        Assert.Equal(4242, store.Read().Value!.Pid);
    }

    [Fact]
    public void TheWordsForAnUnreadableRecordTellTheOperatorHowToOpenTheScreens()
    {
        var words = OutputOwnership.Words(OutputClaim.Unknown, null);
        Assert.Contains("could not be read", words);
        Assert.Contains("does not open them by itself", words);
        Assert.Contains("OUTPUTS ON", words);
        Assert.Contains("(locked)", OutputOwnership.UnknownWords("locked"));
    }
}
