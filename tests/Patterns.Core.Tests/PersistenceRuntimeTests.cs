using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 65.12: the show's files on one lane — in order, an overtaken save skipped, a clear behind
/// the writes, the exit bounded and truthful, and nothing written when the lane is off.
/// </summary>
public class PersistenceRuntimeTests
{
    private static (PersistenceRuntime Lane, SettingsStore Store, string Dir) Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-lane-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SettingsStore(dir);
        return (new PersistenceRuntime(store, new RecoveryStore(dir)), store, dir);
    }

    private static ShowState Show(string colour)
    {
        var show = new ShowState();
        show.Brand.PrimaryColor = colour;
        return show;
    }

    private static (RecoverySnapshot Record, string Json)? Record(bool audio)
    {
        var r = new RecoverySnapshot(true, audio, DateTime.UtcNow);
        return (r, RecoveryStore.Serialize(r));
    }

    [Fact]
    public async Task SavesLandInOrderAndAnOvertakenSaveIsSkipped()
    {
        var (lane, store, dir) = Make();
        try
        {
            using var gate = new ManualResetEventSlim(false);
            lane.Queue("a slow share", () => gate.Wait(TimeSpan.FromSeconds(10)));
            for (var i = 1; i <= 5; i++) lane.SaveInBackground(Show($"#00000{i}"), snapshotMs: 0);
            gate.Set();
            await lane.Pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("#000005", store.Load().Brand.PrimaryColor);
            Assert.Equal(4, lane.Files.Coalesced);                                               // four saves a newer one overtook: one serialisation, one write
            Assert.Equal(1, lane.Files.Of(FileBudget.SaveWrite)!.Count);
            Assert.Equal(5, lane.Files.Of(FileBudget.SaveSnapshot)!.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AClearNeverRacesAWriteAndAWriteNeverRacesAClear()
    {
        var (lane, _, dir) = Make();
        var path = Path.Combine(dir, "patterns.recovery.json");
        try
        {
            RecoverySnapshot? written = null;
            lane.WriteRecovery(() => Record(audio: false), r => written = r);
            lane.ClearRecovery();
            await lane.Pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(File.Exists(path));                                                     // the clear came last
            Assert.NotNull(written);                                                             // and the write ran whole before it
            lane.ClearRecovery();
            lane.WriteRecovery(() => Record(audio: true), _ => { });
            await lane.Pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(new RecoveryStore(dir).Read()!.AudioPlaying);                           // the write came last
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RecoveryRecordsCoalesceToTheLatestAndTheOvertakenAreNeverMade()
    {
        var (lane, _, dir) = Make();
        try
        {
            using var gate = new ManualResetEventSlim(false);
            lane.Queue("a slow share", () => gate.Wait(TimeSpan.FromSeconds(10)));
            var made = 0;
            for (var i = 0; i < 3; i++)
            {
                lane.WriteRecovery(() =>
                {
                    Interlocked.Increment(ref made);
                    return Record(audio: false);
                }, _ => { });
            }
            gate.Set();
            await lane.Pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, made);                                                               // the clone and the serialisation happen once, for the latest
            Assert.Equal(2, lane.Files.Coalesced);
            Assert.Equal(1, lane.Files.Of(FileBudget.RecoveryWrite)!.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task TheExitWaitsABoundedTimeAndSaysWhetherTheShowReachedTheDisk()
    {
        var (lane, store, dir) = Make();
        try
        {
            using var gate = new ManualResetEventSlim(false);
            lane.Queue("a share that never answers", () => gate.Wait(TimeSpan.FromSeconds(20)));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(lane.SaveAtExit(Show("#123456"), TimeSpan.FromMilliseconds(200)));      // the exit goes on without it
            Assert.InRange(sw.ElapsedMilliseconds, 150, 5000);
            gate.Set();                                                                          // the share answers late: the save still lands, in order
            await lane.Pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("#123456", store.Load().Brand.PrimaryColor);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
        var (lane2, store2, dir2) = Make();
        try
        {
            Assert.True(lane2.SaveAtExit(Show("#654321"), TimeSpan.FromSeconds(10)));            // an idle lane: the show is on the disk before the exit goes on
            Assert.Equal("#654321", store2.Load().Brand.PrimaryColor);
        }
        finally
        {
            Directory.Delete(dir2, true);
        }
    }

    [Fact]
    public async Task OffMeansNoFileOfTheShowIsWritten()
    {
        var (lane, store, dir) = Make();
        try
        {
            lane.Autosave = false;
            lane.SaveInBackground(Show("#000000"), 0);
            lane.SaveNow(Show("#000000"));
            Assert.True(lane.SaveAtExit(Show("#000000"), TimeSpan.FromSeconds(1)));
            await lane.Pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(File.Exists(store.SettingsPath));
            Assert.Equal(0, lane.Files.Coalesced);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AStepThatThrowsIsLoggedAndTheLaneCarriesOn()
    {
        var (lane, store, dir) = Make();
        try
        {
            lane.Queue("a bad share", () => throw new IOException("the share is gone"));
            lane.SaveInBackground(Show("#ABCDEF"), 0);
            await lane.Pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("#ABCDEF", store.Load().Brand.PrimaryColor);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
