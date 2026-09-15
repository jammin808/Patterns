using Patterns.Core.Geometry;
using Patterns.Core.Rendering;
using Patterns.Core.RigDay;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

public class RigDayTests
{
    [Fact]
    public void TheShowReadyBarFillsStepByStepAndSkipsWhatDoesNotApply()
    {
        var full = ShowReady.Score(new ReadyFacts(true, 3, 0, 4, 0.6, true, true, CheckLight.Green));
        Assert.Equal(5, full.Total);
        Assert.True(full.IsFull);
        Assert.Equal("show-ready 5/5 ▰▰▰▰▰", full.Words);
        Assert.StartsWith("all clear", full.Next);

        var cold = ShowReady.Score(new ReadyFacts(false, 0, 0, 0, null, false, false, null));
        Assert.Equal(2, cold.Done);                                                    // no joins, no projectors: those two count as done
        Assert.Equal("▰▰▱▱▱".Length, cold.Bar.Length);
        Assert.Contains(cold.Steps, s => s.Name == "Joins" && s.Done && !s.Applies && s.Words == "no joins to blend");
        Assert.Contains(cold.Steps, s => s.Name == "Calibration" && s.Done && !s.Applies);
        Assert.StartsWith("outputs closed", cold.Next);

        var rough = ShowReady.Score(new ReadyFacts(true, 2, 1, 2, 2.5, true, true, CheckLight.Amber));
        Assert.Equal(2, rough.Done);
        Assert.Contains(rough.Steps, s => s.Name == "Joins" && !s.Done && s.Words.StartsWith("1 of 2 joins warn"));
        Assert.Contains(rough.Steps, s => s.Name == "Calibration" && !s.Done && s.Words.Contains("2.5 px") && s.Words.Contains("alignment game"));
        Assert.Contains(rough.Steps, s => s.Name == "Super-check" && s.Words.Contains("amber"));
        Assert.Contains(ShowReady.Score(new ReadyFacts(true, 0, 0, 1, null, false, true, null)).Steps, s => s.Name == "Calibration" && s.Words.Contains("no calibration applied"));
    }

    [Fact]
    public void TheAlignmentGameScoresEachNodeLocksWithinAPixelAndFillsTheLattice()
    {
        // A 3×3 lattice over 1920×1080: the solver wants the centre node 10 right and 6 up; the rest at rest.
        var target = new float[18];
        target[4 * 2] = 10;
        target[4 * 2 + 1] = -6;
        var game = new AlignmentGame("p1", "Left", 3, 3, 1920, 1080, target, new float[18]);
        Assert.Equal(9, game.NodeCount);
        Assert.Equal(8, game.LockedCount);
        Assert.Equal(4, game.Picked);                                                   // the first open node
        Assert.Equal(11.66f, game.Residual(4), 0.01f);
        Assert.Equal(11.66f, game.WorstResidual, 0.01f);
        Assert.False(game.IsDone);
        Assert.StartsWith("Left: node 5/9 · 11.7 px · 8 of 9 locked", game.Words);
        Assert.True(game.Next());                                                        // the only open node: it wraps to itself
        Assert.Equal(4, game.Picked);

        // A nudge is a move for the desk to apply; the mesh comes back; the node locks when it is within a pixel.
        var (node, dx, dy) = game.Nudge(40, 0);
        Assert.Equal((4, AlignmentGame.MaxStepPx, 0f), (node, dx, dy));
        var mesh = WarpGrid.Nudged(WarpGrid.Format(game.Current), 3, 3, node, 10, 0);
        game.SetCurrent(WarpGrid.Parse(mesh, 3, 3));
        Assert.Equal(6f, game.Residual(4), 0.01f);
        var snap = game.SnapMove();
        Assert.Equal((4, 0f, -6f), (snap.Node, MathF.Round(snap.Dx, 2), MathF.Round(snap.Dy, 2)));
        mesh = WarpGrid.Nudged(mesh, 3, 3, snap.Node, snap.Dx, snap.Dy);
        game.SetCurrent(WarpGrid.Parse(mesh, 3, 3));
        Assert.True(game.IsLocked(4));
        Assert.True(game.IsDone);
        Assert.Equal(1.0, game.Progress);
        Assert.Contains("every node within a pixel", game.Words);
        Assert.Contains("1 nudge", game.Words);

        // Two open nodes: Next and Prev walk them and skip the locked; a solver's mesh of another density is resampled.
        var t2 = new float[18];
        t2[0] = 5; t2[17] = -5;
        var g2 = new AlignmentGame("p1", "Left", 3, 3, 1920, 1080, t2, new float[18]);
        Assert.Equal(0, g2.Picked);
        Assert.True(g2.Next());
        Assert.Equal(8, g2.Picked);
        Assert.True(g2.Next());
        Assert.Equal(0, g2.Picked);
        Assert.True(g2.Prev());
        Assert.Equal(8, g2.Picked);
        g2.Pick(3);
        Assert.Equal(3, g2.Picked);
        var from = AlignmentGame.From("p2", "Right", 3, 3, 1920, 1080, "", WarpGrid.Format(new float[50]), 5, 5);
        Assert.Equal(9, from.NodeCount);
        Assert.True(from.IsDone);
        Assert.Equal(9, from.TargetNodes.Count);
    }

