using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Particles;

/// <summary>
/// Pooled, allocation-free-per-frame particle simulation. Rendering goes through a single
/// DrawAtlas call (one white sprite tinted per particle), so tens of thousands of particles
/// stay cheap. Deterministic for a given seed + step sequence.
/// </summary>
public sealed class ParticleSim : IDisposable
{
    private struct P
    {
        public float X, Y, Vx, Vy;
        public float Size;          // radius-ish, px
        public float Rot, RotV;     // radians, radians/s
        public float Age;
        public float WobPhase, WobFreq;
        public byte ColorIdx;
    }

    private const int SpriteSize = 128;

    private P[] _pool = Array.Empty<P>();
    private SKRect[] _spriteRects = Array.Empty<SKRect>();
    private SKRotationScaleMatrix[] _xforms = Array.Empty<SKRotationScaleMatrix>();
    private SKColor[] _tints = Array.Empty<SKColor>();
    private SKColor[] _colors = { SKColors.White };
    private SKImage? _atlas;
    private Random _rng = new(1);
    private string _configKey = "";
    private int _count;
    private double _lastTime = double.NaN;
    private float _w = 1920, _h = 1080;
    private ParticleOptions _o = new();

    /// <summary>Re-seeds and rebuilds when anything relevant changed; cheap no-op otherwise.</summary>
    public void Configure(ParticleOptions o, ShowSnapshot snap, SKSizeI canvas)
    {
        var brand = snap.State.Brand;
        var colorKey = o.UseBrandColors
            ? $"brand:{brand.PrimaryColor}/{brand.SecondaryColor}/{brand.AccentColor}"
            : o.ColorsCsv;
        var key = string.Join('|',
            o.Count, o.Emitter, o.Shape, o.SizeMin, o.SizeMax, o.SpeedMin, o.SpeedMax,
            o.DirectionDeg, o.SpreadDeg, o.GravityY, o.WindX, o.Wobble, o.RotationSpeed,
            o.Seed, colorKey, canvas.Width, canvas.Height, o.Glow,
            o.Shape == ParticleShape.Logo ? brand.LogoPath : "");
        if (key == _configKey) { _o = o; return; }
        _configKey = key;
        _o = o;
        _w = canvas.Width;
        _h = canvas.Height;

        _colors = o.UseBrandColors
            ? new[]
            {
                snap.Color(brand.PrimaryColor, SKColors.White),
                snap.Color(brand.SecondaryColor, SKColors.Silver),
                snap.Color(brand.AccentColor, SKColors.Gold),
                SKColors.White,
            }
            : ColorUtil.ParseList(o.ColorsCsv, SKColors.White);

        _count = Math.Clamp(o.Count, 1, 20000);
        if (_pool.Length != _count)
        {
            // Exact length — DrawAtlas draws every array entry, so these must match the live count.
            _pool = new P[_count];
            _spriteRects = new SKRect[_count];
            _xforms = new SKRotationScaleMatrix[_count];
            _tints = new SKColor[_count];
        }

        BuildAtlas(snap);

        _rng = new Random(o.Seed);
        for (var i = 0; i < _count; i++)
        {
            Spawn(ref _pool[i], preWarm: true);
        }
        _lastTime = double.NaN;

        // Settle the field so it never starts empty on screen.
        for (var i = 0; i < 90; i++) StepFixed(1f / 30f);
    }

    public void Advance(double time)
    {
        if (double.IsNaN(_lastTime)) { _lastTime = time; return; }
        var dt = (float)Math.Clamp(time - _lastTime, 0, 0.1);
        _lastTime = time;
        if (dt > 0) StepFixed(dt);
    }

