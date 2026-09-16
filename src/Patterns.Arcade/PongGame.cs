using System.Globalization;
using Patterns.Core.Arcade;
using Patterns.Rendering;
using SkiaSharp;

namespace Patterns.Arcade;

/// <summary>Pong: two paddles, a ball that speeds up with every hit and leaves at the angle it was struck, first to seven. One player faces the house.</summary>
public sealed class PongGame : IArcadeGame
{
    private const float W = ArcadeStage.Width;
    private const float H = ArcadeStage.Height;
    private const float PadH = 180f;
    private const float PadW = 24f;
    private const float PadInset = 60f;
    private const float Ball = 20f;
    private const float PadSpeed = 1000f;
    public const int WinScore = 7;

    private readonly float[] _y = new float[2];
    private readonly float[] _yPrev = new float[2];
    private readonly bool[] _human = new bool[2];
    private readonly int[] _scores = new int[2];
    private readonly float[] _aiError = new float[2];
    private float _bx, _by, _bxPrev, _byPrev, _vx, _vy;
    private int _serve;
    private int _serveTo;
    private float _speedMul = 1f;
    private float _diff = 0.5f;
    private ArcadeRandom _rng = new(1);
    private bool _over;
    private int _winner;

    public string Id => "pong";
    public string Title => "PONG";
    public string Blurb => "▲ ▼ move · first to 7 · a hit near the edge sends it steep";
    public int MaxPlayers => 2;
    public int Seats => 2;
    public bool IsOver => _over;
    public int Winner => _winner;
    public IReadOnlyList<int> Scores => _scores;
    public bool IsHuman(int seat) => seat >= 0 && seat < 2 && _human[seat];
    public string Words => $"PONG — {_scores[0]} : {_scores[1]}";

    public int DifficultyHint
    {
        get
        {
            if (!_over || (_human[0] == _human[1])) return 0;            // no one against the house
            var human = _human[0] ? 0 : 1;
            var margin = _scores[human] - _scores[1 - human];
            return margin >= 3 ? 1 : margin <= -3 ? -1 : 0;
        }
    }

    private float BaseSpeed => 650f + 550f * _diff;

    public void Reset(ReadOnlySpan<bool> humans, ArcadeRandom rng, float difficulty)
    {
        _rng = rng;
        _diff = Math.Clamp(difficulty, 0f, 1f);
        for (var i = 0; i < 2; i++)
        {
            _human[i] = i < humans.Length && humans[i];
            _scores[i] = 0;
            _y[i] = _yPrev[i] = H / 2;
        }
        _over = false;
        _winner = 0;
        _serveTo = 0;
        Centre();
        _serve = 90;
    }

    private void Centre()
    {
        _bx = _bxPrev = W / 2;
        _by = _byPrev = H / 2;
        _vx = _vy = 0;
    }

    public void Step(ReadOnlySpan<PadState> pads, float dt, long step)
    {
        if (_over) return;
        MovePaddle(0, pads.Length > 0 ? pads[0] : default, dt, _vx < 0);
        MovePaddle(1, pads.Length > 1 ? pads[1] : default, dt, _vx > 0);

        _bxPrev = _bx;
        _byPrev = _by;
        if (_serve > 0)
        {
            _serve--;
            if (_serve == 0) Launch();
            return;
        }
        _bx += _vx * dt;
        _by += _vy * dt;
        if (_by < Ball / 2) { _by = Ball / 2; _vy = -_vy; }
        if (_by > H - Ball / 2) { _by = H - Ball / 2; _vy = -_vy; }

        var leftFace = PadInset + PadW;
        if (_vx < 0 && _bx - Ball / 2 <= leftFace && _bx - Ball / 2 >= PadInset - 40 && MathF.Abs(_by - _y[0]) <= PadH / 2 + Ball / 2)
        {
            _bx = leftFace + Ball / 2;
            Bounce(_y[0], +1);
        }
        var rightFace = W - PadInset - PadW;
        if (_vx > 0 && _bx + Ball / 2 >= rightFace && _bx + Ball / 2 <= W - PadInset + 40 && MathF.Abs(_by - _y[1]) <= PadH / 2 + Ball / 2)
        {
            _bx = rightFace - Ball / 2;
            Bounce(_y[1], -1);
        }
        if (_bx < -Ball) Point(1);
        else if (_bx > W + Ball) Point(0);
    }

