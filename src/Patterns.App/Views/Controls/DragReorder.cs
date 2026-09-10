using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Patterns.App.Views.Controls;

/// <summary>
/// Drag a row to reorder a list, on the desk's own thread with no operating-system drag loop.
///
/// Put <c>c:DragReorder.Grip="True"</c> on the handle inside an item's template and
/// <c>c:DragReorder.Host="True"</c> on the ItemsControl. Pressing the grip captures the pointer;
/// moving it past a few pixels starts the drag; from then on the item follows the pointer by
/// moving in the list as it crosses each neighbour's middle, so what the operator sees is the
/// finished order rather than a hint of it. Nothing is dropped and nothing is undone: releasing
/// just ends the drag.
///
/// It moves rather than rebuilds, which is what keeps the drag alive — an ItemsControl reuses the
/// container of a moved item, so the grip the pointer is holding is still the same control. It is
/// also why this exists at all rather than Avalonia's DragDrop: the OS drag loop is a modal pump
/// with its own latency, and a show desk's list has to move under the finger.
/// </summary>
public static class DragReorder
{
    /// <summary>Pixels the pointer must travel before a press becomes a drag — below this it is a click.</summary>
    public const double Threshold = 4;

    public static readonly AttachedProperty<bool> GripProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Grip", typeof(DragReorder));

    public static readonly AttachedProperty<bool> HostProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, bool>("Host", typeof(DragReorder));

    public static void SetGrip(Control control, bool value) => control.SetValue(GripProperty, value);

    public static bool GetGrip(Control control) => control.GetValue(GripProperty);

    public static void SetHost(ItemsControl control, bool value) => control.SetValue(HostProperty, value);

    public static bool GetHost(ItemsControl control) => control.GetValue(HostProperty);

    /// <summary>Told when a row is dropped in a new place: the host, the index it came from, the index it is at.</summary>
    public static Action<ItemsControl, int, int>? Moved { get; set; }

    static DragReorder()
    {
        GripProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (e.NewValue is true) Hook(c);
        });
    }

    private static void Hook(Control grip)
    {
        grip.Cursor = new Cursor(StandardCursorType.SizeAll);
        grip.PointerPressed += OnPressed;
        grip.PointerMoved += OnMoved;
        grip.PointerReleased += OnReleased;
        grip.PointerCaptureLost += (_, _) => End();
    }

    private static ItemsControl? _host;
    private static Control? _container;
    private static int _from = -1;
    private static Point _start;
    private static bool _dragging;

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control grip || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
        var host = grip.FindAncestorOfType<ItemsControl>();
        if (host is null || !GetHost(host)) return;
        var container = ContainerOf(host, grip);
        if (container is null) return;
        _host = host;
        _container = container;
        _from = host.IndexFromContainer(container);
        _start = e.GetPosition(host);
        _dragging = false;
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_host is null || _container is null || sender is not Control grip) return;
        if (!ReferenceEquals(e.Pointer.Captured, grip)) return;
        var at = e.GetPosition(_host);
        if (!_dragging)
        {
            if (Math.Abs(at.Y - _start.Y) < Threshold && Math.Abs(at.X - _start.X) < Threshold) return;
            _dragging = true;
        }
        var over = IndexAt(_host, at);
        var now = _host.IndexFromContainer(_container);
        if (over < 0 || now < 0 || over == now) return;
        Moved?.Invoke(_host, now, over);
        e.Handled = true;
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging) e.Handled = true;
        e.Pointer.Capture(null);
        End();
    }

    private static void End()
    {
        _host = null;
        _container = null;
        _from = -1;
        _dragging = false;
    }

    /// <summary>The item container the grip lives in — the child of the host the grip descends from.</summary>
    private static Control? ContainerOf(ItemsControl host, Control grip)
    {
        Visual? v = grip;
        while (v is not null && !ReferenceEquals(v, host))
        {
            if (v is Control c && host.IndexFromContainer(c) >= 0) return c;
            v = v.GetVisualParent();
        }
        return null;
    }

    /// <summary>
    /// Which row the pointer is over, by the containers' own boxes — above the first is the first,
    /// below the last is the last, so a drag past the end of the list still lands.
    /// </summary>
    public static int IndexAt(ItemsControl host, Point at)
    {
        var count = host.ItemCount;
        if (count == 0) return -1;
        var best = -1;
        var bestGap = double.MaxValue;
        for (var i = 0; i < count; i++)
        {
            if (host.ContainerFromIndex(i) is not Control c) continue;
            var box = c.Bounds;
            var origin = c.TranslatePoint(new Point(0, 0), host) ?? new Point(box.X, box.Y);
            var top = origin.Y;
            var bottom = top + box.Height;
            if (at.Y >= top && at.Y <= bottom) return i;
            var gap = at.Y < top ? top - at.Y : at.Y - bottom;
            if (gap >= bestGap) continue;
            bestGap = gap;
            best = i;
        }
        return best;
    }
}
