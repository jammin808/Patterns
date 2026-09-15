using Patterns.Core.Arcade;
using Patterns.Rendering;
using SkiaSharp;

namespace Patterns.Arcade;

public enum ArcadePhase
{
    /// <summary>No game: the title card.</summary>
    Idle,
    /// <summary>The house plays itself and the board shows; START joins.</summary>
    Attract,
    /// <summary>Seats are being taken: a short count before the match.</summary>
    Joining,
    Playing,
    Paused,
    /// <summary>The match ended; the words hold, then the attract mode returns.</summary>
    Over,
}

/// <summary>What the engine is doing, for the status line and the wire.</summary>
public sealed record ArcadeSnapshot(ArcadePhase Phase, string GameId, string Title, int Seats, IReadOnlyList<int> Scores, IReadOnlyList<bool> Humans, long Step, long Seed, float Difficulty, string Words, int Winner);

/// <summary>
/// The arcade's engine — what game engines know, brought over and kept small: the world advances
/// in exact steps at 120 Hz whatever the frame rate (an accumulator, a cap so a stall never spirals),
/// the frame draws the world interpolated between the last two steps, input is sampled at the step
/// so a dropped frame never drops a press, every match is seeded and its presses recorded so it can
/// be replayed, and nothing is allocated in a step or a frame. The games are pure state machines in
/// 1920×1080 units; the engine fits them to the size it renders at and draws the words around them.
/// </summary>
public sealed class ArcadeEngine
{
    public const int StepHz = 120;
    public const float Dt = 1f / StepHz;
    public const int MaxStepsPerAdvance = 8;
    public const int MaxPads = 4;
    public const int JoinSteps = 3 * StepHz;
    public const int OverHoldSteps = 6 * StepHz;
    public const int RecordingCap = 200_000;

    public static readonly IReadOnlyList<ArcadeGameInfo> Catalogue = new[]
    {
        new ArcadeGameInfo("pong", "PONG", 2, new PongGame().Blurb),
        new ArcadeGameInfo("snake", "SNAKE", 4, new SnakeGame().Blurb),
        new ArcadeGameInfo("breakout", "BREAKOUT", 1, new BreakoutGame().Blurb),
    };

    private readonly PadState[] _pads = new PadState[MaxPads];
    private readonly PadButtons[] _recorded = new PadButtons[MaxPads];
    private readonly bool[] _joined = new bool[MaxPads];
    private readonly bool[] _startHeld = new bool[MaxPads];
    private readonly bool[] _humans = new bool[MaxPads];
    private readonly List<ArcadeInputEvent> _recording = new();
    private double _acc;
    private long _phaseStep;

    public ArcadePhase Phase { get; private set; } = ArcadePhase.Idle;
    public IArcadeGame? Game { get; private set; }
    public string GameId => Game?.Id ?? "";
    public long Step { get; private set; }
    public long Seed { get; private set; }
    public float Difficulty { get; set; } = 0.5f;
    public float Alpha { get; private set; }
    public IReadOnlyList<ArcadeInputEvent> Recording => _recording;
    public bool RecordingTruncated { get; private set; }
    public ReadOnlySpan<PadState> Pads => _pads;
    public ReadOnlySpan<bool> Joined => _joined;
    /// <summary>The board's best for the attract screen — the node's leaderboard behind it.</summary>
    public Func<string, IReadOnlyList<ScoreEntry>>? TopScores { get; set; }
    /// <summary>A match with people on the pads ended: the game with its scores, for the board.</summary>
    public event Action<IArcadeGame>? MatchEnded;
    /// <summary>The phase or the game changed — the status line, the pages.</summary>
    public event Action? Changed;

    public int Humans
    {
        get
        {
            var n = 0;
            for (var i = 0; i < MaxPads; i++) if (_humans[i]) n++;
            return n;
        }
    }

    public string Words => Phase switch
    {
        ArcadePhase.Idle => "Idle — no game. ARCADE START pong, or a number on the pad.",
        ArcadePhase.Attract => $"Attract: {Game?.Title} — the house plays; START joins.",
        ArcadePhase.Joining => $"{Game?.Title} — starting for {JoinedWords()} in {Math.Max(1, (JoinSteps - (Step - _phaseStep) + StepHz - 1) / StepHz)}…",
        ArcadePhase.Playing => Game?.Words ?? "",
        ArcadePhase.Paused => $"Paused — {Game?.Words}",
        ArcadePhase.Over => $"Game over — {Game?.Words}{WinnerWords()}",
        _ => "",
    };

