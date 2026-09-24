using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Services;

namespace Patterns.App.Views.Controls;

/// <summary>
/// The God's Eye's picture (round 66): the graph the desk's Eye service holds, drawn straight
/// from its layout through a camera — pan by drag, zoom about the pointer, click selects,
/// double-click focuses, right-click opens the thing's own desk menu, Esc is the whole picture.
/// It redraws only when the picture changed, the camera moved or the pointer crossed a thing;
/// its timer runs only while the camera's spring or an emphasis is still moving. Text layouts
/// are cached by words and size; hit-testing is rectangle maths; a thing off the view is not drawn.
/// </summary>
public sealed class EyeCanvas : Control
{
    private const double AttackSeconds = 0.3;
    private const double ReleaseSeconds = 0.6;

    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#0E0F13"));
    private static readonly IBrush BandFill = new SolidColorBrush(Color.Parse("#12151B"));
    private static readonly IBrush BandInk = new SolidColorBrush(Color.Parse("#4A505E"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#9AA3B2"));
    private static readonly IPen Selected = new Pen(new SolidColorBrush(Color.Parse("#FFFFFF")), 2);
    private static readonly IPen Hovered = new Pen(new SolidColorBrush(Color.Parse("#C0CBDB")), 1.5);

    private readonly EyeCamera _camera = new();
    private readonly DispatcherTimer _anim;
    private readonly Dictionary<string, double> _emphasis = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Text, int Size, bool Bold), FormattedText> _texts = new();
    private readonly Dictionary<(string Hex, int Alpha), IBrush> _brushes = new();
    private readonly Dictionary<(string Hex, int Alpha, bool Dashed), IPen> _pens = new();

    private MainViewModel? _vm;
    private EyeService? _eye;
    private long _revSeen = -1;
    private string? _focusSeen;
    private EyeView? _kept;
    private bool _fitted;
    private string? _hover;
    private bool _pressed;
    private bool _dragging;
    private Point _pressAt;
    private Point _lastAt;
    private long _lastTick;

    public EyeCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Menus.SetKind(this, "eye");
        Menus.SetSubjectAt(this, p => NodeAt(p));
        _anim = DeskTimers.Make(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render);
        _anim.Tick += (_, _) => Animate();
    }

    /// <summary>The camera, for a test: where the picture is and where it is going.</summary>
    public EyeCamera Camera => _camera;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hook();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Hook();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _anim.Stop();
        if (_eye is not null) _eye.Changed -= OnEyeChanged;
        _eye = null;
        _vm = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void Hook()
    {
        var vm = DataContext as MainViewModel;
        if (ReferenceEquals(vm, _vm) && _eye is not null) return;
        if (_eye is not null) _eye.Changed -= OnEyeChanged;
        _vm = vm;
        _eye = vm?.Services.Eye;
        if (_eye is not null)
        {
            _eye.Changed += OnEyeChanged;
            _fitted = false;
            InvalidateVisual();
        }
    }

    /// <summary>The picture or the view moved: a new focus fits the camera, a reset brings the kept view back, a rebuild keeps the focus in frame.</summary>
    private void OnEyeChanged()
    {
        if (_eye is null) return;
        var w = Bounds.Width;
        var h = Bounds.Height;
        var focus = _eye.FocusId;
        if (focus != _focusSeen)
        {
            if (focus is not null)
            {
                _kept ??= _camera.Target;
                if (w > 0 && h > 0) _camera.FocusOn(_eye.Placement.Around(focus, _eye.Shown), w, h);
            }
            else
            {
                if (_kept is { } kept) _camera.Restore(kept);
                else if (w > 0 && h > 0) _camera.FitTo(_eye.Placement.Bounds, w, h);
                _kept = null;
            }
            _focusSeen = focus;
        }
        else if (focus is not null && _eye.Rev != _revSeen && w > 0 && h > 0)
        {
            _camera.FocusOn(_eye.Placement.Around(focus, _eye.Shown), w, h);
        }
        _revSeen = _eye.Rev;
        StartAnimating();
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_eye is null || e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        if (_eye.FocusId is null) _camera.FitTo(_eye.Placement.Bounds, e.NewSize.Width, e.NewSize.Height, animate: false);
        _fitted = true;
        InvalidateVisual();
    }

