using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 76.1, the pure half of restart continuity: the supervisor's handover code and beat, the
/// deliberate flag on the recovery record, the playhead sidecar, and where a clip resumes.
/// </summary>
public class RestartContinuityTests
{
    [Fact]
    public void AHandoverExitWithNoReplacementRunningComesStraightBackLikeARestartRequest()
    {
        var policy = new SupervisorPolicy();
        var verdict = policy.OnExit(SupervisorPolicy.ReplacedExitCode, killedForHang: false, TimeSpan.FromMinutes(10), DateTime.UtcNow);
        Assert.Equal(SupervisorAction.Restart, verdict.Action);
        Assert.Equal(TimeSpan.Zero, verdict.Delay);
        Assert.Contains("handed over", ExitCodes.Describe(SupervisorPolicy.ReplacedExitCode));
        // The beat that asks for a replacement is not the beat that says alive, and the old desk gives
        // up on a replacement before the supervisor would call that replacement a startup hang.
        Assert.NotEqual(SupervisorPolicy.AliveBeat, SupervisorPolicy.HandoverBeat);
        Assert.True(SupervisorPolicy.HandoverPatience < SupervisorPolicy.StartupDeadline);
    }

    [Fact]
    public void TheDeliberateFlagRidesTheRecordAndAnOlderBuildsRecordReadsAsNotDeliberate()
    {
        var json = RecoveryStore.Serialize(new RecoverySnapshot(true, false, DateTime.UtcNow, Deliberate: true));
        var back = JsonUtil.Deserialize<RecoverySnapshot>(json);
        Assert.NotNull(back);
        Assert.True(back!.Deliberate);
        Assert.True(back.Live);
        var older = JsonUtil.Deserialize<RecoverySnapshot>("{\"Live\":true,\"AudioPlaying\":false,\"UpdatedUtc\":\"2026-01-01T00:00:00Z\"}");
        Assert.NotNull(older);
        Assert.False(older!.Deliberate);
    }

    [Fact]
    public void ThePlayheadSidecarRoundTripsAndClears()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-playhead-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new PlayheadStore(dir);
            Assert.Null(store.Read());
            var when = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            store.Write(new PlayheadRecord(when, new[] { new ClipPlace("video:C:\\clips\\a.mp4", 61.5, false), new ClipPlace("video:C:\\clips\\loop.mp4", 3.25, true) }, new MusicPlace(3, 12.25)));
            var back = store.Read();
            Assert.NotNull(back);
            Assert.Equal(when, back!.UpdatedUtc);
            Assert.Equal(2, back.Clips.Count);
            Assert.Equal(61.5, back.Clips[0].Seconds);
            Assert.True(back.Clips[1].Loops);
            Assert.Equal(3, back.Music!.Index);
            Assert.Equal(12.25, back.Music.Seconds);
            Assert.False(back.IsEmpty);
            Assert.EndsWith(PlayheadStore.FileName, store.FilePath);
            store.Clear();
            Assert.Null(store.Read());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(10, 5, 100, false, 15)]      // where it was plus the time since
    [InlineData(10, 5, 0, false, 15)]        // a length the decoder has not said: taken on trust
    [InlineData(95, 10, 100, true, 5)]       // a loop wraps around its length
    [InlineData(95, 10, 100, false, -1)]     // a clip that would have ended by now starts again (null)
    [InlineData(10, 5000, 100, true, -1)]    // a record older than the age limit is not a position
    [InlineData(10, -5, 100, false, -1)]     // a record from the future (a clock that jumped) is not trusted
    public void AClipResumesWhereItWouldBeByNow(double recorded, double elapsedSeconds, double length, bool loops, double expected)
    {
        var recordedUtc = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
        var target = PlayheadResume.Target(recorded, recordedUtc, recordedUtc.AddSeconds(elapsedSeconds), length, loops);
        if (expected < 0) Assert.Null(target);
        else Assert.Equal(expected, target!.Value, 6);
    }

    [Fact]
    public void ThePlayheadRecordIsFreshWithinTheAgeLimitAndNotBeyondIt()
    {
        var when = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
        var record = new PlayheadRecord(when, Array.Empty<ClipPlace>());
        Assert.True(record.IsEmpty);
        Assert.True(PlayheadResume.IsFresh(record, when.AddSeconds(45)));
        Assert.False(PlayheadResume.IsFresh(record, when + PlayheadResume.MaxAge + TimeSpan.FromSeconds(1)));
        Assert.False(PlayheadResume.IsFresh(record, when.AddSeconds(-1)));
    }
}
