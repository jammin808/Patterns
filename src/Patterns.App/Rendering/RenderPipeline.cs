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

    /// <summary>Physical rotation applied when blitting to the window (content stays upright).</summary>
    public OutputRotation Rotation { get; init; } = OutputRotation.None;

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
        var vp = _viewport;
        var physicalPx = new SKSizeI(
            Math.Max(1, (int)Math.Round(widthDips * renderScaling)),
            Math.Max(1, (int)Math.Round(heightDips * renderScaling)));

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

            switch (vp.Rotation)
            {
                case OutputRotation.Rot90:
                    canvas.Translate(physicalPx.Width, 0);
                    canvas.RotateDegrees(90);
                    break;
                case OutputRotation.Rot180:
                    canvas.Translate(physicalPx.Width, physicalPx.Height);
                    canvas.RotateDegrees(180);
                    break;
                case OutputRotation.Rot270:
                    canvas.Translate(0, physicalPx.Height);
                    canvas.RotateDegrees(270);
                    break;
            }

            _engine.Render(canvas, _bus.Current, in ctx, _sink);

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
        }
    }

    public void Dispose()
    {
        _trimFilter?.Dispose();
        _trimPaint.Dispose();
        _sink.Dispose();
    }
}
