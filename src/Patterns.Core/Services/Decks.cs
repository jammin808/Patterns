using System.Globalization;
using SkiaSharp;

namespace Patterns.Core.Services;

/// <summary>
/// Decks — PDF presentations — in the workflow: how a page is named in a cue or on the wire, and
/// how big a page is rendered for the rig it will be shown on. Pure.
/// </summary>
public static class Decks
{
    /// <summary>The smallest raster a page is rendered at, whatever the rig: a page never looks soft on a 1080p screen.</summary>
    public static readonly SKSizeI MinimumRaster = new(1920, 1080);

    /// <summary>The largest raster a page is rendered at: a 4K wall gets a 4K page, a bigger canvas a fitted 4K one.</summary>
    public static readonly SKSizeI MaximumRaster = new(4096, 4096);

    /// <summary>
    /// A page reference as a cue, the wire or a phone writes it: a number (1-based), or a word —
    /// first, last, next, prev / previous / back. The number is 0 for a word.
    /// </summary>
    public static bool TryParsePage(string? value, out int page, out string word)
    {
        page = 0;
        word = "";
        var t = (value ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return false;
        switch (t)
        {
            case "first" or "start" or "home": word = "first"; return true;
            case "last" or "end": word = "last"; return true;
            case "next" or "forward": word = "next"; return true;
            case "prev" or "previous" or "back": word = "prev"; return true;
        }
        if (t.StartsWith("page ", StringComparison.Ordinal) || t.StartsWith("p", StringComparison.Ordinal) && t.Length > 1 && char.IsAsciiDigit(t[1]))
        {
            t = t.TrimStart('p').TrimStart("age ".ToCharArray()).Trim();
        }
        if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1) return false;
        page = n;
        return true;
    }

    /// <summary>"page 5", "the first page", "the last page", "the next page", "the previous page"; the text itself when it is none of those.</summary>
    public static string DescribePage(string? value)
    {
        if (!TryParsePage(value, out var page, out var word)) return (value ?? "").Trim();
        return word switch
        {
            "first" => "the first page",
            "last" => "the last page",
            "next" => "the next page",
            "prev" => "the previous page",
            _ => $"page {page.ToString(CultureInfo.InvariantCulture)}",
        };
    }

    /// <summary>The page a reference lands on in a deck of <paramref name="count"/> pages from <paramref name="current"/>; 0 when the reference is not a page.</summary>
    public static int Resolve(string? value, int current, int count)
    {
        if (!TryParsePage(value, out var page, out var word) || count <= 0) return 0;
        var target = word switch
        {
            "first" => 1,
            "last" => count,
            "next" => current + 1,
            "prev" => current - 1,
            _ => page,
        };
        return Math.Clamp(target, 1, count);
    }

    /// <summary>
    /// The raster a deck's pages are rendered at for a rig: the largest target's raster, never
    /// under <see cref="MinimumRaster"/>, never over <see cref="MaximumRaster"/>. A page is then
    /// fitted into it at its own shape, so the sharpest screen gets a sharp page.
    /// </summary>
    public static SKSizeI RasterCeiling(RigGeometry? rig)
    {
        var w = MinimumRaster.Width;
        var h = MinimumRaster.Height;
        if (rig is not null)
        {
            foreach (var target in rig.Targets)
            {
                var size = rig.RasterSizeOf(target);
                w = Math.Max(w, size.Width);
                h = Math.Max(h, size.Height);
            }
        }
        return new SKSizeI(Math.Min(w, MaximumRaster.Width), Math.Min(h, MaximumRaster.Height));
    }

    /// <summary>The raster a page of <paramref name="shape"/> is rendered at inside <paramref name="ceiling"/>: as large as fits, its shape kept, at least 1×1.</summary>
    public static SKSizeI FitInto(SKSize shape, SKSizeI ceiling)
    {
        var sw = shape.Width > 0 ? shape.Width : 1;
        var sh = shape.Height > 0 ? shape.Height : 1;
        var scale = Math.Min(ceiling.Width / sw, ceiling.Height / sh);
        return new SKSizeI(Math.Max(1, (int)Math.Round(sw * scale)), Math.Max(1, (int)Math.Round(sh * scale)));
    }
}
