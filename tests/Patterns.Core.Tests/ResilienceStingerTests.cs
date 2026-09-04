using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

public class SupervisorPolicyTests
{
    private static readonly DateTime T0 = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CleanExitStops()
    {
        var policy = new SupervisorPolicy();
        var verdict = policy.OnExit(0, killedForHang: false, ranFor: TimeSpan.FromMinutes(1), T0);
        Assert.Equal(SupervisorAction.Stop, verdict.Action);
    }

    [Fact]
    public void CrashesRestartWithGrowingBackoff()
    {
        var policy = new SupervisorPolicy();
        var first = policy.OnExit(1, false, TimeSpan.FromSeconds(30), T0);
        var second = policy.OnExit(1, false, TimeSpan.FromSeconds(30), T0.AddSeconds(40));
        var third = policy.OnExit(1, false, TimeSpan.FromSeconds(30), T0.AddSeconds(80));
        Assert.Equal(SupervisorAction.Restart, first.Action);
        Assert.Equal(2, first.Delay.TotalSeconds);
        Assert.Equal(4, second.Delay.TotalSeconds);
        Assert.Equal(8, third.Delay.TotalSeconds);
    }

    [Fact]
    public void HangWithCleanExitCodeStillRestarts()
    {
        var policy = new SupervisorPolicy();
        var verdict = policy.OnExit(0, killedForHang: true, TimeSpan.FromMinutes(2), T0);
        Assert.Equal(SupervisorAction.Restart, verdict.Action);
    }

    [Fact]
    public void AStableRunResetsTheBackoff()
    {
        var policy = new SupervisorPolicy();
        policy.OnExit(1, false, TimeSpan.FromSeconds(10), T0);
        policy.OnExit(1, false, TimeSpan.FromSeconds(10), T0.AddMinutes(11));
        // Ran 20 minutes before dying — that's a fresh incident, not a loop.
        var after = policy.OnExit(1, false, TimeSpan.FromMinutes(20), T0.AddMinutes(31));
        Assert.Equal(SupervisorAction.Restart, after.Action);
        Assert.Equal(2, after.Delay.TotalSeconds);
    }

    [Fact]
    public void ACrashLoopGivesUpInsteadOfFlappingTheScreens()
    {
        var policy = new SupervisorPolicy(maxCrashesInWindow: 6, crashWindowMinutes: 10);
        SupervisorVerdict verdict = default;
        for (var i = 0; i < 7; i++)
        {
            verdict = policy.OnExit(1, false, TimeSpan.FromSeconds(5), T0.AddSeconds(i * 20));
        }
        Assert.Equal(SupervisorAction.GiveUp, verdict.Action);
    }

    [Fact]
    public void OldCrashesFallOutOfTheWindow()
    {
        var policy = new SupervisorPolicy(maxCrashesInWindow: 2, crashWindowMinutes: 10);
        policy.OnExit(1, false, TimeSpan.FromSeconds(5), T0);
        policy.OnExit(1, false, TimeSpan.FromSeconds(5), T0.AddMinutes(1));
        // Two crashes aged out — this third one is within budget again.
        var later = policy.OnExit(1, false, TimeSpan.FromSeconds(5), T0.AddMinutes(30));
        Assert.Equal(SupervisorAction.Restart, later.Action);
    }

    [Fact]
    public void HangDetectionNeedsABeatThatStopped()
    {
        Assert.False(SupervisorPolicy.IsHung(null, T0));
        Assert.False(SupervisorPolicy.IsHung(T0.AddSeconds(-5), T0));
        Assert.True(SupervisorPolicy.IsHung(T0 - SupervisorPolicy.HangTimeout - TimeSpan.FromSeconds(1), T0));
    }
}

public class RecoveryStoreTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void WritesReadsAndClears()
    {
        var store = new RecoveryStore(TempDir());
        Assert.Null(store.Read());

        store.Write(live: true, audioPlaying: false);
        var snap = store.Read();
        Assert.NotNull(snap);
        Assert.True(snap!.Live);
        Assert.False(snap.AudioPlaying);
        Assert.True(RecoveryStore.IsFresh(snap, DateTime.UtcNow));

        store.Clear();
        Assert.Null(store.Read());
    }

    [Fact]
    public void StaleSnapshotsAreNotFresh()
    {
        var old = new RecoverySnapshot(true, true, DateTime.UtcNow.AddDays(-2));
        Assert.False(RecoveryStore.IsFresh(old, DateTime.UtcNow));
    }

    [Fact]
    public void GarbageFileReadsAsNull()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "patterns.recovery.json"), "{not json");
        Assert.Null(new RecoveryStore(dir).Read());
    }
}

public class HealthMonitorTests
{
    [Fact]
    public void ErrorsAreCountedIntoTheSummary()
    {
        var before = HealthMonitor.Faults;
        Log.Error("test fault for the health counter");
        Assert.True(HealthMonitor.Faults > before);
        var summary = HealthMonitor.Summary(DateTime.UtcNow);
        Assert.Contains("fault", summary);
        Assert.StartsWith("Up ", summary);
    }
}

public class StingerProtocolTests
{
    [Theory]
    [InlineData("STINGER 3", RemoteCommandKind.Stinger, 3, "")]
    [InlineData("stinger 1", RemoteCommandKind.Stinger, 1, "")]
    [InlineData("STINGER Take your seats", RemoteCommandKind.Stinger, 0, "Take your seats")]
    [InlineData("STINGER STOP", RemoteCommandKind.StingerStop, 0, "")]
    [InlineData("stinger stop", RemoteCommandKind.StingerStop, 0, "")]
    [InlineData("VOG 3", RemoteCommandKind.Vog, 3, "")]
    [InlineData("vog Take your seats", RemoteCommandKind.Vog, 0, "Take your seats")]
    [InlineData("VOG STOP", RemoteCommandKind.StingerStop, 0, "")]
    [InlineData("STING 2", RemoteCommandKind.Sting, 2, "")]
    [InlineData("sting Whoosh", RemoteCommandKind.Sting, 0, "Whoosh")]
    [InlineData("sting stop", RemoteCommandKind.StingerStop, 0, "")]
    public void ParsesStingerCommands(string line, RemoteCommandKind kind, int intArg, string textArg)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.Equal(kind, cmd.Kind);
        Assert.Equal(intArg, cmd.IntArg);
        Assert.Equal(textArg, cmd.TextArg);
    }

    [Fact]
    public void BareStingerIsUnknown()
        => Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("STINGER").Kind);

    [Fact]
    public void BareVogAndBareStingAreUnknown()
    {
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("VOG").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("STING").Kind);
    }

    [Fact]
    public void DisplayNameFallsBackToTheFileName()
    {
        var item = new StingerItemConfig { Path = "/shows/take-seats.mp3" };
        Assert.Equal("take-seats.mp3", item.DisplayName);
        item.Name = "Take your seats";
        Assert.Equal("Take your seats", item.DisplayName);
    }
}
