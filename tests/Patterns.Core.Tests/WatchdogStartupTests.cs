using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 65: the watchdog's two deadlines. A child that never sends its first heartbeat is a
/// startup hang once the startup deadline passes — before that it is starting; a child that beat
/// and then went silent is hung after the hang timeout; and a kill for either goes through the
/// same restart policy, backoff and crash-loop cap included.
/// </summary>
public class WatchdogStartupTests
{
    private static readonly DateTime T0 = new(2026, 9, 15, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AChildThatNeverBeatsIsStartingUntilTheDeadlineAndAStartupHangAfterIt()
    {
        Assert.Equal(SupervisorPolicy.ChildPhase.Starting, SupervisorPolicy.Phase(T0, null, T0.AddSeconds(1)));
        Assert.Equal(SupervisorPolicy.ChildPhase.Starting, SupervisorPolicy.Phase(T0, null, T0 + SupervisorPolicy.StartupDeadline));
        Assert.Equal(SupervisorPolicy.ChildPhase.StartupHang, SupervisorPolicy.Phase(T0, null, T0 + SupervisorPolicy.StartupDeadline + TimeSpan.FromSeconds(1)));
        Assert.Equal(SupervisorPolicy.ChildPhase.StartupHang, SupervisorPolicy.Phase(T0, null, T0.AddHours(1)));
    }

    [Fact]
    public void AFirstBeatJustBeforeTheDeadlineMakesTheChildABeatingDeskJudgedByTheHangTimeoutAlone()
    {
        var beat = T0 + SupervisorPolicy.StartupDeadline - TimeSpan.FromSeconds(1);
        Assert.Equal(SupervisorPolicy.ChildPhase.Beating, SupervisorPolicy.Phase(T0, beat, beat.AddSeconds(5)));
        Assert.Equal(SupervisorPolicy.ChildPhase.Beating, SupervisorPolicy.Phase(T0, beat, beat + SupervisorPolicy.HangTimeout));      // the startup deadline no longer counts
        Assert.Equal(SupervisorPolicy.ChildPhase.Hung, SupervisorPolicy.Phase(T0, beat, beat + SupervisorPolicy.HangTimeout + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ARunningChildThatStopsBeatingIsHungAfterTheHangTimeout()
    {
        var beat = T0.AddMinutes(10);
        Assert.Equal(SupervisorPolicy.ChildPhase.Beating, SupervisorPolicy.Phase(T0, beat, beat.AddSeconds(29)));
        Assert.Equal(SupervisorPolicy.ChildPhase.Hung, SupervisorPolicy.Phase(T0, beat, beat.AddSeconds(31)));
        Assert.True(SupervisorPolicy.IsHung(beat, beat.AddSeconds(31)));
    }

    [Fact]
    public void AStartupHangGoesThroughTheRestartPolicyWithBackoffAndTheCrashLoopCap()
    {
        var policy = new SupervisorPolicy(maxCrashesInWindow: 3, crashWindowMinutes: 10, stableRunMinutes: 5);
        var first = policy.OnExit(1, killedForHang: true, ranFor: SupervisorPolicy.StartupDeadline, T0);
        Assert.Equal(SupervisorAction.Restart, first.Action);
        Assert.True(first.Delay > TimeSpan.Zero);                                                      // a hang is a crash for the backoff
        var second = policy.OnExit(1, true, SupervisorPolicy.StartupDeadline, T0.AddMinutes(3));
        Assert.Equal(SupervisorAction.Restart, second.Action);
        Assert.True(second.Delay > first.Delay);
        policy.OnExit(1, true, SupervisorPolicy.StartupDeadline, T0.AddMinutes(6));
        var fourth = policy.OnExit(1, true, SupervisorPolicy.StartupDeadline, T0.AddMinutes(9));
        Assert.Equal(SupervisorAction.GiveUp, fourth.Action);                                          // the loop limit still applies
    }

    [Fact]
    public void AChildThatExitsBeforeItsFirstBeatIsACrashNotAHang()
    {
        var policy = new SupervisorPolicy();
        var verdict = policy.OnExit(-1073741819, killedForHang: false, ranFor: TimeSpan.FromSeconds(4), T0);
        Assert.Equal(SupervisorAction.Restart, verdict.Action);
        Assert.Equal(SupervisorPolicy.ChildPhase.Starting, SupervisorPolicy.Phase(T0, null, T0.AddSeconds(4)));   // it was still starting when it died
    }
}
