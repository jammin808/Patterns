using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Arcade;

/// <summary>Breakout: six rows of bricks worth more the higher they sit, a paddle that narrows and a ball that quickens with each level, three lives.</summary>
public sealed class BreakoutGame : IArcadeGame
{
    private const float W = ArcadeStage.Width;
    private const float H = ArcadeStage.Height;
    public const int Cols = 14;
    public const int Rows = 6;
    private const float BrickW = 120f;
    private const float BrickH = 40f;
    private const float Gap = 8f;
    private const float BandTop = 140f;
    private const float Left = (W - (Cols * (BrickW + Gap) - Gap)) / 2;
    private const float PadY = 1000f;
    private const float PadH = 22f;
    private const float PadSpeed = 1200f;
    private const float Ball = 18f;

    private static readonly int[] RowPoints = { 60, 50, 40, 30, 20, 10 };
    private static readonly SKColor[] RowColours =
    {
        new(0xFF, 0x5C, 0x7A), new(0xFF, 0x9E, 0x58), new(0xFF, 0xC2, 0x4D), new(0x7C, 0xF5, 0xC8), new(0x5F, 0xD0, 0xFF), new(0xB1, 0x8C, 0xFF),
    };

    private readonly bool[] _alive = new bool[Cols * Rows];
    private readonly int[] _scores = new int[1];
    private int _bricksLeft;
    private float _px, _pxPrev, _padW;
    private float _bx, _by, _bxPrev, _byPrev, _vx, _vy;
    private bool _held;
    private int _serveWait;
    private int _lives, _level;
    private bool _human;
    private float _diff = 0.5f;
    private float _speed;
    private ArcadeRandom _rng = new(1);
    private bool _over;
    private int _levelsCleared;

    public string Id => "breakout";
    public string Title => "BREAKOUT";
    public string Blurb => "◀ ▶ move · A serves · three lives · the top rows pay most";
    public int MaxPlayers => 1;
    public int Seats => 1;
    public bool IsOver => _over;
    public int Winner => 0;
    public IReadOnlyList<int> Scores => _scores;
    public bool IsHuman(int seat) => seat == 0 && _human;
    public string Words => $"BREAKOUT — {_scores[0]} · level {_level} · {_lives} {(_lives == 1 ? "life" : "lives")}";
    public int DifficultyHint => !_over || !_human ? 0 : _levelsCleared >= 1 ? 1 : _scores[0] < 150 ? -1 : 0;
    public int Lives => _lives;
    public int Level => _level;
    public int BricksLeft => _bricksLeft;

    public void Reset(ReadOnlySpan<bool> humans, ArcadeRandom rng, float difficulty)
    {
        _rng = rng;
        _diff = Math.Clamp(difficulty, 0f, 1f);
        _human = humans.Length > 0 && humans[0];
        _scores[0] = 0;
        _lives = 3;
        _level = 1;
        _levelsCleared = 0;
        _over = false;
        _padW = 220f;
        _speed = 700f + 400f * _diff;
        _px = _pxPrev = W / 2;
        Build();
        Hold();
    }

    private void Build()
    {
        for (var i = 0; i < _alive.Length; i++) _alive[i] = true;
        _bricksLeft = _alive.Length;
    }

    private void Hold()
    {
        _held = true;
        _serveWait = 90;
        _vx = _vy = 0;
        _bx = _bxPrev = _px;
        _by = _byPrev = PadY - Ball / 2 - 1;
    }

