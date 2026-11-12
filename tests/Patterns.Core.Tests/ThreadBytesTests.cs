using System.Diagnostics;
using System.Runtime;
using Patterns.Core.Services;
using Patterns.Tests;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// What an allocation fence reads (<see cref="ThreadBytes"/>). The renderer's per-frame address check allocates nothing, and
/// still a raw <see cref="GC.GetAllocatedBytesForCurrentThread"/> window around it moves when a background collection
/// another thread started ends its mark meanwhile: the collector voids every thread's allocation context there without
/// counting its unused tail back. A window opened by <see cref="ThreadBytes.Start"/> holds no context to void, so under
/// the same collection it reads what the loop allocated — nothing; a window read through <see cref="ThreadBytes.Allocating"/>
/// holds one, and no collection can begin inside it.
/// </summary>
public class ThreadBytesTests
{
    /// <summary>The addresses the renderer asks about every frame: none carries a pass, and the held ones are in their own form.</summary>
    private static readonly string[] Addresses =
    {
        "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42",
        "https://bbc.co.uk/news/live",
        "https://example.com/schedule?day=2&lang=en",
        "kept://10.0.0.5:9924/abcdefghijkl",
        "kept://[::1]:9924/abcdefghijkl",
        "unkept://10.0.0.5:9924",
    };

    /// <summary>
    /// How many live objects the background collection marks: enough that its concurrent mark runs for many milliseconds,
    /// so the witness opens inside it on most collections whatever the machine.
    /// </summary>
    private const int Marked = 2_000_000;

    /// <summary>The collections whose end of mark must provably fall inside a fenced window.</summary>
    private const int Proven = 6;

    /// <summary>The most collections the test starts to find <see cref="Proven"/> of them; each is bounded by <see cref="Deadline"/>.</summary>
    private const int MostCollections = 24;

    /// <summary>The longest any one wait of a window may take before the test fails with what never happened.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static int Ask()
    {
        var held = 0;
        foreach (var a in Addresses) held += WindowAddresses.LooksPrivate(a) || !ReferenceEquals(WindowAddresses.Held(a), a) ? 1 : 0;
        return held;
    }

    private static long BackgroundIndex() => GC.GetGCMemoryInfo(GCKind.Background).Index;

    /// <summary>What one collection did to the two windows open across it.</summary>
    private readonly record struct Collection(bool Ended, long Fenced, long Raw, int Held);