    [Fact]
    public void BlendQuestMakesALevelOfEveryJoinAndABossOfTheMiddle()
    {
        static BlendNote Warn(string t) => new(true, t);
        static BlendNote Fine(string t) => new(false, t);
        var row = new[]
        {
            new ArrangedScreen("a", RasterRect.Create(0, 0, 1920, 1080), true),
            new ArrangedScreen("b", RasterRect.Create(1720, 0, 1920, 1080), true),
            new ArrangedScreen("c", RasterRect.Create(5000, 0, 1920, 1080), true),   // apart: no join
        };
        var levels = BlendQuest.Levels(row, id => new[] { Fine("✓ join") }, id => id.ToUpperInvariant());
        var level = Assert.Single(levels);
        Assert.Equal("A ⟷ B", level.Name);
        Assert.True(level.Cleared);
        Assert.Contains("200 px", level.Words);
        Assert.Equal("Blend Quest: 1 of 1 join clear — every level clear", BlendQuest.Words(levels));
        var warned = BlendQuest.Levels(row, id => id == "b" ? new[] { Warn("⚠ a join fading on one side only") } : new[] { Fine("✓") }, id => id);
        Assert.False(warned[0].Cleared);
        Assert.Contains("fading on one side", warned[0].Words);
        Assert.Equal("Blend Quest: 0 of 1 join clear", BlendQuest.Words(warned));
        Assert.StartsWith("Blend Quest: no joins", BlendQuest.Words(Array.Empty<QuestLevel>()));

        var grid = new[]
        {
            new ArrangedScreen("tl", RasterRect.Create(0, 0, 1920, 1080), true),
            new ArrangedScreen("tr", RasterRect.Create(1720, 0, 1920, 1080), true),
            new ArrangedScreen("bl", RasterRect.Create(0, 880, 1920, 1080), true),
            new ArrangedScreen("br", RasterRect.Create(1720, 880, 1920, 1080), true),
        };
        var quest = BlendQuest.Levels(grid, _ => new[] { Fine("✓") }, id => id);
        Assert.Equal(4, quest.Count(l => !l.Boss));                                    // the four edges, not the diagonals
        var boss = Assert.Single(quest, l => l.Boss);
        Assert.Equal(4, boss.Members.Count);
        Assert.True(boss.Cleared);
        Assert.Contains("200×200 px middle", boss.Words);
        Assert.Equal("Blend Quest: 4 of 4 joins clear · the boss is down — every level clear", BlendQuest.Words(quest));
        var waiting = BlendQuest.Levels(grid, id => id == "br" ? new[] { Warn("⚠ widths differ") } : new[] { Fine("✓") }, id => id);
        Assert.Equal("Blend Quest: 2 of 4 joins clear · the boss waits", BlendQuest.Words(waiting));
    }

    [Fact]
    public void TheOnTimeStreakCountsGosWithinThePlansDrift()
    {
        var streak = new OnTimeStreak();
        Assert.Equal("", streak.Words);
        var tol = TimeSpan.FromSeconds(30);
        streak.Record(null, tol);                                                        // no plan: not counted
        Assert.Equal(0, streak.Counted);
        streak.Record(TimeSpan.FromSeconds(10), tol);
        streak.Record(TimeSpan.FromSeconds(-25), tol);
        streak.Record(TimeSpan.FromSeconds(30), tol);
        Assert.Equal("3 on time in a row · best 3", streak.Words);
        streak.Record(TimeSpan.FromSeconds(31), tol);
        Assert.Equal("streak reset · best 3 · 3 of 4 on time", streak.Words);
        streak.Record(TimeSpan.Zero, tol);
        Assert.Equal("1 on time in a row · best 3", streak.Words);
        streak.Reset();
        Assert.Equal("", streak.Words);
    }
}
