using Patterns.Core.Effects;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Particles;

/// <summary>
/// Pooled, allocation-free-per-frame particle simulation. Rendering goes through a single
/// DrawAtlas call (one white sprite tinted per particle), so tens of thousands of particles
/// stay cheap. Deterministic for a given seed + step sequence: the random stream is the sim's
/// own (a seeded xorshift, cloneable), the step is fixed, and the quality ladder never touches
/// the field — it only decides how many of the particles are drawn — so two sinks at different
/// levels, or one that joined late from a running leader (<see cref="JoinTimeline"/>), show the
/// same particles.
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

    /// <summary>
    /// The sim's own random stream (xorshift64*): seeded by the scene, the same sequence on
    /// every sink and every machine, and a plain value — so a late sink can copy a leader's
    /// stream along with its field and continue in step with it (System.Random cannot be copied).
    /// </summary>
    private struct Rng
    {
        private ulong _s;

        public Rng(int seed)
        {
            var z = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0xD1B54A32D192ED03UL;
            z ^= z >> 30; z *= 0xBF58476D1CE4E5B9UL;
            z ^= z >> 27; z *= 0x94D049BB133111EBUL;
            z ^= z >> 31;
            _s = z == 0 ? 0x2545F4914F6CDD1DUL : z;
        }

        private ulong Next()
        {
            var x = _s;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            _s = x;
            return x * 0x2545F4914F6CDD1DUL;
        }

        /// <summary>[0, 1) with 53 bits, like System.Random.</summary>
        public double NextDouble() => (Next() >> 11) * (1.0 / 9007199254740992.0);

        /// <summary>[0, n).</summary>
        public int Next(int n) => (int)(NextDouble() * n);
    }

    private const int SpriteSize = 128;

    private readonly object _gate = new();
    private P[] _pool = Array.Empty<P>();
    private SKRect[] _spriteRects = Array.Empty<SKRect>();
    private SKRotationScaleMatrix[] _xforms = Array.Empty<SKRotationScaleMatrix>();
    private SKColor[] _tints = Array.Empty<SKColor>();
    private SKColor[] _colors = { SKColors.White };
    private SKImage? _atlas;
    private Rng _rng = new(1);
    private string _configKey = "";
    private int _count;
    private double _quality = 1;
    private bool _disposed;
    private SKRect[] _drawRects = Array.Empty<SKRect>();
    private SKRotationScaleMatrix[] _drawXforms = Array.Empty<SKRotationScaleMatrix>();
    private SKColor[] _drawTints = Array.Empty<SKColor>();

    /// <summary>
    /// The quality ladder's factor: the share of the field that draws. Every particle keeps
    /// moving whatever the level — the integration is the cheap part, the draw is the cost —
    /// so stepping down hides particles rather than freezing or re-seeding them, stepping up
    /// shows them where they would have been, and two sinks at different levels show the same
    /// particles: the level never touches the field or its random stream.
    /// </summary>
    public double Quality
    {
        get => _quality;
        set => _quality = Math.Clamp(value, 0.05, 1);
    }

    /// <summary>How many particles draw at the current quality — never fewer than one.</summary>
    public int ActiveCount => _count == 0 ? 0 : Math.Max(1, Math.Min(_count, (int)Math.Round(_count * _quality)));
    private long _doneSteps = -1;
    private float _w = 1920, _h = 1080;
    private ParticleOptions _o = new();
    private EdgeFlux _flux = EdgeFlux.None;
    private EffectSurge _surge = EffectSurge.Zero;   // the pulse as of the last Advance, for the draw

    /// <summary>
    /// Fixed integration step. Every sink quantizes the shared show clock to this grid and
    /// executes the identical step sequence, so identically-seeded sims stay bit-exact across
    /// preview, span halves and NDI regardless of their individual frame timing.
    /// </summary>
    public const float StepSeconds = 1f / 120f;

    /// <summary>Baselines snap to this many steps (~4.3 s) so sinks configured within the same
    /// window start from the same absolute step index.</summary>
    private const long BaselineQuantum = 512;

    /// <summary>
    /// A sink further behind the clock than this (~17 s: a preview hidden for a while, a
    /// starved output) re-anchors on the quantised grid instead of catching up — it has lost
    /// its place with the other sinks either way.
    /// </summary>
    public const long MaxBehindSteps = 2048;

    /// <summary>
    /// The catch-up a frame may do in particle-updates (steps × particles): a sink a few
    /// seconds behind catches up over a few frames rather than in one frame that stalls the
    /// compositor — 2048 steps of 20 000 particles was 40 million updates in one draw.
    /// </summary>
    public const long CatchUpBudget = 3_000_000;

    /// <summary>Never fewer steps than a second of sim per frame, however large the field.</summary>
    public const int MinStepsPerFrame = 120;

    /// <summary>The steps one frame may catch up for this field's size.</summary>
    public int StepsPerFrame => (int)Math.Clamp(CatchUpBudget / Math.Max(1, _count), MinStepsPerFrame, MaxBehindSteps);

    /// <summary>Everything that makes a field: the scene, its colours, the canvas — the key a sink's cache and the snapshot's leaders file sims under.</summary>
    public static string KeyFor(ParticleOptions o, ShowSnapshot snap, SKSizeI canvas)
    {
        var brand = snap.State.Brand;
        var colorKey = o.UseBrandColors
            ? $"brand:{brand.PrimaryColor}/{brand.SecondaryColor}/{brand.AccentColor}"
            : o.ColorsCsv;
        return string.Join('|',
            o.Count, o.Emitter, o.Shape, o.SizeMin, o.SizeMax, o.SpeedMin, o.SpeedMax,
            o.DirectionDeg, o.SpreadDeg, o.GravityY, o.WindX, o.Wobble, o.RotationSpeed,
            o.Seed, colorKey, canvas.Width, canvas.Height, o.Glow,
            o.Shape == ParticleShape.Logo ? brand.LogoPath : "");
    }

    /// <summary>The key of the field this sim holds ("" before the first configure).</summary>
    public string ConfigKey => _configKey;

    /// <summary>Re-seeds and rebuilds when anything relevant changed; cheap no-op otherwise.</summary>
    public void Configure(ParticleOptions o, ShowSnapshot snap, SKSizeI canvas) => Configure(o, snap, canvas, KeyFor(o, snap, canvas));

    /// <summary>As <see cref="Configure(ParticleOptions, ShowSnapshot, SKSizeI)"/> with the key already built (a cache computes it once).</summary>
    public void Configure(ParticleOptions o, ShowSnapshot snap, SKSizeI canvas, string key)
    {
        if (_disposed) return;
        if (key == _configKey) { _o = o; return; }
        var brand = snap.State.Brand;
        _configKey = key;
        _o = o;
        _w = Math.Max(1, canvas.Width);
        _h = Math.Max(1, canvas.Height);
        _flux = EdgeFlux.Estimate(o, _w, _h);

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

        lock (_gate)
        {
            _rng = new Rng(o.Seed);
            for (var i = 0; i < _count; i++)
            {
                Spawn(ref _pool[i], preWarm: true);
            }
            _doneSteps = -1;

            // Settle the field so it never starts empty on screen.
            for (var i = 0; i < 90; i++) StepFixed(1f / 30f);
        }
    }

    /// <summary>
    /// Steps the field to the show clock. A fresh sim anchors on the quantised grid; one behind
    /// by a few seconds catches up over frames (<see cref="StepsPerFrame"/>); one hopelessly
    /// behind, or one whose clock went backwards (a designer scrub), re-anchors and keeps moving.
    /// </summary>
    public void Advance(double time)
    {
        if (_disposed) return;
        lock (_gate)
        {
            var target = (long)(time / StepSeconds);
            if (_doneSteps < 0 || target < _doneSteps || target - _doneSteps > MaxBehindSteps)
            {
                // Fresh sim, a clock that went back, or a sink that stalled far behind (e.g. a
                // hidden preview): re-anchor on the shared quantized grid instead of freezing or
                // grinding through thousands of catch-up steps.
                _doneSteps = Math.Max(0, (target / BaselineQuantum) * BaselineQuantum);
            }
            var stop = Math.Min(target, _doneSteps + StepsPerFrame);
            for (; _doneSteps < stop; _doneSteps++)
            {
                // The pulse is read at the quantised step clock, so a span's halves and NDI step
                // through the same surge on the same steps.
                StepFixed(StepSeconds, EffectImpulses.SurgeAt((_doneSteps + 1) * StepSeconds));
            }
            _surge = EffectImpulses.SurgeAt(time);
        }
    }

    /// <summary>The step index the field has reached (tests, and the cache's join).</summary>
    public long StepsDone => _doneSteps;

    /// <summary>True once <see cref="Dispose"/> ran: the sim draws nothing and steps nothing.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Takes a running leader's field as this sim's own — every particle, the random stream,
    /// the step index — so a sink opened late (an output window after OUTPUTS ON, an NDI send
    /// started mid-show, a display re-plugged) shows the same particles as the sinks already
    /// drawing the same field, from its first frame. Both sims must hold the same configuration;
    /// the copy is taken under the leader's gate, between its frames.
    /// </summary>
    public bool JoinTimeline(ParticleSim leader)
    {
        if (ReferenceEquals(leader, this) || _disposed || leader._disposed) return false;
        if (leader._configKey != _configKey || leader._count != _count || _count == 0) return false;
        lock (leader._gate)
        {
            if (leader._doneSteps < 0) return false;
            lock (_gate)
            {
                Array.Copy(leader._pool, _pool, _count);
                _rng = leader._rng;
                _doneSteps = leader._doneSteps;
                _surge = leader._surge;
            }
        }
        return true;
    }

    /// <summary>One deterministic simulation step with no pulse (also used directly by tests).</summary>
    public void StepFixed(float dt) => StepFixed(dt, EffectSurge.Zero);

    /// <summary>
    /// One deterministic simulation step under a pulse: everything moves faster by its Speed,
    /// and its Burst re-births a share of the field at the emitter with a kick — an explosion
    /// from a centre, a rush from an edge — that the envelope then settles.
    /// </summary>
    public void StepFixed(float dt, in EffectSurge surge)
    {
        // Slow motion holds the field (never quite still, so it keeps breathing); speed rushes it.
        var speedMul = (1 + 3f * surge.Speed) * (1 - 0.95f * surge.Slow);
        var gravity = (float)_o.GravityY * (1 - 2f * surge.Lift);        // lift reverses gravity: 0.5 weightless, 1 falls up
        var windAccel = (float)_o.WindX * 0.35f + surge.Gust * 900f;      // a gust is a side wind slam
        var starfield = _o.Emitter == ParticleEmitter.Center;
        float cx = _w / 2, cy = _h / 2;
        var maxDist = MathF.Sqrt(cx * cx + cy * cy) + 1;
        var swirl = surge.Swirl;
        var swirlAngle = swirl * 2.2f * dt;                                // the whirl: the field turns round the centre…
        var swirlPull = MathF.Abs(swirl) * 110f * dt;                     // …and is drawn inward
        var swirlCos = MathF.Cos(swirlAngle);
        var swirlSin = MathF.Sin(swirlAngle);

        // Every particle steps, whatever the ladder says: the level decides the draw alone.
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
                p.X += dx / dist * speed * dt * speedMul;
                p.Y += dy / dist * speed * dt * speedMul;
            }
            else
            {
                p.Vx += windAccel * dt;
                p.Vy += gravity * dt;
                p.X += p.Vx * dt * speedMul + MathF.Sin(p.Age * p.WobFreq + p.WobPhase) * (float)_o.Wobble * 42f * dt;
                p.Y += p.Vy * dt * speedMul;
            }

            if (swirl != 0)
            {
                var dx = p.X - cx;
                var dy = p.Y - cy;
                var dist = MathF.Sqrt(dx * dx + dy * dy) + 0.01f;
                var rx = dx * swirlCos - dy * swirlSin;
                var ry = dx * swirlSin + dy * swirlCos;
                var inward = MathF.Max(0f, 1f - swirlPull / dist);
                p.X = cx + rx * inward;
                p.Y = cy + ry * inward;
            }

            p.Rot += p.RotV * dt;

            var m = p.Size * 3 + 8;
            // Off the canvas, or not a number any more (a poisoned particle would never leave
            // by the bounds test and would hand Skia a NaN transform): born again.
            if (p.X < -m || p.X > _w + m || p.Y < -m || p.Y > _h + m || !float.IsFinite(p.X + p.Y + p.Vx + p.Vy))
            {
                Spawn(ref p, preWarm: false);
            }
        }

        if (surge.Burst > 0.001f)
        {
            var births = (int)Math.Ceiling(_count * surge.Burst * 0.015f);
            var kick = 1 + 2.5f * surge.Burst;
            for (var k = 0; k < births; k++)
            {
                var i = _rng.Next(_count);
                Spawn(ref _pool[i], preWarm: false);
                _pool[i].Vx *= kick;
                _pool[i].Vy *= kick;
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
            case ParticleEmitter.BottomEdge:
            {
                var top = o.Emitter == ParticleEmitter.TopEdge;
                if (preWarm)
                {
                    p.X = (float)(_rng.NextDouble() * _w);
                    p.Y = (float)(_rng.NextDouble() * _h);
                    break;
                }
                // The upwind side edge takes its share of births (EdgeFlux) so a drifting field
                // keeps the whole canvas covered. The draw is taken on every birth, so the
                // sequence — and every sink's field — never depends on the estimate's value.
                var side = _rng.NextDouble() < _flux.SideFraction;
                if (!side)
                {
                    p.X = (float)(_rng.NextDouble() * _w);
                    p.Y = top ? -margin : _h + margin;
                    break;
                }
                // Deeper entries where the wind has had time to work; the speed a particle born
                // on the top would have built by this depth, so the side entries match the field.
                var u = _rng.NextDouble();
                var depth = (float)(_h * (u + (Math.Sqrt(u) - u) * _flux.AccelShare));
                var t = MathF.Min(EdgeFlux.TimeToTravel(_flux.V0, _flux.A, depth), _flux.Cross);
                p.X = _flux.FromLeft ? -margin : _w + margin;
                p.Y = top ? depth : _h - depth;
                p.Vx += _flux.Ax * t;
                p.Vy += _flux.Ay * t;
                break;
            }
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
        if (_disposed || _atlas is null || _count == 0) return;

        var starfield = _o.Emitter == ParticleEmitter.Center;
        float cx = _w / 2, cy = _h / 2;
        var maxDist = MathF.Sqrt(cx * cx + cy * cy) + 1;
        var sprite = SKRect.Create(0, 0, SpriteSize, SpriteSize);
        var streaky = _o.Shape == ParticleShape.Streak;
        var surge = _surge;

        // Sprites drawn at 2×size px: bigger under a glow, bigger again under a scale, and a
        // ripple is a ring of enlarged sprites rolling out from the centre as the sting runs.
        var sizeMul = 2 * (1 + 0.6f * surge.Glow) * (1 + 1.5f * surge.Scale);
        var ripple = surge.Ripple;
        var ringRadius = surge.Progress * maxDist * 1.2f;
        var ringWidth = MathF.Max(1f, maxDist * 0.12f);
        var colors = surge.Hue != 0 && EffectColor.HueStrength(surge.Hue) > 0.002f ? Turned(surge.Hue) : _colors;

        var active = ActiveCount;
        for (var i = 0; i < active; i++)
        {
            ref var p = ref _pool[i];
            _spriteRects[i] = sprite;

            var scalePx = p.Size * sizeMul;
            if (starfield || ripple > 0.001f)
            {
                var dx = p.X - cx;
                var dy = p.Y - cy;
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                if (starfield) scalePx *= 0.25f + dist / maxDist * 1.6f;
                if (ripple > 0.001f)
                {
                    var d = (dist - ringRadius) / ringWidth;
                    scalePx *= 1 + 2.5f * ripple * MathF.Exp(-d * d);
                }
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

            var col = colors[p.ColorIdx % colors.Length];
            var alpha = Math.Min(1f, p.Age * 2.5f); // quick fade-in avoids spawn pops
            _tints[i] = col.WithAlpha((byte)(col.Alpha * alpha));
        }

        var shaking = surge.Shake > 0.01f;
        if (shaking)
        {
            // The same offset on every sink for the same instant — a function of the sting's phase.
            var (dx, dy) = ShakeOffset(surge, _h);
            c.Save();
            c.Translate(dx, dy);
        }
        var paint = pc.FillAA(SKColors.White);
        paint.BlendMode = _o.Glow || surge.Glow > 0.25f ? SKBlendMode.Plus : SKBlendMode.SrcOver;
        if (active == _count)
        {
            c.DrawAtlas(_atlas, _spriteRects, _xforms, _tints, SKBlendMode.Modulate, DrawUtil.Smooth, paint);
        }
        else
        {
            // DrawAtlas draws every array entry: the active share goes through arrays of its own
            // length, re-sized only when the level changes, never per frame.
            if (_drawRects.Length != active)
            {
                _drawRects = new SKRect[active];
                _drawXforms = new SKRotationScaleMatrix[active];
                _drawTints = new SKColor[active];
            }
            Array.Copy(_spriteRects, _drawRects, active);
            Array.Copy(_xforms, _drawXforms, active);
            Array.Copy(_tints, _drawTints, active);
            c.DrawAtlas(_atlas, _drawRects, _drawXforms, _drawTints, SKBlendMode.Modulate, DrawUtil.Smooth, paint);
        }
        paint.BlendMode = SKBlendMode.SrcOver;
        if (shaking) c.Restore();
    }

    /// <summary>Where a shaking picture sits at this instant: a pseudo-random offset that is a pure function of the sting's phase.</summary>
    public static (float Dx, float Dy) ShakeOffset(in EffectSurge surge, float height)
    {
        var amp = surge.Shake * 0.025f * height;
        return (amp * MathF.Sin(surge.Phase * 71f), amp * MathF.Cos(surge.Phase * 53f));
    }

    private SKColor[] _turned = Array.Empty<SKColor>();
    private float _turnedHue = float.NaN;

    private SKColor[] Turned(float hue)
    {
        if (_turned.Length == _colors.Length && _turnedHue == hue) return _turned;
        if (_turned.Length != _colors.Length) _turned = new SKColor[_colors.Length];
        for (var i = 0; i < _colors.Length; i++) _turned[i] = EffectColor.Turn(_colors[i], hue);
        _turnedHue = hue;
        return _turned;
    }

    private void BuildAtlas(ShowSnapshot snap)
    {
        _atlas?.Dispose();
        _atlas = null;

        var info = new SKImageInfo(SpriteSize, SpriteSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null) return;   // no memory for a sprite: the field draws nothing rather than faulting
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

    /// <summary>Lets the sprite go and marks the sim: a draw after this is nothing, never a freed native handle.</summary>
    public void Dispose()
    {
        _disposed = true;
        _atlas?.Dispose();
        _atlas = null;
    }

    // Test hooks.
    public int Count => _count;
    public (float X, float Y) PositionOf(int i) => (_pool[i].X, _pool[i].Y);
    public float AgeOf(int i) => _pool[i].Age;
    /// <summary>Tests: a particle's velocity (a starfield particle carries its base speed in X).</summary>
    public (float Vx, float Vy) VelocityOf(int i) => (_pool[i].Vx, _pool[i].Vy);
}
