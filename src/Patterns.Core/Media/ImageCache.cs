using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Media;

/// <summary>
/// Decoded-image cache shared by every sink (raster SKImages are immutable and safe to draw
/// from multiple threads). Keyed by path + write time so an updated file is picked up; failed
/// decodes are remembered so a broken path never re-decodes per frame. Bounded in bytes — the
/// budget a machine of this size gets (<see cref="MemoryBudget.PictureCacheBytes"/>) — and in
/// count: ten pictures used to be the whole rule, and ten 8K photographs are not ten icons.
/// </summary>
public static class ImageCache
{
    /// <summary>The most decoded pictures resident whatever their size: the memory ceilings' count.</summary>
    public const int Capacity = 32;
    private static readonly object Gate = new();
    private static long _budgetBytes = -1;
    private static long _bytes;

    /// <summary>The pictures' byte budget: the machine's class decides unless set; least recently drawn go first past it.</summary>
    public static long BudgetBytes
    {
        get
        {
            var b = Interlocked.Read(ref _budgetBytes);
            return b > 0 ? b : Services.MemoryBudget.PictureCacheBytes(Services.MemoryBudget.MachineMB);
        }
        set => Interlocked.Exchange(ref _budgetBytes, value);
    }

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

    /// <summary>Bytes the resident pictures hold (the graveyard's not counted: it is on its way out).</summary>
    public static long Bytes
    {
        get
        {
            lock (Gate)
            {
                return _bytes;
            }
        }
    }

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public SKImage? Image;
        public long Bytes;
        public DateTime WriteTimeUtc;
        public long LastUse;
        /// <summary>When the file was last looked at on disk — its write time is trusted for <see cref="StatHold"/> after this.</summary>
        public long StatAt;
    }

    /// <summary>What a decoded picture costs: its pixels.</summary>
    public static long BytesOf(SKImage? image) => image is null ? 0 : (long)image.Info.BytesSize;

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
    // they are comfortably older than any in-flight frame — or, past half the budget of them,
    // oldest first: a picture replaced within the last few frames is the only one a draw can
    // still hold, and half a budget of pictures cannot be replaced that fast.
    private static readonly List<(SKImage Image, DateTime RetiredUtc)> Graveyard = new();
    private static readonly TimeSpan GraveyardHold = TimeSpan.FromSeconds(5);

    /// <summary>Bytes waiting in the graveyard.</summary>
    public static long GraveyardBytes
    {
        get
        {
            lock (Gate)
            {
                long b = 0;
                foreach (var g in Graveyard) b += BytesOf(g.Image);
                return b;
            }
        }
    }

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
        var cap = BudgetBytes / 2;
        long held = 0;
        foreach (var g in Graveyard) held += BytesOf(g.Image);
        while (held > cap && Graveyard.Count > 1)
        {
            held -= BytesOf(Graveyard[0].Image);
            Graveyard[0].Image.Dispose();
            Graveyard.RemoveAt(0);
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
            if (old is not null)
            {
                Retire(old.Image);
                _bytes -= old.Bytes;
            }
            var entry = new Entry { Image = image, Bytes = BytesOf(image), WriteTimeUtc = writeTime, LastUse = ++_useCounter, StatAt = now };
            Entries[path] = entry;
            _bytes += entry.Bytes;
            EvictIfNeeded(keep: path);
            return image;
        }
    }

    /// <summary>Least recently drawn first, past the count or past the bytes — never the picture just asked for.</summary>
    private static void EvictIfNeeded(string keep)
    {
        var budget = BudgetBytes;
        while (Entries.Count > 1 && (Entries.Count > Capacity || _bytes > budget))
        {
            string? lruKey = null;
            long lru = long.MaxValue;
            foreach (var (k, v) in Entries)
            {
                if (k == keep) continue;
                if (v.LastUse < lru)
                {
                    lru = v.LastUse;
                    lruKey = k;
                }
            }
            if (lruKey is null) return;
            var gone = Entries[lruKey];
            Retire(gone.Image);
            _bytes -= gone.Bytes;
            Entries.Remove(lruKey);
        }
        SweepGraveyard();   // what was just evicted is bounded at once, not at the next ask
    }

    /// <summary>Tests: every picture gone, at once.</summary>
    public static void ClearForTests()
    {
        lock (Gate)
        {
            foreach (var e in Entries.Values) e.Image?.Dispose();
            Entries.Clear();
            _bytes = 0;
            foreach (var g in Graveyard) g.Image.Dispose();
            Graveyard.Clear();
        }
    }
}
