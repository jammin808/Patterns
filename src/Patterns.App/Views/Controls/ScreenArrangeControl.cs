using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Views.Controls;

/// <summary>
/// The graphical screen overview: every detected screen as a tile showing the content it is
/// actually displaying (span slices, custom patterns, blackout — rendered by the real engine).
/// Drag a screen flush against another to join them into one canvas; drag it away to split.
/// Click selects; the detail strip below the control edits the selection.
/// </summary>
public sealed class ScreenArrangeControl : Control
{
    private const double MaxViewScale = 0.22;

    private readonly PatternEngine _engine = new();
    private readonly Dictionary<string, SinkState> _sinks = new();
    private readonly DispatcherTimer _animTimer;
    private long _frame;

    private MainViewModel? _vm;
    private Action? _publishedHandler;

    // Drag state (UI thread only).
    private ScreenPlacement? _dragPlacement;
    private SKRectI _dragStartRect;
    private Point _pointerStart;
    private SKRectI _dragPreview;
    private bool _dragging;
    private bool _snapConnected;
    private ViewState? _lastView; // what the last frame drew — the animation check walks its tiles

    public ScreenArrangeControl()
    {
        ClipToBounds = true;
        _animTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _animTimer.Tick += (_, _) =>
        {
            if (AnyTileAnimated()) InvalidateVisual();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _publishedHandler = () => InvalidateVisual();
            _vm.Services.SnapshotPublished += _publishedHandler;
        }
        _animTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _animTimer.Stop();
        if (_vm is not null && _publishedHandler is not null)
        {
            _vm.Services.SnapshotPublished -= _publishedHandler;
        }
        foreach (var s in _sinks.Values)
        {
            s.Dispose();
        }
        _sinks.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private bool AnyTileAnimated()
    {
        if (_vm is null) return false;
        if (_dragging) return true;
        var snap = _vm.Services.Bus.Current;
        if (_lastView is not { } view) return false;
        foreach (var tile in view.Tiles)
        {
            // The tile's viewport names its content target (a canvas key for a member) —
            // the same resolution the output makes, so the overview animates when it does.
            if (!tile.Placement.Enabled || tile.Viewport is not { } vp) continue;
            if (PatternEngine.CadenceOf(snap, vp.ScreenId, DateTime.UtcNow) == RedrawCadence.Continuous)
            {
                return true;
            }
        }
        return false;
    }

    // ---- view transform -----------------------------------------------------

    private sealed record Tile(
        ScreenPlacement Placement, ScreenInfo Info, SKRectI Arranged, Rect View,
        bool Selected, int GroupIndex, int GroupSize, PipelineViewport? Viewport, int Number);

    private sealed record ViewState(double Scale, double OffsetX, double OffsetY, List<Tile> Tiles,
        List<(Rect View, string Label, int GroupIndex)> GroupOutlines);

    private ViewState? BuildView()
    {
        if (_vm is null || Bounds.Width < 40 || Bounds.Height < 40) return null;
        var screens = _vm.Services.Screens.All;
        var placements = _vm.State.Output.Placements;

        var entries = new List<(ScreenPlacement P, ScreenInfo Info, SKRectI Rect)>();
        foreach (var p in placements)
        {
            var info = screens.FirstOrDefault(s => s.Id == p.ScreenId);
            if (info is null) continue;
            var size = OutputWindowManager.EffectiveSize(p, info);
            var rect = p == _dragPlacement && _dragging
                ? _dragPreview
                : SKRectI.Create(p.X, p.Y, size.Width, size.Height);
            entries.Add((p, info, rect));
        }
        if (entries.Count == 0) return null;

        // Fit all rects (plus margin) into the control.
        var b = entries[0].Rect;
        foreach (var e in entries.Skip(1))
        {
            b = new SKRectI(Math.Min(b.Left, e.Rect.Left), Math.Min(b.Top, e.Rect.Top),
                Math.Max(b.Right, e.Rect.Right), Math.Max(b.Bottom, e.Rect.Bottom));
        }
        const double pad = 26;
        var scale = Math.Min(MaxViewScale, Math.Min(
            (Bounds.Width - 2 * pad) / Math.Max(1, b.Width),
            (Bounds.Height - 2 * pad) / Math.Max(1, b.Height)));
        var offX = (Bounds.Width - b.Width * scale) / 2 - b.Left * scale;
        var offY = (Bounds.Height - b.Height * scale) / 2 - b.Top * scale;

        // Grouping over ENABLED screens at their committed (or previewed) positions.
        var enabledArr = entries.Where(x => x.P.Enabled)
            .Select(x => new ArrangedScreen(x.P.ScreenId, x.Rect, x.P.BlendAuto))
            .ToList();
        var groups = ScreenLayout.Groups(enabledArr);
        var groupIndexOf = new Dictionary<string, (int Index, int Size)>();
        for (var gi = 0; gi < groups.Count; gi++)
        {
            foreach (var m in groups[gi])
            {
                groupIndexOf[m.Id] = (gi, groups[gi].Count);
            }
        }

        // What each enabled screen actually shows.
        var viewports = OutputWindowManager
            .BuildViewports(placements.Where(p => p.Enabled), screens)
            .ToDictionary(x => x.Screen.Id, x => x.Viewport);

        var numbered = entries.OrderBy(x => x.Rect.Left).ThenBy(x => x.Rect.Top).ToList();
        var numberOf = numbered.Select((x, i) => (x.P.ScreenId, N: i + 1)).ToDictionary(x => x.ScreenId, x => x.N);

        var tiles = new List<Tile>();
        foreach (var (p, info, rect) in entries)
        {
            var view = new Rect(offX + rect.Left * scale, offY + rect.Top * scale, rect.Width * scale, rect.Height * scale);
            var (gi, gs) = p.Enabled && groupIndexOf.TryGetValue(p.ScreenId, out var g) ? g : (-1, 1);
            viewports.TryGetValue(p.ScreenId, out var vp);
            tiles.Add(new Tile(p, info, rect, view, p == _vm.SelectedPlacement, gi, gs, p.Enabled ? vp : null, numberOf[p.ScreenId]));
        }

        var outlines = new List<(Rect, string, int)>();
        var canvasIndex = 0;
        for (var gi = 0; gi < groups.Count; gi++)
        {
            if (groups[gi].Count < 2) continue;
            var u = ScreenLayout.Union(groups[gi]);
            var view = new Rect(offX + u.Left * scale, offY + u.Top * scale, u.Width * scale, u.Height * scale);
            outlines.Add((view, $"Canvas {(char)('A' + canvasIndex++)} · {u.Width}×{u.Height}", gi));
        }

        return new ViewState(scale, offX, offY, tiles, outlines);
    }

    // ---- rendering ----------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var view = BuildView();
        _lastView = view;
        if (_vm is null)
        {
            return;
        }
        context.Custom(new ArrangeDrawOp(new Rect(0, 0, Bounds.Width, Bounds.Height), this, view,
            _vm.Services.Bus.Current, _frame++, _dragging, _snapConnected, _dragPlacement?.ScreenId));
    }

