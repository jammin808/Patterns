using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Views.Controls;

/// <summary>
/// The designer's preview: a dark stage with the safe-area frame and the selected design at an
/// instant of its own timeline, drawn by the same renderer the outputs use. The design is shown
/// at 0 on the timeline and hidden after its hold (its own, or 1.5 s when it waits to be told),
/// so the scrubber runs the way in, the hold and the way out.
/// </summary>
public sealed class LowerThirdPreview : Control
{
    public static readonly StyledProperty<LowerThirdDesign?> DesignProperty =
        AvaloniaProperty.Register<LowerThirdPreview, LowerThirdDesign?>(nameof(Design));

    public static readonly StyledProperty<ShowState?> StateProperty =
        AvaloniaProperty.Register<LowerThirdPreview, ShowState?>(nameof(State));

    public static readonly StyledProperty<double> TimeMsProperty =
        AvaloniaProperty.Register<LowerThirdPreview, double>(nameof(TimeMs));

    private readonly SinkState _sink = new();
    private long _version;

    static LowerThirdPreview()
    {
        AffectsRender<LowerThirdPreview>(DesignProperty, StateProperty, TimeMsProperty);
    }

    public LowerThirdPreview()
    {
        ClipToBounds = true;
    }

    public LowerThirdDesign? Design
    {
        get => GetValue(DesignProperty);
        set => SetValue(DesignProperty, value);
    }

    public ShowState? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public double TimeMs
    {
        get => GetValue(TimeMsProperty);
        set => SetValue(TimeMsProperty, value);
    }

    /// <summary>The hold the preview gives a design that waits to be hidden.</summary>
    public const int WaitingHoldMs = 1500;

    public override void Render(DrawingContext context)
    {
        var design = Design;
        var state = State;
        if (design is null || state is null || Bounds.Width < 1 || Bounds.Height < 1) return;
        context.Custom(new DrawOp(new Rect(0, 0, Bounds.Width, Bounds.Height), this, design, state, TimeMs));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _sink.Dispose();
    }

    /// <summary>
    /// The picture, on any Skia canvas: the stage at 16:9 inside the given size, the safe-area
    /// frame, and the design at <paramref name="timeMs"/> of its timeline. Public so a test can
    /// draw it into a raster surface.
    /// </summary>
    public static void RenderPreview(SKCanvas c, SinkState sink, ShowState state, LowerThirdDesign design, double timeMs, float width, float height, long version)
    {
        c.Clear(new SKColor(0x0B, 0x0C, 0x10));
        var scale = Math.Min(width / 1920f, height / 1080f);
        if (scale <= 0) return;
        var stageW = 1920f * scale;
        var stageH = 1080f * scale;
        c.Save();
        c.Translate((width - stageW) / 2f, (height - stageH) / 2f);
        c.Scale(scale);

        // The stage: a quiet gradient so light and dark designs both read, and the safe-area frame.
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 1080),
                   new[] { new SKColor(0x2A, 0x31, 0x3E), new SKColor(0x14, 0x17, 0x1E) }, SKShaderTileMode.Clamp))
        {
            var stage = sink.Paints.Fill(SKColors.White);
            stage.Shader = shader;
            c.DrawRect(SKRect.Create(0, 0, 1920, 1080), stage);
            stage.Shader = null;
        }
        c.DrawRect(SKRect.Create(96, 54, 1728, 972), sink.Paints.StrokeAA(new SKColor(0xFF, 0xFF, 0xFF, 0x2A), 2));

        var snap = new ShowSnapshot { State = state, Version = version };
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(1920, 1080),
            ReferenceSize = new SKSizeI(1920, 1080),
            Time = timeMs / 1000.0,
            Now = DateTime.Now,
            UtcNow = DateTime.UtcNow,
            Sink = SinkKind.Preview,
            SinkIndex = 0,
            SinkLabel = "design",
        };
        var frame = new PatternFrame
        {
            Snapshot = snap,
            Config = state.Pattern,
            Ctx = ctx,
            Sink = sink,
            Canvas = new SKSizeI(1920, 1080),
            Palette = Palette.Resolve(snap),
        };
        double? hiddenAt = design.HoldMs > 0 ? null : (design.InMs + WaitingHoldMs) / 1000.0;
        LowerThirdRenderer.Render(c, in frame, design, shownAt: 0, hiddenAt, timeMs / 1000.0);
        c.Restore();
    }

    private sealed class DrawOp : ICustomDrawOperation
    {
        private readonly LowerThirdPreview _owner;
        private readonly LowerThirdDesign _design;
        private readonly ShowState _state;
        private readonly double _timeMs;

        public DrawOp(Rect bounds, LowerThirdPreview owner, LowerThirdDesign design, ShowState state, double timeMs)
        {
            Bounds = bounds;
            _owner = owner;
            _design = design;
            _state = state;
            _timeMs = timeMs;
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
            var scaling = TopLevel.GetTopLevel(_owner)?.RenderScaling ?? 1.0;
            var canvas = lease.SkCanvas;
            canvas.Save();
            canvas.Scale((float)scaling);
            try
            {
                // Every frame is a new version: the caches re-check their keys (a cheap no-op when nothing changed) so an edit shows at once.
                RenderPreview(canvas, _owner._sink, _state, _design, _timeMs, (float)Bounds.Width, (float)Bounds.Height, ++_owner._version);
            }
            catch (Exception ex)
            {
                Log.Warn("Lower-third preview failed to draw.", ex);
            }
            finally
            {
                canvas.Restore();
            }
        }
    }
}
