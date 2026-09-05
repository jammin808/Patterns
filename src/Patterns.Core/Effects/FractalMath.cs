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

    /// <summary><paramref name="warp"/> (0–1) folds the second warp deeper — a sting's morph; the shader does the same.</summary>
    public static double Warp(double x, double y, double time, double warp = 0)
    {
        var px = x * 1.5;
        var py = y * 1.5;
        var fold = 3 + 3 * warp;
        var q = Fbm(px + time * 0.11, py + time * 0.07);
        var r = Fbm(px + fold * q + 1.7 - time * 0.05, py + fold * q + 9.2 - time * 0.05);
        return Math.Clamp(Fbm(px + 3 * r, py + 3 * r), 0, 1);
    }

    public static double Fbm(double x, double y)
    {
        var v = 0.0;
        var amp = 0.5;
        for (var i = 0; i < 4; i++)
        {
            v += amp * Noise(x, y);
            var nx = x * 2.03 + 17.1;
            var ny = y * 2.03 + 9.3;
            x = nx;
            y = ny;
            amp *= 0.5;
        }
        return v;
    }

    public static double Noise(double x, double y)
    {
        var ix = Math.Floor(x);
        var iy = Math.Floor(y);
        var fx = x - ix;
        var fy = y - iy;
        var ux = fx * fx * (3 - 2 * fx);
        var uy = fy * fy * (3 - 2 * fy);
        var a = Hash(ix, iy);
        var b = Hash(ix + 1, iy);
        var c = Hash(ix, iy + 1);
        var d = Hash(ix + 1, iy + 1);
        return Lerp(Lerp(a, b, ux), Lerp(c, d, ux), uy);
    }

    /// <summary>A lattice hash in 0..1, the same arithmetic the shader uses.</summary>
    public static double Hash(double x, double y)
    {
        var px = Fract(x * 123.34);
        var py = Fract(y * 456.21);
        var dot = px * (px + 45.32) + py * (py + 45.32);
        px += dot;
        py += dot;
        return Fract(px * py);
    }

    private static double Fract(double v) => v - Math.Floor(v);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
