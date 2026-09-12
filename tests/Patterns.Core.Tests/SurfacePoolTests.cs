using Patterns.Core.Effects;
using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// One sink, two sizes a frame — a multiview's tiles, a screen layer, a dissolve — and the CPU
/// rasters keep a frame per size instead of disposing and allocating one on every draw.
/// </summary>
[Collection("InputBus")]
public class SurfacePoolTests
{
    private sealed class Frame : IDisposable
    {
        public Frame(SKSizeI size) => Size = size;
        public SKSizeI Size { get; }
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void APoolKeepsAFramePerSizeAndDropsTheLeastRecentlyDrawnPastItsCapacity()
    {
        using var pool = new SurfacePool<Frame>();
        var a = pool.Get(new SKSizeI(240, 135), s => new Frame(s));
        var b = pool.Get(new SKSizeI(120, 200), s => new Frame(s));
        Assert.NotSame(a, b);
        Assert.Same(a, pool.Get(new SKSizeI(240, 135), s => new Frame(s)));     // the same size is the same frame
        Assert.Same(b, pool.Get(new SKSizeI(120, 200), s => new Frame(s)));
        Assert.Equal(2, pool.Count);
        Assert.Same(b, pool.Latest);

        pool.Get(new SKSizeI(1, 1), s => new Frame(s));
        pool.Get(new SKSizeI(2, 2), s => new Frame(s));
        Assert.Equal(SurfacePool<Frame>.Capacity, pool.Count);
        pool.Get(new SKSizeI(3, 3), s => new Frame(s));                        // one past capacity: the least recently drawn goes
        Assert.Equal(SurfacePool<Frame>.Capacity, pool.Count);
        Assert.True(a.Disposed);
        Assert.False(b.Disposed);
    }

    [Fact]
    public void AFractalSinkDrawingTwoSizesKeepsTwoFramesInsteadOfReallocatingEachDraw()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.Fractal;
            s.Pattern.Fractal.Quality = FractalQuality.Fast;
            s.Pattern.Fractal.Iterations = 8;
        });
        var snap = RenderTestHarness.Snap(state);
        using var sink = new SinkState();
        var engine = new PatternEngine();
        using var wide = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var tall = SKSurface.Create(new SKImageInfo(180, 320, SKColorType.Bgra8888, SKAlphaType.Premul));

        void Draw(SKSurface surface, int w, int h, double time)
        {
            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(w, h), ReferenceSize = new SKSizeI(w, h), Time = time,
                Now = new DateTime(2026, 9, 12, 12, 0, 0), UtcNow = RenderTestHarness.FixedUtcNow, Sink = SinkKind.Ndi, SinkIndex = 1, SinkLabel = "test",
            };
            engine.Render(surface.Canvas, snap, in ctx, sink);
        }

        Draw(wide, 320, 180, 1.0);
        Draw(tall, 180, 320, 1.0);
        Assert.Equal(2, sink.Fractals.Count);
        var frames = sink.Fractals.All.ToArray();
        Draw(wide, 320, 180, 1.1);
        Draw(tall, 180, 320, 1.1);
        Assert.Equal(2, sink.Fractals.Count);
        Assert.Equal(frames, sink.Fractals.All.ToArray());                     // the same two frames, drawn into again
        Assert.Empty(sink.FractalEffects);                                     // the CPU path: no shader compiled
    }

    [Fact]
    public void ALowerThirdFractalRastersAtTheDesignsSizeAndKeepsAFramePerSizeItIsAsked()
    {
        var state = RenderTestHarness.State(s => s.Pattern.Kind = PatternKind.FlatField);
        var design = new LowerThirdDesign { Name = "Wave", Width = 1200, Height = 300, InMs = 100, OutMs = 100 };
        var element = new LowerThirdElement { Name = "Wave", Kind = LowerThirdElementKind.Fractal, X = 0, Y = 0, W = 1200, H = 300 };
        element.Fractal.Quality = FractalQuality.Fast;
        element.Fractal.Iterations = 8;
        design.Elements.Add(element);
        state.LowerThirds.Designs.Add(design);
        state.LowerThirds.Show(design, ShowClock.UtcAt(1));
        var snap = RenderTestHarness.Snap(state);
        using var sink = new SinkState();
        var engine = new PatternEngine();
        using var big = SKSurface.Create(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var small = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));

        void Draw(SKSurface surface, int w, int h, double time)
        {
            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(w, h), ReferenceSize = new SKSizeI(w, h), Time = time,
                Now = new DateTime(2026, 9, 12, 12, 0, 0), UtcNow = RenderTestHarness.FixedUtcNow, Sink = SinkKind.Ndi, SinkIndex = 1, SinkLabel = "test",
            };
            engine.Render(surface.Canvas, snap, in ctx, sink);
        }

        // Two tiles of one wall: the raster is the design's own working size, so one frame serves both and the 25 fps gate holds across them.
        Draw(big, 640, 360, 1.5);
        Draw(small, 320, 180, 1.5);
        var cache = sink.LowerThirds[element.Id];
        Assert.Equal(1, cache.FractalSizes);
        Assert.Equal(1, cache.FractalFrames);
        Draw(big, 640, 360, 1.51);
        Assert.Equal(1, cache.FractalFrames);

        // A finer quality is a wider raster: a second frame, drawn at once, beside the first.
        element.Fractal.Quality = FractalQuality.Fine;
        snap = RenderTestHarness.Snap(state, version: 2);
        Draw(big, 640, 360, 1.52);
        Assert.Equal(2, cache.FractalSizes);
        Assert.Equal(2, cache.FractalFrames);

        // Back to the first: its frame is still there and still fresh, so nothing is rastered.
        element.Fractal.Quality = FractalQuality.Fast;
        snap = RenderTestHarness.Snap(state, version: 3);
        Draw(big, 640, 360, 1.53);
        Assert.Equal(2, cache.FractalSizes);
        Assert.Equal(2, cache.FractalFrames);
    }
}
