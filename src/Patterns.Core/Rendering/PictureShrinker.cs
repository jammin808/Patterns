using Patterns.Core.Media;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// The render side's picture codec, offered to the core through <see cref="Pictures.Shrinker"/>:
/// decode, fit the long side within the limit, re-encode — PNG when preferred and it fits under
/// the ceiling (a screenshot's text stays crisp), JPEG at falling quality otherwise. Knows
/// nothing of who asked.
/// </summary>
public static class PictureShrinker
{
    public static ShrunkPicture? Shrink(byte[] bytes, int maxSide, int maxBytes, bool preferPng)
    {
        SKBitmap? decoded;
        try
        {
            decoded = SKBitmap.Decode(bytes);   // null for bytes no codec knows; some builds throw instead
        }
        catch (ArgumentException)
        {
            decoded = null;
        }
        using var _ = decoded;
        if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0) return null;
        var side = Math.Max(decoded.Width, decoded.Height);
        var scale = side > maxSide ? maxSide / (double)side : 1.0;
        var w = Math.Max(1, (int)Math.Round(decoded.Width * scale));
        var h = Math.Max(1, (int)Math.Round(decoded.Height * scale));
        using var sized = scale < 1.0 ? decoded.Resize(new SKImageInfo(w, h, decoded.ColorType, decoded.AlphaType), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) : null;
        var bitmap = sized ?? decoded;
        using var image = SKImage.FromBitmap(bitmap);
        if (image is null) return null;
        if (preferPng)
        {
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            if (png is not null && png.Size <= maxBytes) return new ShrunkPicture(png.ToArray(), "image/png", decoded.Width, decoded.Height, w, h);
        }
        foreach (var quality in new[] { 85, 70, 55 })
        {
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (jpeg is null) break;
            if (jpeg.Size <= maxBytes || quality == 55) return new ShrunkPicture(jpeg.ToArray(), "image/jpeg", decoded.Width, decoded.Height, w, h);
        }
        return null;
    }
}