    private void EnsureFitted(double w, double h)
    {
        if (_fitted || _eye is null || w <= 0 || h <= 0 || _eye.Placement.Bounds.IsEmpty) return;
        _camera.FitTo(_eye.Placement.Bounds, w, h, animate: false);
        _fitted = true;
    }

    // ---- drawing ------------------------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(Paper, new Rect(0, 0, w, h));
        if (_eye is null) return;
        var g = _eye.Shown;
        var p = _eye.Placement;
        if (g.Nodes.Count == 0)
        {
            var empty = Text(g.Headline, 14, false, Dim);
            ctx.DrawText(empty, new Point(Math.Max(12, (w - empty.Width) / 2), Math.Max(12, (h - empty.Height) / 2)));
            return;
        }
        EnsureFitted(w, h);
        var lens = _eye.Lens;
        var hops = _eye.FocusId is { } f ? g.Hops(f) : null;
        var scale = _camera.Scale;
        var view = new Rect(0, 0, w, h);

        foreach (var band in p.Bands)
        {
            var r = ToRect(_camera.WorldToView(band.Rect));
            if (!r.Intersects(view)) continue;
            ctx.FillRectangle(BandFill, r, 14);
            if (scale >= 0.25)
            {
                var label = Text(band.Label, Math.Clamp(11 * scale, 8, 16), true, BandInk);
                ctx.DrawText(label, new Point(r.X + 12 * scale, r.Y + 6 * scale));
            }
        }

        foreach (var e in g.Edges)
        {
            var a = g.Find(e.From);
            var b = g.Find(e.To);
            if (a is null || b is null || !g.Visible(a, lens) || !g.Visible(b, lens)) continue;
            var ra = p.Of(a.Id);
            var rb = p.Of(b.Id);
            if (ra.IsEmpty || rb.IsEmpty) continue;
            var alpha = Math.Min(Emphasis(a.Id, hops), Emphasis(b.Id, hops));
            var (x1, y1, x2, y2) = Ends(ra, rb);
            var from = _camera.WorldToView(x1, y1);
            var to = _camera.WorldToView(x2, y2);
            var pen = Pen(EyeService.Hue(e.Light), (int)Math.Round(alpha * 200), e.Light == CheckLight.Grey, Math.Max(1, 1.5 * scale));
            ctx.DrawLine(pen, new Point(from.X, from.Y), new Point(to.X, to.Y));
            var dot = Brush(EyeService.Hue(e.Light), (int)Math.Round(alpha * 230));
            var radius = Math.Max(1.5, 3 * scale);
            ctx.DrawEllipse(dot, null, new Point(to.X, to.Y), radius, radius);
        }

