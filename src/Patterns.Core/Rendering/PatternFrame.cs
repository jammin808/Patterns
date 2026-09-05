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
}

public interface IPatternRenderer
{
    void Render(SKCanvas canvas, in PatternFrame f);
}
