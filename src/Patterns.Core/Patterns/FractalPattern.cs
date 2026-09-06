using Patterns.Core.Effects;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Patterns;

/// <summary>
/// The Fractal pattern. Outputs, the preview and the monitors draw with a runtime shader —
/// full resolution at the display's rate on the graphics card; NDI and thumbnails, and any
/// sink whose shader will not compile, draw the same view on the CPU at a modest resolution
/// and scale it up. Both read the one view (<see cref="FractalView"/>) and the one sound
/// channel (<see cref="AudioLevels"/>), so every sink shows the same moment.
/// </summary>
public sealed class FractalPattern : IPatternRenderer
{
    /// <summary>The shader's loop bound: SkSL wants a constant, and past this the GPU cost is not worth the detail.</summary>
    public const int ShaderIterationCap = 256;

    private const string Common = """
        uniform float2 res;
        uniform float2 center;
        uniform float upp;
        uniform float2 c;
        uniform float iters;
        uniform float offset;
        uniform float bright;
        uniform float ncol;
        uniform float3 p0;
        uniform float3 p1;
        uniform float3 p2;
        uniform float3 p3;
        uniform float3 p4;
        uniform float t;
        uniform float2 rot;
        uniform float warp;
        float2 plane(float2 px) {
            float2 d = px - res * 0.5;
            d = float2(d.x * rot.x - d.y * rot.y, d.x * rot.y + d.y * rot.x);
            return center + d * upp;
        }
        float3 pick(float i) {
            if (i < 0.5) return p0;
            if (i < 1.5) return p1;
            if (i < 2.5) return p2;
            if (i < 3.5) return p3;
            return p4;
        }
        float3 pal(float u) {
            float f = fract(u) * ncol;
            float i = floor(f);
            float k = f - i;
            float j = i + 1.0;
            if (j >= ncol) j = 0.0;
            return mix(pick(i), pick(j), k);
        }
        half4 finish(float v, float inside) {
            if (inside > 0.5) return half4(0.0, 0.0, 0.0, 1.0);
            float3 col = clamp(pal(v * 3.0 + offset) * bright, 0.0, 1.0);
            return half4(half3(col), 1.0);
        }

        """;

    private const string Mandelbrot = Common + """
        half4 main(float2 px) {
            float2 c0 = plane(px);
            float2 z = float2(0.0, 0.0);
            float n = 0.0;
            float inside = 1.0;
            for (int i = 0; i < 256; ++i) {
                if (float(i) >= iters) break;
                z = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c0;
                float m = dot(z, z);
                if (m > 16.0) { n = float(i) + 1.0 - log2(log2(m) * 0.5); inside = 0.0; break; }
            }
            return finish(clamp(n / iters, 0.0, 1.0), inside);
        }
        """;

    private const string Julia = Common + """
        half4 main(float2 px) {
            float2 z = plane(px);
            float n = 0.0;
            float inside = 1.0;
            for (int i = 0; i < 256; ++i) {
                if (float(i) >= iters) break;
                z = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c;
                float m = dot(z, z);
                if (m > 16.0) { n = float(i) + 1.0 - log2(log2(m) * 0.5); inside = 0.0; break; }
            }
            return finish(clamp(n / iters, 0.0, 1.0), inside);
        }
        """;

    private const string BurningShip = Common + """
        half4 main(float2 px) {
            float2 c0 = plane(px);
            float2 z = float2(0.0, 0.0);
            float n = 0.0;
            float inside = 1.0;
            for (int i = 0; i < 256; ++i) {
                if (float(i) >= iters) break;
                z = float2(abs(z.x), abs(z.y));
                z = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c0;
                float m = dot(z, z);
                if (m > 16.0) { n = float(i) + 1.0 - log2(log2(m) * 0.5); inside = 0.0; break; }
            }
            return finish(clamp(n / iters, 0.0, 1.0), inside);
        }
        """;

