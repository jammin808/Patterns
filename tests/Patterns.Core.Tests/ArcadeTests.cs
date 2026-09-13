using Patterns.Core.Arcade;
using Patterns.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

public class ArcadeTests
{
    private static void Run(ArcadeEngine e, int steps)
    {
        for (var i = 0; i < steps; i++) e.StepOnce();
    }

    [Fact]
    public void TheCatalogueFindsAGameByIdTitleOrNumberAndMakesIt()
    {
        Assert.Equal(new[] { "pong", "snake", "breakout" }, ArcadeEngine.Catalogue.Select(g => g.Id));
        Assert.Equal("SNAKE", ArcadeEngine.Find("2")!.Title);
        Assert.Equal("pong", ArcadeEngine.Find("PONG")!.Id);
        Assert.Equal("breakout", ArcadeEngine.Find(" Breakout ")!.Id);
        Assert.Null(ArcadeEngine.Find("tetris"));
        Assert.Null(ArcadeEngine.Find(""));
        Assert.Null(ArcadeEngine.Find("4"));
        Assert.IsType<PongGame>(ArcadeEngine.Make("pong"));
        Assert.Null(ArcadeEngine.Make("chess"));
        Assert.Equal(PadButtons.A, PadState.ParseButton("fire"));
        Assert.Equal(PadButtons.Start, PadState.ParseButton("START"));
        Assert.Equal(PadButtons.None, PadState.ParseButton("select"));
    }

    [Fact]
    public void TheEngineStepsAtAFixedRateCapsAStallAndInterpolatesBetweenSteps()
    {
        var e = new ArcadeEngine();
        Assert.Equal(ArcadePhase.Idle, e.Phase);
        Assert.True(e.Start("pong", 1, seed: 7));
        Assert.Equal(ArcadePhase.Playing, e.Phase);
        Assert.Equal(1, e.Humans);
        Assert.Equal(7, e.Seed);

        // 1/60 s of wall time is two steps of 1/120; the half step left over is the frame's alpha.
        Assert.Equal(2, e.Advance(1.0 / 60));
        Assert.Equal(2, e.Step);
        Assert.Equal(0f, e.Alpha, 0.01f);
        Assert.Equal(0, e.Advance(ArcadeEngine.Dt * 0.5));
        Assert.Equal(0.5f, e.Alpha, 0.05f);
        // A stall of a second is capped at eight steps and the debt is dropped, never chased.
        Assert.Equal(ArcadeEngine.MaxStepsPerAdvance, e.Advance(1.0));
        Assert.Equal(0, e.Advance(0));
        Assert.True(e.Alpha is >= 0f and <= 1f);
    }

    [Fact]
    public void AMatchReplaysFromItsSeedAndItsPresses()
    {
        var a = new ArcadeEngine();
        a.Start("pong", 1, seed: 42);
        // A press pattern: up for a while, then down, then nothing — recorded at the steps it changed.
        for (var s = 0; s < 1800; s++)
        {
            if (s == 10) a.Press(0, PadButtons.Up, true);
            if (s == 300) { a.Press(0, PadButtons.Up, false); a.Press(0, PadButtons.Down, true); }
            if (s == 700) a.Press(0, PadButtons.Down, false);
            a.StepOnce();
        }
        var snapA = a.Snapshot();
        Assert.True(a.Recording.Count >= 3, a.Recording.Count.ToString());
        Assert.False(a.RecordingTruncated);

        var b = new ArcadeEngine();
        b.Replay("pong", 1, 42, a.Recording, 1800);
        var snapB = b.Snapshot();
        Assert.Equal(snapA.Scores, snapB.Scores);
        Assert.Equal(snapA.Step, snapB.Step);
        Assert.Equal(snapA.Words, snapB.Words);

        // Another seed is another match (the serve's angle, the house's error).
        var c = new ArcadeEngine();
        c.Replay("pong", 1, 43, a.Recording, 1800);
        Assert.True(c.Snapshot().Scores.Sum() + snapA.Scores.Sum() > 0);
    }