    private void MovePaddle(int seat, PadState pad, float dt, bool ballComing)
    {
        _yPrev[seat] = _y[seat];
        if (_human[seat])
        {
            var dy = (pad.Has(PadButtons.Down) ? 1f : 0f) - (pad.Has(PadButtons.Up) ? 1f : 0f);
            _y[seat] += dy * PadSpeed * dt;
        }
        else
        {
            // The house: follows the ball when it is coming, with an error set at the serve so it
            // misses now and then at low difficulty; drifts home when the ball is going away.
            var target = ballComing && _serve == 0 ? _by + _aiError[seat] : H / 2;
            var speed = PadSpeed * (0.45f + 0.55f * _diff);
            var delta = target - _y[seat];
            if (MathF.Abs(delta) > 6f) _y[seat] += MathF.Sign(delta) * MathF.Min(MathF.Abs(delta), speed * dt);
        }
        _y[seat] = Math.Clamp(_y[seat], PadH / 2, H - PadH / 2);
    }

    private void Launch()
    {
        var angle = _rng.Range(-0.55f, 0.55f);
        var dir = _serveTo == 0 ? -1f : 1f;
        _speedMul = 1f;
        _vx = dir * BaseSpeed * MathF.Cos(angle);
        _vy = BaseSpeed * MathF.Sin(angle);
        var slack = (1f - _diff) * 110f;
        _aiError[0] = _rng.Range(-slack, slack);
        _aiError[1] = _rng.Range(-slack, slack);
    }

    private void Bounce(float padY, float sign)
    {
        var offset = Math.Clamp((_by - padY) / (PadH / 2), -1f, 1f);
        _speedMul = MathF.Min(1.6f, _speedMul * 1.04f);
        var speed = BaseSpeed * _speedMul;
        var angle = offset * 1.0f;
        _vx = sign * speed * MathF.Cos(angle);
        _vy = speed * MathF.Sin(angle);
        var slack = (1f - _diff) * 110f;
        _aiError[0] = _rng.Range(-slack, slack);
        _aiError[1] = _rng.Range(-slack, slack);
    }

    private void Point(int scorer)
    {
        _scores[scorer]++;
        if (_scores[scorer] >= WinScore)
        {
            _over = true;
            _winner = scorer + 1;
            Centre();
            return;
        }
        _serveTo = 1 - scorer;
        Centre();
        _serve = 90;
    }

    public void Draw(SKCanvas c, float alpha, PaintCache p)
    {
        c.Clear(new SKColor(0x10, 0x13, 0x19));
        var net = p.Fill(new SKColor(0xFF, 0xFF, 0xFF, 0x30));
        for (var y = 20f; y < H; y += 48f) c.DrawRect(W / 2 - 3, y, 6, 24, net);
        var white = p.FillAA(SKColors.White);
        for (var i = 0; i < 2; i++)
        {
            var y = ArcadeText.Lerp(_yPrev[i], _y[i], alpha);
            var x = i == 0 ? PadInset : W - PadInset - PadW;
            c.DrawRoundRect(SKRect.Create(x, y - PadH / 2, PadW, PadH), 6, 6, white);
        }
        var bx = ArcadeText.Lerp(_bxPrev, _bx, alpha);
        var by = ArcadeText.Lerp(_byPrev, _by, alpha);
        c.DrawRect(bx - Ball / 2, by - Ball / 2, Ball, Ball, white);
        var dim = new SKColor(0xFF, 0xFF, 0xFF, 0xB0);
        ArcadeText.Draw(c, p, _scores[0].ToString(CultureInfo.InvariantCulture), W / 2 - 160, 170, 140, dim);
        ArcadeText.Draw(c, p, _scores[1].ToString(CultureInfo.InvariantCulture), W / 2 + 160, 170, 140, dim);
        var faint = new SKColor(0xFF, 0xFF, 0xFF, 0x60);
        ArcadeText.Draw(c, p, _human[0] ? "P1" : "HOUSE", W / 2 - 160, 210, 28, faint);
        ArcadeText.Draw(c, p, _human[1] ? "P2" : "HOUSE", W / 2 + 160, 210, 28, faint);
    }
}