        var selected = _vm?.EyeSelectedId ?? "";
        foreach (var n in g.Nodes)
        {
            if (!g.Visible(n, lens)) continue;
            var r = ToRect(_camera.WorldToView(p.Of(n.Id)));
            if (!r.Intersects(view)) continue;
            var alpha = Emphasis(n.Id, hops);
            var hue = EyeService.Hue(n.Light);
            var radius = 8 * scale;
            ctx.DrawRectangle(Brush("#181B22", (int)Math.Round(alpha * 255)), Pen(hue, (int)Math.Round(alpha * (n.IsProblem ? 220 : 120)), false, Math.Max(1, 1.2 * scale)), new RoundedRect(r, radius));
            if (n.Id == selected) ctx.DrawRectangle(null, Selected, new RoundedRect(r.Inflate(2), radius + 2));
            else if (n.Id == _hover) ctx.DrawRectangle(null, Hovered, new RoundedRect(r.Inflate(1), radius + 1));
            var dotR = Math.Max(2, 5 * scale);
            ctx.DrawEllipse(Brush(hue, (int)Math.Round(alpha * 255)), null, new Point(r.X + 14 * scale, r.Y + r.Height / 2), dotR, dotR);
            if (scale < 0.35) continue;
            var textX = r.X + 26 * scale;
            var maxW = Math.Max(10, r.Width - 34 * scale);
            var labelSize = Math.Clamp(13 * scale, 7, 26);
            var label = Text(n.Label, labelSize, true, Brush("#E6EAF2", (int)Math.Round(alpha * 255)), maxW);
            var subShown = scale >= 0.6 && n.Sub.Length > 0;
            var labelY = subShown ? r.Y + 7 * scale : r.Y + (r.Height - label.Height) / 2;
            ctx.DrawText(label, new Point(textX, labelY));
            if (subShown)
            {
                var sub = Text(n.Sub, Math.Clamp(10.5 * scale, 7, 20), false, Brush("#9AA3B2", (int)Math.Round(alpha * 255)), maxW);
                ctx.DrawText(sub, new Point(textX, r.Y + r.Height - sub.Height - 6 * scale));
            }
        }
    }

    /// <summary>The line between two things leaves the right edge and lands on the left edge when the target is to the right; otherwise centre to centre.</summary>
    private static (double X1, double Y1, double X2, double Y2) Ends(EyeRect a, EyeRect b)
    {
        if (a.Right <= b.X) return (a.Right, a.CenterY, b.X, b.CenterY);
        if (b.Right <= a.X) return (a.X, a.CenterY, b.Right, b.CenterY);
        return (a.CenterX, a.Bottom <= b.Y ? a.Bottom : a.Y, b.CenterX, a.Bottom <= b.Y ? b.Y : b.Bottom);
    }

    private static Rect ToRect(EyeRect r) => new(r.X, r.Y, Math.Max(0, r.W), Math.Max(0, r.H));

    private double Emphasis(string id, IReadOnlyDictionary<string, int>? hops)
    {
        var target = EyeGraph.EmphasisOf(id, hops);
        return _emphasis.TryGetValue(id, out var current) ? current : target;
    }

    private FormattedText Text(string words, double size, bool bold, IBrush brush, double maxWidth = double.PositiveInfinity)
    {
        var key = (words, (int)Math.Round(size * 4), bold);
        if (!_texts.TryGetValue(key, out var text))
        {
            var face = new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal);
            text = new FormattedText(words, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush)
            {
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            if (_texts.Count > 2000) _texts.Clear();
            _texts[key] = text;
        }
        text.MaxTextWidth = double.IsFinite(maxWidth) ? Math.Max(1, maxWidth) : 100000;
        text.SetForegroundBrush(brush);
        return text;
    }

    private IBrush Brush(string hex, int alpha)
    {
        alpha = Math.Clamp(alpha, 0, 255);
        if (_brushes.TryGetValue((hex, alpha), out var b)) return b;
        var c = Color.Parse(hex);
        b = new SolidColorBrush(Color.FromArgb((byte)alpha, c.R, c.G, c.B));
        _brushes[(hex, alpha)] = b;
        return b;
    }

    private IPen Pen(string hex, int alpha, bool dashed, double thickness)
    {
        alpha = Math.Clamp(alpha, 0, 255);
        var key = (hex, alpha * 100 + (int)Math.Round(thickness * 10), dashed);
        if (_pens.TryGetValue(key, out var pen)) return pen;
        pen = new Pen(Brush(hex, alpha), thickness, dashed ? DashStyle.Dash : null);
        if (_pens.Count > 400) _pens.Clear();
        _pens[key] = pen;
        return pen;
    }

    // ---- motion --------------------------------------------------------------------------------

    private void StartAnimating()
    {
        if (_anim.IsEnabled) return;
        _lastTick = Environment.TickCount64;
        _anim.Start();
    }

    private void Animate()
    {
        var now = Environment.TickCount64;
        var dt = Math.Clamp((now - _lastTick) / 1000.0, 0.001, 0.25);
        _lastTick = now;
        var moving = _camera.Step(dt);
        if (_eye is not null)
        {
            var hops = _eye.FocusId is { } f ? _eye.Shown.Hops(f) : null;
            foreach (var n in _eye.Shown.Nodes)
            {
                var target = EyeGraph.EmphasisOf(n.Id, hops);
                var current = _emphasis.TryGetValue(n.Id, out var c) ? c : target;
                if (Math.Abs(target - current) < 0.004)
                {
                    _emphasis[n.Id] = target;
                    continue;
                }
                var rate = target > current ? dt / AttackSeconds : dt / ReleaseSeconds;
                _emphasis[n.Id] = current + (target - current) * Math.Min(1, rate * 3);
                moving = true;
            }
        }
        InvalidateVisual();
        if (!moving) _anim.Stop();
    }

    // ---- the pointer and the keys ------------------------------------------------------------

    /// <summary>The thing under a view point, or null.</summary>
    public EyeNode? NodeAt(Point view)
    {
        if (_eye is null) return null;
        var (wx, wy) = _camera.ViewToWorld(view.X, view.Y);
        var id = _eye.Placement.At(wx, wy);
        if (id is null) return null;
        var n = _eye.Shown.Find(id);
        return n is not null && _eye.Shown.Visible(n, _eye.Lens) ? n : null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var pos = e.GetPosition(this);
        _camera.ZoomAt(pos.X, pos.Y, e.Delta.Y > 0 ? 1.15 : 1 / 1.15);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        _pressAt = _lastAt = point.Position;
        _pressed = true;
        _dragging = false;
        if (e.ClickCount == 2)
        {
            _pressed = false;
            var node = NodeAt(point.Position);
            if (node is not null) _vm?.EyeFocusCommand.Execute(node.Id);
            else _vm?.EyeResetCommand.Execute(null);
            e.Handled = true;
            return;
        }
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_pressed)
        {
            if (!_dragging && (Math.Abs(pos.X - _pressAt.X) > 4 || Math.Abs(pos.Y - _pressAt.Y) > 4)) _dragging = true;
            if (_dragging)
            {
                _camera.Pan(pos.X - _lastAt.X, pos.Y - _lastAt.Y);
                _lastAt = pos;
                InvalidateVisual();
            }
            return;
        }
        var hover = NodeAt(pos)?.Id;
        if (hover == _hover) return;
        _hover = hover;
        Cursor = hover is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        if (_dragging)
        {
            _dragging = false;
            return;
        }
        var node = NodeAt(e.GetPosition(this));
        if (_vm is not null) _vm.EyeSelectedId = node?.Id ?? "";
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is null) return;
        _hover = null;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm is null) return;
        switch (e.Key)
        {
            case Key.Escape:
                _vm.EyeResetCommand.Execute(null);
                break;
            case Key.Right:
            case Key.Down:
            case Key.N:
                _vm.EyeNextCommand.Execute(null);
                break;
            case Key.Left:
            case Key.Up:
            case Key.P:
                _vm.EyePrevCommand.Execute(null);
                break;
            case Key.F:
            case Key.Enter:
                _vm.EyeFocusSelectedCommand.Execute(null);
                break;
            case Key.Home:
                if (_eye is not null && Bounds.Width > 0) _camera.FitTo(_eye.Placement.Bounds, Bounds.Width, Bounds.Height);
                StartAnimating();
                break;
            case Key.OemPlus:
            case Key.Add:
                _camera.ZoomAt(Bounds.Width / 2, Bounds.Height / 2, 1.25);
                InvalidateVisual();
                break;
            case Key.OemMinus:
            case Key.Subtract:
                _camera.ZoomAt(Bounds.Width / 2, Bounds.Height / 2, 1 / 1.25);
                InvalidateVisual();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