    private sealed class ArrangeDrawOp : ICustomDrawOperation
    {
        private static readonly SKColor BgColor = new(0x0B, 0x0C, 0x10);
        private static readonly SKColor DotColor = new(0xFF, 0xFF, 0xFF, 0x14);
        private static readonly SKColor TileOff = new(0x23, 0x26, 0x2E);

        private readonly ScreenArrangeControl _owner;
        private readonly ViewState? _view;
        private readonly ShowSnapshot _snap;
        private readonly long _frame;
        private readonly bool _dragging;
        private readonly bool _snapConnected;
        private readonly string? _dragId;

        public ArrangeDrawOp(Rect bounds, ScreenArrangeControl owner, ViewState? view, ShowSnapshot snap,
            long frame, bool dragging, bool snapConnected, string? dragId)
        {
            Bounds = bounds;
            _owner = owner;
            _view = view;
            _snap = snap;
            _frame = frame;
            _dragging = dragging;
            _snapConnected = snapConnected;
            _dragId = dragId;
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
            var c = lease.SkCanvas;

            var save = c.Save();
            try
            {
                c.ClipRect(SKRect.Create(0, 0, (float)Bounds.Width, (float)Bounds.Height));
                c.Clear(BgColor);
                DrawDotGrid(c);

                if (_view is null)
                {
                    DrawCentered(c, "No screens detected", (float)Bounds.Width / 2, (float)Bounds.Height / 2, 14, SKColors.Gray);
                    return;
                }

                foreach (var tile in _view.Tiles)
                {
                    DrawTile(c, tile);
                }

                foreach (var (view, label, gi) in _view.GroupOutlines)
                {
                    var hue = DrawUtil.Hue(gi, Math.Max(1, _view.GroupOutlines.Count + 2));
                    using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = hue, PathEffect = DrawUtil.DashLong };
                    var r = ToSk(view).InflateCopy(4);
                    c.DrawRoundRect(r, 8, 8, stroke);
                    DrawBadge(c, label, r.MidX, r.Top - 12, hue);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Screen overview render failed.", ex);
            }
            finally
            {
                c.RestoreToCount(save);
            }
        }

