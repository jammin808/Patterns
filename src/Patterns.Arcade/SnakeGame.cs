using System.Globalization;
using Patterns.Core.Arcade;
using Patterns.Rendering;
using SkiaSharp;

namespace Patterns.Arcade;

/// <summary>
/// Snake on a 48×27 board: up to four snakes by colour, three pieces of food, a snake that grows by
/// three at each; a wall, itself or another snake ends it. Alone it is the classic; with rivals the
/// last one moving wins, or the longest score when the two minutes are up.
/// </summary>
public sealed class SnakeGame : IArcadeGame
{
    public const int Cols = 48;
    public const int Rows = 27;
    private const float Cell = ArcadeStage.Width / Cols;      // 40 units — 48×40 by 27×40 fills the stage exactly
    private const int Food = 3;
    private const int Grow = 3;
    private const long MatchSteps = 120 * ArcadeEngine.StepHz; // two minutes for a race

    private static readonly int[] Dx = { 0, 1, 0, -1 };          // up, right, down, left
    private static readonly int[] Dy = { -1, 0, 1, 0 };
    private static readonly SKColor[] Colours =
    {
        new(0x7C, 0xF5, 0xC8), new(0xFF, 0x9E, 0x58), new(0x5F, 0xD0, 0xFF), new(0xFF, 0x6E, 0xC7),
    };

    private sealed class Snake
    {
        public readonly int[] X = new int[Cols * Rows];
        public readonly int[] Y = new int[Cols * Rows];
        public int Head;        // index of the head in the ring
        public int Len;
        public int Dir, NextDir;
        public bool Alive, Human, Seated;
        public int Score, Grow;
        public int TailPrevX, TailPrevY;
        public int Cx(int fromHead) => X[(Head - fromHead + X.Length) % X.Length];
        public int Cy(int fromHead) => Y[(Head - fromHead + Y.Length) % Y.Length];
    }

    private readonly Snake[] _snakes = { new(), new(), new(), new() };
    private readonly int[] _fx = new int[Food];
    private readonly int[] _fy = new int[Food];
    private readonly int[] _nextX = new int[4];
    private readonly int[] _nextY = new int[4];
    private readonly bool[] _dies = new bool[4];
    private readonly int[] _scores = new int[4];
    private int _seats;
    private int _stepsPerMove = 10;
    private int _counter;
    private long _steps;
    private long _pulse;
    private float _diff = 0.5f;
    private ArcadeRandom _rng = new(1);
    private bool _over;
    private int _winner;

    public string Id => "snake";
    public string Title => "SNAKE";
    public string Blurb => "▲ ▼ ◀ ▶ steer · eat to grow · walls and tails end it";
    public int MaxPlayers => 4;
    public int Seats => _seats;
    public bool IsOver => _over;
    public int Winner => _winner;
    public IReadOnlyList<int> Scores => _scores;
    public bool IsHuman(int seat) => seat >= 0 && seat < _seats && _snakes[seat].Human;
    public int StepsPerMove => _stepsPerMove;

    public string Words
    {
        get
        {
            if (_seats <= 1) return $"SNAKE — {_scores[0]}";
            var parts = new string[_seats];
            for (var i = 0; i < _seats; i++) parts[i] = _scores[i].ToString(CultureInfo.InvariantCulture);
            return "SNAKE — " + string.Join(" : ", parts);
        }
    }

    public int DifficultyHint
    {
        get
        {
            if (!_over) return 0;
            var humans = 0;
            var houses = 0;
            for (var i = 0; i < _seats; i++) { if (_snakes[i].Human) humans++; else houses++; }
            if (humans == 0 || houses == 0) return _seats == 1 && _scores[0] >= 200 ? 1 : 0;
            if (_winner == 0) return 0;
            return _snakes[_winner - 1].Human ? 1 : -1;
        }
    }

