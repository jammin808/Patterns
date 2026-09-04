using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Media;

/// <summary>
/// How much of a live frame to cut away on each side, as a share of the frame (0–45 % each, so
/// at least a tenth of every axis always survives). Pure: the rect maths a source draws with and
/// the shape the inset takes are both here, and both unit tested.
/// </summary>
public readonly record struct FrameCrop(double LeftPct, double TopPct, double RightPct, double BottomPct)
{
    public static readonly FrameCrop None = default;

    public const double MaxPct = 45;

    public bool Any => LeftPct > 0 || TopPct > 0 || RightPct > 0 || BottomPct > 0;

    public static FrameCrop From(PipOverlay pip)
        => new(pip.CropLeftPct, pip.CropTopPct, pip.CropRightPct, pip.CropBottomPct);

    /// <summary>The part of a frame of this size that survives the crop, in the frame's own pixels.</summary>
    public SKRect SourceRect(SKSizeI frame)
    {
        var l = Math.Clamp(LeftPct, 0, MaxPct) / 100.0;
        var r = Math.Clamp(RightPct, 0, MaxPct) / 100.0;
        var t = Math.Clamp(TopPct, 0, MaxPct) / 100.0;
        var b = Math.Clamp(BottomPct, 0, MaxPct) / 100.0;
        return new SKRect(
            (float)(frame.Width * l),
            (float)(frame.Height * t),
            (float)(frame.Width * (1 - r)),
            (float)(frame.Height * (1 - b)));
    }

    /// <summary>The shape the cropped picture has — what the inset should be sized to.</summary>
    public float AspectOf(SKSizeI frame)
    {
        var rect = SourceRect(frame);
        return rect.Height > 0 && rect.Width > 0 ? rect.Width / rect.Height : 16f / 9f;
    }
}