    public void Step(ReadOnlySpan<PadState> pads, float dt, long step)
    {
        if (_over) return;
        var pad = pads.Length > 0 ? pads[0] : default;
        _pxPrev = _px;
        if (_human)
        {
            var dx = (pad.Has(PadButtons.Right) ? 1f : 0f) - (pad.Has(PadButtons.Left) ? 1f : 0f);
            _px += dx * PadSpeed * dt;
        }
        else
        {
            // The house keeps the paddle under the ball, a little late at low difficulty.
            var target = _held ? W / 2 : _bx;
            var speed = PadSpeed * (0.5f + 0.5f * _diff);
            var delta = target - _px;
            if (MathF.Abs(delta) > 4f) _px += MathF.Sign(delta) * MathF.Min(MathF.Abs(delta), speed * dt);
        }
        _px = Math.Clamp(_px, _padW / 2, W - _padW / 2);

        _bxPrev = _bx;
        _byPrev = _by;
        if (_held)
        {
            _bx = _px;
            _by = PadY - Ball / 2 - 1;
            if (_serveWait > 0) _serveWait--;
            var serve = _human ? pad.Has(PadButtons.A) && _serveWait == 0 : _serveWait == 0;
            if (serve)
            {
                _held = false;
                var angle = _rng.Range(-0.5f, 0.5f);
                _vx = _speed * MathF.Sin(angle);
                _vy = -_speed * MathF.Cos(angle);
            }
            return;
        }

        _bx += _vx * dt;
        _by += _vy * dt;
        if (_bx < Ball / 2) { _bx = Ball / 2; _vx = -_vx; }
        if (_bx > W - Ball / 2) { _bx = W - Ball / 2; _vx = -_vx; }
        if (_by < Ball / 2) { _by = Ball / 2; _vy = -_vy; }

        if (_vy > 0 && _by + Ball / 2 >= PadY && _by - Ball / 2 <= PadY + PadH && MathF.Abs(_bx - _px) <= _padW / 2 + Ball / 2)
        {
            _by = PadY - Ball / 2;
            var offset = Math.Clamp((_bx - _px) / (_padW / 2), -1f, 1f);
            var angle = offset * 1.05f;                                   // up to 60° off the vertical
            _vx = _speed * MathF.Sin(angle);
            _vy = -_speed * MathF.Cos(angle);
        }

        HitBricks();

        if (_by > H + Ball)
        {
            _lives--;
            if (_lives <= 0) { _over = true; return; }
            Hold();
        }
    }

    private void HitBricks()
    {
        var col = (int)MathF.Floor((_bx - Left) / (BrickW + Gap));
        var row = (int)MathF.Floor((_by - BandTop) / (BrickH + Gap));
        if (col < 0 || col >= Cols || row < 0 || row >= Rows) return;
        var i = row * Cols + col;
        if (!_alive[i]) return;
        var bx0 = Left + col * (BrickW + Gap);
        var by0 = BandTop + row * (BrickH + Gap);
        if (_bx + Ball / 2 < bx0 || _bx - Ball / 2 > bx0 + BrickW || _by + Ball / 2 < by0 || _by - Ball / 2 > by0 + BrickH) return;
        _alive[i] = false;
        _bricksLeft--;
        _scores[0] += RowPoints[row];
        // The shallower overlap says which face was struck.
        var overlapX = MathF.Min(_bx + Ball / 2 - bx0, bx0 + BrickW - (_bx - Ball / 2));
        var overlapY = MathF.Min(_by + Ball / 2 - by0, by0 + BrickH - (_by - Ball / 2));
        if (overlapX < overlapY) _vx = -_vx; else _vy = -_vy;
        if (_bricksLeft == 0)
        {
            _level++;
            _levelsCleared++;
            _speed *= 1.08f;
            _padW = MathF.Max(140f, _padW - 30f);
            Build();
            Hold();
        }
    }

    public void Draw(SKCanvas c, float alpha, PaintCache p)
    {
        c.Clear(new SKColor(0x0F, 0x10, 0x16));
        for (var row = 0; row < Rows; row++)
        {
            var paint = p.FillAA(RowColours[row]);
            for (var col = 0; col < Cols; col++)
            {
                if (!_alive[row * Cols + col]) continue;
                c.DrawRoundRect(SKRect.Create(Left + col * (BrickW + Gap), BandTop + row * (BrickH + Gap), BrickW, BrickH), 5, 5, paint);
            }
        }
        var white = p.FillAA(SKColors.White);
        var px = ArcadeText.Lerp(_pxPrev, _px, alpha);
        c.DrawRoundRect(SKRect.Create(px - _padW / 2, PadY, _padW, PadH), 8, 8, white);
        var bx = ArcadeText.Lerp(_bxPrev, _bx, alpha);
        var by = ArcadeText.Lerp(_byPrev, _by, alpha);
        c.DrawCircle(bx, by, Ball / 2, white);
        var dim = new SKColor(0xFF, 0xFF, 0xFF, 0xB0);
        ArcadeText.Draw(c, p, $"SCORE {_scores[0]}", 40, 70, 44, dim, SKTextAlign.Left);
        ArcadeText.Draw(c, p, $"LEVEL {_level}", W / 2, 70, 44, dim);
        ArcadeText.Draw(c, p, _human ? "P1" : "HOUSE", W - 40, 44, 24, new SKColor(0xFF, 0xFF, 0xFF, 0x60), SKTextAlign.Right);
        for (var i = 0; i < _lives; i++) c.DrawRoundRect(SKRect.Create(W - 40 - (i + 1) * 70, 56, 56, 12), 4, 4, white);
        if (_held && _human) ArcadeText.Draw(c, p, "A TO SERVE", W / 2, PadY - 40, 32, new SKColor(0xFF, 0xFF, 0xFF, 0x80));
    }
}