    private const string Newton = Common + """
        half4 main(float2 px) {
            float2 z = plane(px);
            float n = 0.0;
            for (int i = 0; i < 256; ++i) {
                if (float(i) >= iters) break;
                float2 z2 = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y);
                float2 z3 = float2(z2.x * z.x - z2.y * z.y, z2.x * z.y + z2.y * z.x);
                float2 f = z3 - float2(1.0, 0.0);
                if (dot(f, f) < 0.000001) break;
                float2 d = 3.0 * z2;
                float dd = dot(d, d) + 0.000001;
                float2 q = float2(f.x * d.x + f.y * d.y, f.y * d.x - f.x * d.y) / dd;
                z = z - q;
                n = float(i) + 1.0;
            }
            float ang = atan(z.y, z.x);
            float root = floor(mod(ang / 6.2831853 + 1.0 + 1.0 / 6.0, 1.0) * 3.0);
            float shade = 1.0 - clamp(n / iters, 0.0, 1.0) * 0.8;
            float index = mod(root + floor(offset * ncol), ncol);
            float3 col = clamp(pick(index) * shade * bright, 0.0, 1.0);
            return half4(half3(col), 1.0);
        }
        """;

    private const string DomainWarp = Common + """
        float hash(float2 p) {
            p = fract(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return fract(p.x * p.y);
        }
        float noise(float2 p) {
            float2 i = floor(p);
            float2 f = fract(p);
            float2 u = f * f * (3.0 - 2.0 * f);
            float a = hash(i);
            float b = hash(i + float2(1.0, 0.0));
            float c2 = hash(i + float2(0.0, 1.0));
            float d = hash(i + float2(1.0, 1.0));
            return mix(mix(a, b, u.x), mix(c2, d, u.x), u.y);
        }
        float fbm(float2 p) {
            float v = 0.0;
            float amp = 0.5;
            for (int i = 0; i < 4; ++i) {
                v += amp * noise(p);
                p = p * 2.03 + float2(17.1, 9.3);
                amp *= 0.5;
            }
            return v;
        }
        half4 main(float2 px) {
            float2 p = plane(px) * 1.5;
            float q = fbm(p + float2(t * 0.11, t * 0.07));
            float r = fbm(p + (3.0 + 3.0 * warp) * q + float2(1.7, 9.2) - t * 0.05);
            float v = fbm(p + 3.0 * r);
            float3 col = clamp(pal(v * 1.5 + offset) * bright, 0.0, 1.0);
            return half4(half3(col), 1.0);
        }
        """;

    public static string SourceFor(FractalKind kind) => kind switch
    {
        FractalKind.Julia => Julia,
        FractalKind.BurningShip => BurningShip,
        FractalKind.Newton => Newton,
        FractalKind.DomainWarp => DomainWarp,
        _ => Mandelbrot,
    };

    /// <summary>The sinks that draw on the graphics card; the rest take the CPU path.</summary>
    public static bool UsesShader(SinkKind sink) => sink is SinkKind.Output or SinkKind.Preview or SinkKind.Monitor;

    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Fractal;
        var sink = f.Sink;
        var palette = Palette(sink, o.ColorsCsv);
        var audio = o.AudioSource == AudioSourceKind.None ? AudioLevelFrame.Zero : AudioLevels.Read(f.Ctx.UtcNow);
        var surge = EffectImpulses.SurgeAt(f.Ctx.Time);
        int w = f.W, h = f.H;

        // A shaking picture is drawn a little larger and offset, so no edge ever shows.
        var shaking = surge.Shake > 0.01f;
        var dest = SKRect.Create(0, 0, w, h);
        if (shaking)
        {
            var (dx, dy) = Particles.ParticleSim.ShakeOffset(surge, h);
            var margin = surge.Shake * 0.025f * h + 1;
            dest = SKRect.Create(-margin, -margin, w + 2 * margin, h + 2 * margin);
            c.Save();
            c.Translate(dx, dy);
        }

        // The quality ladder's factor, read once per frame so every sink draws the same set.
        var quality = Services.QualityLadder.Shared.Factor;
        if (UsesShader(f.Ctx.Sink))
        {
            var view = FractalView.Of(o, f.Ctx.Time, audio, ShaderIterationCap, surge, quality);
            if (TryDrawShader(c, sink, o.Kind, palette, in view, w, h, dest, f.Paints))
            {
                if (shaking) c.Restore();
                EffectFlash.Draw(c, w, h, surge.Flash, f.Paints);
                return;
            }
        }

