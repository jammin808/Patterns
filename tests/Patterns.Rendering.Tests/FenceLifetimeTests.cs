using Patterns.Rendering.Media;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 64: the render fence takes positive evidence only. A frame that drew a picture holds it
/// until that frame closes, however long it stays open — a stalled render thread quarantines what
/// it holds, never hands it out; a seat is never reseated under an open frame; a sink leaving
/// mid-frame holds until the frame ends; every retired image carries the table of the frames
/// that drew it, so a hung frame holds one frame's worth and nothing published after it.
/// </summary>
[Collection("RenderFence")]
public sealed class FenceLifetimeTests : IDisposable
{
    private const long T0 = 1_000_000_000L;
    private long _now = T0;

    private static long Sec(double seconds) => (long)(seconds * System.Diagnostics.Stopwatch.Frequency);

    public FenceLifetimeTests()
    {
        RenderFence.ResetForTests();
        RetiredFrames.ClearForTests();
        RenderFence.Clock = () => _now;
    }

    public void Dispose()
    {
        RenderFence.Clock = null;
        RenderFence.ResetForTests();
        RetiredFrames.ClearForTests();
    }

    [Fact]
    public void AStalledFrameHoldsItsLeaseForThirtySecondsAndReleasesItOnlyWhenItCloses()
    {
        var sink = RenderFence.Register("Output 1");
        var info = new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var pool = new FramePool(info, 8, 4);
        pool.Publish(pool.Acquire());                                                                    // slot 0 on show

        RenderFence.BeginFrame(sink);
        Assert.True(pool.TryLease(out var lease));                                                       // the frame draws slot 0
        Assert.Equal(0, lease.Slot);
        pool.Publish(pool.Acquire());                                                                    // slot 1 on show: slot 0 retires behind the open frame
        Assert.Equal(1, pool.Retired);

        // Thirty seconds pass with the frame still open: a hung frame. Its slot is not handed out,
        // the pool starves rather than reuses, and the health numbers say so.
        _now += Sec(30);
        Assert.Equal(2, pool.Acquire());
        Assert.Equal(3, pool.Acquire());
        Assert.Equal(-1, pool.Acquire());                                                                // 0 held by the hung frame, 1 on show, 2 and 3 locked
        Assert.Equal(1, pool.Starved);
        Assert.Equal(1, pool.Retired);
        Assert.Equal(RenderFence.Hold.Hung, pool.Hold);
        Assert.Equal(1, RenderFence.HungSinks);
        Assert.Equal(1, RenderFence.HungFrames);
        Assert.Equal(0, RenderFence.LiveSinks);                                                          // not drawing: hung is not live
        Assert.True(RenderFence.OldestOpenMs >= 30_000);
        var fault = Assert.Single(RenderFence.Faults);
        Assert.Equal(FenceFaultKind.Hung, fault.Kind);
        Assert.Equal("Output 1", fault.Label);
        Assert.True(fault.OpenMs >= 30_000);

        // A disposed pool under a hung frame stays in quarantine, its memory intact.
        pool.Release(2);
        pool.Release(3);
        pool.Dispose();
        FramePools.Sweep();
        Assert.False(pool.IsFreed);
        Assert.Equal(pool.Bytes, FramePools.QuarantinedBytes);
        _now += Sec(60);
        FramePools.Sweep();
        Assert.False(pool.IsFreed);                                                                      // a minute more is still no evidence

        // The frame closes: everything it held goes, and the record says it recovered.
        RenderFence.EndFrame(sink);
        FramePools.Sweep();
        Assert.True(pool.IsFreed);
        Assert.Equal(0, FramePools.QuarantinedBytes);
        Assert.Equal(0, RenderFence.HungSinks);
        Assert.Equal(1, RenderFence.HungFrames);                                                         // the session's count keeps it
        Assert.Equal(2, RenderFence.Faults.Count);
        Assert.Equal(FenceFaultKind.Recovered, RenderFence.Faults[1].Kind);
        RenderFence.Unregister(sink);
    }

    [Fact]
    public void ASinkLeavingIdleIsGoneAtOnceAndOneLeavingMidFrameHoldsUntilTheFrameCloses()
    {
        var idle = RenderFence.Register("idle");
        var busy = RenderFence.Register("busy");
        var table = new long[RenderFence.MaxSinks];

        RenderFence.BeginFrame(idle);
        RenderFence.Touch(table);
        RenderFence.EndFrame(idle);
        RenderFence.Unregister(idle);                                                                    // idle: its seat is free now
        Assert.False(RenderFence.IsSeated(idle));
        var mark = RenderFence.Take();
        Assert.True(RenderFence.Cleared(in mark, table));                                               // and what its closed frame drew was free already

        RenderFence.BeginFrame(busy);
        RenderFence.Touch(table);
        var mark2 = RenderFence.Take();
        RenderFence.Unregister(busy);                                                                    // mid-frame: the seat stays until the frame ends
        Assert.True(RenderFence.IsSeated(busy));
        Assert.True(RenderFence.IsOpen(busy));
        Assert.False(RenderFence.Cleared(in mark2, table));
        RenderFence.EndFrame(busy);
        Assert.False(RenderFence.IsSeated(busy));                                                        // gone with the frame
        Assert.True(RenderFence.Cleared(in mark2, table));

        // A stale ticket does nothing: the seat was given to another sink.
        var next = RenderFence.Register("next");
        RenderFence.BeginFrame(busy);                                                                    // the old sink, late: no seat
        Assert.Equal(-1, RenderFence.CurrentSink);
        RenderFence.EndFrame(busy);
        Assert.False(RenderFence.IsOpen(next));
        RenderFence.Unregister(next);
    }

