using Patterns.Core.Media;
using SkiaSharp;

namespace Patterns.Rendering.Media;

/// <summary>The crop's fractions as the canvas rectangle a source draws from: the render side's face of <see cref="FrameCrop"/>.</summary>
public static class FrameCropSkia
{
    /// <summary>The part of a frame of this size that survives the crop, in the frame's own pixels.</summary>
    public static SKRect SourceRect(this in FrameCrop crop, SKSizeI frame)
    {
        var (l, t, r, b) = crop.Fractions();
        return new SKRect(
            (float)(frame.Width * l),
            (float)(frame.Height * t),
            (float)(frame.Width * (1 - r)),
            (float)(frame.Height * (1 - b)));
    }

    /// <summary>The shape the cropped picture has — what the inset should be sized to.</summary>
    public static float AspectOf(this in FrameCrop crop, SKSizeI frame)
    {
        var rect = crop.SourceRect(frame);
        return rect.Height > 0 && rect.Width > 0 ? rect.Width / rect.Height : 16f / 9f;
    }
}
