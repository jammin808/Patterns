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

    /// <summary>
    /// Monitor mode: render the whole target (<see cref="ReferenceSize"/>) and scale it to fit
    /// the control, letterboxed — a true miniature, so a grid has the cell count it has on the
    /// wall. Rotation, warp and trims are the output's business and are not applied.
    /// </summary>
    public bool FitReference { get; init; }

    /// <summary>Read the sandbox snapshot while one is open (the PVW side of a monitor).</summary>
    public bool UsePreviewSnapshot { get; init; }

    /// <summary>A monitor of one content target: PGM or PVW side, at its true size, scaled to fit.</summary>
    public static PipelineViewport Monitor(string? targetId, SKSizeI targetSize, string label, bool previewSide)
        => new(SinkKind.Monitor, targetSize, default, targetId, 0, label)
        {
            FitReference = true,
            UsePreviewSnapshot = previewSide,
        };

    /// <summary>Physical rotation applied when blitting to the window (content stays upright).</summary>
    public OutputRotation Rotation { get; init; } = OutputRotation.None;

    /// <summary>4-corner warp offsets in physical pixels (all zero = no warp).</summary>
    public int WarpTlx { get; init; }
    public int WarpTly { get; init; }
    public int WarpTrx { get; init; }
    public int WarpTry { get; init; }
    public int WarpBlx { get; init; }
    public int WarpBly { get; init; }
    public int WarpBrx { get; init; }
    public int WarpBry { get; init; }

    public bool HasWarp =>
        WarpTlx != 0 || WarpTly != 0 || WarpTrx != 0 || WarpTry != 0 ||
        WarpBlx != 0 || WarpBly != 0 || WarpBrx != 0 || WarpBry != 0;

    /// <summary>Per-output colour trims (100/1.0/100/100/100 = neutral).</summary>
    public double BrightnessPct { get; init; } = 100;
    public double Gamma { get; init; } = 1.0;
    public double TrimRPct { get; init; } = 100;
    public double TrimGPct { get; init; } = 100;
    public double TrimBPct { get; init; } = 100;

    public bool HasTrims =>
        Math.Abs(BrightnessPct - 100) > 0.01 || Math.Abs(Gamma - 1.0) > 0.001 ||
        Math.Abs(TrimRPct - 100) > 0.01 || Math.Abs(TrimGPct - 100) > 0.01 || Math.Abs(TrimBPct - 100) > 0.01;

    public string TrimKey => $"{BrightnessPct:0.##}|{Gamma:0.###}|{TrimRPct:0.##}|{TrimGPct:0.##}|{TrimBPct:0.##}";
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

    // A draw op queued for the compositor's render thread can run after the control that
    // owns this pipeline has gone (a wall tile rebuilt, a window closed). Render and Dispose
    // therefore share one gate: a disposed pipeline draws nothing instead of touching freed
    // Skia handles.
    private readonly object _gate = new();
    private bool _disposed;

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
            // A running crossfade needs vsync redraw whatever the content would need.
            if (_sink.TransitionEndClock > ShowClock.Seconds) return RedrawCadence.Continuous;
            var vp = _viewport;
            var screenId = ScreenIdOverride?.Invoke() ?? vp.ScreenId;
            return PatternEngine.CadenceOf(SnapshotFor(vp), screenId, DateTime.UtcNow);
        }
    }

    /// <summary>The preview (and a monitor's PVW side) follows the sandbox while look programming is sandboxed; outputs, NDI and thumbnails always show program.</summary>
    private ShowSnapshot SnapshotFor(PipelineViewport vp)
        => vp.Kind == SinkKind.Preview || vp.UsePreviewSnapshot ? _bus.Sandbox ?? _bus.Current : _bus.Current;

    /// <summary>The snapshot this pipeline is showing right now (tally and pending checks).</summary>
    public ShowSnapshot CurrentSnapshot => SnapshotFor(_viewport);

    private SKColorFilter? _trimFilter;
    private string _trimFilterKey = "";
    private readonly SKPaint _trimPaint = new();
    private static readonly byte[] IdentityTable = BuildIdentity();

    private static byte[] BuildIdentity()
    {
        var t = new byte[256];
        for (var i = 0; i < 256; i++) t[i] = (byte)i;
        return t;
    }

    /// <summary>Renders into a leased Skia canvas whose current transform maps DIPs → device px.</summary>
    public void Render(SKCanvas canvas, double widthDips, double heightDips, double renderScaling)
    {
        lock (_gate)
        {
            if (_disposed) return;
            RenderLocked(canvas, widthDips, heightDips, renderScaling);
        }
    }

    private void RenderLocked(SKCanvas canvas, double widthDips, double heightDips, double renderScaling)
    {
        var frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var vp = _viewport;
        var physicalPx = new SKSizeI(
            Math.Max(1, (int)Math.Round(widthDips * renderScaling)),
            Math.Max(1, (int)Math.Round(heightDips * renderScaling)));

        if (vp.FitReference && vp.ReferenceSize != SKSizeI.Empty)
        {
            RenderFitted(canvas, vp, physicalPx, renderScaling, frameStart);
            return;
        }

        // For 90/270 rotations the engine renders portrait content, blitted rotated below.
        var rotated = vp.Rotation is OutputRotation.Rot90 or OutputRotation.Rot270;
        var effectivePx = rotated ? new SKSizeI(physicalPx.Height, physicalPx.Width) : physicalPx;
        var reference = vp.ReferenceSize == SKSizeI.Empty ? effectivePx : vp.ReferenceSize;

        var ctx = new RenderContext
        {
            ViewportSize = effectivePx,
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
            canvas.ClipRect(SKRect.Create(0, 0, physicalPx.Width, physicalPx.Height));

            var layered = false;
            if (vp.HasTrims)
            {
                if (_trimFilter is null || _trimFilterKey != vp.TrimKey)
                {
                    _trimFilter?.Dispose();
                    _trimFilter = SKColorFilter.CreateTable(
                        IdentityTable,
                        TrimTable.Build(vp.BrightnessPct, vp.Gamma, vp.TrimRPct),
                        TrimTable.Build(vp.BrightnessPct, vp.Gamma, vp.TrimGPct),
                        TrimTable.Build(vp.BrightnessPct, vp.Gamma, vp.TrimBPct));
                    _trimFilterKey = vp.TrimKey;
                }
                _trimPaint.ColorFilter = _trimFilter;
                canvas.SaveLayer(_trimPaint);
                layered = true;
            }

            if (vp.HasWarp)
            {
                // Keystone path: content renders to an offscreen surface at the effective
                // size, then blits through warp ∘ rotation as one perspective image draw.
                var surface = EnsureOffscreen(effectivePx);
                _engine.Render(surface.Canvas, SnapshotFor(vp), in ctx, _sink);
                surface.Canvas.Flush();
                using var image = surface.Snapshot();

                var warp = WarpMath.QuadWarp(physicalPx.Width, physicalPx.Height,
                    new SKPoint(vp.WarpTlx, vp.WarpTly),
                    new SKPoint(physicalPx.Width + vp.WarpTrx, vp.WarpTry),
                    new SKPoint(vp.WarpBlx, physicalPx.Height + vp.WarpBly),
                    new SKPoint(physicalPx.Width + vp.WarpBrx, physicalPx.Height + vp.WarpBry));
                canvas.Clear(SKColors.Black);
                canvas.Concat(in warp);
                canvas.Concat(RotationMatrix(vp.Rotation, physicalPx));
                canvas.DrawImage(image, 0, 0, Patterns.Core.Rendering.DrawUtil.Smooth, _warpPaint);
            }
            else
            {
                canvas.Concat(RotationMatrix(vp.Rotation, physicalPx));
                _engine.Render(canvas, SnapshotFor(vp), in ctx, _sink);
            }

            if (layered)
            {
                canvas.Restore();
            }
        }
        catch (Exception ex)
        {
            // Never let a render fault propagate into the compositor.
            Log.Error("Pipeline render failed.", ex);
        }
        finally
        {
            canvas.RestoreToCount(save);
            RenderStats.Record(vp.Kind, vp.SinkIndex,
                System.Diagnostics.Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds);
        }
    }

    private static readonly SKColor LetterboxColor = new(0x0A, 0x0A, 0x0F);

    /// <summary>
    /// Monitor rendering: the engine draws the target at its real size into a canvas scaled
    /// to fit the control, so the miniature is the output's picture, not a re-layout at the
    /// control's aspect. The same approach the screen overview uses.
    /// </summary>
    private void RenderFitted(SKCanvas canvas, PipelineViewport vp, SKSizeI physicalPx, double renderScaling, long frameStart)
    {
        var target = vp.ReferenceSize;
        var scale = Math.Min(physicalPx.Width / (float)target.Width, physicalPx.Height / (float)target.Height);
        var dx = (physicalPx.Width - target.Width * scale) / 2f;
        var dy = (physicalPx.Height - target.Height * scale) / 2f;

        var ctx = new RenderContext
        {
            ViewportSize = target,
            ReferenceSize = target,
            ViewportOrigin = default,
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
            canvas.Scale((float)(1.0 / renderScaling));
            canvas.ClipRect(SKRect.Create(0, 0, physicalPx.Width, physicalPx.Height));
            canvas.Clear(LetterboxColor);
            canvas.Translate(dx, dy);
            canvas.Scale(scale);
            canvas.ClipRect(SKRect.Create(0, 0, target.Width, target.Height));
            _engine.Render(canvas, SnapshotFor(vp), in ctx, _sink);
        }
        catch (Exception ex)
        {
            Log.Error("Monitor render failed.", ex);
        }
        finally
        {
            canvas.RestoreToCount(save);
            RenderStats.Record(vp.Kind, vp.SinkIndex,
                System.Diagnostics.Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds);
        }
    }

    private SKSurface? _offscreen;
    private SKSizeI _offscreenSize;
    private readonly SKPaint _warpPaint = new() { IsAntialias = true };

    private SKSurface EnsureOffscreen(SKSizeI size)
    {
        if (_offscreen is null || _offscreenSize != size)
        {
            _offscreen?.Dispose();
            _offscreen = SKSurface.Create(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            _offscreenSize = size;
        }
        return _offscreen!;
    }

    private static SKMatrix RotationMatrix(OutputRotation rotation, SKSizeI physicalPx) => rotation switch
    {
        OutputRotation.Rot90 => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateTranslation(physicalPx.Width, 0)),
        OutputRotation.Rot180 => SKMatrix.CreateRotationDegrees(180).PostConcat(SKMatrix.CreateTranslation(physicalPx.Width, physicalPx.Height)),
        OutputRotation.Rot270 => SKMatrix.CreateRotationDegrees(270).PostConcat(SKMatrix.CreateTranslation(0, physicalPx.Height)),
        _ => SKMatrix.Identity,
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _trimFilter?.Dispose();
            _trimPaint.Dispose();
            _warpPaint.Dispose();
            _offscreen?.Dispose();
            _sink.Dispose();
        }
    }
}