    [Fact]
    public void PongScoresWhenTheBallLeavesAndTheHouseWinsAtSeven()
    {
        var e = new ArcadeEngine();
        e.Start("pong", 1, seed: 3);
        var pong = (PongGame)e.Game!;
        Assert.True(pong.IsHuman(0));
        Assert.False(pong.IsHuman(1));
        // P1 holds the paddle at the top: the house scores every rally sooner or later.
        e.Press(0, PadButtons.Up, true);
        var steps = 0;
        while (!pong.IsOver && steps < 120 * 240) { e.StepOnce(); steps++; }
        Assert.True(pong.IsOver, $"over after {steps} steps: {pong.Words}");
        Assert.Equal(PongGame.WinScore, pong.Scores[1]);
        Assert.Equal(2, pong.Winner);
        Assert.Equal(ArcadePhase.Over, e.Phase);
        Assert.StartsWith("Game over — PONG — ", e.Words);
        Assert.EndsWith("— the house wins", e.Words);
        Assert.Equal(-1, pong.DifficultyHint);
        Assert.Equal(0.4f, e.Difficulty, 0.01f);      // the house won big: the next match is easier

        // Six seconds of the words, then the attract mode with the house on both paddles.
        Run(e, ArcadeEngine.OverHoldSteps + 1);
        Assert.Equal(ArcadePhase.Attract, e.Phase);
        Assert.Equal(0, e.Humans);
        Assert.False(e.Game!.IsHuman(0));
    }

    [Fact]
    public void StartOnAPadTakesASeatCountsDownAndPausesAMatch()
    {
        var e = new ArcadeEngine();
        var changes = 0;
        e.Changed += () => changes++;
        e.Attract("snake");
        Assert.Equal(ArcadePhase.Attract, e.Phase);
        Assert.Equal(4, e.Game!.Seats);                                   // the house plays four
        Run(e, 50);
        e.Press(1, PadButtons.Start, true);                                // the second pad presses START
        e.Press(1, PadButtons.Start, false);
        Assert.Equal(ArcadePhase.Joining, e.Phase);
        Assert.Contains("starting for P2 in 3", e.Words);
        e.Press(0, PadButtons.Start, true);                                // the first joins during the count
        Assert.True(e.Joined[0] && e.Joined[1]);
        Run(e, ArcadeEngine.JoinSteps + 1);
        Assert.Equal(ArcadePhase.Playing, e.Phase);
        Assert.Equal(2, e.Humans);
        Assert.Equal(2, e.Game.Seats);
        Assert.True(e.Game.IsHuman(0) && e.Game.IsHuman(1));
        // START during the match pauses; again resumes; a held START is one press.
        e.Press(0, PadButtons.Start, false);
        e.Press(0, PadButtons.Start, true);
        Assert.Equal(ArcadePhase.Paused, e.Phase);
        Assert.StartsWith("Paused — SNAKE", e.Words);
        var step = e.Step;
        Run(e, 10);
        Assert.Equal(step + 10, e.Step);                                   // steps count; the game does not move
        e.Press(0, PadButtons.Start, false);
        e.Press(0, PadButtons.Start, true);
        Assert.Equal(ArcadePhase.Playing, e.Phase);
        Assert.True(e.Pause() && e.Resume());
        Assert.False(e.Resume());
        e.Stop();
        Assert.Equal(ArcadePhase.Idle, e.Phase);
        Assert.Null(e.Game);
        Assert.True(changes >= 6);
    }

    [Fact]
    public void SnakeGrowsOnFoodAndEndsOnAWall()
    {
        var e = new ArcadeEngine();
        e.Start("snake", 1, seed: 11);
        var snake = (SnakeGame)e.Game!;
        Assert.Equal(1, snake.Seats);
        Assert.Equal("SNAKE — 0", snake.Words);
        // Heading right from column 9 on a 48-wide board: a wall in 38 moves, with nothing pressed.
        var moves = snake.StepsPerMove * 39;
        Run(e, moves);
        Assert.True(snake.IsOver, snake.Words);
        Assert.Equal(ArcadePhase.Over, e.Phase);
        Assert.Equal(0, snake.Winner);
    }