    private string JoinedWords()
    {
        var parts = new List<string>();
        for (var i = 0; i < MaxPads; i++) if (_joined[i]) parts.Add("P" + (i + 1));
        return parts.Count == 0 ? "the house" : string.Join(" · ", parts);
    }

    private string WinnerWords()
    {
        if (Game is null || Game.Winner <= 0) return "";
        var seat = Game.Winner - 1;
        return Game.IsHuman(seat) ? $" — P{Game.Winner} wins" : " — the house wins";
    }

    /// <summary>A game by its id, its title or its number in the catalogue.</summary>
    public static ArcadeGameInfo? Find(string? word)
    {
        var w = (word ?? "").Trim();
        if (w.Length == 0) return null;
        if (int.TryParse(w, out var n) && n >= 1 && n <= Catalogue.Count) return Catalogue[n - 1];
        return Catalogue.FirstOrDefault(g => g.Id.Equals(w, StringComparison.OrdinalIgnoreCase) || g.Title.Equals(w, StringComparison.OrdinalIgnoreCase));
    }

    public static IArcadeGame? Make(string id) => id.ToLowerInvariant() switch
    {
        "pong" => new PongGame(),
        "snake" => new SnakeGame(),
        "breakout" => new BreakoutGame(),
        _ => null,
    };

    /// <summary>Straight into a match: <paramref name="players"/> seats are people (P1 first); the rest is the house. A seed makes it a replayable match.</summary>
    public bool Start(string gameId, int players, long? seed = null)
    {
        var info = Find(gameId);
        if (info is null) return false;
        var game = Make(info.Id)!;
        Game = game;
        for (var i = 0; i < MaxPads; i++) { _joined[i] = i < Math.Clamp(players, 0, info.MaxPlayers); _humans[i] = _joined[i]; }
        BeginMatch(seed);
        return true;
    }

    /// <summary>The house plays itself: the attract mode of the given game, or the one in hand, or the first in the catalogue.</summary>
    public bool Attract(string? gameId = null)
    {
        var info = Find(gameId) ?? (Game is null ? Catalogue[0] : Find(Game.Id)) ?? Catalogue[0];
        if (Game is null || Game.Id != info.Id) Game = Make(info.Id)!;
        for (var i = 0; i < MaxPads; i++) { _joined[i] = false; _humans[i] = false; }
        Seed = NewSeed();
        Game.Reset(_humans, new ArcadeRandom(Seed), Difficulty);
        _recording.Clear();
        RecordingTruncated = false;
        SetPhase(ArcadePhase.Attract);
        return true;
    }

    public void Stop()
    {
        Game = null;
        for (var i = 0; i < MaxPads; i++) { _joined[i] = false; _humans[i] = false; }
        SetPhase(ArcadePhase.Idle);
    }

    public bool Pause()
    {
        if (Phase != ArcadePhase.Playing) return false;
        SetPhase(ArcadePhase.Paused);
        return true;
    }

    public bool Resume()
    {
        if (Phase != ArcadePhase.Paused) return false;
        SetPhase(ArcadePhase.Playing);
        return true;
    }

    /// <summary>A button on a pad went down or up. START joins a seat before a match, pauses and resumes during one.</summary>
    public void Press(int player, PadButtons button, bool down)
    {
        if (player < 0 || player >= MaxPads || button == PadButtons.None) return;
        if (down) _pads[player].Buttons |= button; else _pads[player].Buttons &= ~button;
        if (button == PadButtons.Start) StartEdge(player, down);
    }

    /// <summary>A pad's whole state at once — an XInput pad polled at the step.</summary>
    public void SetPad(int player, PadButtons buttons)
    {
        if (player < 0 || player >= MaxPads) return;
        var was = _pads[player].Buttons;
        _pads[player].Buttons = buttons;
        var startNow = (buttons & PadButtons.Start) != 0;
        var startWas = (was & PadButtons.Start) != 0;
        if (startNow != startWas) StartEdge(player, startNow);
    }

    private void StartEdge(int player, bool down)
    {
        if (!down) { _startHeld[player] = false; return; }
        if (_startHeld[player]) return;
        _startHeld[player] = true;
        switch (Phase)
        {
            case ArcadePhase.Idle:
                Game ??= Make(Catalogue[0].Id);
                goto case ArcadePhase.Attract;
            case ArcadePhase.Attract:
            case ArcadePhase.Over:
                if (Game is null) return;
                for (var i = 0; i < MaxPads; i++) _joined[i] = false;
                _joined[Math.Min(player, Game.MaxPlayers - 1)] = true;
                SetPhase(ArcadePhase.Joining);
                break;
            case ArcadePhase.Joining:
                if (Game is not null && player < Game.MaxPlayers) _joined[player] = true;
                break;
            case ArcadePhase.Playing:
                SetPhase(ArcadePhase.Paused);
                break;
            case ArcadePhase.Paused:
                SetPhase(ArcadePhase.Playing);
                break;
        }
    }

