using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Views.Controls;

/// <summary>
/// The mesh editor on the Screens page: the selected output's raster scaled to fit, its lattice
/// as it stands, the picked point ringed. A press picks the nearest point, a drag pulls it (the
/// projector follows when the pointer is released), the arrows nudge it a pixel (ten with
/// Shift), Escape drops the pick. Everything it changes goes through the rig editor, so the show
/// file carries the mesh and the outputs draw it.
/// </summary>
public sealed class WarpMeshControl : Control
{
    private const double Pad = 18;
    private const float Reach = 16;

    private MainViewModel? _vm;
    private System.ComponentModel.PropertyChangedEventHandler? _pageHandler;
    private Action? _publishedHandler;
    private int _drag = -1;
    private SKPoint _dragOffsetStart;
    private Point _pointerStart;
    private bool _dragging;
    private SKPoint _preview;

    public WarpMeshControl()
    {
        ClipToBounds = true;
        Focusable = true;
        MinHeight = 160;
    }

    private ScreensPage? Page => _vm?.Screens;

    private ScreenPlacement? Placement => Page?.SelectedPlacement;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _pageHandler = (_, args) =>
            {
                if (args.PropertyName is nameof(ScreensPage.SelectedPlacement) or nameof(ScreensPage.MeshSelectedIndex) or nameof(ScreensPage.MeshDescription) or nameof(ScreensPage.SelectedMeshDensity)) InvalidateVisual();
            };
            _vm.Screens.PropertyChanged += _pageHandler;
            _publishedHandler = () => InvalidateVisual();
            _vm.Services.SnapshotPublished += _publishedHandler;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_vm is not null)
        {
            if (_pageHandler is not null) _vm.Screens.PropertyChanged -= _pageHandler;
            if (_publishedHandler is not null) _vm.Services.SnapshotPublished -= _publishedHandler;
        }
        base.OnDetachedFromVisualTree(e);
    }

    // ---- the view: the raster fitted into the control ------------------------------------------

    private sealed record View(float Scale, float OffsetX, float OffsetY, float W, float H, SKPoint[] Nodes, int Columns, int Rows);

    private View? BuildView()
    {
        if (_vm is null || Placement is not { } p || Bounds.Width < 40 || Bounds.Height < 40) return null;
        var raster = _vm.Services.RigEditor.RasterOf(p);
        float w = Math.Max(1, raster.Width), h = Math.Max(1, raster.Height);
        var scale = (float)Math.Min((Bounds.Width - 2 * Pad) / w, (Bounds.Height - 2 * Pad) / h);
        var ox = (float)((Bounds.Width - w * scale) / 2);
        var oy = (float)((Bounds.Height - h * scale) / 2);
        var nodes = WarpGrid.NodesOf(p, w, h);
        if (_dragging && _drag >= 0 && _drag < nodes.Length)
        {
            var rest = WarpGrid.Rest(_drag % p.WarpMeshColumns, _drag / p.WarpMeshColumns, p.WarpMeshColumns, p.WarpMeshRows, w, h);
            nodes[_drag] = new SKPoint(rest.X + _preview.X, rest.Y + _preview.Y);
        }
        return new View(scale, ox, oy, w, h, nodes, p.WarpMeshColumns, p.WarpMeshRows);
    }

    private static SKPoint ToView(View v, SKPoint raster) => new(v.OffsetX + raster.X * v.Scale, v.OffsetY + raster.Y * v.Scale);

    private static SKPoint ToRaster(View v, Point view) => new((float)((view.X - v.OffsetX) / v.Scale), (float)((view.Y - v.OffsetY) / v.Scale));

    // ---- input ------------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (Page is not { } page || Placement is not { } p) return;
        var view = BuildView();
        if (view is null) return;
        var pos = e.GetPosition(this);
        var hit = WarpGrid.Nearest(view.Nodes.Select(n => ToView(view, n)).ToList(), new SKPoint((float)pos.X, (float)pos.Y), Reach);
        page.MeshSelectedIndex = hit;
        if (hit < 0) return;
        var offsets = WarpGrid.Parse(p.WarpMesh, p.WarpMeshColumns, p.WarpMeshRows);
        _drag = hit;
        _dragOffsetStart = new SKPoint(offsets[hit * 2], offsets[hit * 2 + 1]);
        _preview = _dragOffsetStart;
        _pointerStart = pos;
        _dragging = false;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag < 0 || !ReferenceEquals(e.Pointer.Captured, this)) return;
        var view = BuildView();
        if (view is null) return;
        var pos = e.GetPosition(this);
        if (!_dragging && Math.Abs(pos.X - _pointerStart.X) + Math.Abs(pos.Y - _pointerStart.Y) < 3) return;
        _dragging = true;
        _preview = new SKPoint(
            _dragOffsetStart.X + (float)((pos.X - _pointerStart.X) / view.Scale),
            _dragOffsetStart.Y + (float)((pos.Y - _pointerStart.Y) / view.Scale));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag < 0) return;
        e.Pointer.Capture(null);
        if (_dragging && _vm is not null && Placement is { } p)
        {
            _vm.Services.RigEditor.PullMeshPoint(p, _drag, (float)Math.Round(_preview.X), (float)Math.Round(_preview.Y));
            Page?.RaiseMesh();
        }
        _dragging = false;
        _drag = -1;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm is null || Page is not { } page || Placement is not { } p) return;
        if (e.Key == Key.Escape)
        {
            page.MeshSelectedIndex = -1;
            e.Handled = true;
            return;
        }
        if (page.MeshSelectedIndex < 0) return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            Key.Down => (0, step),
            _ => (0, 0),
        };
        if (dx == 0 && dy == 0) return;
        var line = WarpGrid.Nudged(p.WarpMesh, p.WarpMeshColumns, p.WarpMeshRows, page.MeshSelectedIndex, dx, dy);
        var offsets = WarpGrid.Parse(line, p.WarpMeshColumns, p.WarpMeshRows);
        _vm.Services.RigEditor.PullMeshPoint(p, page.MeshSelectedIndex, offsets[page.MeshSelectedIndex * 2], offsets[page.MeshSelectedIndex * 2 + 1]);
        page.RaiseMesh();
        e.Handled = true;
        InvalidateVisual();
    }

    // ---- drawing --------------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.Custom(new MeshDrawOp(new Rect(0, 0, Bounds.Width, Bounds.Height), BuildView(), Page?.MeshSelectedIndex ?? -1, Placement is null));
    }

    private sealed class MeshDrawOp : ICustomDrawOperation
    {
        private static readonly SKColor Bg = new(0x0B, 0x0C, 0x10);
        private static readonly SKColor Raster = new(0x1A, 0x1D, 0x25);
        private static readonly SKColor Line = new(0x3E, 0xC1, 0xF3);
        private static readonly SKColor Pick = new(0xFF, 0xB0, 0x2E);

        private readonly View? _view;
        private readonly int _picked;
        private readonly bool _nothing;

        public MeshDrawOp(Rect bounds, View? view, int picked, bool nothing)
        {
            Bounds = bounds;
            _view = view;
            _picked = picked;
            _nothing = nothing;
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
                c.Clear(Bg);
                if (_view is null)
                {
                    using var grey = new SKPaint { IsAntialias = true, Color = SKColors.Gray };
                    using var font = new SKFont { Size = 13 };
                    c.DrawText(_nothing ? "Select a screen" : "No room to draw", 12, 24, SKTextAlign.Left, font, grey);
                    return;
                }
                var v = _view;
                using var raster = new SKPaint { IsAntialias = true, Color = Raster };
                c.DrawRect(SKRect.Create(v.OffsetX, v.OffsetY, v.W * v.Scale, v.H * v.Scale), raster);
                using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = Line };
                foreach (var (a, b) in WarpGrid.Lines(v.Nodes, v.Columns, v.Rows))
                {
                    c.DrawLine(ToView(v, a), ToView(v, b), line);
                }
                using var dot = new SKPaint { IsAntialias = true, Color = Line };
                using var pick = new SKPaint { IsAntialias = true, Color = Pick };
                using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = SKColors.White };
                for (var k = 0; k < v.Nodes.Length; k++)
                {
                    var p = ToView(v, v.Nodes[k]);
                    if (k == _picked)
                    {
                        c.DrawCircle(p, 7, ring);
                        c.DrawCircle(p, 5, pick);
                    }
                    else
                    {
                        c.DrawCircle(p, 3.5f, dot);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Mesh editor render failed.", ex);
            }
            finally
            {
                c.RestoreToCount(save);
            }
        }
    }
}