    [Fact]
    public void AFullTableReseatsOnlyAnIdleSeatAndNeverOneWhoseFrameIsOpen()
    {
        var seats = new int[RenderFence.MaxSinks];
        for (var i = 0; i < seats.Length; i++)
        {
            seats[i] = RenderFence.Register($"seat {i}");
            Assert.True(seats[i] >= 0);
        }
        var open = seats[7];
        RenderFence.BeginFrame(open);                                                                    // one seat inside a long frame
        var table = new long[RenderFence.MaxSinks];
        RenderFence.Touch(table);
        var mark = RenderFence.Take();

        // Every other seat drew once long ago and stopped — idle, and reseatable; the open one is not.
        for (var i = 0; i < seats.Length; i++)
        {
            if (i == 7) continue;
            RenderFence.BeginFrame(seats[i]);
            RenderFence.EndFrame(seats[i]);
        }
        _now += Sec(30);
        for (var n = 0; n < 40; n++)
        {
            var late = RenderFence.Register("late");
            Assert.True(late >= 0);
            Assert.NotEqual(open & 0xFF, late & 0xFF);                                                    // never the open frame's seat
        }
        Assert.True(RenderFence.IsOpen(open));
        Assert.False(RenderFence.Cleared(in mark, table));                                              // and its lease is intact
        RenderFence.EndFrame(open);
        Assert.True(RenderFence.Cleared(in mark, table));

        // Every seat open: no seat to give.
        RenderFence.ResetForTests();
        for (var i = 0; i < seats.Length; i++)
        {
            seats[i] = RenderFence.Register();
            RenderFence.BeginFrame(seats[i]);
        }
        _now += Sec(30);
        Assert.Equal(-1, RenderFence.Register("nobody"));
        RenderFence.ResetForTests();
    }

    [Fact]
    public void AScratchFrameCarriesTheTableOfTheFramesThatDrewItSoAHungFrameHoldsOneFrameAndNothingAfter()
    {
        var hung = RenderFence.Register("hung");
        var slot = new FrameSlot();
        var first = SKImage.Create(new SKImageInfo(4, 4));
        slot.Publish(first);

        using var surface = SKSurface.Create(new SKImageInfo(8, 8));
        RenderFence.BeginFrame(hung);
        Assert.True(slot.Draw(surface.Canvas, SKRect.Create(8, 8), null, default));                     // the frame draws the first scratch frame

        // Frames keep coming while that frame stays open: each replaces the last, and every one
        // the hung frame never drew is freed at once — the quarantine is one frame, not a stream.
        for (var i = 0; i < 50; i++) slot.Publish(SKImage.Create(new SKImageInfo(4, 4)));
        _now += Sec(5);
        RetiredFrames.Sweep();
        Assert.Equal(1, RetiredFrames.Count);                                                            // the first alone waits
        Assert.NotEqual(IntPtr.Zero, first.Handle);
        Assert.Equal(1, RenderFence.HungSinks);
        Assert.Equal(4 * 4 * 4, RetiredFrames.QuarantinedBytes);

        RenderFence.EndFrame(hung);
        RetiredFrames.Sweep();
        Assert.Equal(0, RetiredFrames.Count);
        Assert.Equal(IntPtr.Zero, first.Handle);
        slot.Dispose();
        RenderFence.Unregister(hung);
    }

    /// <summary>
    /// Round 65: a full table refuses the next sink rather than seating it over an open frame, the
    /// refusal is on the list and in the fault record, and the sink is seated — and taken off the
    /// list — the moment a frame ends and its seat is given back.
    /// </summary>
    [Fact]
    public void AFullTableRefusesTheNextSinkAndSeatsItWhenAFrameEnds()
    {
        RenderFence.ResetForTests();
        try
        {
            var seats = new int[RenderFence.MaxSinks];
            for (var i = 0; i < seats.Length; i++)
            {
                seats[i] = RenderFence.Register($"S{i}");
                RenderFence.BeginFrame(seats[i]);
            }
            Assert.Equal(-1, RenderFence.Register("Late"));
            Assert.Equal(new[] { "Late" }, RenderFence.Refused);
            Assert.Equal(FenceFaultKind.Refused, RenderFence.Faults[^1].Kind);
            Assert.Equal(-1, RenderFence.Register("Late"));                                             // asked again: still refused, listed once
            Assert.Equal(new[] { "Late" }, RenderFence.Refused);

            RenderFence.EndFrame(seats[3]);
            RenderFence.Unregister(seats[3]);
            var late = RenderFence.Register("Late");
            Assert.True(late >= 0);
            Assert.Empty(RenderFence.Refused);
            Assert.Equal(FenceFaultKind.Seated, RenderFence.Faults[^1].Kind);
            Assert.Contains("seated (Late)", RenderFence.Faults[^1].ToString());

            RenderFence.BeginFrame(late);                                                               // its frame open: the table is full again
            Assert.Equal(-1, RenderFence.Register("Gone"));
            RenderFence.Withdraw("Gone");                                                               // a refused sink disposed leaves the list
            Assert.Empty(RenderFence.Refused);
        }
        finally
        {
            RenderFence.ResetForTests();
        }
    }
}
