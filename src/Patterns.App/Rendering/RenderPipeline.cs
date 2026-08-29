using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Rendering;

/// <summary>Static description of what a sink shows (recomputed by the owner when screens change).</summary>
public sealed record PipelineViewport(
    SinkKind Kind,
    SKSizeI ReferenceSize,
    SKPointI ViewportOrigin,
    string? ScreenId,
    int SinkIndex,
    string Label)
{
    public static PipelineViewport Preview { get; } = new(SinkKind.Preview, SKSizeI.Empty, default, null, 0, "Preview");
}

/// <summary>
/// Glues one on-screen sink (preview or output window) to the engine: owns the sink state,
/// builds the per-frame context, measures FPS and renders 1:1 device pixels.
/// </summary>
public sealed class RenderPipeline : IDisposable
{
    private readonly PatternEngine _engine = new();
    private readonly SinkState _sink = new();
    private readonly SnapshotBus _bus;
    private long _frame;
    private volatile PipelineViewport _viewport;

    public RenderPipeline(SnapshotBus bus, PipelineViewport viewport)
    {
        _bus = bus;
        _viewport = viewport;
    }

    public PipelineViewport Viewport
    {
        get => _viewport;
        set => _viewport = value;
    }

    /// <summary>When the preview edits an independent screen, it mirrors that screen's pattern.</summary>
    public Func<string?>? ScreenIdOverride { get; set; }

    public RedrawCadence Cadence
    {
        get
        {
            var vp = _viewport;
            var screenId = ScreenIdOverride?.Invoke() ?? vp.ScreenId;
            return PatternEngine.CadenceOf(_bus.Current, screenId, DateTime.UtcNow);
        }
    }

    /// <summary>Renders into a leased Skia canvas whose current transform maps DIPs → device px.</summary>
    public void Render(SKCanvas canvas, double widthDips, double heightDips, double renderScaling)
    {
        var vp = _viewport;
        var viewportPx = new SKSizeI(
            Math.Max(1, (int)Math.Round(widthDips * renderScaling)),
            Math.Max(1, (int)Math.Round(heightDips * renderScaling)));

        var reference = vp.ReferenceSize == SKSizeI.Empty ? viewportPx : vp.ReferenceSize;

        var ctx = new RenderContext
        {
            ViewportSize = viewportPx,
            ReferenceSize = reference,
            ViewportOrigin = vp.ViewportOrigin,
            Time = ShowClock.Seconds,
            Now = DateTime.Now,
            UtcNow = DateTime.UtcNow,
            Frame = _frame++,
            Sink = vp.Kind,
            SinkIndex = vp.SinkIndex,
            SinkLabel = vp.Label,
            ScreenId = ScreenIdOverride?.Invoke() ?? vp.ScreenId,
            MeasuredFps = _sink.Fps.Fps,
        };
        _sink.Fps.Tick(ctx.Time);

        var save = canvas.Save();
        try
        {
            // Undo DPI scaling so the engine draws in device pixels — pixel-exact output.
            canvas.Scale((float)(1.0 / renderScaling));
            canvas.ClipRect(SKRect.Create(0, 0, viewportPx.Width, viewportPx.Height));
            _engine.Render(canvas, _bus.Current, in ctx, _sink);
        }
        catch (Exception ex)
        {
            // Never let a render fault propagate into the compositor.
            Log.Error("Pipeline render failed.", ex);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    public void Dispose() => _sink.Dispose();
}
