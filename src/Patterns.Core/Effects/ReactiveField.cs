using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Effects;

/// <summary>
/// Everything a reactive scene needs for one frame, read once and handed to both paths. The
/// graphics card gets it as shader uniforms and the CPU gets it as this struct, so the two draw
/// the same picture from the same numbers rather than from two readings of the clock and the sound.
/// </summary>
public readonly record struct ReactiveView(
    double Time,
    double Depth,
    int Symmetry,
    double Angle,
    float Level,
    float Low,
    float Mid,
    float High,
    float Amount,
    float Brightness)
{
    /// <summary>
    /// The view for a moment. The scene's own clock runs on <paramref name="seconds"/> × speed, so
    /// a scene with no sound at all still moves — which is every scene off Windows, where there is
    /// no capture, and every walk-in before the music starts. A sting's surge rides on top: it
    /// hurries the clock, turns the picture and deepens the warp, the same channels the particles
    /// and the fractals already answer to.
    /// </summary>
    public static ReactiveView Of(ReactiveOptions o, double seconds, AudioLevelFrame audio, EffectSurge surge = default)
    {
        var amount = (float)o.AudioAmount;
        var speed = o.Speed * (1 + surge.Speed * 0.9) * (1 - Math.Clamp(surge.Slow, 0, 0.9));
        // The sound hurries the scene a little as well as lighting it, but never stops it: at a
        // silent moment the clock still runs at the operator's own speed.
        var lift = 1 + audio.Low * amount * 0.6;
        return new ReactiveView(
            Time: seconds * speed * lift,
            Depth: Math.Clamp(o.Depth + surge.Zoom * 0.25 + audio.Mid * amount * 0.25, 0, 1.5),
            Symmetry: o.Symmetry,
            Angle: seconds * o.Rotation * Math.Tau * 0.1 + surge.Rotate * Math.Tau,
            Level: audio.Level,
            Low: audio.Low,
            Mid: audio.Mid,
            High: audio.High,
            Amount: amount,
            // The sound moves the brightness inside a bound that cannot become a flash; a sting's
            // own flash channel is the thing that may, and that goes through the guard.
            Brightness: FlashGuard.Brightness((float)o.Brightness * (1 + surge.Glow * 0.5f), audio.Level, amount));
    }
}

/// <summary>
/// The scenes' arithmetic, written once here for the CPU and mirrored line for line in the shader
/// sources. Every scene is one closed-form sample per pixel — no loops, no feedback, no state — so
/// the CPU twin that draws NDI, the stream and the thumbnails costs about what a plasma cost in
/// 1998, and the two paths agree to a pixel bound the tests hold.
///
/// Coordinates: <paramref name="px"/> and <paramref name="py"/> are the pixel's place with the
/// centre at 0 and the height at 1, so a wide canvas simply sees more of the scene.
/// </summary>
public static class ReactiveField
{
    /// <summary>The scene's value at a point, 0–1, before the palette.</summary>
    public static double Sample(ReactiveScene scene, double px, double py, in ReactiveView v)
    {
        // The picture turns as a whole, so a rotation is the same on both paths.
        var cos = Math.Cos(v.Angle);
        var sin = Math.Sin(v.Angle);
        var x = px * cos - py * sin;
        var y = px * sin + py * cos;
        var t = v.Time;
        var r = Math.Sqrt(x * x + y * y);
        var a = Math.Atan2(y, x);

        switch (scene)
        {
            case ReactiveScene.Tunnel:
            {
                // Down the tunnel: the bands run on 1/r, so they crowd towards the middle.
                var depth = 0.15 + v.Depth * 0.35;
                var spokes = Math.Sin(a * v.Symmetry + t * 0.4) * 0.06 * v.Depth;
                return Fract(depth / Math.Max(r, 0.02) - t * 0.35 + spokes);
            }

            case ReactiveScene.Kaleidoscope:
            {
                // The angle folded into wedges, then a plasma read through the fold.
                var wedge = Math.Tau / Math.Max(2, v.Symmetry);
                var folded = Math.Abs(Fract(a / wedge + 0.5) - 0.5) * wedge;
                var fx = Math.Cos(folded) * r;
                var fy = Math.Sin(folded) * r;
                return Plasma(fx * 5, fy * 5, t, v.Depth);
            }

            case ReactiveScene.Pulse:
            {
                // Rings rolling out, their spacing opening on the low end.
                var rings = 4 + v.Low * v.Amount * 5;
                var edge = Math.Sin((r * rings - t * 0.5) * Math.Tau) * 0.5 + 0.5;
                return Math.Clamp(Math.Pow(edge, 1.5 + v.Depth * 2), 0, 1);
            }

            case ReactiveScene.Vortex:
            {
                // The field swirled around the middle, tighter towards the edge.
                var twist = a + r * (2 + v.Depth * 8) + t * 0.25;
                return Fract(twist / Math.Tau * v.Symmetry * 0.5 + Math.Sin(r * 6 - t * 0.4) * 0.15);
            }

            case ReactiveScene.StarWarp:
            {
                // Streaks running out past the viewer: a value per spoke, brightest near the edge.
                var spokes = Math.Max(2, v.Symmetry) * 6;
                var lane = Fract(a / Math.Tau * spokes);
                var seed = Math.Floor(a / Math.Tau * spokes);
                var along = Fract(Math.Log(Math.Max(r, 0.015)) * 0.7 + t * 0.5 + Hash(seed));
                var streak = Math.Pow(along, 3);
                var across = 1 - Math.Min(1, Math.Abs(lane - 0.5) * 6);
                return Math.Clamp(streak * Math.Max(0, across) * (0.55 + r * 1.2), 0, 1);
            }

            default:
                return Plasma(x * 6, y * 6, t, v.Depth);
        }
    }

