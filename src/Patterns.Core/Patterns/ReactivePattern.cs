using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Patterns;

/// <summary>
/// The reactive scenes: a curated family of sound-reactive pictures for a walk-in, an interlude or
/// an exhibition stand — a tunnel, a kaleidoscope, plasma, rings, a vortex, a star field.
///
/// Two paths, one picture. Outputs, the preview and the desk's monitors draw a single full-canvas
/// runtime shader on the graphics card. NDI, the stream and the thumbnails have no card to draw on
/// — they are raster surfaces — so they take the CPU twin in <see cref="ReactiveRaster"/> at a
/// modest working size and upscale. Both read the same <see cref="ReactiveView"/> and the same
/// arithmetic (<see cref="ReactiveField"/>), so the stream shows the wall's picture; a test holds
/// the two within a pixel bound.
///
/// The sound moves a scene and never drives it: with no capture at all — every machine that is not
/// Windows, and every walk-in before the music starts — the scenes run on the show clock and look
/// finished. And every whole-screen light change goes through the sink's
/// <see cref="FlashGuard"/>, so a beat cannot become a strobe.
/// </summary>
public sealed class ReactivePattern : IPatternRenderer
{
    /// <summary>The sinks that draw on the graphics card; the rest take the CPU path.</summary>
    public static bool UsesShader(SinkKind sink) => sink is SinkKind.Output or SinkKind.Preview or SinkKind.Monitor;

    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Reactive;
        var sink = f.Sink;
        var palette = Palette(sink, f, o);
        var audio = o.AudioSource == AudioSourceKind.None ? AudioLevelFrame.Zero : AudioLevels.Read(f.Ctx.UtcNow);
        var surge = EffectImpulses.SurgeAt(f.Ctx.Time);
        var view = ReactiveView.Of(o, f.Ctx.Time, audio, surge);
        int w = f.W, h = f.H;

        // A shaking picture is drawn a little larger and offset, so no edge ever shows — the same
        // treatment the fractals and the particles give a sting's shake.
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

        // The ladder's factor, read once a frame so every sink draws the same set.
        var quality = Services.QualityLadder.Shared.Factor;
        var drawn = false;
        if (UsesShader(f.Ctx.Sink))
        {
            drawn = TryDrawShader(c, sink, o.Scene, palette, in view, w, h, dest, f.Paints);
        }

        if (!drawn)
        {
            var size = ReactiveRaster.SizeFor(o.Quality, f.Canvas, Services.QualityLadder.RasterScale(quality));
            sink.Reactive = ReactiveRaster.Render(sink.Reactive, size, o.Scene, palette, in view);
            using var image = SKImage.FromBitmap(sink.Reactive.Bitmap);
            if (image is not null) c.DrawImage(image, dest, DrawUtil.Smooth, f.Paints.Fill(SKColors.White));
        }

