using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Patterns.Core.Rendering;
using Patterns.Core.Services;

namespace Patterns.App.Rendering;

/// <summary>
/// Hosts a <see cref="RenderPipeline"/> in the Avalonia compositor via the Skia lease.
/// Redraw is demand-driven: continuous (vsync) only while the snapshot is animated,
/// once per second for clocks, and only on change for static patterns — idle cost ~0.
/// </summary>
public class SkiaCanvasControl : Control
{
    private RenderPipeline? _pipeline;
    private readonly DispatcherTimer _secondTimer;
    private bool _frameRequested;

    public SkiaCanvasControl()
    {
        _secondTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _secondTimer.Tick += (_, _) => InvalidateVisual();
        ClipToBounds = true;
    }

    public RenderPipeline? Pipeline
    {
        get => _pipeline;
        set
        {
            _pipeline = value;
            InvalidateVisual();
        }
    }

    /// <summary>Call when a new snapshot was published (UI thread).</summary>
    public void NotifyChanged() => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var pipeline = _pipeline;
        if (pipeline is null || Bounds.Width < 1 || Bounds.Height < 1) return;

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        context.Custom(new PipelineDrawOp(new Rect(0, 0, Bounds.Width, Bounds.Height), pipeline, scaling));

        ScheduleNext(pipeline);
    }

    private long _pacerSlot = -1;

    private void ScheduleNext(RenderPipeline pipeline)
    {
        switch (pipeline.Cadence)
        {
            case RedrawCadence.Continuous:
                _secondTimer.Stop();
                RequestFrame();
                break;

            case RedrawCadence.PerSecond:
                if (!_secondTimer.IsEnabled) _secondTimer.Start();
                break;

            default:
                _secondTimer.Stop();
                break;
        }
    }

    /// <summary>
    /// One vsync callback at a time. An output with a target rate presents only when the show
    /// clock has entered a new frame slot at that rate (<see cref="FramePacer"/>); on the other
    /// vsyncs it just asks for the next one, so a 30 fps show on a 60 Hz display draws every
    /// other refresh and never a frame late. Unpaced sinks draw on every vsync, as before.
    /// </summary>
    private void RequestFrame()
    {
        if (_frameRequested) return;
        _frameRequested = true;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            var target = _pipeline?.Viewport.TargetFps ?? 0;
            if (target > 0 && _pipeline?.Cadence == RedrawCadence.Continuous &&
                !FramePacer.ShouldPresent(ShowClock.Seconds, target, ref _pacerSlot))
            {
                RequestFrame();
                return;
            }
            InvalidateVisual();
        });
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _secondTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private sealed class PipelineDrawOp : ICustomDrawOperation
    {
        private readonly RenderPipeline _pipeline;
        private readonly double _scaling;

        public PipelineDrawOp(Rect bounds, RenderPipeline pipeline, double scaling)
        {
            Bounds = bounds;
            _pipeline = pipeline;
            _scaling = scaling;
        }

        public Rect Bounds { get; }

        public void Dispose()
        {
        }

        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;
            using var lease = leaseFeature.Lease();
            _pipeline.Render(lease.SkCanvas, Bounds.Width, Bounds.Height, _scaling);
        }
    }
}
