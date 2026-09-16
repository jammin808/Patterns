using Patterns.Core.Media;
using Patterns.Core.Services;
using Patterns.Rendering.Media;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// A page's frames from the browser to the glass (round 68): each JPEG is decoded straight into a
/// pooled buffer, queued for its time, published on the show clock and drawn through a lease; a
/// starved pool makes room by dropping the oldest waiting frame; a cut leaves nothing waiting; a
/// pool remade for a new size drops what waited at the old one.
/// </summary>
public class WebFramePipelineTests : IDisposable
{
    private const double Cadence = 1.0 / 30;
    private long _now = 1_000_000_000L;

    public WebFramePipelineTests()
    {
        RenderFence.ResetForTests();
        RenderFence.Clock = () => _now;
    }

    public void Dispose()
    {
        RenderFence.Clock = null;
        RenderFence.ResetForTests();
    }

    private static WebFramePipeline Standard(WebSmoothing mode = WebSmoothing.Smooth, long budget = 64L << 20)
        => new(FrameSmoother.Bounds.For(MachineClass.Standard), mode, budget);

    /// <summary>A JPEG of one colour, as the browser would send it.</summary>
    private static byte[] Jpeg(SKColor color, int width = 64, int height = 36)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    /// <summary>Draws the frame on show into a small surface and reads its middle.</summary>
    private static (DrawnFrame Drawn, SKColor Middle) Draw(WebFramePipeline pipeline)
    {
        using var surface = SKSurface.Create(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        var drawn = pipeline.Draw(surface.Canvas, new SKRect(0, 0, 8, 8), null, in FrameCrop.None);
        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        return (drawn, bitmap.GetPixel(4, 4));
    }

    private static void AssertNear(SKColor want, SKColor got)
    {
        Assert.True(Math.Abs(want.Red - got.Red) <= 12 && Math.Abs(want.Green - got.Green) <= 12 && Math.Abs(want.Blue - got.Blue) <= 12, $"wanted {want}, got {got}");
    }

    [Fact]
    public void AFrameIsDecodedIntoThePoolAndShownAtItsTime()
    {
        using var p = Standard();
        Assert.False(p.HasFrame);
        Assert.Equal(-1, p.PublishedClock);
        Assert.True(p.Offer(Jpeg(SKColors.Red), 10.0));
        Assert.Equal(1, p.Smoother.Held);
        Assert.NotNull(p.Pool);
        Assert.Equal(1, p.Pool!.Queued);
        Assert.Equal(p.Pool.Count - 3, p.Smoother.Cap);
        Assert.False(p.Present(10.05));                                                                   // due at 10.067
        Assert.False(p.HasFrame);
        Assert.True(p.Present(10.07));
        Assert.True(p.HasFrame);
        Assert.Equal(new SKSizeI(64, 36), p.Size);
        Assert.Equal(10.0, p.PublishedClock);
        Assert.Equal(0, p.Pool.Queued);
        var (drawn, middle) = Draw(p);
        Assert.True(drawn.Drew);
        Assert.True(drawn.IsLive);
        Assert.Equal(10.0, drawn.FrameClock);
        AssertNear(SKColors.Red, middle);
        Assert.True(p.DecodeMs > 0);
        Assert.Equal(1, p.Decoded);
        Assert.Equal(0, p.RefusedNoBuffer);
    }

    [Fact]
    public void TheNewestDueFrameShowsAndTheOlderOnesGoBackToThePool()
    {
        using var p = Standard();
        Assert.True(p.Offer(Jpeg(SKColors.Red), 10));
        Assert.True(p.Offer(Jpeg(SKColors.Lime), 10 + Cadence));
        Assert.True(p.Offer(Jpeg(SKColors.Blue), 10 + 2 * Cadence));
        Assert.Equal(3, p.Pool!.Queued);
        Assert.True(p.Present(10.5));
        AssertNear(SKColors.Blue, Draw(p).Middle);
        Assert.Equal(0, p.Pool.Queued);                                                                   // the two never shown are free again, not leaked
        Assert.Equal(2, p.Smoother.Dropped);
        Assert.Equal(10 + 2 * Cadence, p.PublishedClock, 9);
        // Every buffer but the one on show can be taken again.
        var free = 0;
        for (var i = 0; i < p.Pool.Count; i++)
        {
            var slot = p.Pool.Acquire();
            if (slot < 0) break;
            free++;
        }
        Assert.Equal(p.Pool.Count - 1, free);
    }

    [Fact]
    public void LowLatencyShowsAFrameAtOnce()
    {
        using var p = Standard(WebSmoothing.LowLatency);
        Assert.True(p.Offer(Jpeg(SKColors.Red), 10));
        Assert.True(p.Present(10));
        AssertNear(SKColors.Red, Draw(p).Middle);
        Assert.Equal(WebSmoothing.LowLatency, p.Mode);
        p.Mode = WebSmoothing.Smooth;
        Assert.Equal(WebSmoothing.Smooth, p.Smoother.Mode);
    }

    [Fact]
    public void AStarvedPoolMakesRoomByDroppingTheOldestWaitingFrame()
    {
        using var p = Standard(budget: 1);                                                                // the floor: four buffers, room for one waiting frame
        for (var n = 0; n < 5; n++)
        {
            Assert.True(p.Offer(Jpeg(n % 2 == 0 ? SKColors.Red : SKColors.Blue), 10 + n * Cadence));
        }
        Assert.Equal(FramePool.MinBuffers, p.Pool!.Count);
        Assert.Equal(1, p.Smoother.Cap);
        Assert.Equal(4, p.Smoother.Held);                                                                 // the pool bounds the ring: the first frame made room
        Assert.Equal(1, p.Smoother.Dropped);
        Assert.Equal(1, p.Pool.Starved);
        Assert.Equal(0, p.RefusedNoBuffer);
        Assert.True(p.Present(11));
        Assert.Equal(4, p.Smoother.Dropped);
        Assert.Equal(0, p.Pool.Queued);
        AssertNear(SKColors.Red, Draw(p).Middle);                                                         // frame 4
        Assert.Equal(10 + 4 * Cadence, p.PublishedClock, 9);
    }

    [Fact]
    public void CutReleasesEveryWaitingBufferAndTakesNoMoreUntilResume()
    {
        using var p = Standard();
        SKColor[] colours = { SKColors.Red, SKColors.Lime, SKColors.Blue };
        for (var n = 0; n < 3; n++) Assert.True(p.Offer(Jpeg(colours[n]), 10 + n * Cadence));
        p.Cut();
        Assert.Equal(0, p.Pool!.Queued);
        Assert.Equal(0, p.Smoother.Held);
        Assert.False(p.Offer(Jpeg(SKColors.Red), 10.2));
        Assert.Equal(1, p.Smoother.RefusedDraining);
        Assert.False(p.Present(11));
        Assert.False(p.HasFrame);                                                                         // nothing was ever shown; nothing waits
        p.Resume();
        Assert.True(p.Offer(Jpeg(SKColors.Blue), 10.3));                                                 // the same picture as the last before the cut: a cut forgets, so it decodes
        Assert.Equal(1, p.Smoother.Held);
        p.BeginLeaving();
        Assert.True(p.Smoother.Draining);
        Assert.True(p.Present(11));                                                                       // held frames still show through the fade
    }

    [Fact]
    public void ASizeChangeRemakesThePoolAndDropsWhatWaited()
    {
        using var p = Standard();
        Assert.True(p.Offer(Jpeg(SKColors.Red), 10));
        var first = p.Pool!;
        Assert.True(p.Offer(Jpeg(SKColors.Lime, 32, 18), 10 + Cadence));
        Assert.NotSame(first, p.Pool);
        Assert.Equal(32, p.Pool!.Width);
        Assert.Equal(1, p.Smoother.Held);
        Assert.Equal(1, p.Pool.Queued);
        Assert.True(p.Present(11));
        Assert.Equal(new SKSizeI(32, 18), p.Size);
        AssertNear(SKColors.Lime, Draw(p).Middle);
        Assert.True(first.IsFreed || first.Retired > 0 || first.Hold != RenderFence.Hold.Clear || first.Latest is null);
    }

    [Fact]
    public void TheSameBytesAgainAreSkippedBeforeAnyDecode()
    {
        using var p = Standard(WebSmoothing.LowLatency);
        var red = Jpeg(SKColors.Red);
        Assert.True(p.Offer(red, 10));
        Assert.False(p.Offer(red, 10 + Cadence));
        Assert.False(p.Offer(red, 10 + 2 * Cadence));
        Assert.Equal(1, p.Decoded);
        Assert.Equal(2, p.Duplicates);
        Assert.Equal(2, p.Report.Duplicates);
        Assert.True(p.Offer(Jpeg(SKColors.Blue), 10 + 3 * Cadence));                                      // a change decodes again
        Assert.Equal(2, p.Decoded);
        Assert.True(p.Offer(red, 10 + 4 * Cadence));                                                      // back to red: different from the last frame, so decoded
        Assert.Equal(3, p.Decoded);
    }

    [Fact]
    public void ABadFrameIsRefusedAndCostsNoBuffer()
    {
        using var p = Standard();
        Assert.False(p.Offer(new byte[] { 1, 2, 3 }, 10));
        Assert.False(p.Offer(ReadOnlySpan<byte>.Empty, 10));
        Assert.Null(p.Pool);
        Assert.True(p.Offer(Jpeg(SKColors.Red), 10));
        Assert.False(p.Offer(new byte[] { 0xFF, 0xD8, 0xFF }, 10.1));
        Assert.Equal(1, p.Pool!.Queued);
        Assert.Equal(1, p.Decoded);
    }

    [Fact]
    public void TheReportSaysWhatTheBufferIsDoing()
    {
        Assert.Equal("", WebFrameReport.None.Words);
        using var p = Standard();
        Assert.StartsWith("smooth 2 (67 ms)", p.Report.Words);
        Assert.True(p.Offer(Jpeg(SKColors.Red), 10));
        var report = p.Report;
        Assert.Equal(2, report.Depth);
        Assert.Equal(1, report.Held);
        Assert.True(report.DecodeMs > 0);
        Assert.Contains("decode", report.Words);
        Assert.Equal(p.Pool!.Bytes, report.PoolBytes);
        Assert.Equal(0, report.Underruns);
    }

    [Fact]
    public void DisposeRetiresThePoolAndRefusesEverything()
    {
        var p = Standard();
        Assert.True(p.Offer(Jpeg(SKColors.Red), 10));
        Assert.True(p.Present(11));
        p.Dispose();
        Assert.Null(p.Pool);
        Assert.False(p.HasFrame);
        Assert.False(p.Offer(Jpeg(SKColors.Red), 12));
        Assert.False(p.Present(13));
        Assert.False(p.Draw(SKSurface.Create(new SKImageInfo(4, 4)).Canvas, new SKRect(0, 0, 4, 4), null, in FrameCrop.None).Drew);
        p.Dispose();                                                                                      // twice is nothing
    }

    [Fact]
    public void AQueuedFrameSurvivesAnotherPublishAndFreesOnRelease()
    {
        using var pool = new FramePool(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Opaque), 32, 4);
        var a = pool.Acquire();
        pool.Queue(a);
        Assert.Equal(1, pool.Queued);
        var b = pool.Acquire();
        pool.Decoded(b);
        var c = pool.Acquire();
        Assert.NotNull(pool.Publish(c));                                                                  // b (decoded, unshown) is dropped; a (queued) waits on
        Assert.Equal(1, pool.Queued);
        Assert.Equal(b, pool.Acquire());
        Assert.NotNull(pool.Publish(a));                                                                  // its time came
        Assert.Equal(0, pool.Queued);
        var d = pool.Acquire();
        pool.Queue(d);
        pool.Release(d);
        Assert.Equal(0, pool.Queued);
        Assert.Null(pool.Publish(d));                                                                     // let go: nothing to show from it
        Assert.Equal(d, pool.Acquire());
    }
}
