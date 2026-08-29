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

    // Replaced/evicted images are retired, not disposed: another render thread may still be
    // mid-draw with the reference it fetched a moment ago. Retired images are disposed once
    // they are comfortably older than any in-flight frame.
    private static readonly List<(SKImage Image, DateTime RetiredUtc)> Graveyard = new();
    private static readonly TimeSpan GraveyardHold = TimeSpan.FromSeconds(5);

    private static void Retire(SKImage? image)
    {
        if (image is not null) Graveyard.Add((image, DateTime.UtcNow));
    }

    private static void SweepGraveyard()
    {
        var cutoff = DateTime.UtcNow - GraveyardHold;
        for (var i = Graveyard.Count - 1; i >= 0; i--)
        {
            if (Graveyard[i].RetiredUtc < cutoff)
            {
                Graveyard[i].Image.Dispose();
                Graveyard.RemoveAt(i);
            }
        }
    }

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
            SweepGraveyard();

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
            Retire(old?.Image);
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
            Retire(Entries[lruKey].Image);
            Entries.Remove(lruKey);
        }
    }
}