    [Fact]
    public void BreakoutServesOnAAndLosesALifeWhenTheBallIsMissed()
    {
        var e = new ArcadeEngine();
        e.Start("breakout", 1, seed: 5);
        var game = (BreakoutGame)e.Game!;
        Assert.Equal(3, game.Lives);
        Assert.Equal(BreakoutGame.Cols * BreakoutGame.Rows, game.BricksLeft);
        Run(e, 100);                                                       // the ball waits on the paddle
        Assert.Equal(3, game.Lives);
        e.Press(0, PadButtons.A, true);
        e.StepOnce();
        e.Press(0, PadButtons.A, false);
        // The paddle parked at the far left: the ball is lost sooner or later and a life goes.
        e.Press(0, PadButtons.Left, true);
        var steps = 0;
        while (game.Lives == 3 && steps < 120 * 30) { e.StepOnce(); steps++; }
        Assert.Equal(2, game.Lives);
        Assert.Contains("2 lives", game.Words);
        Assert.False(game.IsOver);
    }

    [Fact]
    public void TheHouseClearsBricksInBreakoutsAttractMode()
    {
        var e = new ArcadeEngine();
        e.Attract("breakout");
        var game = (BreakoutGame)e.Game!;
        Run(e, 120 * 40);
        Assert.True(game.BricksLeft < BreakoutGame.Cols * BreakoutGame.Rows || game.Level > 1, game.Words);
    }

    [Fact]
    public void TheBoardKeepsFiftyPerGameRanksAnEntryAndTakesInitials()
    {
        var board = new Leaderboard();
        var when = new DateTime(2026, 9, 13, 20, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, board.Add("pong", 7, "P1", when, "Gala"));
        Assert.Equal(1, board.Add("pong", 9, "P2", when.AddMinutes(1), "Gala"));
        Assert.Equal(2, board.Add("pong", 8, "P1", when.AddMinutes(2), "Gala"));
        Assert.Equal(1, board.Add("snake", 40, "P1", when, "Gala"));
        Assert.Equal(new[] { 9, 8, 7 }, board.Top("pong").Select(e => e.Score));
        Assert.True(board.Name(null, "abc"));
        Assert.Equal("ABC", board.Top("snake")[0].Name);
        Assert.False(board.Name("nope", "X"));
        Assert.False(board.Name(null, "  "));
        for (var i = 0; i < 60; i++) board.Add("breakout", i, "P1", when.AddSeconds(i), "Gala");
        Assert.Equal(Leaderboard.KeepPerGame, board.Entries.Count(e => e.Game == "breakout"));
        Assert.Equal(0, board.Add("breakout", 1, "P1", when.AddHours(1), "Gala"));   // below the fifty: not kept
        Assert.Equal(59, board.Top("breakout", 1)[0].Score);

        var back = Leaderboard.Parse(board.Json());
        Assert.Equal(board.Entries.Count, back.Entries.Count);
        Assert.Equal("ABC", back.Top("snake")[0].Name);
        Assert.Empty(Leaderboard.Parse("not json").Entries);
        Assert.Empty(Leaderboard.Parse("").Entries);
    }

    [Fact]
    public void EveryPhaseDrawsAtAnySizeWithoutAFault()
    {
        using var paints = new PaintCache();
        var e = new ArcadeEngine();
        e.TopScores = _ => new[] { new ScoreEntry("1", "pong", 7, "ABC", DateTime.UtcNow, "Gala") };
        foreach (var size in new[] { (640, 360), (1920, 1080), (3840, 1080), (300, 900) })
        {
            using var surface = SKSurface.Create(new SKImageInfo(size.Item1, size.Item2, SKColorType.Bgra8888, SKAlphaType.Premul));
            e.Render(surface.Canvas, size.Item1, size.Item2, paints);            // idle: the title
            foreach (var id in new[] { "pong", "snake", "breakout" })
            {
                e.Attract(id);
                Run(e, 30);
                e.Render(surface.Canvas, size.Item1, size.Item2, paints);        // attract with the board
                e.Press(0, PadButtons.Start, true);
                e.Press(0, PadButtons.Start, false);
                e.Render(surface.Canvas, size.Item1, size.Item2, paints);        // the count
                Run(e, ArcadeEngine.JoinSteps + 5);
                e.Render(surface.Canvas, size.Item1, size.Item2, paints);        // playing
                e.Pause();
                e.Render(surface.Canvas, size.Item1, size.Item2, paints);        // paused
            }
            using var image = surface.Snapshot();
            Assert.NotNull(image);
        }
        Assert.Equal(ArcadePhase.Paused, e.Phase);
    }
}