        private void DrawTile(SKCanvas c, Tile tile)
        {
            var r = ToSk(tile.View);
            var isDragTile = _dragging && tile.Placement.ScreenId == _dragId;

            // Live content, rendered by the real engine exactly as the output shows it.
            if (tile.Placement.Enabled && tile.Viewport is { } vp)
            {
                var sink = _owner.SinkFor(tile.Placement.ScreenId);
                var deviceW = tile.Arranged.Width;
                var deviceH = tile.Arranged.Height;
                var scale = r.Width / Math.Max(1, deviceW);

                var save = c.Save();
                c.ClipRoundRect(new SKRoundRect(r, 5, 5), antialias: true);
                c.Translate(r.Left, r.Top);
                c.Scale(scale);
                var reference = vp.ReferenceSize == SKSizeI.Empty ? new SKSizeI(deviceW, deviceH) : vp.ReferenceSize;
                var ctx = new RenderContext
                {
                    ViewportSize = new SKSizeI(deviceW, deviceH),
                    ReferenceSize = reference,
                    ViewportOrigin = vp.ViewportOrigin,
                    Time = ShowClock.Seconds,
                    Now = DateTime.Now,
                    UtcNow = DateTime.UtcNow,
                    Frame = _frame,
                    Sink = SinkKind.Thumbnail,
                    SinkIndex = tile.Number,
                    SinkLabel = tile.Info.Label,
                    ScreenId = vp.ScreenId,
                };
                _owner._engine.Render(c, _snap, in ctx, sink);
                c.RestoreToCount(save);
            }
            else
            {
                using var off = new SKPaint { IsAntialias = true, Color = TileOff };
                c.DrawRoundRect(r, 5, 5, off);
                DrawCentered(c, "OFF", r.MidX, r.MidY, Math.Max(10, r.Height * 0.16f), new SKColor(0x8A, 0x93, 0xA3));
            }

            // Frame.
            var borderColor = tile.Selected
                ? new SKColor(0x3E, 0xC1, 0xF3)
                : tile.GroupSize > 1 && tile.GroupIndex >= 0
                    ? DrawUtil.Hue(tile.GroupIndex, Math.Max(1, 4))
                    : new SKColor(0x4A, 0x50, 0x5E);
            using (var border = new SKPaint
                   {
                       IsAntialias = true, Style = SKPaintStyle.Stroke,
                       StrokeWidth = tile.Selected ? 2.5f : 1.5f,
                       Color = isDragTile && _snapConnected ? new SKColor(0x2E, 0xE6, 0x8A) : borderColor,
                   })
            {
                c.DrawRoundRect(r, 5, 5, border);
            }

            if (isDragTile && _snapConnected)
            {
                using var glow = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6,
                    Color = new SKColor(0x2E, 0xE6, 0x8A, 0x50),
                };
                c.DrawRoundRect(r, 6, 6, glow);
            }

            // Number badge + caption.
            DrawBadge(c, tile.Number.ToString(), r.Left + 14, r.Top + 14, borderColor);
            var caption = $"{tile.Info.Label} · {tile.Arranged.Width}×{tile.Arranged.Height}";
            DrawCentered(c, caption, r.MidX, r.Bottom + 11, 11, new SKColor(0xC8, 0xD0, 0xDC));
        }

        private void DrawDotGrid(SKCanvas c)
        {
            using var dot = new SKPaint { Color = DotColor };
            for (float y = 8; y < Bounds.Height; y += 22)
            {
                for (float x = 8; x < Bounds.Width; x += 22)
                {
                    c.DrawRect(SKRect.Create(x, y, 1.4f, 1.4f), dot);
                }
            }
        }