    private void BeginMatch(long? seed)
    {
        if (Game is null) return;
        for (var i = 0; i < MaxPads; i++) _humans[i] = _joined[i];
        Seed = seed ?? NewSeed();
        Game.Reset(_humans, new ArcadeRandom(Seed), Difficulty);
        _recording.Clear();
        RecordingTruncated = false;
        for (var i = 0; i < MaxPads; i++) _recorded[i] = PadButtons.None;
        SetPhase(ArcadePhase.Playing);
    }

    private static long NewSeed() => DateTime.UtcNow.Ticks ^ Environment.TickCount64;

    private void SetPhase(ArcadePhase phase)
    {
        Phase = phase;
        _phaseStep = Step;
        Changed?.Invoke();
    }

    /// <summary>Wall time went by: the steps it holds, at most the cap; the rest waits. The frame's interpolation follows.</summary>
    public int Advance(double elapsedSeconds)
    {
        const double stepSeconds = 1.0 / StepHz;
        _acc += Math.Clamp(elapsedSeconds, 0, 0.25);
        var steps = 0;
        // A hair of slack: 1/60 s is two steps of 1/120, not one and a rounding error.
        while (_acc >= stepSeconds - 1e-9 && steps < MaxStepsPerAdvance)
        {
            StepOnce();
            _acc -= stepSeconds;
            steps++;
        }
        if (_acc < 0) _acc = 0;
        if (steps == MaxStepsPerAdvance && _acc > stepSeconds) _acc = 0;   // a stall: drop the debt rather than chase it
        Alpha = Math.Clamp((float)(_acc / stepSeconds), 0f, 1f);
        return steps;
    }

    /// <summary>Exactly one step — the tests' clock, and the replay's.</summary>
    public void StepOnce()
    {
        Step++;
        switch (Phase)
        {
            case ArcadePhase.Playing:
                Record();
                Game!.Step(_pads, Dt, Step);
                if (Game.IsOver)
                {
                    if (Humans > 0)
                    {
                        Difficulty = Math.Clamp(Difficulty + Game.DifficultyHint * 0.1f, 0.1f, 1f);
                        MatchEnded?.Invoke(Game);
                    }
                    SetPhase(ArcadePhase.Over);
                }
                break;
            case ArcadePhase.Attract:
                Game!.Step(_pads, Dt, Step);
                if (Game.IsOver) Attract(Game.Id);
                break;
            case ArcadePhase.Joining:
                if (Step - _phaseStep >= JoinSteps) BeginMatch(null);
                break;
            case ArcadePhase.Over:
                if (Step - _phaseStep >= OverHoldSteps) Attract(Game?.Id);
                break;
        }
    }

    private void Record()
    {
        for (var i = 0; i < MaxPads; i++)
        {
            if (_pads[i].Buttons == _recorded[i]) continue;
            _recorded[i] = _pads[i].Buttons;
            if (_recording.Count >= RecordingCap) { RecordingTruncated = true; continue; }
            _recording.Add(new ArcadeInputEvent(Step, (byte)i, _pads[i].Buttons));
        }
    }

    /// <summary>The same match again from its seed and its presses: the state after <paramref name="steps"/> steps is the state it was.</summary>
    public void Replay(string gameId, int players, long seed, IReadOnlyList<ArcadeInputEvent> presses, long steps)
    {
        if (!Start(gameId, players, seed)) return;
        var next = 0;
        for (var i = 0; i < MaxPads; i++) _pads[i].Buttons = PadButtons.None;
        for (long s = 0; s < steps; s++)
        {
            while (next < presses.Count && presses[next].Step <= Step + 1)
            {
                _pads[presses[next].Player].Buttons = presses[next].Buttons;
                next++;
            }
            StepOnce();
            if (Phase != ArcadePhase.Playing) break;
        }
    }

    public ArcadeSnapshot Snapshot()
    {
        var humans = new bool[MaxPads];
        Array.Copy(_humans, humans, MaxPads);
        return new ArcadeSnapshot(Phase, GameId, Game?.Title ?? "", Game?.Seats ?? 0, Game?.Scores ?? Array.Empty<int>(), humans, Step, Seed, Difficulty, Words, Game?.Winner ?? 0);
    }