    /// <summary>One deterministic simulation step (also used directly by tests).</summary>
    public void StepFixed(float dt)
    {
        var starfield = _o.Emitter == ParticleEmitter.Center;
        float cx = _w / 2, cy = _h / 2;
        var maxDist = MathF.Sqrt(cx * cx + cy * cy) + 1;

        for (var i = 0; i < _count; i++)
        {
            ref var p = ref _pool[i];
            p.Age += dt;

            if (starfield)
            {
                // Radial acceleration: faster (and larger) with distance — classic warp.
                var dx = p.X - cx;
                var dy = p.Y - cy;
                var dist = MathF.Sqrt(dx * dx + dy * dy) + 0.01f;
                var speed = (0.15f + dist / maxDist * 2.6f) * (p.Vx); // Vx stores base speed here
                p.X += dx / dist * speed * dt;
                p.Y += dy / dist * speed * dt;
            }
            else
            {
                p.Vx += (float)_o.WindX * dt * 0.35f;
                p.Vy += (float)_o.GravityY * dt;
                p.X += p.Vx * dt + MathF.Sin(p.Age * p.WobFreq + p.WobPhase) * (float)_o.Wobble * 42f * dt;
                p.Y += p.Vy * dt;
            }

            p.Rot += p.RotV * dt;

            var m = p.Size * 3 + 8;
            if (p.X < -m || p.X > _w + m || p.Y < -m || p.Y > _h + m)
            {
                Spawn(ref p, preWarm: false);
            }
        }
    }

