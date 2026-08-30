using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Views.Controls;

/// <summary>
/// Drag editor for irregular LED maps: each panel is a rectangle in wall pixels. Drag panels
/// into place (they snap flush to neighbours), click to select, edit exact numbers in the
/// strip next to the editor. Same arrange-canvas foundation as the screen overview.
/// </summary>
public sealed class LedMapEditorControl : Control
{
    private MainViewModel? _vm;
    private Action? _publishedHandler;

    // Drag state (UI thread only).
    private LedTileConfig? _dragTile;
    private SKRectI _dragStartRect;
    private Point _pointerStart;
    private SKRectI _dragPreview;
    private bool _dragging;

    public LedMapEditorControl()
    {
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _publishedHandler = () => InvalidateVisual();
            _vm.Services.SnapshotPublished += _publishedHandler;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_vm is not null)
        {
            if (_publishedHandler is not null) _vm.Services.SnapshotPublished -= _publishedHandler;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedLedTile) or "") InvalidateVisual();
    }

    // ---- view transform -----------------------------------------------------

    private sealed record TileView(LedTileConfig Tile, SKRectI Arranged, Rect View, int Number, bool Selected);

    private sealed record ViewState(double Scale, double OffsetX, double OffsetY, List<TileView> Tiles, SKRectI Canvas);

    private ViewState? BuildView()
    {
        if (_vm is null || Bounds.Width < 40 || Bounds.Height < 40) return null;
        var tiles = _vm.ActivePattern.LedWall.CustomTiles;
        if (tiles.Count == 0) return null;

        var rects = new List<(LedTileConfig T, SKRectI Rect)>();
        foreach (var t in tiles)
        {
            var rect = t == _dragTile && _dragging
                ? _dragPreview
                : SKRectI.Create(t.X, t.Y, t.Width, t.Height);
            rects.Add((t, rect));
        }

        var b = rects[0].Rect;
        foreach (var e in rects.Skip(1))
        {
            b = new SKRectI(Math.Min(b.Left, e.Rect.Left), Math.Min(b.Top, e.Rect.Top),
                Math.Max(b.Right, e.Rect.Right), Math.Max(b.Bottom, e.Rect.Bottom));
        }
        const double pad = 24;
        var scale = Math.Min(1.4, Math.Min(
            (Bounds.Width - 2 * pad) / Math.Max(1, b.Width),
            (Bounds.Height - 2 * pad) / Math.Max(1, b.Height)));
        var offX = (Bounds.Width - b.Width * scale) / 2 - b.Left * scale;
        var offY = (Bounds.Height - b.Height * scale) / 2 - b.Top * scale;

        var list = new List<TileView>();
        var n = 0;
        foreach (var (t, rect) in rects)
        {
            n++;
            var view = new Rect(offX + rect.Left * scale, offY + rect.Top * scale, rect.Width * scale, rect.Height * scale);
            list.Add(new TileView(t, rect, view, n, t == _vm.SelectedLedTile));
        }
        return new ViewState(scale, offX, offY, list, b);
    }

    // ---- rendering ----------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        context.Custom(new MapDrawOp(new Rect(0, 0, Bounds.Width, Bounds.Height), BuildView()));
    }

    private sealed class MapDrawOp : ICustomDrawOperation
    {
        private static readonly SKColor BgColor = new(0x0B, 0x0C, 0x10);
        private static readonly SKColor DotColor = new(0xFF, 0xFF, 0xFF, 0x14);

        private readonly ViewState? _view;

        public MapDrawOp(Rect bounds, ViewState? view)
        {
            Bounds = bounds;
            _view = view;
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
                    DrawCentered(c, "No panels yet — Add panel, or Import grid", (float)Bounds.Width / 2, (float)Bounds.Height / 2, 13, SKColors.Gray);
                    return;
                }

                foreach (var tile in _view.Tiles)
                {
                    DrawTile(c, tile);
                }

                // Canvas extent outline + size label.
                var cv = _view.Canvas;
                var outline = SKRect.Create(
                    (float)(_view.OffsetX + cv.Left * _view.Scale) - 4,
                    (float)(_view.OffsetY + cv.Top * _view.Scale) - 4,
                    (float)(cv.Width * _view.Scale) + 8,
                    (float)(cv.Height * _view.Scale) + 8);
                using var stroke = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f,
                    Color = new SKColor(0x8A, 0x93, 0xA3, 0xA0), PathEffect = DrawUtil.DashLong,
                };
                c.DrawRoundRect(outline, 6, 6, stroke);
                DrawCentered(c, $"Wall canvas {cv.Width}×{cv.Height} px", outline.MidX, outline.Top - 10, 11, new SKColor(0xC8, 0xD0, 0xDC));
            }
            catch (Exception ex)
            {
                Log.Error("LED map editor render failed.", ex);
            }
            finally
            {
                c.RestoreToCount(save);
            }
        }

        private void DrawTile(SKCanvas c, TileView tile)
        {
            var r = SKRect.Create((float)tile.View.X, (float)tile.View.Y, (float)tile.View.Width, (float)tile.View.Height);
            var hue = DrawUtil.Hue(tile.Number - 1, Math.Max(6, _view!.Tiles.Count));

            using (var fill = new SKPaint { IsAntialias = true, Color = hue.WithAlpha(0x30) })
            {
                c.DrawRect(r, fill);
            }
            using (var border = new SKPaint
                   {
                       IsAntialias = true, Style = SKPaintStyle.Stroke,
                       StrokeWidth = tile.Selected ? 2.5f : 1.2f,
                       Color = tile.Selected ? new SKColor(0x3E, 0xC1, 0xF3) : hue.WithAlpha(0xC0),
                   })
            {
                c.DrawRect(r, border);
            }

            var label = string.IsNullOrWhiteSpace(tile.Tile.Label) ? tile.Number.ToString() : tile.Tile.Label;
            if (r.Height > 26 && r.Width > 26)
            {
                DrawCentered(c, label, r.MidX, r.MidY - (r.Height > 44 ? 7 : 0), Math.Min(15, r.Height * 0.3f), SKColors.White);
                if (r.Height > 44)
                {
                    DrawCentered(c, $"{tile.Tile.Width}×{tile.Tile.Height}", r.MidX, r.MidY + 9, Math.Min(10.5f, r.Height * 0.2f), new SKColor(0xC8, 0xD0, 0xDC));
                }
            }
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

        private static void DrawCentered(SKCanvas c, string text, float cx, float cy, float size, SKColor color)
        {
            using var font = new SKFont(Typefaces.Regular, size);
            using var paint = new SKPaint { IsAntialias = true, Color = color };
            var m = font.Metrics;
            c.DrawText(text, cx, cy - (m.Ascent + m.Descent) / 2, SKTextAlign.Center, font, paint);
        }
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

        _vm.SelectedLedTile = hit.Tile;
        _dragTile = hit.Tile;
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
        if (_vm is null || _dragTile is null || !ReferenceEquals(e.Pointer.Captured, this)) return;
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

        var others = view.Tiles.Where(t => t.Tile != _dragTile).Select(t => t.Arranged).ToList();
        var threshold = Math.Max(6, (int)(14 / view.Scale));
        _dragPreview = ScreenLayout.Snap(moving, others, threshold);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_vm is null || _dragTile is null) return;
        e.Pointer.Capture(null);

        if (_dragging)
        {
            var view = BuildView();
            var others = view?.Tiles.Where(t => t.Tile != _dragTile).Select(t => t.Arranged).ToList() ?? new List<SKRectI>();
            if (!ScreenLayout.OverlapsAny(_dragPreview, others))
            {
                // Re-anchor the whole map at the origin: the wall canvas is the bounding box
                // of the panels, so panels keep their relative gaps but never drift (or go
                // negative, which the model clamps away).
                var tiles = _vm.ActivePattern.LedWall.CustomTiles;
                var minX = _dragPreview.Left;
                var minY = _dragPreview.Top;
                foreach (var t in tiles.Where(t => t != _dragTile))
                {
                    minX = Math.Min(minX, t.X);
                    minY = Math.Min(minY, t.Y);
                }
                _dragTile.X = Math.Max(0, _dragPreview.Left - minX);
                _dragTile.Y = Math.Max(0, _dragPreview.Top - minY);
                if (minX != 0 || minY != 0)
                {
                    foreach (var t in tiles.Where(t => t != _dragTile))
                    {
                        t.X = Math.Max(0, t.X - minX);
                        t.Y = Math.Max(0, t.Y - minY);
                    }
                }
            }
        }

        _dragTile = null;
        _dragging = false;
        InvalidateVisual();
    }
}