    public void Reset(ReadOnlySpan<bool> humans, ArcadeRandom rng, float difficulty)
    {
        _rng = rng;
        _diff = Math.Clamp(difficulty, 0f, 1f);
        var highest = -1;
        for (var i = 0; i < humans.Length && i < 4; i++) if (humans[i]) highest = i;
        _seats = highest < 0 ? 4 : highest + 1;                   // the house alone plays four; people take the seats up to the last pad pressed
        _stepsPerMove = Math.Clamp((int)MathF.Round(14f - 7f * _diff), 6, 16);
        _counter = 0;
        _steps = 0;
        _over = false;
        _winner = 0;
        for (var i = 0; i < 4; i++)
        {
            var s = _snakes[i];
            s.Seated = i < _seats;
            s.Human = i < humans.Length && humans[i];
            s.Alive = s.Seated;
            s.Score = 0;
            s.Grow = 0;
            _scores[i] = 0;
            if (!s.Seated) continue;
            // Four lanes, each snake in its own row facing right, four long.
            var row = _seats == 1 ? Rows / 2 : 4 + i * ((Rows - 8) / 3);
            s.Len = 4;
            s.Head = 3;
            for (var k = 0; k < 4; k++) { s.X[k] = 6 + k; s.Y[k] = row; }
            s.Dir = s.NextDir = 1;
            s.TailPrevX = 5;
            s.TailPrevY = row;
        }
        for (var f = 0; f < Food; f++) PlaceFood(f);
    }

    private bool Occupied(int x, int y)
    {
        for (var i = 0; i < 4; i++)
        {
            var s = _snakes[i];
            if (!s.Alive) continue;
            for (var k = 0; k < s.Len; k++) if (s.Cx(k) == x && s.Cy(k) == y) return true;
        }
        return false;
    }