        var cpuView = FractalView.Of(o, f.Ctx.Time, audio, surge: surge, quality: quality);
        var size = FractalRaster.SizeFor(o.Quality, f.Canvas, Services.QualityLadder.RasterScale(quality));
        sink.Fractal = FractalRaster.Render(sink.Fractal, size, o.Kind, palette, cpuView);
        using var image = SKImage.FromBitmap(sink.Fractal.Bitmap);
        if (image is not null) c.DrawImage(image, dest, DrawUtil.Smooth, f.Paints.Fill(SKColors.White));
        if (shaking) c.Restore();
        EffectFlash.Draw(c, w, h, surge.Flash, f.Paints);
    }

    /// <summary>
    /// Draws the view through the sink's runtime shader over <paramref name="dest"/>: a picture
    /// <paramref name="w"/> × <paramref name="h"/> pixels across, centred on the view, in the
    /// canvas's current local space — a caller drawing a box translates to its corner first. The
    /// lower-third fractal element draws through here too, so the outputs, the preview and the
    /// monitors never raster a fractal. False when the sink has no shader for the kind (it would
    /// not compile there); the caller rasters instead.
    /// </summary>
    public static bool TryDrawShader(SKCanvas c, SinkState sink, FractalKind kind, SKColor[] palette, in FractalView view, float w, float h, SKRect dest, PaintCache paints)
    {
        if (Shader(sink, kind) is not { } fx) return false;
        if (palette.Length == 0) palette = new[] { SKColors.White };
        var uniforms = new SKRuntimeEffectUniforms(fx)
        {
            ["res"] = new[] { w, h },
            ["center"] = new[] { (float)view.CenterX, (float)view.CenterY },
            ["upp"] = (float)view.UnitsPerPixel(Math.Max(1, (int)Math.Round(h))),
            ["c"] = new[] { (float)view.JuliaRe, (float)view.JuliaIm },
            ["iters"] = (float)view.Iterations,
            ["offset"] = (float)view.PaletteOffset,
            ["bright"] = (float)view.Brightness,
            ["ncol"] = (float)Math.Min(palette.Length, PaletteColors),
            ["p0"] = Rgb(palette, 0),
            ["p1"] = Rgb(palette, 1),
            ["p2"] = Rgb(palette, 2),
            ["p3"] = Rgb(palette, 3),
            ["p4"] = Rgb(palette, 4),
            ["t"] = (float)view.Time,
            ["rot"] = new[] { (float)Math.Cos(view.Angle), (float)Math.Sin(view.Angle) },
            ["warp"] = (float)view.Warp,
        };
        using var shader = fx.ToShader(uniforms);
        var paint = paints.Fill(SKColors.White);
        paint.Shader = shader;
        c.DrawRect(dest, paint);
        paint.Shader = null;
        return true;
    }

    /// <summary>How many colours a palette carries: the shader has five slots, and the CPU path reads the same five so every sink agrees.</summary>
    public const int PaletteColors = 5;

    /// <summary>The colours a fractal cycles through: the list as written, capped at <see cref="PaletteColors"/>, white when it is empty.</summary>
    public static SKColor[] PaletteOf(string csv)
    {
        var parsed = ColorUtil.ParseList(csv, SKColors.White);
        return parsed.Length == 0 ? new[] { SKColors.White } : parsed.Take(PaletteColors).ToArray();
    }

    private static SKRuntimeEffect? Shader(SinkState sink, FractalKind kind)
    {
        if (sink.FractalEffects.TryGetValue(kind, out var ready)) return ready;
        if (sink.FractalUnavailable.Contains(kind)) return null;
        var effect = SKRuntimeEffect.CreateShader(SourceFor(kind), out var errors);
        if (effect is null)
        {
            Services.Log.Warn($"Fractal shader ({kind}) unavailable — drawing on the CPU: {errors}");
            sink.FractalUnavailable.Add(kind);
            return null;
        }
        sink.FractalEffects[kind] = effect;
        return effect;
    }

    private static SKColor[] Palette(SinkState sink, string csv)
    {
        if (sink.FractalColorsKey != csv || sink.FractalColors.Length == 0)
        {
            sink.FractalColors = PaletteOf(csv);
            sink.FractalColorsKey = csv;
        }
        return sink.FractalColors;
    }

    private static float[] Rgb(SKColor[] palette, int i)
    {
        var col = palette[Math.Min(i, palette.Length - 1)];
        return new[] { col.Red / 255f, col.Green / 255f, col.Blue / 255f };
    }
}
