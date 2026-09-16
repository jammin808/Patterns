using Patterns.Core.Media;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Rendering.Media;

/// <summary>
/// Decoded-image cache shared by every sink (raster SKImages are immutable and safe to draw
/// from multiple threads). Keyed by path + write time so an updated file is picked up; failed
/// decodes are remembered so a broken path never re-decodes per frame. Bounded in bytes — the
/// budget a machine of this size gets (<see cref="MemoryBudget.PictureCacheBytes"/>) — and in
/// count: ten pictures used to be the whole rule, and ten 8K photographs are not ten icons. A
/// picture let go is not disposed: every fetch notes the sink whose frame is running on the
/// picture's own table, and the picture retires behind the <see cref="RenderFence"/> with that
/// table (<see cref="RetiredFrames"/>) — freed once the sinks that drew it have started another
/// frame, never because the byte cap was passed while a draw could still read it.
/// </summary>
public static class ImageCache
{
    /// <summary>The most decoded pictures resident whatever their size: the memory ceilings' count.</summary>
    public const int Capacity = MemoryBudget.PictureCapacity;
    private static readonly object Gate = new();
    private static long _budgetBytes = -1;
    private static long _bytes;

    /// <summary>The pictures' byte budget: the machine's class decides unless set; least recently drawn go first past it.</summary>
    public static long BudgetBytes
    {
        get
        {
            var b = Interlocked.Read(ref _budgetBytes);
            return b > 0 ? b : Patterns.Core.Services.MemoryBudget.PictureCacheBytes(Patterns.Core.Services.MemoryBudget.MachineMB);
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
        /// <summary>When a sink last fetched it (the tick clock): the residency ledger's idle clock (round 69).</summary>
        public long LastDrawnTicks;
        /// <summary>When the file was last looked at on disk — its write time is trusted for <see cref="StatHold"/> after this.</summary>
        public long StatAt;
        /// <summary>Which frame of each sink last drew this picture: the fence's table, retired with the picture.</summary>
        public readonly long[] DrewAt = new long[RenderFence.MaxSinks];
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

    /// <summary>The tick clock the idle ages are read on (round 69): the machine's, or a test's.</summary>
    public static Func<long>? Clock { get; set; }

    private static long Now => Clock?.Invoke() ?? Environment.TickCount64;

    /// <summary>Bytes of pictures let go and waiting on the fence (they are <see cref="RetiredFrames"/>' now, of the picture kind).</summary>
    public static long GraveyardBytes => RetiredFrames.BytesOf(RetiredFrames.Kind.Picture);

    /// <summary>A picture let go goes behind the fence with the table of the sinks that drew it.</summary>
    private static void Retire(Entry gone)
    {
        if (gone.Image is not null) RetiredFrames.Retire(gone.Image, gone.DrewAt, RetiredFrames.Kind.Picture);
    }

    public static SKImage? Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var now = Now;
        lock (Gate)
        {
            // The picture as it was: trusted for a moment before the disk is asked again.
            if (Entries.TryGetValue(path, out var fresh) && now - fresh.StatAt < StatHold.TotalMilliseconds)
            {
                fresh.LastUse = ++_useCounter;
                fresh.LastDrawnTicks = now;
                RenderFence.Touch(fresh.DrewAt);                                                       // this sink's running frame draws it: noted with the fetch, under the lock
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
            if (Entries.TryGetValue(path, out var e) && e.WriteTimeUtc == writeTime)
            {
                e.LastUse = ++_useCounter;
                e.LastDrawnTicks = now;
                e.StatAt = now;
                RenderFence.Touch(e.DrewAt);
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
                Retire(old);
                _bytes -= old.Bytes;
            }
            var entry = new Entry { Image = image, Bytes = BytesOf(image), WriteTimeUtc = writeTime, LastUse = ++_useCounter, LastDrawnTicks = now, StatAt = now };
            Entries[path] = entry;
            _bytes += entry.Bytes;
            RenderFence.Touch(entry.DrewAt);
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
            Retire(gone);
            _bytes -= gone.Bytes;
            Entries.Remove(lruKey);
        }
    }

    /// <summary>
    /// The pressure ladder's step: lets pictures go, least recently drawn first, until the resident
    /// bytes are within <paramref name="bytes"/> — never the last one drawn, so a picture on air stays.
    /// Returns how many went.
    /// </summary>
    public static int TrimTo(long bytes)
    {
        var gone = 0;
        lock (Gate)
        {
            while (Entries.Count > 1 && _bytes > bytes)
            {
                string? lruKey = null;
                long lru = long.MaxValue;
                string? newestKey = null;
                long newest = long.MinValue;
                foreach (var (k, v) in Entries)
                {
                    if (v.LastUse < lru)
                    {
                        lru = v.LastUse;
                        lruKey = k;
                    }
                    if (v.LastUse > newest)
                    {
                        newest = v.LastUse;
                        newestKey = k;
                    }
                }
                if (lruKey is null || lruKey == newestKey) break;
                var entry = Entries[lruKey];
                Retire(entry);
                _bytes -= entry.Bytes;
                Entries.Remove(lruKey);
                gone++;
            }
        }
        return gone;
    }

    /// <summary>Pictures the residency sweep let go for being idle, this session (round 69).</summary>
    public static int IdleSwept { get; private set; }

    /// <summary>Every resident picture with what it costs and how long since a sink fetched it, for the residency ledger (round 69).</summary>
    public static IReadOnlyList<(string Path, long Bytes, long IdleMs)> Snapshot(long? nowTicks = null)
    {
        var now = nowTicks ?? Now;
        lock (Gate)
        {
            var list = new List<(string, long, long)>(Entries.Count);
            foreach (var (path, e) in Entries) list.Add((path, e.Bytes, Math.Max(0, now - e.LastDrawnTicks)));
            return list;
        }
    }

    /// <summary>A picture fetched within this long is being drawn: the sweep never touches it, whatever the grace — the floor under a grace of nought.</summary>
    public const long DrawnWithinMs = 1500;

    /// <summary>
    /// The residency sweep (round 69): a picture no sink has fetched for longer than <paramref name="graceMs"/>
    /// (and never within <see cref="DrawnWithinMs"/>) is let go — behind the fence like any other — unless
    /// <paramref name="keep"/> says the show still names it. A picture on air is fetched every frame and is
    /// never idle; idle things leave on their own clock, not only when the budget is passed.
    /// </summary>
    public static int SweepIdle(long graceMs, Func<string, bool>? keep = null, long? nowTicks = null)
    {
        var now = nowTicks ?? Now;
        var floor = Math.Max(graceMs, DrawnWithinMs);
        var gone = 0;
        lock (Gate)
        {
            foreach (var k in Entries.Keys.ToList())
            {
                var e = Entries[k];
                if (now - e.LastDrawnTicks <= floor) continue;
                if (keep is not null && keep(k)) continue;
                Retire(e);
                _bytes -= e.Bytes;
                Entries.Remove(k);
                gone++;
            }
            IdleSwept += gone;
        }
        return gone;
    }

    /// <summary>Tests: every picture gone, at once — the retired ones too.</summary>
    public static void ClearForTests()
    {
        lock (Gate)
        {
            foreach (var e in Entries.Values) e.Image?.Dispose();
            Entries.Clear();
            _bytes = 0;
        }
        RetiredFrames.ClearForTests();
    }
}
