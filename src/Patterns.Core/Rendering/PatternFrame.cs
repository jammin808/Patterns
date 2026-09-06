using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>Bundle handed to a pattern renderer for one frame. Allocation-free (readonly struct).</summary>
public readonly struct PatternFrame
{
    public required ShowSnapshot Snapshot { get; init; }
    /// <summary>The pattern being drawn on this sink (independent screens may differ from program).</summary>
    public required PatternConfig Config { get; init; }
    public required RenderContext Ctx { get; init; }
    public required SinkState Sink { get; init; }
    /// <summary>Resolved pattern canvas — renderers draw in [0..W)×[0..H).</summary>
    public required SKSizeI Canvas { get; init; }
    public required Palette Palette { get; init; }

    /// <summary>
    /// The dead strips of the target this frame draws (bezels, the air between LED pillars);
    /// <see cref="GapMap.Empty"/> for a plain screen. The wall patterns lay their tiles out
    /// across them when they were built for this very raster.
    /// </summary>
    public GapMap Gaps { get => _gaps ?? GapMap.Empty; init => _gaps = value; }

    private readonly GapMap? _gaps;

    public int W => Canvas.Width;
    public int H => Canvas.Height;
    public PaintCache Paints => Sink.Paints;
    public SKColor Color(string? hex, SKColor fallback) => Snapshot.Color(hex, fallback);

    /// <summary>
    /// Device pixels per canvas pixel for this frame: the sink's own scale (<see cref="RenderContext.DeviceScale"/>)
    /// times the canvas's map onto the reference — 1 on an output, 0.05 on a wall tile showing a
    /// 1920-wide target. Set by the engine; 0 (a frame built by hand) reads as 1.
    /// </summary>
    public float DeviceScale { get; init; }

    /// <summary>
    /// A line width in canvas pixels that is at least one device pixel on this sink: a one-pixel
    /// line drawn at 0.05 device pixels on a wall tile drops out (no pixel centre falls inside it),
    /// so it widens to the tile's own pixel; on an output nothing changes.
    /// </summary>
    public int Hairline(int px) => HairlineFor(DeviceScale, px);

    /// <summary>Whether a spacing in canvas pixels still reads as a spacing on this sink (at least <paramref name="minDevicePx"/> apart) — minor lines closer than that on a tile would fill it, so they are left out.</summary>
    public bool Resolves(float canvasPx, float minDevicePx = 3f) => ResolvesAt(DeviceScale, canvasPx, minDevicePx);

    /// <summary>The hairline rule on its own: <paramref name="px"/> or the canvas pixels one device pixel spans, whichever is more.</summary>
    public static int HairlineFor(float deviceScale, int px)
    {
        var scale = deviceScale > 0 ? deviceScale : 1f;
        return Math.Max(px, (int)MathF.Ceiling(1f / scale - 1e-4f));
    }

    /// <summary>The spacing rule on its own.</summary>
    public static bool ResolvesAt(float deviceScale, float canvasPx, float minDevicePx = 3f)
    {
        var scale = deviceScale > 0 ? deviceScale : 1f;
        return canvasPx * scale >= minDevicePx;
    }
}

public interface IPatternRenderer
{
    void Render(SKCanvas canvas, in PatternFrame f);
}