    /// <summary>The picture at any size: the game's stage fitted and centred, the words of the phase over it.</summary>
    public void Render(SKCanvas c, int width, int height, PaintCache p)
    {
        c.Clear(new SKColor(0x08, 0x09, 0x0C));
        if (width < 1 || height < 1) return;
        var scale = MathF.Min(width / ArcadeStage.Width, height / ArcadeStage.Height);
        c.Save();
        c.Translate((width - ArcadeStage.Width * scale) / 2f, (height - ArcadeStage.Height * scale) / 2f);
        c.Scale(scale);
        c.ClipRect(SKRect.Create(0, 0, ArcadeStage.Width, ArcadeStage.Height));
        var game = Game;
        if (game is null || Phase == ArcadePhase.Idle) DrawTitle(c, p);
        else
        {
            game.Draw(c, Alpha, p);
            DrawWords(c, p, game);
        }
        c.Restore();
    }

    private void DrawTitle(SKCanvas c, PaintCache p)
    {
        c.Clear(new SKColor(0x0B, 0x0C, 0x10));
        ArcadeText.Draw(c, p, "PATTERNS ARCADE", ArcadeStage.Width / 2, 380, 120, new SKColor(0x5F, 0xD0, 0xFF));
        var y = 520f;
        for (var i = 0; i < Catalogue.Count; i++)
        {
            ArcadeText.Draw(c, p, $"{i + 1}   {Catalogue[i].Title}", ArcadeStage.Width / 2, y, 56, new SKColor(0xFF, 0xFF, 0xFF, 0xD0));
            y += 80;
        }
        ArcadeText.Draw(c, p, "a number on the pad, or ARCADE START <game> on the wire", ArcadeStage.Width / 2, 900, 30, new SKColor(0xFF, 0xFF, 0xFF, 0x70), bold: false);
    }

    private void DrawWords(SKCanvas c, PaintCache p, IArcadeGame game)
    {
        var cx = ArcadeStage.Width / 2;
        var cy = ArcadeStage.Height / 2;
        var white = SKColors.White;
        var faint = new SKColor(0xFF, 0xFF, 0xFF, 0x90);
        switch (Phase)
        {
            case ArcadePhase.Attract:
            {
                Veil(c, p, 0x70);
                ArcadeText.Draw(c, p, game.Title, cx, cy - 120, 150, new SKColor(0x5F, 0xD0, 0xFF));
                ArcadeText.Draw(c, p, game.Blurb, cx, cy - 40, 34, faint, bold: false);
                if ((Step / (StepHz / 2)) % 2 == 0) ArcadeText.Draw(c, p, "PRESS START", cx, cy + 80, 64, white);
                var top = TopScores?.Invoke(game.Id);
                if (top is { Count: > 0 })
                {
                    ArcadeText.Draw(c, p, "BEST", cx, cy + 190, 30, faint);
                    var y = cy + 240;
                    for (var i = 0; i < top.Count && i < 5; i++)
                    {
                        ArcadeText.Draw(c, p, $"{i + 1}  {top[i].Name,-6} {top[i].Score,6}", cx, y, 34, faint, bold: false);
                        y += 42;
                    }
                }
                break;
            }
            case ArcadePhase.Joining:
            {
                Veil(c, p, 0x60);
                var left = Math.Max(1, (JoinSteps - (Step - _phaseStep) + StepHz - 1) / StepHz);
                ArcadeText.Draw(c, p, $"STARTING IN {left}", cx, cy - 20, 110, white);
                ArcadeText.Draw(c, p, JoinedWords() + (game.MaxPlayers > 1 ? " — START on another pad joins" : ""), cx, cy + 70, 40, faint, bold: false);
                break;
            }
            case ArcadePhase.Paused:
                Veil(c, p, 0x80);
                ArcadeText.Draw(c, p, "PAUSED", cx, cy, 130, white);
                break;
            case ArcadePhase.Over:
            {
                Veil(c, p, 0x80);
                ArcadeText.Draw(c, p, "GAME OVER", cx, cy - 40, 130, white);
                var who = game.Winner > 0 ? (game.IsHuman(game.Winner - 1) ? $"P{game.Winner} WINS" : "THE HOUSE WINS") : game.Words;
                ArcadeText.Draw(c, p, who, cx, cy + 60, 56, new SKColor(0x5F, 0xD0, 0xFF));
                ArcadeText.Draw(c, p, "START plays again", cx, cy + 130, 34, faint, bold: false);
                break;
            }
        }
    }

    private static void Veil(SKCanvas c, PaintCache p, byte alpha)
        => c.DrawRect(SKRect.Create(0, 0, ArcadeStage.Width, ArcadeStage.Height), p.Fill(new SKColor(0, 0, 0, alpha)));
}