    /// <summary>Summed sines — the cheapest thing on either path, and the scene everything else falls back to.</summary>
    private static double Plasma(double x, double y, double t, double depth)
    {
        var warp = 0.5 + depth;
        var s = Math.Sin(x + t)
                + Math.Sin(y * 0.9 - t * 0.7)
                + Math.Sin((x + y) * 0.6 * warp + t * 0.4)
                + Math.Sin(Math.Sqrt(x * x + y * y) * 1.3 * warp - t * 1.1);
        return Fract(s * 0.125 + 0.5);
    }

    /// <summary>A settled pseudo-random value for a whole number — the star field's lanes, the same on both paths.</summary>
    private static double Hash(double n) => Fract(Math.Sin(n * 12.9898) * 43758.5453);

    /// <summary>The fractional part, always 0–1 (C#'s % keeps the sign; the shader's fract does not).</summary>
    public static double Fract(double v)
    {
        var f = v - Math.Floor(v);
        return f < 0 ? f + 1 : f;
    }

    /// <summary>
    /// The palette at a point of the cycle: the colours in order, wrapping, mixed smoothly, then
    /// taken up or down by the brightness. Five slots, like the fractal palette, so a brand kit
    /// and a hand-written list both land the same way.
    /// </summary>
    public static SKColor Map(double v, IReadOnlyList<SKColor> colors, float brightness)
    {
        var n = colors.Count;
        if (n == 0) return SKColors.Black;
        if (n == 1) return Scale(colors[0], brightness);
        var pos = Fract(v) * n;
        var i = (int)Math.Floor(pos) % n;
        var j = (i + 1) % n;
        var k = pos - Math.Floor(pos);
        // Smoothstep between the two, so a slow drift has no visible banding.
        k = k * k * (3 - 2 * k);
        return Mix(colors[i], colors[j], k, brightness);
    }

    private static SKColor Mix(SKColor a, SKColor b, double k, double g)
    {
        static byte Ch(byte x, byte y, double k, double g) => (byte)Math.Clamp((x + (y - x) * k) * g, 0, 255);
        return new SKColor(Ch(a.Red, b.Red, k, g), Ch(a.Green, b.Green, k, g), Ch(a.Blue, b.Blue, k, g));
    }

    private static SKColor Scale(SKColor c, double g)
        => new((byte)Math.Clamp(c.Red * g, 0, 255), (byte)Math.Clamp(c.Green * g, 0, 255), (byte)Math.Clamp(c.Blue * g, 0, 255));

    /// <summary>How many colours a scene cycles through: the shader has five slots and the CPU reads the same five.</summary>
    public const int PaletteColors = 5;
}
