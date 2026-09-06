using Patterns.Core.Model;

namespace Patterns.Core.Effects;

/// <summary>
/// The fractal families point by point, pure and deterministic — the CPU path draws with these,
/// and the shaders in <see cref="Patterns.FractalPattern"/> say the same thing in SkSL.
/// </summary>
public static class FractalMath
{
    /// <summary>
    /// The escape-time families return 0..1 with smooth colouring (1 = never escaped: inside the
    /// set). Newton returns the root it converged to in thirds — 0, ⅓, ⅔ — plus how slowly, as a
    /// fraction of that third. Domain warp returns a noise value in 0..1 that drifts with time.
    /// </summary>
    public static double Sample(FractalKind kind, double x, double y, double cr, double ci, int maxIter, double time, double warp = 0)
    {
        switch (kind)
        {
            case FractalKind.Julia:
                return Escape(x, y, cr, ci, maxIter, ship: false);
            case FractalKind.BurningShip:
                return Escape(0, 0, x, y, maxIter, ship: true);
            case FractalKind.Newton:
                return Newton(x, y, maxIter);
            case FractalKind.DomainWarp:
                return Warp(x, y, time, warp);
            default:
                return Escape(0, 0, x, y, maxIter, ship: false);
        }
    }

    private static double Escape(double zx, double zy, double cx, double cy, int maxIter, bool ship)
    {
        for (var i = 0; i < maxIter; i++)
        {
            if (ship)
            {
                zx = Math.Abs(zx);
                zy = Math.Abs(zy);
            }
            var nx = zx * zx - zy * zy + cx;
            var ny = 2 * zx * zy + cy;
            zx = nx;
            zy = ny;
            var m = zx * zx + zy * zy;
            if (m > 16)
            {
                var n = i + 1 - Math.Log2(Math.Log2(m) * 0.5);
                return Math.Clamp(n / maxIter, 0, 0.999999);
            }
        }
        return 1;
    }

    private static double Newton(double zx, double zy, int maxIter)
    {
        var n = 0;
        for (var i = 0; i < maxIter; i++)
        {
            var z2x = zx * zx - zy * zy;
            var z2y = 2 * zx * zy;
            var z3x = z2x * zx - z2y * zy;
            var z3y = z2x * zy + z2y * zx;
            var fx = z3x - 1;
            var fy = z3y;
            if (fx * fx + fy * fy < 1e-12) break;
            var dx = 3 * z2x;
            var dy = 3 * z2y;
            var dd = dx * dx + dy * dy + 1e-12;
            var qx = (fx * dx + fy * dy) / dd;
            var qy = (fy * dx - fx * dy) / dd;
            zx -= qx;
            zy -= qy;
            n = i + 1;
        }
        var ang = Math.Atan2(zy, zx);
        var root = (int)Math.Floor(((ang / (2 * Math.PI) + 1 + 1.0 / 6) % 1.0) * 3);
        var speed = Math.Clamp(n / (double)maxIter, 0, 0.999);
        return root / 3.0 + speed / 3.0;
    }

    // ---- domain warp: value noise, three folds ------------------------------------------
    //
    // In single precision on purpose, the shader's own. The lattice hash multiplies by 123.34 and
    // 456.21 and takes the fraction, then folds the result into itself: a difference in the last
    // place of the constant — 123.34 as a double is not 123.34 as a float — grows a hundredfold
    // through the fold and lands anywhere in 0..1. Computed in doubles, the CPU path drew a
    // different cloud from the graphics card's: NDI and the stream did not show what the
    // projectors showed. In floats, with the shader's constants and its order of operations, the
    // two agree (the fidelity test holds them together).

    /// <summary><paramref name="warp"/> (0–1) folds the second warp deeper — a sting's morph; the shader does the same.</summary>
    public static double Warp(double x, double y, double time, double warp = 0)
    {
        var px = (float)x * 1.5f;
        var py = (float)y * 1.5f;
        var t = (float)time;
        var fold = 3f + 3f * (float)warp;
        var q = FbmF(px + t * 0.11f, py + t * 0.07f);
        var r = FbmF(px + fold * q + 1.7f - t * 0.05f, py + fold * q + 9.2f - t * 0.05f);
        return Math.Clamp(FbmF(px + 3f * r, py + 3f * r), 0f, 1f);
    }

    public static double Fbm(double x, double y) => FbmF((float)x, (float)y);

    public static double Noise(double x, double y) => NoiseF((float)x, (float)y);

    /// <summary>A lattice hash in 0..1, the same arithmetic the shader uses.</summary>
    public static double Hash(double x, double y) => HashF((float)x, (float)y);

    private static float FbmF(float x, float y)
    {
        var v = 0f;
        var amp = 0.5f;
        for (var i = 0; i < 4; i++)
        {
            v += amp * NoiseF(x, y);
            var nx = x * 2.03f + 17.1f;
            var ny = y * 2.03f + 9.3f;
            x = nx;
            y = ny;
            amp *= 0.5f;
        }
        return v;
    }

    private static float NoiseF(float x, float y)
    {
        var ix = MathF.Floor(x);
        var iy = MathF.Floor(y);
        var fx = x - ix;
        var fy = y - iy;
        var ux = fx * fx * (3f - 2f * fx);
        var uy = fy * fy * (3f - 2f * fy);
        var a = HashF(ix, iy);
        var b = HashF(ix + 1f, iy);
        var c = HashF(ix, iy + 1f);
        var d = HashF(ix + 1f, iy + 1f);
        return Lerp(Lerp(a, b, ux), Lerp(c, d, ux), uy);
    }

    private static float HashF(float x, float y)
    {
        var px = Fract(x * 123.34f);
        var py = Fract(y * 456.21f);
        var dot = px * (px + 45.32f) + py * (py + 45.32f);
        px += dot;
        py += dot;
        return Fract(px * py);
    }

    private static float Fract(float v) => v - MathF.Floor(v);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
