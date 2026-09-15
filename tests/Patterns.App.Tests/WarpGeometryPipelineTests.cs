using Patterns.App.Rendering;
using Patterns.Core.Model;
using Patterns.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The geometry cache on the real output pipeline: built once, held through frames and through
/// the lattice's picks, rebuilt when the operator pulls a point or the window changes size — and
/// a steady mesh frame parsing and allocating none of it.
/// </summary>
public class WarpGeometryPipelineTests
{
    /// <summary>
    /// What the geometry path may still cost a steady frame over a plain output's frame: the two
    /// Skia wrappers the offscreen picture needs (its snapshot and that snapshot's shader, about
    /// 235 bytes here) — never the 256 patches' 512 arrays, the nodes or the parsed line, which
    /// were some 100 KB a frame before the cache.
    /// </summary>
    private const double GeometryBytesPerFrame = 512;

    /// <summary>
    /// What a steady frame of the whole pipeline may allocate, geometry and content together: a
    /// plain flat-field frame measures 0 here once the badge's shaped text and the transition
    /// key's lookup stopped allocating (it was 1.6 KB), a meshed one the wrappers above. The
    /// bound leaves room for another runtime's small change and none for a regression.
    /// </summary>
    private const double WholeFrameBytes = 1024;

    private static double BytesPerFrame(RenderPipeline pipeline, SKSurface surface, int w, int h)
    {
        for (var i = 0; i < 5; i++) pipeline.Render(surface.Canvas, w, h, 1.0);                      // warm-up: the offscreen, the shaders, the geometry
        const int frames = 20;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < frames; i++) pipeline.Render(surface.Canvas, w, h, 1.0);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)frames;
    }

    private static SnapshotBus WhiteBus()
    {
        var state = new ShowState();
        state.Pattern.Canvas.FollowOutput = true;
        state.Pattern.Kind = PatternKind.FlatField;
        state.Pattern.FlatField.Color = "#FFFFFF";
        state.Pattern.FlatField.ShowLabel = false;
        state.Pattern.FlatField.ShowBorder = false;
        state.Overlays.Clock.Enabled = false;
        state.Overlays.Info.Enabled = false;
        state.Countdown.Enabled = false;
        var bus = new SnapshotBus(state);
        bus.Publish(state);
        return bus;
    }

    private static PipelineViewport Output() => new(SinkKind.Output, SKSizeI.Empty, default, null, 1, "test");

    private static PipelineViewport DenseMesh(string mesh) => Output() with
    {
        WarpMeshColumns = 17, WarpMeshRows = 17, WarpMesh = mesh,
        WarpTopBow = 12, WarpTlx = 8,
        BlendRightPx = 120, BlendBlackPct = 6,
        ShowLattice = true, LatticePoint = 3,
    };

    [Fact]
    public void ASteadyMeshFrameBuildsItsGeometryOnceAndAllocatesNoneOfIt()
    {
        var rest = string.Join(";", Enumerable.Repeat("0,0", 17 * 17));
        var mesh = WarpGrid.Moved(rest, 17, 17, 0, 40, 30);
        var bus = WhiteBus();
        using var pipeline = new RenderPipeline(bus, DenseMesh(mesh));
        using var plain = new RenderPipeline(bus, Output());
        var info = new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var plainBytes = BytesPerFrame(plain, surface, 640, 360);
        var meshBytes = BytesPerFrame(pipeline, surface, 640, 360);
        Assert.Equal(1, pipeline.GeometryBuilds);
        Assert.Equal(0, plain.GeometryBuilds);
        Assert.True(meshBytes - plainBytes < GeometryBytesPerFrame, $"a steady 17 × 17 mesh frame allocated {meshBytes:0} bytes, a plain frame {plainBytes:0}: the geometry path costs {meshBytes - plainBytes:0} a frame");
        Assert.True(meshBytes < WholeFrameBytes, $"a steady 17 × 17 mesh frame allocated {meshBytes:0} bytes in all (a plain frame {plainBytes:0})");

        // The picture is the picture: the pulled corner is outside it, and between the lattice's lines it is white.
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        Assert.True(bmp.GetPixel(4, 4).Red < 30, $"the pulled corner: {bmp.GetPixel(4, 4)}");
        Assert.Equal(255, bmp.GetPixel(300, 168).Red);                                            // (320, 180) is a node: its dot is cyan
    }

    [Fact]
    public void APullARebuildOfTheViewportForAPickAndAResizeCostWhatTheyShould()
    {
        var rest = string.Join(";", Enumerable.Repeat("0,0", 17 * 17));
        var mesh = WarpGrid.Moved(rest, 17, 17, 0, 40, 30);
        using var pipeline = new RenderPipeline(WhiteBus(), DenseMesh(mesh));
        var info = new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        pipeline.Render(surface.Canvas, 640, 360, 1.0);
        Assert.Equal(1, pipeline.GeometryBuilds);

        // The operator pulls another point: the viewport is rebuilt with the new line, and the geometry once.
        pipeline.Viewport = DenseMesh(WarpGrid.Moved(mesh, 17, 17, 5, -10, 4));
        pipeline.Render(surface.Canvas, 640, 360, 1.0);
        pipeline.Render(surface.Canvas, 640, 360, 1.0);
        Assert.Equal(2, pipeline.GeometryBuilds);

        // The operator picks a point: the viewport is rebuilt for the overlay, and the geometry not at all.
        pipeline.Viewport = pipeline.Viewport with { LatticePoint = 7 };
        pipeline.Render(surface.Canvas, 640, 360, 1.0);
        Assert.Equal(2, pipeline.GeometryBuilds);

        // The window changes size: the nodes move, so the geometry is built again.
        pipeline.Render(surface.Canvas, 320, 180, 1.0);
        Assert.Equal(3, pipeline.GeometryBuilds);
        pipeline.Render(surface.Canvas, 320, 180, 1.0);
        Assert.Equal(3, pipeline.GeometryBuilds);
    }

    [Fact]
    public void APlainOutputBuildsNoGeometryAndAKeystonedOneBuildsItOnce()
    {
        var info = new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        using var plain = new RenderPipeline(WhiteBus(), Output());
        plain.Render(surface.Canvas, 320, 180, 1.0);
        plain.Render(surface.Canvas, 320, 180, 1.0);
        Assert.Equal(0, plain.GeometryBuilds);

        using var keyed = new RenderPipeline(WhiteBus(), Output() with { WarpTlx = 40, WarpTly = 20 });
        keyed.Render(surface.Canvas, 320, 180, 1.0);
        keyed.Render(surface.Canvas, 320, 180, 1.0);
        Assert.Equal(1, keyed.GeometryBuilds);
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        Assert.True(bmp.GetPixel(4, 4).Red < 30, $"top-left, keystoned: {bmp.GetPixel(4, 4)}");
        Assert.Equal(255, bmp.GetPixel(160, 90).Red);
    }
}