        private static void DrawBadge(SKCanvas c, string text, float cx, float cy, SKColor color)
        {
            using var font = new SKFont(Typefaces.SemiBold, 11);
            using var textPaint = new SKPaint { IsAntialias = true, Color = SKColors.White };
            using var bg = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 0xB4) };
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = color };
            var w = font.MeasureText(text) + 12;
            var rect = SKRect.Create(cx - w / 2, cy - 9, w, 18);
            c.DrawRoundRect(rect, 9, 9, bg);
            c.DrawRoundRect(rect, 9, 9, stroke);
            var m = font.Metrics;
            c.DrawText(text, rect.MidX, rect.MidY - (m.Ascent + m.Descent) / 2, SKTextAlign.Center, font, textPaint);
        }

        private static void DrawCentered(SKCanvas c, string text, float cx, float cy, float size, SKColor color)
        {
            using var font = new SKFont(Typefaces.Regular, size);
            using var paint = new SKPaint { IsAntialias = true, Color = color };
            var m = font.Metrics;
            c.DrawText(text, cx, cy - (m.Ascent + m.Descent) / 2, SKTextAlign.Center, font, paint);
        }

        private static SKRect ToSk(Rect r) => SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
    }

    private SinkState SinkFor(string screenId)
    {
        if (!_sinks.TryGetValue(screenId, out var sink))
        {
            sink = new SinkState();
            _sinks[screenId] = sink;
        }
        return sink;
    }

    // ---- interaction --------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_vm is null) return;
        var view = BuildView();
        if (view is null) return;

        var pos = e.GetPosition(this);
        var hit = view.Tiles.LastOrDefault(t => t.View.Contains(pos));
        if (hit is null) return;

        _vm.SelectedPlacement = hit.Placement;
        _dragPlacement = hit.Placement;
        _dragStartRect = hit.Arranged;
        _dragPreview = hit.Arranged;
        _pointerStart = pos;
        _dragging = false;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_vm is null || _dragPlacement is null || !ReferenceEquals(e.Pointer.Captured, this)) return;
        var view = BuildView();
        if (view is null) return;

        var pos = e.GetPosition(this);
        if (!_dragging && (Math.Abs(pos.X - _pointerStart.X) + Math.Abs(pos.Y - _pointerStart.Y)) < 4)
        {
            return;
        }
        _dragging = true;

        var dx = (int)Math.Round((pos.X - _pointerStart.X) / view.Scale);
        var dy = (int)Math.Round((pos.Y - _pointerStart.Y) / view.Scale);
        var moving = SKRectI.Create(_dragStartRect.Left + dx, _dragStartRect.Top + dy, _dragStartRect.Width, _dragStartRect.Height);

        var others = new List<SKRectI>();
        var enabledOthers = new List<ArrangedScreen>();
        foreach (var t in view.Tiles)
        {
            if (t.Placement == _dragPlacement) continue;
            others.Add(t.Arranged);
            if (t.Placement.Enabled) enabledOthers.Add(new ArrangedScreen(t.Placement.ScreenId, t.Arranged, t.Placement.BlendAuto));
        }

        var threshold = Math.Max(12, (int)(18 / view.Scale));
        _dragPreview = ScreenLayout.Snap(moving, others, threshold);
        var preview = new ArrangedScreen(_dragPlacement.ScreenId, _dragPreview, _dragPlacement.BlendAuto);
        _snapConnected = _dragPlacement.Enabled &&
                         enabledOthers.Any(o => ScreenLayout.Connected(preview, o));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_vm is null || _dragPlacement is null) return;
        e.Pointer.Capture(null);

        if (_dragging)
        {
            var view = BuildView();
            var tiles = view?.Tiles.Where(t => t.Placement != _dragPlacement).ToList() ?? new List<Tile>();
            // An overlap is a mistake — unless a blending projector is involved on both sides of
            // it: then the overlap is the blend zone, and the drop is exactly what was meant.
            var overlapped = tiles.Where(t => ScreenLayout.OverlapsAny(_dragPreview, new[] { t.Arranged })).ToList();
            var allowed = overlapped.Count == 0 ||
                          _dragPlacement.BlendAuto ||
                          overlapped.All(t => t.Placement.BlendAuto);
            if (allowed)
            {
                _dragPlacement.X = _dragPreview.Left;
                _dragPlacement.Y = _dragPreview.Top;
            }
        }

        _dragPlacement = null;
        _dragging = false;
        _snapConnected = false;
        _vm.ReconcilePlacements();
        InvalidateVisual();
    }
}

file static class SkRectExtensions
{
    public static SKRect InflateCopy(this SKRect r, float amount)
        => new(r.Left - amount, r.Top - amount, r.Right + amount, r.Bottom + amount);
}