    private void PlaceFood(int f)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var x = _rng.Next(Cols);
            var y = _rng.Next(Rows);
            if (Occupied(x, y)) continue;
            var onFood = false;
            for (var o = 0; o < Food; o++) if (o != f && _fx[o] == x && _fy[o] == y) onFood = true;
            if (onFood) continue;
            _fx[f] = x;
            _fy[f] = y;
            return;
        }
        _fx[f] = -1;
        _fy[f] = -1;
    }

    public void Step(ReadOnlySpan<PadState> pads, float dt, long step)
    {
        _pulse = step;
        if (_over) return;
        _steps++;
        for (var i = 0; i < _seats; i++)
        {
            var s = _snakes[i];
            if (!s.Alive) continue;
            if (s.Human)
            {
                var pad = i < pads.Length ? pads[i] : default;
                var want = pad.Has(PadButtons.Up) ? 0 : pad.Has(PadButtons.Right) ? 1 : pad.Has(PadButtons.Down) ? 2 : pad.Has(PadButtons.Left) ? 3 : -1;
                if (want >= 0 && want != (s.Dir + 2) % 4) s.NextDir = want;
            }
            else
            {
                s.NextDir = ChooseDir(s);
            }
        }
        _counter++;
        if (_counter >= _stepsPerMove)
        {
            _counter = 0;
            MoveAll();
        }
        var alive = 0;
        var last = -1;
        for (var i = 0; i < _seats; i++) if (_snakes[i].Alive) { alive++; last = i; }
        if (_seats == 1)
        {
            if (alive == 0) { _over = true; _winner = 0; }
        }
        else if (alive <= 1 || _steps >= MatchSteps)
        {
            _over = true;
            if (alive == 1) _winner = last + 1;
            else
            {
                var best = -1;
                var bestScore = -1;
                var tie = false;
                for (var i = 0; i < _seats; i++)
                {
                    if (_scores[i] > bestScore) { bestScore = _scores[i]; best = i; tie = false; }
                    else if (_scores[i] == bestScore) tie = true;
                }
                _winner = tie ? 0 : best + 1;
            }
        }
    }

    private int ChooseDir(Snake s)
    {
        // Straight, left or right: the safe one that gets nearest the nearest food; a coin for a tie.
        var hx = s.Cx(0);
        var hy = s.Cy(0);
        var bestDir = s.Dir;
        var bestDist = int.MaxValue;
        var found = false;
        for (var turn = -1; turn <= 1; turn++)
        {
            var d = (s.Dir + turn + 4) % 4;
            var nx = hx + Dx[d];
            var ny = hy + Dy[d];
            if (nx < 0 || ny < 0 || nx >= Cols || ny >= Rows || Occupied(nx, ny)) continue;
            var dist = int.MaxValue;
            for (var f = 0; f < Food; f++)
            {
                if (_fx[f] < 0) continue;
                var m = Math.Abs(_fx[f] - nx) + Math.Abs(_fy[f] - ny);
                if (m < dist) dist = m;
            }
            if (!found || dist < bestDist || (dist == bestDist && _rng.Chance(0.5)))
            {
                found = true;
                bestDist = dist;
                bestDir = d;
            }
        }
        return bestDir;
    }

    private void MoveAll()
    {
        for (var i = 0; i < _seats; i++)
        {
            var s = _snakes[i];
            _dies[i] = false;
            if (!s.Alive) continue;
            s.Dir = s.NextDir;
            _nextX[i] = s.Cx(0) + Dx[s.Dir];
            _nextY[i] = s.Cy(0) + Dy[s.Dir];
            if (_nextX[i] < 0 || _nextY[i] < 0 || _nextX[i] >= Cols || _nextY[i] >= Rows || Occupied(_nextX[i], _nextY[i])) _dies[i] = true;
        }
        // Two heads into one cell: both end.
        for (var i = 0; i < _seats; i++)
        {
            if (!_snakes[i].Alive) continue;
            for (var j = i + 1; j < _seats; j++)
            {
                if (!_snakes[j].Alive) continue;
                if (_nextX[i] == _nextX[j] && _nextY[i] == _nextY[j]) { _dies[i] = true; _dies[j] = true; }
            }
        }
        for (var i = 0; i < _seats; i++)
        {
            var s = _snakes[i];
            if (!s.Alive) continue;
            if (_dies[i]) { s.Alive = false; continue; }
            s.Head = (s.Head + 1) % s.X.Length;
            s.X[s.Head] = _nextX[i];
            s.Y[s.Head] = _nextY[i];
            if (s.Grow > 0) { s.Grow--; s.Len++; }
            else
            {
                s.TailPrevX = s.Cx(s.Len);   // the cell the tail just left
                s.TailPrevY = s.Cy(s.Len);
            }
            for (var f = 0; f < Food; f++)
            {
                if (_fx[f] == _nextX[i] && _fy[f] == _nextY[i])
                {
                    s.Grow += Grow;
                    s.Score += 10;
                    _scores[i] = s.Score;
                    PlaceFood(f);
                }
            }
        }
    }

    public void Draw(SKCanvas c, float alpha, PaintCache p)
    {
        c.Clear(new SKColor(0x0E, 0x11, 0x16));
        var grid = p.Stroke(new SKColor(0xFF, 0xFF, 0xFF, 0x0C));
        for (var x = 1; x < Cols; x++) c.DrawLine(x * Cell, 0, x * Cell, ArcadeStage.Height, grid);
        for (var y = 1; y < Rows; y++) c.DrawLine(0, y * Cell, ArcadeStage.Width, y * Cell, grid);
        var pulse = 12f + 3f * MathF.Sin(_pulse * 0.08f);
        var food = p.FillAA(new SKColor(0xFF, 0xC2, 0x4D));
        for (var f = 0; f < Food; f++)
        {
            if (_fx[f] < 0) continue;
            c.DrawCircle(_fx[f] * Cell + Cell / 2, _fy[f] * Cell + Cell / 2, pulse, food);
        }
        var t = Math.Clamp((_counter + alpha) / _stepsPerMove, 0f, 1f);
        for (var i = 0; i < _seats; i++)
        {
            var s = _snakes[i];
            var colour = s.Alive ? Colours[i] : Colours[i].WithAlpha(0x50);
            var body = p.FillAA(colour);
            for (var k = 0; k < s.Len; k++)
            {
                var cx = (float)s.Cx(k);
                var cy = (float)s.Cy(k);
                if (s.Alive && k == 0 && s.Len > 1) { cx = ArcadeText.Lerp(s.Cx(1), cx, t); cy = ArcadeText.Lerp(s.Cy(1), cy, t); }
                else if (s.Alive && k == s.Len - 1 && s.Grow == 0) { cx = ArcadeText.Lerp(s.TailPrevX, cx, t); cy = ArcadeText.Lerp(s.TailPrevY, cy, t); }
                c.DrawRoundRect(SKRect.Create(cx * Cell + 3, cy * Cell + 3, Cell - 6, Cell - 6), 8, 8, body);
            }
            ArcadeText.Draw(c, p, $"{(s.Human ? "P" + (i + 1) : "HOUSE")} {s.Score}", 24 + i * 300, 44, 30, colour, SKTextAlign.Left);
        }
    }
}
