using Patterns.Core.RigDay;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Rig day's moments: named once each, timed, and the sweep's maths a canvas draws by.</summary>
public class CelebrationTests
{
    private static readonly DateTime T0 = new(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AMomentHasALengthAPhaseAndAnEnd()
    {
        var locked = Celebration.For(CelebrationKind.NodeLocked, "node 3 locked", T0);
        var aligned = Celebration.For(CelebrationKind.ProjectorAligned, "aligned — 25 of 25", T0);
        Assert.Equal(Celebration.Brief, locked.Length);
        Assert.Equal(Celebration.Full, aligned.Length);
        Assert.Equal(0, aligned.Phase(T0));
        Assert.Equal(0.5, aligned.Phase(T0.AddSeconds(2)), 3);
        Assert.Equal(1, aligned.Phase(T0.AddSeconds(9)));
        Assert.False(aligned.IsOver(T0.AddSeconds(3.9)));
        Assert.True(aligned.IsOver(T0.AddSeconds(4)));
        Assert.Equal(0, aligned.Phase(T0.AddSeconds(-1)));                              // never negative
        Assert.Equal("LOCKED", locked.Chip);
        Assert.Equal("ALIGNED", aligned.Chip);
        Assert.Equal("JOIN CLEAR", Celebration.For(CelebrationKind.JoinCleared, "", T0).Chip);
        Assert.Equal("BOSS CLEARED", Celebration.For(CelebrationKind.BossCleared, "", T0).Chip);
        Assert.Equal("SHOW READY", Celebration.For(CelebrationKind.ShowReady, "", T0).Chip);
    }

    [Fact]
    public void TheSweepGoesOutFastAndFadesToNothing()
    {
        var (r0, a0) = Celebration.Ring(0);
        var (r1, a1) = Celebration.Ring(0.5);
        var (r2, a2) = Celebration.Ring(1);
        Assert.Equal(0, r0, 6);
        Assert.Equal(255, a0);
        Assert.True(r1 > 0.5, "out fast: past halfway by the middle");                  // eased: 1 - 0.5^3 = 0.875
        Assert.True(a1 > 0 && a1 < 128, "fading: dimmer than half by the middle");
        Assert.Equal(1, r2, 6);
        Assert.Equal(0, a2);
        Assert.Equal(Celebration.Ring(1), Celebration.Ring(7));                          // clamped
        Assert.Equal(Celebration.Ring(0), Celebration.Ring(-1));
    }

    private static ShowReadyScore Score(int done)
        => new(Enumerable.Range(0, 5).Select(i => new ReadyStep($"step {i}", i < done, true, "")).ToList());

    private static QuestLevel Level(string name, bool cleared, bool boss = false) => new(name, new[] { "a", "b" }, cleared, boss, "");

    [Fact]
    public void TheTrackNamesEachTransitionOnceAndNeverTheStartingState()
    {
        var track = new CelebrationTrack();
        // A rig that is ready when the games come on: the baseline, celebrated by nobody.
        Assert.Null(track.Observe(Score(5), new[] { Level("Join 1", true), Level("Middle", true, boss: true) }, T0));
        Assert.Null(track.Observe(Score(5), new[] { Level("Join 1", true), Level("Middle", true, boss: true) }, T0.AddSeconds(2)));

        // The bar empties and fills again: SHOW READY, once.
        Assert.Null(track.Observe(Score(4), Array.Empty<QuestLevel>(), T0.AddSeconds(4)));
        var full = track.Observe(Score(5), Array.Empty<QuestLevel>(), T0.AddSeconds(6));
        Assert.NotNull(full);
        Assert.Equal(CelebrationKind.ShowReady, full!.Kind);
        Assert.Contains("every step clear", full.Words);
        Assert.Null(track.Observe(Score(5), Array.Empty<QuestLevel>(), T0.AddSeconds(8)));

        // A join clears: its level, once; a second join later, its own; the boss, the boss's.
        var joinA = track.Observe(Score(5), new[] { Level("Join 1", true), Level("Join 2", false), Level("Middle", false, boss: true) }, T0.AddSeconds(10));
        Assert.Equal(CelebrationKind.JoinCleared, joinA!.Kind);
        Assert.Contains("Join 1", joinA.Words);
        Assert.Null(track.Observe(Score(5), new[] { Level("Join 1", true), Level("Join 2", false), Level("Middle", false, boss: true) }, T0.AddSeconds(12)));
        var joinB = track.Observe(Score(5), new[] { Level("Join 1", true), Level("Join 2", true), Level("Middle", false, boss: true) }, T0.AddSeconds(14));
        Assert.Contains("Join 2", joinB!.Words);
        var boss = track.Observe(Score(5), new[] { Level("Join 1", true), Level("Join 2", true), Level("Middle", true, boss: true) }, T0.AddSeconds(16));
        Assert.Equal(CelebrationKind.BossCleared, boss!.Kind);
        // The bar filling and the boss clearing in the same reading: the bar wins, the boss is said next time it is new — which it is not, so never twice.
        var track2 = new CelebrationTrack();
        track2.Observe(Score(4), new[] { Level("Middle", false, boss: true) }, T0);
        var both = track2.Observe(Score(5), new[] { Level("Middle", true, boss: true) }, T0.AddSeconds(2));
        Assert.Equal(CelebrationKind.ShowReady, both!.Kind);
        Assert.Null(track2.Observe(Score(5), new[] { Level("Middle", true, boss: true) }, T0.AddSeconds(4)));
    }
}
