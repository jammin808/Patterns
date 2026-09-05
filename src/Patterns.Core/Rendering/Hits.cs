using Patterns.Core.Media;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>The things a frame draws that the desk can take hold of: a drag moves them, a web page takes the clicks instead.</summary>
public enum HitKind
{
    Layer1,
    Layer2,
    Logo,
    Clock,
    Countdown,
    Message,
    Pip,
    /// <summary>A web page's picture — a press goes to the page as a click rather than moving anything.</summary>
    WebPage,
}

/// <summary>
/// One box a frame drew: in canvas pixels for layers and the canvas overlays, in viewport
/// pixels for the PiP inset (<paramref name="ViewportSpace"/>). Recorded in draw order, so the
/// last one under a point is the one on top. A web page's box also carries the page's mount
/// <paramref name="Key"/>, the <paramref name="Crop"/> its picture was drawn through, and — when
/// the picture was clipped to a layer's box — the visible <paramref name="Bounds"/>.
/// </summary>
public readonly record struct HitRect(HitKind Kind, SKRect Rect, bool ViewportSpace, string Key = "", FrameCrop Crop = default, SKRect? Bounds = null)
{
    /// <summary>Where a point counts as inside: the visible part when the picture was clipped.</summary>
    public bool Contains(SKPoint p) => (Bounds ?? Rect).Contains(p) && Rect.Contains(p);
}

/// <summary>
/// The maths from a point on a monitor pane to the picture it shows: device pixels → target
/// pixels (the pane letterboxes the target at <paramref name="Scale"/> after <paramref name="Dx"/>,
/// <paramref name="Dy"/>) → canvas pixels (the pattern letterboxes its canvas inside the target
/// at <paramref name="CanvasScale"/> after <paramref name="CanvasOffset"/>). Pure, so a drag's
/// arithmetic is unit tested and never guessed at.
/// </summary>
public readonly record struct PaneMap(SKSizeI Target, float Dx, float Dy, float Scale, SKPoint CanvasOffset, float CanvasScale, SKSizeI Canvas)
{
    public SKPoint ToTarget(SKPoint device) => new((device.X - Dx) / Scale, (device.Y - Dy) / Scale);

    public SKPoint ToCanvas(SKPoint device)
    {
        var t = ToTarget(device);
        return new SKPoint((t.X - CanvasOffset.X) / CanvasScale, (t.Y - CanvasOffset.Y) / CanvasScale);
    }

    /// <summary>A device-pixel movement as canvas pixels.</summary>
    public SKPoint CanvasDelta(SKPoint deviceDelta) => new(deviceDelta.X / Scale / CanvasScale, deviceDelta.Y / Scale / CanvasScale);

    /// <summary>A device-pixel movement as target (viewport) pixels.</summary>
    public SKPoint TargetDelta(SKPoint deviceDelta) => new(deviceDelta.X / Scale, deviceDelta.Y / Scale);
}

public static class HitTester
{
    /// <summary>
    /// The box on top under a device-pixel point, or null when the pointer is on the picture
    /// itself. With <paramref name="includeWeb"/> off, web pages are looked through — the desk holds
    /// Alt to take hold of a web layer's box rather than click into its page.
    /// </summary>
    public static HitRect? Find(IReadOnlyList<HitRect> hits, in PaneMap map, SKPoint device, bool includeWeb = true)
    {
        var canvasPoint = map.ToCanvas(device);
        var targetPoint = map.ToTarget(device);
        for (var i = hits.Count - 1; i >= 0; i--)
        {
            var h = hits[i];
            if (!includeWeb && h.Kind == HitKind.WebPage) continue;
            if (h.Contains(h.ViewportSpace ? targetPoint : canvasPoint)) return h;
        }
        return null;
    }
}

/// <summary>The maths between a web page's picture on the canvas and the page's own coordinates (0–1), through its crop. Pure.</summary>
public static class WebPointerMap
{
    /// <summary>Where a canvas point falls on the page, or null when it is outside the picture.</summary>
    public static SKPoint? ToPage(in HitRect hit, SKPoint canvasPoint)
    {
        if (hit.Rect.Width <= 0 || hit.Rect.Height <= 0 || !hit.Contains(canvasPoint)) return null;
        return ToPageUnbounded(in hit, canvasPoint);
    }

    /// <summary>The page point for a canvas point even outside the picture (clamped to the page) — a drag that leaves the box keeps its hold.</summary>
    public static SKPoint ToPageUnbounded(in HitRect hit, SKPoint canvasPoint)
    {
        var nx = hit.Rect.Width <= 0 ? 0 : (canvasPoint.X - hit.Rect.Left) / hit.Rect.Width;
        var ny = hit.Rect.Height <= 0 ? 0 : (canvasPoint.Y - hit.Rect.Top) / hit.Rect.Height;
        var (l, t, r, b) = Fractions(hit.Crop);
        var x = l + nx * (1 - l - r);
        var y = t + ny * (1 - t - b);
        return new SKPoint((float)Math.Clamp(x, 0, 1), (float)Math.Clamp(y, 0, 1));
    }

    /// <summary>Where a page point (0–1) sits inside the picture's box, or null when the crop hides it.</summary>
    public static SKPoint? ToRect(SKRect dest, in FrameCrop crop, SKPoint page)
    {
        var (l, t, r, b) = Fractions(crop);
        var w = 1 - l - r;
        var h = 1 - t - b;
        if (w <= 0 || h <= 0) return null;
        var nx = (page.X - l) / w;
        var ny = (page.Y - t) / h;
        if (nx < 0 || nx > 1 || ny < 0 || ny > 1) return null;
        return new SKPoint(dest.Left + (float)nx * dest.Width, dest.Top + (float)ny * dest.Height);
    }

    private static (double L, double T, double R, double B) Fractions(in FrameCrop c) => (
        Math.Clamp(c.LeftPct, 0, FrameCrop.MaxPct) / 100.0,
        Math.Clamp(c.TopPct, 0, FrameCrop.MaxPct) / 100.0,
        Math.Clamp(c.RightPct, 0, FrameCrop.MaxPct) / 100.0,
        Math.Clamp(c.BottomPct, 0, FrameCrop.MaxPct) / 100.0);
}