    private void Spawn(ref P p, bool preWarm)
    {
        var o = _o;
        p.Size = (float)Lerp(o.SizeMin, o.SizeMax, _rng.NextDouble());
        p.ColorIdx = (byte)_rng.Next(_colors.Length);
        p.Age = 0;
        p.WobPhase = (float)(_rng.NextDouble() * Math.PI * 2);
        p.WobFreq = (float)(0.6 + _rng.NextDouble() * 2.2);
        p.RotV = (float)(o.RotationSpeed * Math.PI * 2 * (0.4 + _rng.NextDouble() * 1.2) * (_rng.Next(2) == 0 ? 1 : -1));
        p.Rot = (float)(_rng.NextDouble() * Math.PI * 2);

        var speed = (float)Lerp(o.SpeedMin, o.SpeedMax, _rng.NextDouble());
        var dir = (float)((o.DirectionDeg + (_rng.NextDouble() - 0.5) * o.SpreadDeg) * Math.PI / 180.0);
        p.Vx = speed * MathF.Cos(dir);
        p.Vy = speed * MathF.Sin(dir);

        var margin = p.Size * 2 + 4;
        switch (o.Emitter)
        {
            case ParticleEmitter.TopEdge:
                p.X = (float)(_rng.NextDouble() * _w);
                p.Y = preWarm ? (float)(_rng.NextDouble() * _h) : -margin;
                break;
            case ParticleEmitter.BottomEdge:
                p.X = (float)(_rng.NextDouble() * _w);
                p.Y = preWarm ? (float)(_rng.NextDouble() * _h) : _h + margin;
                break;
            case ParticleEmitter.Center:
                // Starfield: Vx carries the base speed; position near center.
                p.Vx = Math.Max(20, speed);
                var a = _rng.NextDouble() * Math.PI * 2;
                var r = preWarm ? _rng.NextDouble() : _rng.NextDouble() * 0.06 + 0.01;
                var maxR = Math.Min(_w, _h) / 2.0;
                p.X = (float)(_w / 2 + Math.Cos(a) * r * maxR * (preWarm ? 2.2 : 1));
                p.Y = (float)(_h / 2 + Math.Sin(a) * r * maxR * (preWarm ? 2.2 : 1));
                break;
            default:
                p.X = (float)(_rng.NextDouble() * _w);
                p.Y = (float)(_rng.NextDouble() * _h);
                break;
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public void Render(SKCanvas c, PaintCache pc)
    {
        if (_atlas is null || _count == 0) return;

        var starfield = _o.Emitter == ParticleEmitter.Center;
        float cx = _w / 2, cy = _h / 2;
        var maxDist = MathF.Sqrt(cx * cx + cy * cy) + 1;
        var sprite = SKRect.Create(0, 0, SpriteSize, SpriteSize);
        var streaky = _o.Shape == ParticleShape.Streak;

        for (var i = 0; i < _count; i++)
        {
            ref var p = ref _pool[i];
            _spriteRects[i] = sprite;

            var scalePx = p.Size * 2; // sprite drawn at 2×size px
            if (starfield)
            {
                var dx = p.X - cx;
                var dy = p.Y - cy;
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                scalePx *= 0.25f + dist / maxDist * 1.6f;
            }
            var scale = scalePx / SpriteSize;

            var rot = p.Rot;
            if (streaky || (starfield && _o.Shape != ParticleShape.Bokeh))
            {
                rot = starfield
                    ? MathF.Atan2(p.Y - cy, p.X - cx)
                    : MathF.Atan2(p.Vy, p.Vx);
            }

            _xforms[i] = SKRotationScaleMatrix.Create(scale, rot, p.X, p.Y, SpriteSize / 2f, SpriteSize / 2f);

            var col = _colors[p.ColorIdx % _colors.Length];
            var alpha = Math.Min(1f, p.Age * 2.5f); // quick fade-in avoids spawn pops
            _tints[i] = col.WithAlpha((byte)(col.Alpha * alpha));
        }

        var paint = pc.FillAA(SKColors.White);
        paint.BlendMode = _o.Glow ? SKBlendMode.Plus : SKBlendMode.SrcOver;
        c.DrawAtlas(_atlas, _spriteRects, _xforms, _tints, SKBlendMode.Modulate, DrawUtil.Smooth, paint);
        paint.BlendMode = SKBlendMode.SrcOver;
    }

    private void BuildAtlas(ShowSnapshot snap)
    {
        _atlas?.Dispose();
        _atlas = null;

        var info = new SKImageInfo(SpriteSize, SpriteSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        float half = SpriteSize / 2f;

        using var fill = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
        switch (_o.Shape)
        {
            case ParticleShape.Square:
                c.DrawRect(SKRect.Create(half * 0.25f, half * 0.25f, SpriteSize * 0.75f, SpriteSize * 0.75f), fill);
                break;

            case ParticleShape.Star:
            {
                using var path = new SKPath();
                const int points = 5;
                for (var i = 0; i < points * 2; i++)
                {
                    var r = (i & 1) == 0 ? half * 0.95f : half * 0.4f;
                    var a = i * MathF.PI / points - MathF.PI / 2;
                    var x = half + r * MathF.Cos(a);
                    var y = half + r * MathF.Sin(a);
                    if (i == 0) path.MoveTo(x, y);
                    else path.LineTo(x, y);
                }
                path.Close();
                c.DrawPath(path, fill);
                break;
            }

            case ParticleShape.Streak:
                c.DrawRoundRect(SKRect.Create(4, half - SpriteSize * 0.09f, SpriteSize - 8, SpriteSize * 0.18f),
                    SpriteSize * 0.09f, SpriteSize * 0.09f, fill);
                break;

            case ParticleShape.Bokeh:
            {
                using var shader = SKShader.CreateRadialGradient(
                    new SKPoint(half, half), half * 0.95f,
                    new[] { SKColors.White, SKColors.White.WithAlpha(0xA0), SKColors.White.WithAlpha(0) },
                    new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp);
                fill.Shader = shader;
                c.DrawRect(SKRect.Create(0, 0, SpriteSize, SpriteSize), fill);
                fill.Shader = null;
                break;
            }

            case ParticleShape.Logo:
            {
                var logo = ImageCache.Get(snap.State.Brand.LogoPath);
                if (logo is not null)
                {
                    var dest = DrawUtil.Fit(new SKSizeI(logo.Width, logo.Height),
                        SKRect.Create(4, 4, SpriteSize - 8, SpriteSize - 8), FitMode.Fit);
                    c.DrawImage(logo, dest, DrawUtil.Smooth, fill);
                }
                else
                {
                    c.DrawCircle(half, half, half * 0.85f, fill);
                }
                break;
            }

            default:
                c.DrawCircle(half, half, half * 0.9f, fill);
                break;
        }

        _atlas = surface.Snapshot();
    }

    public void Dispose()
    {
        _atlas?.Dispose();
    }

    // Test hooks.
    public int Count => _count;
    public (float X, float Y) PositionOf(int i) => (_pool[i].X, _pool[i].Y);
}
