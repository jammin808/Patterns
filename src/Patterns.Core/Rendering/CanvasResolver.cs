using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Rendering;

public readonly record struct LedLayout(int Columns, int Rows, int TileWidth, int TileHeight, SKSizeI Canvas)
{
    public int TileCount => Columns * Rows;
}

public readonly record struct BlendLayout(int Projectors, int NativeW, int NativeH, int Overlap, BlendOrientation Orientation, SKSizeI Canvas)
{
    /// <summary>Origin of projector i along the blend axis.</summary>
    public int OriginOf(int i) => i * (AxisNative - Overlap);
    public int AxisNative => Orientation == BlendOrientation.Horizontal ? NativeW : NativeH;
}

/// <summary>Pure layout math shared by renderers, the UI (readouts) and tests.</summary>
public static class CanvasResolver
{
    public static LedLayout Led(LedWallOptions o)
    {
        if (o.DefineByCanvas)
        {
            var cols = (o.CanvasWidth + o.TileWidth - 1) / o.TileWidth;
            var rows = (o.CanvasHeight + o.TileHeight - 1) / o.TileHeight;
            return new LedLayout(cols, rows, o.TileWidth, o.TileHeight, new SKSizeI(o.CanvasWidth, o.CanvasHeight));
        }
        return new LedLayout(
            o.Columns, o.Rows, o.TileWidth, o.TileHeight,
            new SKSizeI(o.Columns * o.TileWidth, o.Rows * o.TileHeight));
    }

    public static SKSizeI VideoWall(VideoWallOptions o)
    {
        var (w, h) = o.Portrait ? (o.ElementHeight, o.ElementWidth) : (o.ElementWidth, o.ElementHeight);
        return new SKSizeI(o.Columns * w, o.Rows * h);
    }

    public static BlendLayout Blend(BlendOptions o)
    {
        var axisNative = o.Orientation == BlendOrientation.Horizontal ? o.NativeWidth : o.NativeHeight;
        var overlap = Math.Min(o.OverlapPx, axisNative - 8);
        var total = o.Projectors * axisNative - (o.Projectors - 1) * overlap;
        var canvas = o.Orientation == BlendOrientation.Horizontal
            ? new SKSizeI(total, o.NativeHeight)
            : new SKSizeI(o.NativeWidth, total);
        return new BlendLayout(o.Projectors, o.NativeWidth, o.NativeHeight, overlap, o.Orientation, canvas);
    }

    /// <summary>The pattern canvas size for a config rendered against a reference (screen/union/NDI) size.</summary>
    public static SKSizeI Resolve(PatternConfig p, SKSizeI reference)
    {
        return p.Kind switch
        {
            PatternKind.LedWall => Led(p.LedWall).Canvas,
            PatternKind.VideoWall => VideoWall(p.VideoWall),
            PatternKind.ProjectionBlend => Blend(p.Blend).Canvas,
            _ => p.Canvas.FollowOutput || p.Canvas.Width <= 0 || p.Canvas.Height <= 0
                ? reference
                : new SKSizeI(p.Canvas.Width, p.Canvas.Height),
        };
    }

    /// <summary>
    /// Maps the canvas into reference space: uniform fit (letterboxed) or centred 1:1.
    /// Returns offset and scale such that ref = canvasPoint * scale + offset.
    /// </summary>
    public static (SKPoint Offset, float Scale) MapToReference(SKSizeI canvas, SKSizeI reference, CanvasScaleMode mode)
    {
        if (canvas == reference) return (new SKPoint(0, 0), 1f);
        if (mode == CanvasScaleMode.OneToOne)
        {
            var ox = MathF.Round((reference.Width - canvas.Width) / 2f);
            var oy = MathF.Round((reference.Height - canvas.Height) / 2f);
            return (new SKPoint(ox, oy), 1f);
        }
        var s = Math.Min((float)reference.Width / canvas.Width, (float)reference.Height / canvas.Height);
        var w = canvas.Width * s;
        var h = canvas.Height * s;
        return (new SKPoint((reference.Width - w) / 2f, (reference.Height - h) / 2f), s);
    }
}
