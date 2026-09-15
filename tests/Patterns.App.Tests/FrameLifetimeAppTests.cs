using Avalonia.Headless.XUnit;
using Patterns.App.Rendering;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Rendering.Media;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 64: a pipeline's frame is explicit on the render fence — open from the render's start to
/// its finally, after the canvas flushed — so a dispose from another thread waits for the frame,
/// a pooled buffer the frame leased is never recycled under it, and the seat goes when the frame
/// has closed.
/// </summary>
public class FrameLifetimeAppTests
{
    [AvaloniaFact]
    public void DisposeWaitsForTheOpenFrameAndNoPoolSlotTheFrameLeasedIsRecycledUnderIt()
    {
        var b = TestApp.Boot();
        var hold = new ManualResetEventSlim(false);
        var inFrame = new ManualResetEventSlim(false);
        var info = new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var pool = new FramePool(info, 8, 4);
        var leasedSlot = -1;
        var acquiredWhileOpen = -2;
        try
        {
            var bus = b.Services.Bus;
            var pipeline = new RenderPipeline(bus, PipelineViewport.Preview);
            pool.Publish(pool.Acquire());                                                                // slot 0 on show
            RenderPipeline.FrameEnding = () =>
            {
                // Inside the frame, on the render thread: the frame leases the frame on show, the
                // decoder replaces it (slot 0 retires behind this open frame), and asks for buffers.
                if (pool.TryLease(out var lease)) leasedSlot = lease.Slot;
                pool.Publish(pool.Acquire());                                                            // slot 1 on show, 0 retired
                pool.Acquire(); pool.Acquire();                                                          // 2 and 3 locked
                acquiredWhileOpen = pool.Acquire();                                                      // 0 must not come back: its frame is open
                inFrame.Set();
                hold.Wait(TimeSpan.FromSeconds(20));
            };
            using var surface = SKSurface.Create(new SKImageInfo(64, 36, SKColorType.Bgra8888, SKAlphaType.Premul));
            var open = RenderFence.OpenFrames;
            var render = new Thread(() => pipeline.Render(surface.Canvas, 64, 36, 1)) { IsBackground = true };
            render.Start();
            Assert.True(inFrame.Wait(TimeSpan.FromSeconds(20)), "the frame ran");
            Assert.Equal(0, leasedSlot);
            Assert.Equal(-1, acquiredWhileOpen);                                                         // starved rather than recycled
            Assert.Equal(open + 1, RenderFence.OpenFrames);

            // Another thread disposes the pipeline: it waits for the frame — the seat stays open,
            // and the retired slot stays held — however long the frame takes.
            var disposed = new ManualResetEventSlim(false);
            var dispose = new Thread(() => { pipeline.Dispose(); disposed.Set(); }) { IsBackground = true };
            dispose.Start();
            Assert.False(disposed.Wait(300), "dispose did not wait for the open frame");
            Assert.Equal(open + 1, RenderFence.OpenFrames);
            Assert.Equal(-1, pool.Acquire());                                                            // still held: 0 under the open frame, 1 on show, 2 and 3 locked
            hold.Set();
            Assert.True(disposed.Wait(TimeSpan.FromSeconds(20)), "dispose completed once the frame closed");
            render.Join(TimeSpan.FromSeconds(20));
            Assert.Equal(open, RenderFence.OpenFrames);
            Assert.Equal(0, pool.Acquire());                                                             // the frame closed: slot 0 is the decoder's again
        }
        finally
        {
            RenderPipeline.FrameEnding = null;
            hold.Set();
            pool.Dispose();
            b.Dispose();
        }
    }

    /// <summary>
    /// Round 65: the fence fails closed. A pipeline whose seat was reseated while it sat idle, with
    /// every seat then held by an open frame, draws black — never a pooled frame outside the fence —
    /// is listed as refused, lights the Fence seats row red with its name, and seats itself and
    /// draws on the first frame after a seat is given back.
    /// </summary>
    [AvaloniaFact]
    public void APipelineWithoutASeatDrawsBlackAndSeatsItselfWhenAFrameEnds()
    {
        var b = TestApp.Boot();
        var fill = new List<int>();
        try
        {
            var bus = b.Services.Bus;
            using var pipeline = new RenderPipeline(bus, PipelineViewport.Preview);
            using var surface = SKSurface.Create(new SKImageInfo(64, 36, SKColorType.Bgra8888, SKAlphaType.Premul));
            pipeline.Render(surface.Canvas, 64, 36, 1);                                                  // seated: an ordinary frame
            Assert.Empty(RenderFence.Refused);

            // Five seconds later on the fence's clock the pipeline's seat is idle; every seat is taken
            // and held by an open frame, the idle one reseated among them.
            RenderFence.Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 5;
            for (var i = 0; i < RenderFence.MaxSinks; i++)
            {
                var s = RenderFence.Register($"fill {i}");
                if (s < 0) break;
                RenderFence.BeginFrame(s);
                fill.Add(s);
            }
            Assert.Equal(-1, RenderFence.Register("probe"));
            RenderFence.Withdraw("probe");                                                              // the probe was never a sink

            surface.Canvas.Clear(SKColors.White);
            pipeline.Render(surface.Canvas, 64, 36, 1);                                                  // no seat: black, never a frame outside the fence
            using (var snap = surface.Snapshot())
            using (var bmp = SKBitmap.FromImage(snap))
            {
                Assert.Equal(SKColors.Black, bmp.GetPixel(32, 18));
            }
            Assert.Contains(PipelineViewport.Preview.Label, RenderFence.Refused);
            var row = SuperCheck.Run(b.Services.Metrics.GatherFacts()).Rows.Single(r => r.Item == "Fence seats");
            Assert.Equal(CheckLight.Red, row.Light);
            Assert.Contains(PipelineViewport.Preview.Label, row.Value);

            RenderFence.EndFrame(fill[0]);                                                              // a frame ends, its seat is given back
            RenderFence.Unregister(fill[0]);
            pipeline.Render(surface.Canvas, 64, 36, 1);
            Assert.DoesNotContain(PipelineViewport.Preview.Label, RenderFence.Refused);
            Assert.DoesNotContain(SuperCheck.Run(b.Services.Metrics.GatherFacts()).Rows, r => r.Item == "Fence seats");
        }
        finally
        {
            RenderFence.Clock = null;
            foreach (var s in fill)
            {
                RenderFence.EndFrame(s);
                RenderFence.Unregister(s);
            }
            RenderFence.Withdraw("probe");
            b.Dispose();
        }
    }
}
