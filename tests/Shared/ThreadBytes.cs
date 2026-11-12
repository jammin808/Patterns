using System.Runtime;

namespace Patterns.Tests;

/// <summary>
/// The bytes this thread allocates over a window, read so that only this thread's own allocations move it — what an
/// allocation fence holds the product to. Compiled into every suite beside <see cref="TempRun"/>.
/// <para>
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> counts the bytes handed to the thread's allocation context less the
/// part of it still unused. A background collection — any thread's allocations can bring one on — voids every thread's
/// context at the end of its mark without counting the unused tail back, so the count moves by up to one context (about
/// 8 KB) when that end falls inside a window, whatever the thread did (Core's <c>ThreadBytesTests</c> holds both halves).
/// </para>
/// <para>
/// A window over code that allocates nothing opens with <see cref="Start"/>: a collection of the thread's own retires its
/// context first and counts the tail back as it does (asked for while a background mark runs, it may first wait for that
/// mark to end), and from then until the thread next allocates it holds no context, so nothing another thread starts can
/// move the count. A window over code that allocates holds a context the moment it
/// does, so it is read through <see cref="Allocating"/> instead: inside a region where no collection can begin, so no
/// background mark can end inside it.
/// </para>
/// </summary>
internal static class ThreadBytes
{
    /// <summary>What a no-collection region may take, every thread's allocations together, before a collection ends it.</summary>
    private const long RegionBytes = 64L * 1024 * 1024;

    /// <summary>The windows <see cref="Allocating"/> opens before it says the region never held.</summary>
    private const int RegionTries = 5;

    /// <summary>
    /// Opens a window over code that allocates nothing: this thread's allocation context retired, then its count read.
    /// Nothing may be allocated between this and the code measured; read it back with <see cref="Since"/>.
    /// </summary>
    public static long Start()
    {
        GC.Collect(0);
        return GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>The bytes this thread allocated since <paramref name="start"/>.</summary>
    public static long Since(long start) => GC.GetAllocatedBytesForCurrentThread() - start;

    /// <summary>
    /// The bytes <paramref name="measured"/> allocates on this thread, counted inside a no-collection region: its start
    /// finishes any background collection under way with a blocking one, and no collection begins until it ends, so no
    /// context is voided inside the window. A window whose region another thread's allocations ended is not counted and is
    /// opened again (<paramref name="measured"/> runs once per window); after <see cref="RegionTries"/> such windows it
    /// throws with the reason.
    /// </summary>
    public static long Allocating(Action measured)
    {
        for (var attempt = 0; attempt < RegionTries; attempt++)
        {
            if (!GC.TryStartNoGCRegion(RegionBytes)) throw new InvalidOperationException("ThreadBytes: the runtime refused a no-collection region of 64 MB");
            long bytes;
            var held = false;
            try
            {
                var start = GC.GetAllocatedBytesForCurrentThread();
                measured();
                bytes = GC.GetAllocatedBytesForCurrentThread() - start;
            }
            finally
            {
                held = GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;                          // a collection inside ends the region: that window counts nothing
                if (held) GC.EndNoGCRegion();
            }
            if (held) return bytes;
        }
        throw new InvalidOperationException($"ThreadBytes: in {RegionTries} windows a collection ended the no-collection region each time — other threads allocated over {RegionBytes / (1024 * 1024)} MB inside it");
    }
}
