using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Patterns.App.Rendering;
using Patterns.Core.LowerThirds;
using Patterns.Core.Media;
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

    /// <summary>The element the designer is editing: a click on the stage picks it, a drag moves it.</summary>
    public static readonly StyledProperty<LowerThirdElement?> SelectedElementProperty =
        AvaloniaProperty.Register<LowerThirdPreview, LowerThirdElement?>(nameof(SelectedElement), defaultBindingMode: BindingMode.TwoWay);

    // The stage's sink lives behind a guard: the draw ops run on the compositor's render thread,
    // the page tab that hosts this control comes and goes on the UI thread, and a sink disposed
    // when the tab was left must never be drawn with again — it once was, on the tab's return,
    // and a fractal element's disposed shader ended the desk in an access violation.
    private readonly SinkGuard _sinks = new();
    private long _version;
    private LowerThirdElement? _drag;
    private Point _dragStart;
    private (double X, double Y) _dragFrom;
    private bool _dragMoved;

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

    public LowerThirdElement? SelectedElement
    {
        get => GetValue(SelectedElementProperty);
        set => SetValue(SelectedElementProperty, value);
    }

    /// <summary>The hold the preview gives a design that waits to be hidden.</summary>
    public const int WaitingHoldMs = 1500;

    // ---- picking and dragging elements on the stage ------------------------------

    /// <summary>The stage inside the control: the 16:9 picture letterboxed at this scale, from this origin — the same maths <see cref="RenderPreview"/> draws with.</summary>
    public static (float Scale, float Left, float Top) Stage(float width, float height)
    {
        var scale = Math.Min(width / 1920f, height / 1080f);
        return (scale, (width - 1920f * scale) / 2f, (height - 1080f * scale) / 2f);
    }

    /// <summary>An element's resting box (its way in ignored) in the control's own units.</summary>
    public static Rect BoxOnStage(LowerThirdDesign design, LowerThirdElement e, float width, float height)
    {
        var (scale, left, top) = Stage(width, height);
        var box = LowerThirdRenderer.BoxOf(design, new SKSizeI(1920, 1080), out var designScale);
        var k = designScale * scale;
        return new Rect(left + (box.Left + (float)e.X * designScale) * scale, top + (box.Top + (float)e.Y * designScale) * scale,
            Math.Max(1, e.W * k), Math.Max(1, e.H * k));
    }

    /// <summary>The topmost enabled element under a point (the last in the list draws on top), or null.</summary>
    public static LowerThirdElement? HitElement(LowerThirdDesign design, Point p, float width, float height)
    {
        for (var i = design.Elements.Count - 1; i >= 0; i--)
        {
            var e = design.Elements[i];
            if (e.Enabled && BoxOnStage(design, e, width, height).Contains(p)) return e;
        }
        return null;
    }

    /// <summary>Moves an element by a pointer travel in the control's units — design pixels follow the stage's scale.</summary>
    public static void DragBy(LowerThirdDesign design, LowerThirdElement e, (double X, double Y) from, Point delta, float width, float height)
    {
        var (scale, _, _) = Stage(width, height);
        LowerThirdRenderer.BoxOf(design, new SKSizeI(1920, 1080), out var designScale);
        var k = scale * designScale;
        if (k <= 0) return;
        e.X = Math.Round(from.X + delta.X / k);
        e.Y = Math.Round(from.Y + delta.Y / k);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Design is not { } design || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        var hit = HitElement(design, p, (float)Bounds.Width, (float)Bounds.Height);
        if (hit is null) return;
        SelectedElement = hit;
        _drag = hit;
        _dragStart = p;
        _dragFrom = (hit.X, hit.Y);
        _dragMoved = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is null || Design is not { } design || !ReferenceEquals(e.Pointer.Captured, this)) return;
        var p = e.GetPosition(this);
        var delta = new Point(p.X - _dragStart.X, p.Y - _dragStart.Y);
        if (!_dragMoved && Math.Abs(delta.X) + Math.Abs(delta.Y) < 3) return;
        _dragMoved = true;
        DragBy(design, _drag, _dragFrom, delta, (float)Bounds.Width, (float)Bounds.Height);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null) return;
        e.Pointer.Capture(null);
        _drag = null;
        _dragMoved = false;
        InvalidateVisual();
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var design = Design;
        var state = State;
        if (design is null || state is null || Bounds.Width < 1 || Bounds.Height < 1) return;
        context.Custom(new DrawOp(new Rect(0, 0, Bounds.Width, Bounds.Height), this, design, state, TimeMs));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _sinks.Open();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _sinks.Close();
    }

    /// <summary>
    /// The picture into any canvas, through the guard: false and nothing drawn while the control is
    /// off the visual tree (a draw op queued before the page was left, or run after). The draw op
    /// comes this way; so can a test, driving the page's own life cycle.
    /// </summary>
    public bool RenderTo(SKCanvas canvas, LowerThirdDesign design, ShowState state, double timeMs, float width, float height)
        => _sinks.Draw(sinks => RenderPreview(canvas, sinks("stage"), state, design, timeMs, width, height, ++_version));

    /// <summary>Whether the stage's sink is alive: on the visual tree, drawing.</summary>
    public bool IsStageOpen => _sinks.IsOpen;

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
        DrawClipNotes(c, sink, design);
        c.Restore();
    }

    /// <summary>
    /// What a clip element is holding, written on the stage. Nothing mounts a decoder for the
    /// designer — the clip plays when the design is on the preview or on air — so an imported
    /// video draws as a dark rectangle here, which reads as an import that failed. The note says
    /// which file it is and where to watch it move, and it is drawn by THIS control alone: the
    /// shared renderer is left exactly as the outputs run it, because a file name on the wall in
    /// front of a room would be a far worse fault than a dark rectangle on a desk.
    /// </summary>
    private static void DrawClipNotes(SKCanvas c, SinkState sink, LowerThirdDesign design)
    {
        var box = LowerThirdRenderer.BoxOf(design, new SKSizeI(1920, 1080), out var designScale);
        foreach (var e in design.Elements)
        {
            if (!e.Enabled || e.Kind != LowerThirdElementKind.Media) continue;
            if (e.Path.Length == 0 || !PlaylistSequencer.IsVideoPath(e.Path)) continue;
            if (InputBus.Resolve(InputKeys.Video(ShowFiles.Resolve(e.Path)), false) is not null) continue;

            var rect = SKRect.Create(
                box.Left + (float)e.X * designScale,
                box.Top + (float)e.Y * designScale,
                Math.Max(1f, (float)e.W * designScale),
                Math.Max(1f, (float)e.H * designScale));
            var missing = !ShowFiles.Exists(e.Path, fresh: false);      // a stage frame, not a check
            var font = sink.Paints.FontBold;
            var was = font.Size;
            font.Size = 30;
            var name = Path.GetFileName(e.Path);
            var note = missing ? "not found" : "plays on PVW and on air";
            var colour = missing ? new SKColor(0xFF, 0x6B, 0x6B) : new SKColor(0xFF, 0xFF, 0xFF, 0xCC);
            DrawUtil.TextCentered(c, name, rect.MidX, rect.MidY - 18, font, sink.Paints.Text(colour));
            font.Size = 24;
            DrawUtil.TextCentered(c, note, rect.MidX, rect.MidY + 22, font, sink.Paints.Text(new SKColor(0xFF, 0xFF, 0xFF, 0x99)));
            font.Size = was;
        }
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
                _owner.RenderTo(canvas, _design, _state, _timeMs, (float)Bounds.Width, (float)Bounds.Height);
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
