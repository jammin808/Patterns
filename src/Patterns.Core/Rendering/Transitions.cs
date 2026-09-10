using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>What a transition needs to know about the change it is drawing, gathered once a frame.</summary>
public readonly record struct TransitionView(
    TransitionKind Kind,
    TransitionDirection Direction,
    ReactiveScene Scene,
    double Softness,
    SKColor DipColor,
    SKColor BrandPrimary,
    SKColor BrandSecondary,
    SKColor BrandBackground,
    double Progress,
    SKSizeI Size);

/// <summary>
/// How one picture becomes the next.
///
/// Everything here draws with plain canvas operations — a rect, a gradient, a translate, a bitmap
/// used as a mask — for one reason: three of this desk's sinks have a graphics card and three do
/// not, and a transition the stream cannot draw is a transition that shows the client something
/// different from the wall. Nothing here compiles a shader, allocates per frame beyond one small
/// matte, or reads a pixel back.
///
/// The shape every kind fits into is the one the engine already had: the incoming picture is
/// drawn, then the outgoing one is drawn over it inside a layer, and the kind decides what that
/// layer is masked or moved by. A kind that needs the outgoing picture underneath instead (a dip,
/// the brand stinger) says so, and the engine swaps the two.
/// </summary>
public static class Transitions
{
    /// <summary>The matte is drawn small and scaled up: a soft edge for free, and a cost that does not follow the wall's size.</summary>
    public const int MatteWidth = 256;

    /// <summary>Above this luminance a dip is a whole-screen light change and goes through the flash limit.</summary>
    public const float BrightDip = 0.5f;

    /// <summary>Smoothstep: nothing on a screen should start or stop moving abruptly.</summary>
    public static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// A dip and the brand stinger cover the screen and change the picture behind the cover, so
    /// the picture underneath is the outgoing one for the first half and the incoming one for the
    /// second. Every other kind draws the incoming picture and works on the outgoing one over it.
    /// </summary>
    public static bool CoversTheCut(TransitionKind kind) => kind is TransitionKind.Dip or TransitionKind.BrandStinger;

    /// <summary>For a covered cut: true while the picture underneath should still be the outgoing one.</summary>
    public static bool ShowsOutgoing(double progress) => progress < 0.5;

    /// <summary>
    /// How much of the cover is drawn: nothing at either end, everything across the middle. The
    /// plateau matters — the cut happens under it, and a cover that is only briefly complete shows
    /// a frame of the switch.
    /// </summary>
    public static double Cover(double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        var v = 1 - Math.Abs(2 * t - 1);
        return Ease(Math.Clamp(v * 1.45, 0, 1));
    }

    /// <summary>The luminance of a colour, 0–1 — what decides whether a dip counts as a light change.</summary>
    public static float Luma(SKColor c) => (0.2126f * c.Red + 0.7152f * c.Green + 0.0722f * c.Blue) / 255f;

    /// <summary>How far the picture slides for a push, in pixels, and which way.</summary>
    public static (float Dx, float Dy) PushBy(TransitionDirection direction, SKSizeI size, double amount)
    {
        var x = (float)(size.Width * amount);
        var y = (float)(size.Height * amount);
        return direction switch
        {
            TransitionDirection.Left => (-x, 0),
            TransitionDirection.Up => (0, -y),
            TransitionDirection.Down => (0, y),
            _ => (x, 0),
        };
    }

    /// <summary>
    /// The gradient a wipe is masked by: opaque where the outgoing picture still stands, clear
    /// where the incoming one has taken over, with a soft band between. The band is carried past
    /// both ends so a wipe is completely gone at 1 and completely there at 0.
    /// </summary>
    public static SKShader WipeShader(in TransitionView v)
    {
        var soft = (float)Math.Clamp(v.Softness, 0.001, 0.6);
        var t = (float)Ease(v.Progress) * (1 + soft) - soft * 0.5f;
        var from = Math.Clamp(t - soft * 0.5f, 0f, 1f);
        var to = Math.Clamp(t + soft * 0.5f, 0f, 1f);
        if (to <= from) to = Math.Min(1f, from + 0.0005f);
        var w = v.Size.Width;
        var h = v.Size.Height;
        var (start, end) = v.Direction switch
        {
            TransitionDirection.Left => (new SKPoint(w, 0), new SKPoint(0, 0)),
            TransitionDirection.Up => (new SKPoint(0, h), new SKPoint(0, 0)),
            TransitionDirection.Down => (new SKPoint(0, 0), new SKPoint(0, h)),
            _ => (new SKPoint(0, 0), new SKPoint(w, 0)),
        };
        // Clear where the wipe has passed, opaque where it has not.
        return SKShader.CreateLinearGradient(start, end,
            new[] { SKColors.Transparent, SKColors.White },
            new[] { from, to },
            SKShaderTileMode.Clamp);
    }

