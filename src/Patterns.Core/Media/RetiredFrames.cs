using SkiaSharp;

namespace Patterns.Core.Media;

/// <summary>
/// Where a replaced picture goes before it is freed: a live source's frame that went the old way
/// (every pooled buffer under a draw), a web page's or a deck's frame, a decoded picture the cache
/// evicted or replaced. A renderer that fetched one a moment ago may still be inside a draw — the
/// canvas flushes at the end of the frame — so nothing here is disposed on time: each image is
/// retired at a <see cref="RenderFence"/> mark with the table of the sinks that drew it (a cached
/// picture's) or none (a frame any sink may have drawn), and freed once those sinks have started a
/// frame after the mark or are dead. One list for every kind, so the ledger and the health row
/// read one number. A mark nobody clears in <see cref="RenderFence.AbandonAfter"/> is freed anyway
/// and counted as forced: a hung sink, never a draw.
/// </summary>
public static class RetiredFrames
{
    public enum Kind
    {
        /// <summary>A live source's or a slot's frame.</summary>
        Frame,
        /// <summary>A decoded picture the cache let go.</summary>
        Picture,
    }

    private readonly record struct Entry(SKImage Image, long[]? DrewAt, RenderFence.Mark Mark, Kind Kind, long Bytes);

    private static readonly object Gate = new();
    private static readonly List<Entry> Held = new();

    /// <summary>Images retired and waiting, across every source: the memory ceilings' number.</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Held.Count;
            }
        }
    }

    /// <summary>Images of one kind waiting.</summary>
    public static int CountOf(Kind kind)
    {
        lock (Gate)
        {
            var n = 0;
            foreach (var h in Held) if (h.Kind == kind) n++;
            return n;
        }
    }

    /// <summary>Bytes the waiting images hold right now.</summary>
    public static long Bytes
    {
        get
        {
            lock (Gate)
            {
                long b = 0;
                foreach (var h in Held) b += h.Bytes;
                return b;
            }
        }
    }

    /// <summary>Bytes of one kind waiting.</summary>
    public static long BytesOf(Kind kind)
    {
        lock (Gate)
        {
            long b = 0;
            foreach (var h in Held) if (h.Kind == kind) b += h.Bytes;
            return b;
        }
    }

    /// <summary>How long the oldest waiting image has waited, ms; -1 with none.</summary>
    public static double OldestMs
    {
        get
        {
            lock (Gate)
            {
                var oldest = -1.0;
                foreach (var h in Held)
                {
                    var age = RenderFence.AgeMs(h.Mark);
                    if (age > oldest) oldest = age;
                }
                return oldest;
            }
        }
    }

    /// <summary>
    /// Takes a replaced image (null is nothing) with the table of the sinks that drew it — null for
    /// a frame any sink may have drawn — and frees whatever has cleared its fence.
    /// </summary>
    public static void Retire(SKImage? image, long[]? drewAt = null, Kind kind = Kind.Frame)
    {
        lock (Gate)
        {
            if (image is not null) Held.Add(new Entry(image, drewAt, RenderFence.Take(), kind, image.Info.BytesSize));
            SweepLocked();
        }
    }

    /// <summary>Frees what has cleared its fence — for a source that stopped publishing, so its last frames do not wait for the next; the pools retired with their sources go the same way.</summary>
    public static void Sweep()
    {
        lock (Gate)
        {
            SweepLocked();
        }
        FramePools.Sweep();
    }

    private static void SweepLocked()
    {
        for (var i = Held.Count - 1; i >= 0; i--)
        {
            var h = Held[i];
            if (RenderFence.Cleared(h.Mark, h.DrewAt))
            {
                h.Image.Dispose();
                Held.RemoveAt(i);
            }
            else if (RenderFence.Abandoned(h.Mark))
            {
                RenderFence.NoteForced();
                h.Image.Dispose();
                Held.RemoveAt(i);
            }
        }
    }

    /// <summary>Tests: everything freed at once.</summary>
    public static void ClearForTests()
    {
        lock (Gate)
        {
            foreach (var h in Held) h.Image.Dispose();
            Held.Clear();
        }
    }
}
