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
    /// <summary>How many decoded pictures stay resident: the memory ceilings' number for the picture cache.</summary>
    public const int Capacity = 10;
    private static readonly object Gate = new();

    /// <summary>Pictures resident right now.</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Entries.Count;
            }
        }
    }
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public SKImage? Image;
        public DateTime WriteTimeUtc;
        public long LastUse;
        /// <summary>When the file was last looked at on disk — its write time is trusted for <see cref="StatHold"/> after this.</summary>
        public long StatAt;
    }

    /// <summary>
    /// How long a file's write time is trusted before the disk is asked again. The logo overlay,
    /// a picture layer and a lower third's photo used to stat their file on every frame of every
    /// sink — six sinks at 60 Hz is three hundred and sixty file-system calls a second on the render
    /// threads, each a round trip on a network share. A picture replaced on disk still shows within
    /// half a second; ShowFiles.Resolve keeps the same hold for the same reason.
    /// </summary>
    public static readonly TimeSpan StatHold = TimeSpan.FromMilliseconds(500);

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

        var now = Environment.TickCount64;
        lock (Gate)
        {
            // The picture as it was: trusted for a moment before the disk is asked again.
            if (Entries.TryGetValue(path, out var fresh) && now - fresh.StatAt < StatHold.TotalMilliseconds)
            {
                fresh.LastUse = ++_useCounter;
                return fresh.Image;
            }
        }

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
                e.StatAt = now;
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
            Entries[path] = new Entry { Image = image, WriteTimeUtc = writeTime, LastUse = ++_useCounter, StatAt = now };
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
