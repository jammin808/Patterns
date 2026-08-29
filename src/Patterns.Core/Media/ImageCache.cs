using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Media;

/// <summary>
/// Decoded-image cache shared by every sink (raster SKImages are immutable and safe to draw
/// from multiple threads). Keyed by path + write time so an updated file is picked up; failed
/// decodes are remembered so a broken path never re-decodes per frame.
/// </summary>
public static class ImageCache
{
    private const int Capacity = 10;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public SKImage? Image;
        public DateTime WriteTimeUtc;
        public long LastUse;
    }

    private static long _useCounter;

    public static SKImage? Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        DateTime writeTime;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            writeTime = fi.LastWriteTimeUtc;
        }
        catch
        {
            return null;
        }

        lock (Gate)
        {
            if (Entries.TryGetValue(path, out var e) && e.WriteTimeUtc == writeTime)
            {
                e.LastUse = ++_useCounter;
                return e.Image;
            }

            SKImage? image = null;
            try
            {
                using var data = SKData.Create(path);
                if (data is not null)
                {
                    using var codec = SKCodec.Create(data);
                    if (codec is not null)
                    {
                        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                        var bmp = new SKBitmap(info);
                        if (codec.GetPixels(info, bmp.GetPixels()) == SKCodecResult.Success)
                        {
                            bmp.SetImmutable();
                            image = SKImage.FromBitmap(bmp);
                        }
                        bmp.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Image '{path}' could not be decoded.", ex);
            }

            Entries.TryGetValue(path, out var old);
            old?.Image?.Dispose();
            Entries[path] = new Entry { Image = image, WriteTimeUtc = writeTime, LastUse = ++_useCounter };
            EvictIfNeeded();
            return image;
        }
    }

    private static void EvictIfNeeded()
    {
        while (Entries.Count > Capacity)
        {
            string? lruKey = null;
            long lru = long.MaxValue;
            foreach (var (k, v) in Entries)
            {
                if (v.LastUse < lru)
                {
                    lru = v.LastUse;
                    lruKey = k;
                }
            }
            if (lruKey is null) return;
            Entries[lruKey].Image?.Dispose();
            Entries.Remove(lruKey);
        }
    }
}
