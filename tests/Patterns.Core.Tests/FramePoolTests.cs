using System.Runtime.InteropServices;
using Patterns.Core.Media;
using Patterns.Core.Ndi;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The render fence and the frame pools: a buffer replaced is written again only once every
/// sink has started a frame after it (or half a second passed), a pool's images read the
/// buffers themselves, a frame that finds every buffer fenced goes the old way and is counted,
/// a decoded frame never shown is dropped, a disposed pool frees its memory behind the fence,
/// and the ledger reads the pools' bytes.
/// </summary>
public class FramePoolTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 14, 20, 0, 0, DateTimeKind.Utc);
    private DateTime _now = T0;

    public FramePoolTests()
    {
        RenderFence.ResetForTests();
        RenderFence.Clock = () => _now;
    }

    public void Dispose()
    {
        RenderFence.Clock = null;
        RenderFence.ResetForTests();
    }

    [Fact]
    public void TheFenceClearsWhenEverySinkStartedAFrameAfterTheMarkOrTheFallbackPassed()
    {
        var a = RenderFence.Register();
        var b = RenderFence.Register();
        Assert.True(a >= 0 && b >= 0 && a != b);
        Assert.Equal(0, RenderFence.LiveSinks);                                                         // registered, no frame yet: holding nothing
        var fresh = RenderFence.Take();
        Assert.True(RenderFence.Cleared(in fresh));                                                     // so nothing waits for them
        RenderFence.Advance(a);
        RenderFence.Advance(b);                                                                         // both in a frame now
        Assert.Equal(2, RenderFence.LiveSinks);
        var mark = RenderFence.Take();
        Assert.False(RenderFence.Cleared(in mark));
        RenderFence.Advance(a);
        Assert.False(RenderFence.Cleared(in mark));                                                     // b may still be mid-frame with the old picture
        RenderFence.Advance(b);
        Assert.True(RenderFence.Cleared(in mark));

        // A sink that has not started a frame in two seconds is not waited for.
        var mark2 = RenderFence.Take();
        _now = T0.AddSeconds(2.5);
        Assert.True(RenderFence.Cleared(in mark2));                                                     // both asleep: nothing mid-frame
        Assert.Equal(0, RenderFence.LiveSinks);

        // The fallback: half a second clears a mark whatever the sinks did.
        RenderFence.Advance(a);
        var mark3 = RenderFence.Take();
        Assert.False(RenderFence.Cleared(in mark3));
        _now = _now.AddMilliseconds(499);
        Assert.False(RenderFence.Cleared(in mark3));
        _now = _now.AddMilliseconds(2);
        Assert.True(RenderFence.Cleared(in mark3));

        RenderFence.Unregister(a);
        RenderFence.Unregister(b);
        Assert.Equal(0, RenderFence.LiveSinks);
        var none = RenderFence.Take();
        Assert.True(RenderFence.Cleared(in none));                                                        // no sink at all: nothing to wait for
    }

    [Fact]
    public unsafe void APoolsImagesReadTheBuffersAndAReplacedBufferWaitsOnTheFence()
    {
        const long MB = 1024L * 1024;
        Assert.Equal(8, FramePool.BuffersFor(1920L * 1080 * 4, 64 * MB));                            // eight 1080p frames in 64 MB
        Assert.Equal(4, FramePool.BuffersFor(3840L * 2160 * 4, 64 * MB));                             // the floor
        Assert.Equal(8, FramePool.BuffersFor(1L * MB, 64 * MB));                                      // the cap

        var sink = RenderFence.Register();
        RenderFence.Advance(sink);                                                                       // the sink is drawing
        var info = new SKImageInfo(4, 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var pool = new FramePool(info, 32, 4);
        Assert.Equal(4, pool.Count);
        Assert.Equal(32 * 2 * 4, pool.Bytes);
        Assert.Equal(1, FramePools.Count);
        Assert.Equal(pool.Bytes, FramePools.Bytes);
        Assert.Null(pool.Latest);

        var slot = pool.Acquire();
        Assert.Equal(0, slot);
        var px = (uint*)pool.Pointer(slot);
        px[0] = 0xFFFF0000;                                                                              // BGRA in memory: 00 00 FF FF — red
        var image = pool.Publish(slot)!;
        Assert.Same(image, pool.Latest);
        Assert.True(pool.Owns(image));
        Assert.Equal(sink, RenderFence.CurrentSink);
        pool.Touch();                                                                                    // the sink draws it on its running frame
        using (var pixmap = image.PeekPixels())
        {
            Assert.Equal(new SKColor(0xFF, 0x00, 0x00), pixmap.GetPixelColor(0, 0));                    // the image reads the buffer
        }
        px[0] = 0xFF00FF00;                                                                              // written after: the image sees it — no copy was made
        using (var pixmap = image.PeekPixels())
        {
            Assert.Equal(new SKColor(0x00, 0xFF, 0x00), pixmap.GetPixelColor(0, 0));
        }

        // The next frames take the free buffers; the frame replaced waits on the fence.
        Assert.Equal(1, pool.Acquire());
        Assert.NotNull(pool.Publish(1));
        Assert.Equal(1, pool.Retired);                                                                   // slot 0, marked
        Assert.Equal(2, pool.Acquire());
        Assert.Equal(3, pool.Acquire());
        Assert.Equal(-1, pool.Acquire());                                                                // 0 is under the fence, 1 on show, 2 and 3 locked
        Assert.Equal(1, pool.Starved);
        RenderFence.Advance(sink);                                                                       // the sink drew a new frame: 0 is free again
        Assert.Equal(0, pool.Acquire());
        Assert.Equal(0, pool.Retired);
        pool.Touch();                                                                                    // and that frame draws the one on show (1)

        // A frame decoded but never shown is dropped when a later one is shown; a released frame is free at once.
        pool.Decoded(2);
        pool.Release(3);
        Assert.Equal(3, pool.Acquire());
        Assert.NotNull(pool.Publish(3));                                                                 // shows 3: 2 (decoded, unshown) is dropped, 1 retires
        Assert.Equal(2, pool.Acquire());
        RenderFence.Unregister(sink);
    }

    [Fact]
    public void OnlyTheSinksThatDrewFromAPoolHoldItsFrames()
    {
        var a = RenderFence.Register();
        var b = RenderFence.Register();
        var c = RenderFence.Register();                                                                  // never draws a frame at all
        var info = new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var pool = new FramePool(info, 8, 4);
        RenderFence.Advance(b);                                                                          // b drew a static page and stopped
        RenderFence.Advance(a);
        pool.Publish(pool.Acquire());
        pool.Touch();                                                                                    // a drew the frame
        pool.Publish(pool.Acquire());                                                                    // frame 0 retires
        pool.Acquire(); pool.Acquire();
        Assert.Equal(-1, pool.Acquire());                                                                // a is still on the frame that drew it
        RenderFence.Advance(a);
        Assert.Equal(0, pool.Acquire());                                                                 // a moved on: free — b and c drew nothing of this pool and are not waited for
        Assert.Equal(a, RenderFence.CurrentSink);                                                       // a's frame is the one running on this thread

        // Off a frame nothing is noted: a touch with no sink running is not a hold.
        RenderFence.ResetForTests();
        var d = RenderFence.Register();
        using var other = new FramePool(info, 8, 4);
        other.Publish(other.Acquire());
        other.Touch();                                                                                   // no frame running here
        RenderFence.Advance(d);                                                                          // d starts a frame after: it did not draw the old picture
        other.Publish(other.Acquire());                                                                  // 0 retires
        Assert.Equal(0, other.Acquire());                                                                // and is free at once: nothing held it
        RenderFence.Unregister(d);
    }

    [Fact]
    public void ADisposedPoolFreesItsMemoryOnceTheFenceClears()
    {
        var sink = RenderFence.Register();
        RenderFence.Advance(sink);                                                                       // the sink is drawing
        var info = new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var pool = new FramePool(info, 8, 4);
        pool.Publish(pool.Acquire());
        pool.Touch();                                                                                    // the sink is drawing the pool's frame
        pool.Publish(pool.Acquire());
        var before = FramePools.Count;
        pool.Dispose();
        Assert.Equal(before - 1, FramePools.Count);
        Assert.Null(pool.Latest);
        Assert.Null(pool.Publish(0));                                                                    // nothing goes on show from a disposed pool
        Assert.False(pool.IsFreed);                                                                      // a sink may still draw the last frame
        Assert.Equal(1, FramePools.PendingFree);
        RenderFence.Advance(sink);
        FramePools.Sweep();
        Assert.True(pool.IsFreed);
        Assert.Equal(0, FramePools.PendingFree);
        RenderFence.Unregister(sink);
    }

    [Fact]
    public unsafe void AnNdiFrameGoesIntoThePoolWithOneCopyAndTheOldWayWhenEveryBufferIsFenced()
    {
        var sink = RenderFence.Register();
        RenderFence.Advance(sink);                                                                       // the sink is drawing
        var info = new SKImageInfo(8, 4, SKColorType.Bgra8888, SKAlphaType.Opaque);
        const int stride = 48;                                                                           // the sender's rows are padded
        var frame = Marshal.AllocHGlobal(stride * 4);
        try
        {
            var p = (uint*)frame;
            for (var y = 0; y < 4; y++) for (var x = 0; x < 8; x++) p[y * 12 + x] = 0xFF000000u | (uint)(x * 16) << 16 | (uint)(y * 64);
            FramePool? pool = null;
            var wasBudget = MemoryBudget.MachineMB;
            MemoryBudget.MachineMB = 16384;
            try
            {
                var first = NdiReceiver.PublishInto(ref pool, info, 32, frame, stride, out var pooled)!;
                Assert.True(pooled);
                Assert.NotNull(pool);
                Assert.Equal(8, pool!.Count);                                                            // a tiny frame: the cap
                pool.Touch();                                                                            // the sink draws from the pool on its running frame
                using (var pixmap = first.PeekPixels())
                {
                    Assert.Equal(new SKColor(0x70, 0x00, 0xC0), pixmap.GetPixelColor(7, 3));            // x 7 → red 0x70, y 3 → blue 0xC0
                }
                // Seven more frames fill the pool (one on show, the rest retired behind the sink that drew and never advanced); the ninth goes the old way.
                for (var i = 0; i < 7; i++)
                {
                    Assert.NotNull(NdiReceiver.PublishInto(ref pool, info, 32, frame, stride, out pooled));
                    Assert.True(pooled, $"frame {i + 2} pooled");
                }
                var ninth = NdiReceiver.PublishInto(ref pool, info, 32, frame, stride, out pooled)!;
                Assert.False(pooled);
                Assert.False(pool!.Owns(ninth));
                Assert.Equal(1, pool.Starved);
                ninth.Dispose();
                RenderFence.Advance(sink);
                Assert.NotNull(NdiReceiver.PublishInto(ref pool, info, 32, frame, stride, out pooled));
                Assert.True(pooled);                                                                     // the fence cleared: pooled again

                // A new size makes a new pool; the old one retires.
                var wider = new SKImageInfo(12, 4, SKColorType.Bgra8888, SKAlphaType.Opaque);
                var old = pool!;
                Assert.NotNull(NdiReceiver.PublishInto(ref pool, wider, 48, frame, stride, out pooled));
                Assert.NotSame(old, pool);
                Assert.Equal(12, pool!.Width);
                pool!.Dispose();
                RenderFence.Advance(sink);
                FramePools.Sweep();
                Assert.True(old.IsFreed);
            }
            finally
            {
                MemoryBudget.MachineMB = wasBudget;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(frame);
            RenderFence.Unregister(sink);
        }
    }

    [Fact]
    public void TheLedgerReadsEveryOwnerLargestFirstAndABrokenReaderReadsAsNought()
    {
        MemoryLedger.ResetForTests();
        try
        {
            MemoryLedger.Register("pictures", () => (84L * 1024 * 1024, "3 cached"));
            MemoryLedger.Register("frame pools", () => (116L * 1024 * 1024, "2 sources"));
            MemoryLedger.Register("thumbnails", () => (0, ""));
            MemoryLedger.Register("broken", () => throw new InvalidOperationException("no"));
            var lines = MemoryLedger.Read();
            Assert.Equal(4, lines.Count);
            Assert.Equal("pictures 84 MB (3 cached)", lines[0].Words);
            Assert.Equal(0, lines[3].Bytes);
            Assert.Equal("no reading", lines[3].Detail);
            Assert.Equal(200L * 1024 * 1024, MemoryLedger.Total());
            Assert.Equal("frame pools 116 MB (2 sources) · pictures 84 MB (3 cached)", MemoryLedger.Describe());   // largest first, the empty left out
            MemoryLedger.Register("pictures", () => (1L * 1024 * 1024, "1 cached"));                       // a reader replaced
            Assert.Equal(4, MemoryLedger.Read().Count);
            MemoryLedger.Unregister("frame pools");
            Assert.Equal("pictures 1 MB (1 cached)", MemoryLedger.Describe());
            MemoryLedger.ResetForTests();
            Assert.Equal("nothing placed yet", MemoryLedger.Describe());
        }
        finally
        {
            MemoryLedger.ResetForTests();
        }
    }

    [Fact]
    public void ThePictureCacheIsBoundedInBytesLeastRecentlyDrawnFirst()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-pictures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var wasBudget = ImageCache.BudgetBytes;
        ImageCache.ClearForTests();
        try
        {
            var paths = new List<string>();
            for (var i = 0; i < 5; i++)
            {
                using var bmp = new SKBitmap(64, 64);
                bmp.Erase(new SKColor((byte)(i * 40), 0, 0));
                using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
                var path = Path.Combine(dir, $"p{i}.png");
                File.WriteAllBytes(path, data.ToArray());
                paths.Add(path);
            }
            const long one = 64 * 64 * 4;
            ImageCache.BudgetBytes = 3 * one + 100;                                                      // room for three
            foreach (var p in paths) Assert.NotNull(ImageCache.Get(p));
            Assert.Equal(3, ImageCache.Count);
            Assert.True(ImageCache.Bytes <= ImageCache.BudgetBytes);
            Assert.Equal(3 * one, ImageCache.Bytes);
            Assert.True(ImageCache.GraveyardBytes <= Math.Max(one, ImageCache.BudgetBytes / 2));        // the evicted wait a moment, bounded
            // The last three asked for are the ones kept; asking for the first again evicts the least recently drawn (the second).
            Assert.NotNull(ImageCache.Get(paths[0]));
            Assert.Equal(3, ImageCache.Count);
            Assert.NotNull(ImageCache.Get(paths[4]));
            Assert.NotNull(ImageCache.Get(paths[3]));
            Assert.Equal(3, ImageCache.Count);
            // A budget with no room for two still keeps the picture just asked for.
            ImageCache.BudgetBytes = one - 1;
            Assert.NotNull(ImageCache.Get(paths[2]));
            Assert.Equal(1, ImageCache.Count);
            Assert.Equal(32, ImageCache.Capacity);
        }
        finally
        {
            ImageCache.BudgetBytes = wasBudget > 0 ? wasBudget : -1;
            ImageCache.ClearForTests();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheMetricsHistoryIsARingThatForgetsByOverwriting()
    {
        var h = new MetricsHistory();
        for (var i = 0; i < 700; i++) h.Add(new MetricSample { Utc = T0.AddSeconds(i), PrivateMB = i, ManagedMB = 10 });
        Assert.Equal(MetricsHistory.RecentCapacity, h.Recent.Count);
        Assert.Equal(T0.AddSeconds(100), h.Recent[0].Utc);                                              // the oldest kept is the 101st
        Assert.Equal(T0.AddSeconds(699), h.Recent[^1].Utc);
        Assert.Equal(700 / MetricsHistory.AggregateEvery, h.LongTerm.Count);
        Assert.Equal(Enumerable.Range(697, 3).Select(i => (double)i), h.Tail(3, s => s.PrivateMB));
        Assert.Equal(14.5, h.LongTerm[0].PrivateMB);                                                    // the first thirty averaged
        Assert.Equal(10, h.LongTerm[0].ManagedMB);
        Assert.Equal(h.Recent.Count, h.Recent.Count());                                                 // enumerates what it indexes
        var ring = new MetricsHistory.Ring<int>(3);
        ring.Add(1); ring.Add(2); ring.Add(3); ring.Add(4);
        Assert.Equal(new[] { 2, 3, 4 }, ring);
        Assert.Throws<ArgumentOutOfRangeException>(() => ring[3]);
    }
}