    /// <summary>
    /// The matte a reactive transition wipes with: the scene's own value at every point, 0–255,
    /// at a size that does not follow the wall's. Built once when a transition arms and read
    /// every frame with a moving threshold, so a 4K output and a thumbnail cost the same.
    /// </summary>
    public static byte[] Matte(ReactiveScene scene, SKSizeI size, double seed)
    {
        var w = Math.Max(2, size.Width);
        var h = Math.Max(2, size.Height);
        var field = new byte[w * h];
        var view = new ReactiveView(Time: seed, Depth: 0.5, Symmetry: 6, Angle: 0,
            Level: 0, Low: 0, Mid: 0, High: 0, Amount: 0, Brightness: 1);
        var scale = 1.0 / h;
        for (var y = 0; y < h; y++)
        {
            var py = (y + 0.5 - h / 2.0) * scale;
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var px = (x + 0.5 - w / 2.0) * scale;
                var s = ReactiveField.Sample(scene, px, py, in view);
                field[row + x] = (byte)Math.Clamp(s * 255, 0, 255);
            }
        }
        return field;
    }

    /// <summary>The matte's size for a viewport: small, and the picture's own shape so nothing is stretched.</summary>
    public static SKSizeI MatteSize(SKSizeI viewport)
    {
        var w = Math.Min(MatteWidth, Math.Max(2, viewport.Width));
        var h = Math.Max(2, (int)Math.Round(w * viewport.Height / (double)Math.Max(1, viewport.Width)));
        return new SKSizeI(w, h);
    }

    /// <summary>
    /// The matte at one moment, as premultiplied white pixels whose alpha is what to keep of the
    /// outgoing picture: opaque where the scene's value is still above the threshold, clear where
    /// it has fallen below, soft between. Writes into the caller's buffer — a transition allocates
    /// nothing per frame.
    /// </summary>
    public static void MatteAt(byte[] field, int[] pixels, double progress, double softness)
    {
        var soft = (float)Math.Clamp(softness, 0.004, 0.6);
        // Carried past both ends so the outgoing picture is whole at 0 and gone at 1.
        var t = (float)Ease(progress) * (1 + soft) - soft * 0.5f;
        var lo = t - soft * 0.5f;
        var span = Math.Max(1e-4f, soft);
        var n = Math.Min(field.Length, pixels.Length);
        for (var i = 0; i < n; i++)
        {
            var value = field[i] / 255f;
            // 1 where the scene is still above the wipe, 0 where it is below it.
            var keep = Math.Clamp((value - lo) / span, 0f, 1f);
            var a = (int)(keep * 255 + 0.5f);
            pixels[i] = (a << 24) | (a << 16) | (a << 8) | a; // premultiplied white
        }
    }

    /// <summary>
    /// The brand stinger's cover: the show's own identity swept over the cut. Two bars come in
    /// from the sides in the brand's primary and secondary, the background fills behind them, and
    /// the logo — when there is one — sits in the middle at the peak.
    /// </summary>
    public static void DrawBrandCover(SKCanvas canvas, in TransitionView v, SKImage? logo)
    {
        var cover = (float)Cover(v.Progress);
        if (cover <= 0) return;
        var w = v.Size.Width;
        var h = v.Size.Height;
        var half = w / 2f;
        var reach = half * cover;

        using var paint = new SKPaint { IsAntialias = false };
        // The ground behind the bars: it only shows once they have met, so it fades in over the
        // last of the sweep rather than appearing under them.
        var ground = (byte)Math.Clamp((cover - 0.6f) / 0.4f * 255, 0, 255);
        if (ground > 0)
        {
            paint.Color = v.BrandBackground.WithAlpha(ground);
            canvas.DrawRect(SKRect.Create(0, 0, w, h), paint);
        }
        paint.Color = v.BrandPrimary;
        canvas.DrawRect(new SKRect(0, 0, reach, h), paint);
        paint.Color = v.BrandSecondary;
        canvas.DrawRect(new SKRect(w - reach, 0, w, h), paint);

        if (logo is null || cover < 0.75f) return;
        // The mark only at the peak, and never larger than a third of the picture.
        var alpha = (byte)Math.Clamp((cover - 0.75f) / 0.25f * 255, 0, 255);
        var box = Math.Min(w, h) * 0.28f;
        var scale = Math.Min(box / Math.Max(1, logo.Width), box / Math.Max(1, logo.Height));
        var lw = logo.Width * scale;
        var lh = logo.Height * scale;
        using var mark = new SKPaint { Color = SKColors.White.WithAlpha(alpha), IsAntialias = true };
        canvas.DrawImage(logo, SKRect.Create((w - lw) / 2f, (h - lh) / 2f, lw, lh), mark);
    }

    /// <summary>The colour a dip goes through: the brand's background, or the one the show named.</summary>
    public static SKColor DipColorFor(TransitionConfig cfg, SKColor brandBackground)
        => cfg.DipUsesBrand ? brandBackground : ColorUtil.Parse(cfg.DipColor, SKColors.Black);
}