        if (shaking) c.Restore();
        EffectFlash.Draw(c, w, h, surge.Flash, f.Paints, sink.Flash, f.Ctx.Time);
    }

    /// <summary>
    /// Draws the scene through the sink's runtime shader over <paramref name="dest"/>. False when
    /// the sink has no shader for the scene (it would not compile there); the caller rasters instead.
    /// </summary>
    public static bool TryDrawShader(
        SKCanvas c, SinkState sink, ReactiveScene scene, SKColor[] palette, in ReactiveView view,
        float w, float h, SKRect dest, PaintCache paints)
    {
        if (Shader(sink, scene) is not { } fx) return false;
        if (palette.Length == 0) palette = new[] { SKColors.White };
        var uniforms = new SKRuntimeEffectUniforms(fx)
        {
            ["res"] = new[] { w, h },
            ["t"] = (float)view.Time,
            ["rot"] = new[] { (float)Math.Cos(view.Angle), (float)Math.Sin(view.Angle) },
            ["depth"] = (float)view.Depth,
            ["sym"] = (float)Math.Max(2, view.Symmetry),
            ["level"] = view.Level,
            ["low"] = view.Low,
            ["amount"] = view.Amount,
            ["bright"] = view.Brightness,
            ["ncol"] = (float)Math.Min(palette.Length, ReactiveField.PaletteColors),
            ["p0"] = Rgb(palette, 0),
            ["p1"] = Rgb(palette, 1),
            ["p2"] = Rgb(palette, 2),
            ["p3"] = Rgb(palette, 3),
            ["p4"] = Rgb(palette, 4),
        };
        using var shader = fx.ToShader(uniforms);
        var paint = paints.Fill(SKColors.White);
        paint.Shader = shader;
        c.DrawRect(dest, paint);
        paint.Shader = null;
        return true;
    }

    private static float[] Rgb(SKColor[] palette, int i)
    {
        var c = palette[Math.Min(i, palette.Length - 1)];
        return new[] { c.Red / 255f, c.Green / 255f, c.Blue / 255f };
    }

    private static SKRuntimeEffect? Shader(SinkState sink, ReactiveScene scene)
    {
        if (sink.ReactiveEffects.TryGetValue(scene, out var ready)) return ready;
        if (sink.ReactiveUnavailable.Contains(scene)) return null;
        var effect = SKRuntimeEffect.CreateShader(SourceFor(scene), out var errors);
        if (effect is null)
        {
            Services.Log.Warn($"Reactive shader ({scene}) unavailable — drawing on the CPU: {errors}");
            sink.ReactiveUnavailable.Add(scene);
            return null;
        }
        sink.ReactiveEffects[scene] = effect;
        return effect;
    }

    /// <summary>The scene's colours: the show's brand kit, or the list the operator wrote.</summary>
    private static SKColor[] Palette(SinkState sink, in PatternFrame f, ReactiveOptions o)
    {
        var csv = o.UseBrandColors ? BrandCsv(f) : o.ColorsCsv;
        if (sink.ReactiveColorsKey != csv || sink.ReactiveColors.Length == 0)
        {
            sink.ReactiveColors = ReactiveRaster.PaletteOf(csv);
            sink.ReactiveColorsKey = csv;
        }
        return sink.ReactiveColors;
    }

    /// <summary>The brand kit as a scene palette: the background first, so the picture sits in the client's own dark.</summary>
    private static string BrandCsv(in PatternFrame f)
    {
        var b = f.Snapshot.State.Brand;
        return string.Join(",", new[] { b.BackgroundColor, b.PrimaryColor, b.SecondaryColor, b.AccentColor, b.TextColor }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    // ---- the shaders -------------------------------------------------------------------------
    //
    // Each source is the same arithmetic as ReactiveField.Sample for that scene, written in the
    // shader's own words. They are kept literally parallel, line for line, because a test draws
    // both and holds them within a pixel bound: change one and change the other.

    private const string Prelude = """
        uniform float2 res;
        uniform float t;
        uniform float2 rot;      // cos, sin of the picture's turn
        uniform float depth;
        uniform float sym;
        uniform float level;
        uniform float low;
        uniform float amount;
        uniform float bright;
        uniform float ncol;
        uniform half3 p0; uniform half3 p1; uniform half3 p2; uniform half3 p3; uniform half3 p4;

        const float TAU = 6.28318530718;

        // The pixel's place with the centre at 0 and the height at 1, turned by the rotation.
        float2 place(float2 frag) {
            float2 p = (frag - res * 0.5) / res.y;
            return float2(p.x * rot.x - p.y * rot.y, p.x * rot.y + p.y * rot.x);
        }

        half3 slot(float i) {
            if (i < 0.5) return p0;
            if (i < 1.5) return p1;
            if (i < 2.5) return p2;
            if (i < 3.5) return p3;
            return p4;
        }

        // The palette at a point of the cycle, mixed smoothly and taken by the brightness — the
        // same wrap and the same smoothstep as ReactiveField.Map.
        half4 paint(float v) {
            float n = max(ncol, 1.0);
            float pos = fract(v) * n;
            float i = floor(pos);
            float k = pos - i;
            k = k * k * (3.0 - 2.0 * k);
            half3 a = slot(mod(i, n));
            half3 b = slot(mod(i + 1.0, n));
            half3 c = mix(a, b, half(k)) * half(bright);
            return half4(clamp(c, 0.0, 1.0), 1.0);
        }

        float plasma(float x, float y, float warp) {
            float s = sin(x + t)
                    + sin(y * 0.9 - t * 0.7)
                    + sin((x + y) * 0.6 * warp + t * 0.4)
                    + sin(sqrt(x * x + y * y) * 1.3 * warp - t * 1.1);
            return fract(s * 0.125 + 0.5);
        }
        """;

    private const string PlasmaSource = Prelude + """

        half4 main(float2 frag) {
            float2 p = place(frag);
            return paint(plasma(p.x * 6.0, p.y * 6.0, 0.5 + depth));
        }
        """;

    private const string TunnelSource = Prelude + """

        half4 main(float2 frag) {
            float2 p = place(frag);
            float r = length(p);
            float a = atan(p.y, p.x);
            float d = 0.15 + depth * 0.35;
            float spokes = sin(a * sym + t * 0.4) * 0.06 * depth;
            return paint(fract(d / max(r, 0.02) - t * 0.35 + spokes));
        }
        """;

    private const string KaleidoscopeSource = Prelude + """

        half4 main(float2 frag) {
            float2 p = place(frag);
            float r = length(p);
            float a = atan(p.y, p.x);
            float wedge = TAU / max(sym, 2.0);
            float folded = abs(fract(a / wedge + 0.5) - 0.5) * wedge;
            return paint(plasma(cos(folded) * r * 5.0, sin(folded) * r * 5.0, 0.5 + depth));
        }
        """;

    private const string PulseSource = Prelude + """

        half4 main(float2 frag) {
            float2 p = place(frag);
            float r = length(p);
            float rings = 4.0 + low * amount * 5.0;
            float edge = sin((r * rings - t * 0.5) * TAU) * 0.5 + 0.5;
            return paint(clamp(pow(edge, 1.5 + depth * 2.0), 0.0, 1.0));
        }
        """;

    private const string VortexSource = Prelude + """

        half4 main(float2 frag) {
            float2 p = place(frag);
            float r = length(p);
            float a = atan(p.y, p.x);
            float twist = a + r * (2.0 + depth * 8.0) + t * 0.25;
            return paint(fract(twist / TAU * sym * 0.5 + sin(r * 6.0 - t * 0.4) * 0.15));
        }
        """;

    private const string StarWarpSource = Prelude + """

        float hash(float n) { return fract(sin(n * 12.9898) * 43758.5453); }

        half4 main(float2 frag) {
            float2 p = place(frag);
            float r = length(p);
            float a = atan(p.y, p.x);
            float spokes = max(sym, 2.0) * 6.0;
            float lane = fract(a / TAU * spokes);
            float seed = floor(a / TAU * spokes);
            float along = fract(log(max(r, 0.015)) * 0.7 + t * 0.5 + hash(seed));
            float streak = along * along * along;
            float across = 1.0 - min(1.0, abs(lane - 0.5) * 6.0);
            return paint(clamp(streak * max(across, 0.0) * (0.55 + r * 1.2), 0.0, 1.0));
        }
        """;

    /// <summary>The shader source for a scene.</summary>
    public static string SourceFor(ReactiveScene scene) => scene switch
    {
        ReactiveScene.Tunnel => TunnelSource,
        ReactiveScene.Kaleidoscope => KaleidoscopeSource,
        ReactiveScene.Pulse => PulseSource,
        ReactiveScene.Vortex => VortexSource,
        ReactiveScene.StarWarp => StarWarpSource,
        _ => PlasmaSource,
    };
}
