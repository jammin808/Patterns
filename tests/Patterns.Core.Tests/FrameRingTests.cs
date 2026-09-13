using Patterns.Core.Media;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The frame ring between the arcade's loop and its readers: the writer never blocks and never
/// takes a buffer a reader holds, a reader always gets the newest whole frame, a lane waits for a
/// newer frame and wakes when told to stop.
/// </summary>
public class FrameRingTests
{
    [Fact]
    public void TheWriterNeverTakesTheNewestOrAHeldBufferAndSkipsRatherThanWaits()
    {
        var ring = new FrameRing(4);
        Assert.Equal(-1, ring.Latest);
        Assert.Equal(-1, ring.Pin());                                // nothing yet

        var a = ring.Acquire();
        Assert.Equal(0, a);
        Assert.Equal(-1, ring.Acquire());                            // one writer at a time
        ring.Publish(a);
        Assert.Equal(0, ring.Latest);
        Assert.Equal(1, ring.Sequence);

        var held = ring.Pin();                                       // a screen holds the newest
        Assert.Equal(0, held);
        var b = ring.Acquire();
        Assert.Equal(1, b);                                          // not the newest, not the held one
        ring.Publish(b);
        Assert.Equal(1, ring.Latest);
        Assert.Equal(1, ring.Pins(0));

        var lane = ring.Pin();                                       // a lane holds the newest
        Assert.Equal(1, lane);
        var c = ring.Acquire();
        Assert.Equal(2, c);
        ring.Publish(c);
        var d = ring.Acquire();
        Assert.Equal(3, d);                                          // the spare
        ring.Publish(d);
        Assert.Equal(2, ring.Acquire());                             // 0 held, 1 held, 3 newest: 2 was superseded and is free again
        ring.Abandon(2);
        Assert.Equal(0, ring.Skipped);
        ring.Unpin(held);
        ring.Unpin(lane);
        Assert.Equal(0, ring.Pins(0));
        Assert.Equal(0, ring.Pins(1));
        Assert.Equal(3, ring.Latest);
        Assert.Equal(4, ring.Sequence);
    }

    [Fact]
    public void WithEveryOtherBufferHeldTheFrameIsSkippedAndCounted()
    {
        var ring = new FrameRing(3);
        var w = ring.Acquire(); ring.Publish(w);                     // 0 newest
        var r1 = ring.Pin();                                         // 0 held
        w = ring.Acquire(); ring.Publish(w);                         // 1 newest
        var r2 = ring.Pin();                                         // 1 held
        w = ring.Acquire(); ring.Publish(w);                         // 2 newest
        Assert.Equal(-1, ring.Acquire());                            // 0 held, 1 held, 2 newest: skip
        Assert.Equal(1, ring.Skipped);
        ring.Unpin(r1);
        Assert.Equal(0, ring.Acquire());                             // freed
        ring.Abandon(0);                                             // a draw that faulted: the last frame stands
        Assert.Equal(2, ring.Latest);
        Assert.Equal(0, ring.Acquire());
        ring.Unpin(r2);
    }

    [Fact]
    public void PublishNamesTheAcquiredBufferOnly()
    {
        var ring = new FrameRing(2);
        Assert.Throws<InvalidOperationException>(() => ring.Publish(0));
        var w = ring.Acquire();
        Assert.Throws<InvalidOperationException>(() => ring.Publish(w + 1));
        ring.Publish(w);
        Assert.Equal(w, ring.Latest);
    }

    [Fact]
    public void ALaneWaitsForANewerFrameAndWakesWhenTold()
    {
        var ring = new FrameRing(4);
        Assert.False(ring.WaitNewer(0, 20, out var seq));
        Assert.Equal(0, seq);

        var t = new Thread(() =>
        {
            Thread.Sleep(30);
            var w = ring.Acquire();
            ring.Publish(w);
        });
        t.Start();
        Assert.True(ring.WaitNewer(0, 5000, out seq));
        Assert.Equal(1, seq);
        t.Join();
        Assert.True(ring.WaitNewer(0, 0, out _));                    // already newer: no wait at all

        var woke = new ManualResetEventSlim();
        var waiter = new Thread(() =>
        {
            ring.WaitNewer(seq, 5000, out _);
            woke.Set();
        });
        waiter.Start();
        Thread.Sleep(20);
        ring.Wake();                                                 // told to stop: the wait ends without a frame
        Assert.True(woke.Wait(5000) || waiter.Join(5000));
    }

    [Fact]
    public void DrainForgetsTheNewestAndWaitsForTheReaders()
    {
        var ring = new FrameRing(4);
        var w = ring.Acquire(); ring.Publish(w);
        var held = ring.Pin();
        Assert.False(ring.Drain(20));                                // a reader still holds one
        Assert.Equal(-1, ring.Pin());                                // and no new reader gets one
        var t = new Thread(() => { Thread.Sleep(30); ring.Unpin(held); });
        t.Start();
        Assert.True(ring.Drain(5000));
        t.Join();
        Assert.Equal(-1, ring.Latest);
    }

    [Fact]
    public void AWriterAndTwoReadersRunningFlatOutNeverShareABuffer()
    {
        var ring = new FrameRing(4);
        var owner = new int[4];                                      // who is in each buffer: 1 writer, 2 reader (shared read is fine)
        var faults = 0;
        var stop = false;
        var writer = new Thread(() =>
        {
            while (!stop)
            {
                var i = ring.Acquire();
                if (i < 0) continue;
                if (Interlocked.CompareExchange(ref owner[i], 1, 0) != 0) Interlocked.Increment(ref faults);
                Thread.SpinWait(50);
                Interlocked.Exchange(ref owner[i], 0);
                ring.Publish(i);
            }
        });
        var readers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            while (!stop)
            {
                var i = ring.Pin();
                if (i < 0) continue;
                if (Volatile.Read(ref owner[i]) == 1) Interlocked.Increment(ref faults);
                Thread.SpinWait(200);
                if (Volatile.Read(ref owner[i]) == 1) Interlocked.Increment(ref faults);
                ring.Unpin(i);
            }
        })).ToList();
        writer.Start();
        readers.ForEach(r => r.Start());
        Thread.Sleep(300);
        stop = true;
        writer.Join();
        readers.ForEach(r => r.Join());
        Assert.Equal(0, faults);
        Assert.True(ring.Sequence > 100);
    }
}