    /// <summary>
    /// One background collection and two threads across it. Another thread starts the collection; once it has begun (the gen2
    /// count moved, every context retired by its start) this thread allocates — a fresh context, its unused tail live, the
    /// one the end of the mark would void — and lets a witness thread take a fresh context of its own and open a raw window.
    /// Only once the witness is open does this thread open its window through <see cref="ThreadBytes.Start"/>. Both close
    /// once the collection has ended. A raw window that moved proves the end of the mark came after the witness opened, so
    /// after this thread held its voidable context and no earlier than its <see cref="ThreadBytes.Start"/> was called: the
    /// collection counts. A raw window that stayed still proves nothing (the mark ended before it opened), and the
    /// collection is not counted.
    /// </summary>
    private static Collection Across()
    {
        var index = BackgroundIndex();
        var count = GC.CollectionCount(2);
        var deadline = Stopwatch.GetTimestamp() + (long)(Deadline.TotalSeconds * Stopwatch.Frequency);
        var ended = 0;                                                                           // 1: the collection ended; 2: it never did by the deadline
        var go = 0;
        var opened = 0;
        var raw = -1L;
        var rawHeld = 0;
        var watcher = new Thread(() =>
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
            while (BackgroundIndex() == index && Stopwatch.GetTimestamp() < deadline) Thread.Sleep(1);
            Volatile.Write(ref ended, BackgroundIndex() == index ? 2 : 1);
        }) { IsBackground = true };
        var witness = new Thread(() =>
        {
            while (Volatile.Read(ref go) == 0 && Volatile.Read(ref ended) == 0) Thread.SpinWait(16);
            var tail = new byte[24];                                                            // a fresh context, its unused tail live
            var before = GC.GetAllocatedBytesForCurrentThread();
            Volatile.Write(ref opened, 1);
            var h = 0;
            while (Volatile.Read(ref ended) == 0) h += Ask();
            raw = GC.GetAllocatedBytesForCurrentThread() - before;
            rawHeld = h;
            GC.KeepAlive(tail);
        }) { IsBackground = true };
        witness.Start();
        watcher.Start();
        while (GC.CollectionCount(2) == count && Stopwatch.GetTimestamp() < deadline) Thread.SpinWait(16);   // the background collection has begun
        var mine = new byte[24];                                                                // this thread's fresh context: what an unfenced window would carry
        Volatile.Write(ref go, 1);
        while (Volatile.Read(ref opened) == 0 && Volatile.Read(ref ended) == 0) Thread.SpinWait(1);
        var start = ThreadBytes.Start();
        var held = 0;
        while (Volatile.Read(ref ended) == 0) held += Ask();
        var fenced = ThreadBytes.Since(start);
        GC.KeepAlive(mine);
        Assert.True(watcher.Join(Deadline) && witness.Join(Deadline), "a window's thread never finished after the collection ended");
        return new Collection(Volatile.Read(ref ended) == 1, fenced, raw, held + rawHeld);
    }

    [Fact]
    public void ABackgroundCollectionMovesARawCountAndNeverAWindowThisThreadStarted()
    {
        Assert.True(GCSettings.LatencyMode != GCLatencyMode.Batch,
            "concurrent collection is off in this host (DOTNET_gcConcurrent=0 or System.GC.Concurrent=false): no background collection can run, so neither half can be shown");
        for (var i = 0; i < 50; i++) Assert.Equal(0, Ask());                                    // every path of the loop has run
        var heap = new object[Marked];                                                          // a heap the background mark takes time to read
        for (var i = 0; i < heap.Length; i++) heap[i] = new object();
        try
        {
            GC.Collect();                                                                       // the heap in the oldest generation, where the background mark reads it
            var seen = new List<Collection>();
            while (seen.Count(c => c.Raw > 0) < Proven && seen.Count < MostCollections)
            {
                var c = Across();
                Assert.True(c.Ended, $"no background collection ended within {Deadline.TotalSeconds:0} s of being asked for (collection {seen.Count + 1})");
                Assert.Equal(0, c.Held);
                seen.Add(c);
            }
            var said = string.Join("; ", seen.Select(c => $"fenced {c.Fenced}, raw {c.Raw}"));
            // The fence: no window it opened moved, across every collection — among them those whose end of mark provably came
            // after this thread held a context to void and no earlier than the window's opening was asked for.
            Assert.True(seen.All(c => c.Fenced == 0), $"a fenced window moved: {said}");
            // The cause: a raw window moves although its loop allocates nothing — in at least six collections.
            Assert.True(seen.Count(c => c.Raw > 0) >= Proven, $"fewer than {Proven} of {seen.Count} collections moved a raw window: {said}");
        }
        finally
        {
            GC.KeepAlive(heap);
        }
    }

    [Fact]
    public void AWindowThisThreadStartedCountsWhatTheThreadAllocatesToTheByte()
    {
        var start = ThreadBytes.Start();
        var made = new byte[1000];                                                            // on a 64-bit runtime: 24 bytes of header and length, 1000 of elements
        var bytes = ThreadBytes.Since(start);
        GC.KeepAlive(made);
        Assert.Equal(1024, bytes);
    }

    [Fact]
    public void AnAllocatingWindowCountsWhatItAllocatesToTheByte()
    {
        var made = new byte[20][];
        var bytes = ThreadBytes.Allocating(() =>
        {
            for (var i = 0; i < made.Length; i++) made[i] = new byte[1000];                     // twenty arrays of 1024 bytes each (64-bit layout)
        });
        Assert.All(made, Assert.NotNull);
        Assert.Equal(20 * 1024, bytes);
    }
}
