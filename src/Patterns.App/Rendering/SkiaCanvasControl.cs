using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Patterns.Rendering;
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
            Interval = UntilNextSecond(DateTime.Now),
        };
        _secondTimer.Tick += (_, _) =>
        {
            // Wake just after each wall-clock second turns, so a clock or a countdown changes its
            // digits within a few milliseconds of the true second — and draws once for it. A fixed
            // quarter-second timer drew every sink with a clock on it four times a second, three of
            // them for nothing, and still changed the digits up to a quarter of a second late.
            _secondTimer.Interval = UntilNextSecond(DateTime.Now);
            InvalidateVisual();
        };
        ClipToBounds = true;
    }

    /// <summary>How long until the next second turns, plus a little so the frame lands after it — never less than a few milliseconds.</summary>
    public static TimeSpan UntilNextSecond(DateTime now)
        => TimeSpan.FromMilliseconds(Math.Max(5, 1000 - now.Millisecond + 5));

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
                if (!_secondTimer.IsEnabled)
                {
                    _secondTimer.Interval = UntilNextSecond(DateTime.Now);
                    _secondTimer.Start();
                }
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
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;                              // not in a window: nothing to ask, and nothing left marked as asked
        _frameRequested = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            var target = _pipeline?.Viewport.TargetFps ?? 0;
            if (target > 0 && _pipeline?.Cadence == RedrawCadence.Continuous)
            {
                // The slots that went by unpresented are the frames the room did not get: counted on
                // this sink's budget, so the glance line and the metrics say "slots missed" from the
                // pacer's own arithmetic, not from a guess at the frame time.
                var present = FramePacer.ShouldPresent(ShowClock.Seconds, target, ref _pacerSlot, out var missed);
                if (missed > 0) _pipeline.Budget.RecordMissed(missed, ShowClock.Seconds);
                if (!present)
                {
                    RequestFrame();
                    return;
                }
            }
            InvalidateVisual();
        });
    }

    /// <summary>Whether a vsync callback is outstanding (tests read it).</summary>
    public bool FrameRequested => _frameRequested;

    /// <summary>The pacer's last presented slot, -1 when none (tests read it).</summary>
    public long PacerSlot => _pacerSlot;

    /// <summary>
    /// Leaving the tree: the clock timer stops, an outstanding request is forgotten (its callback
    /// finds no control to draw and asks for nothing more) and the pacer starts afresh — so a
    /// canvas put back later asks for its frames again rather than waiting on a callback that
    /// was never coming.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _secondTimer.Stop();
        _frameRequested = false;
        _pacerSlot = -1;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Back in the tree (a pop-out closed, a pane re-docked): one frame now, and the cadence carries on from it.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InvalidateVisual();
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
