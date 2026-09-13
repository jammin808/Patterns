using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Views.Controls;

/// <summary>
/// The arcade's picture on the node's window: the newest whole frame the loop drew, fitted 16:9,
/// asked for at the display's own rate while the control is on screen. A click takes the keyboard.
/// </summary>
public sealed class ArcadeSurface : Control
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    public ArcadeSurface()
    {
        ClipToBounds = true;
        Focusable = true;
        _timer.Tick += (_, _) => InvalidateVisual();
    }

    private ArcadeService? Service => (DataContext as IArcadePage)?.Arcade;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Given a height, take it; given none (a scrolling page), be 16:9 of the width.
        var w = double.IsInfinity(availableSize.Width) ? 1280 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? w * 9 / 16 : availableSize.Height;
        return new Size(w, Math.Max(h, 120));
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        context.Custom(new DrawOp(new Rect(0, 0, Bounds.Width, Bounds.Height), this, Service));
    }

    /// <summary>The frame's place: 16:9, centred, letterboxed in the control.</summary>
    public static SKRect Fit(float width, float height, float aspect)
    {
        var w = width;
        var h = w / aspect;
        if (h > height) { h = height; w = h * aspect; }
        return SKRect.Create((width - w) / 2, (height - h) / 2, w, h);
    }

    private sealed class DrawOp : ICustomDrawOperation
    {
        private readonly ArcadeSurface _owner;
        private readonly ArcadeService? _service;

        public DrawOp(Rect bounds, ArcadeSurface owner, ArcadeService? service)
        {
            Bounds = bounds;
            _owner = owner;
            _service = service;
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
                var w = (float)Bounds.Width;
                var h = (float)Bounds.Height;
                canvas.Clear(new SKColor(0x08, 0x09, 0x0C));
                var aspect = _service is { Width: > 0, Height: > 0 } s ? (float)s.Width / s.Height : 16f / 9f;
                var dest = Fit(w, h, aspect);
                if (_service is null || !_service.DrawLatest(canvas, dest))
                {
                    using var paint = new SKPaint { Color = new SKColor(0x14, 0x17, 0x1E), IsAntialias = true };
                    canvas.DrawRect(dest, paint);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("The arcade surface failed to draw.", ex);
            }
            finally
            {
                canvas.Restore();
            }
        }
    }
}
