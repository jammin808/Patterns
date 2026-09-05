using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>The things a frame draws that the desk can take hold of and drag.</summary>
public enum HitKind
{
    Layer1,
    Layer2,
    Logo,
    Clock,
    Countdown,
    Message,
    Pip,
}

/// <summary>
/// One box a frame drew: in canvas pixels for layers and the canvas overlays, in viewport
/// pixels for the PiP inset (<paramref name="ViewportSpace"/>). Recorded in draw order, so the
/// last one under a point is the one on top.
/// </summary>
public readonly record struct HitRect(HitKind Kind, SKRect Rect, bool ViewportSpace);

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
    /// <summary>The box on top under a device-pixel point, or null when the pointer is on the picture itself.</summary>
    public static HitRect? Find(IReadOnlyList<HitRect> hits, in PaneMap map, SKPoint device)
    {
        var canvasPoint = map.ToCanvas(device);
        var targetPoint = map.ToTarget(device);
        for (var i = hits.Count - 1; i >= 0; i--)
        {
            var h = hits[i];
            if (h.Rect.Contains(h.ViewportSpace ? targetPoint : canvasPoint)) return h;
        }
        return null;
    }
}
