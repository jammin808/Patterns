using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.Core.Model;

namespace Patterns.App.Views.Sections;

/// <summary>
/// Media panel. Code-behind adds the playlist's direct manipulation: click a row to select
/// it (timing editor below follows), drag the ≡ handle to re-order — the list re-orders
/// live under the pointer.
/// </summary>
public partial class MediaSection : UserControl
{
    private PlaylistItemConfig? _dragItem;
    private bool _dragging;
    private Point _pressPoint;

    public MediaSection()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnRowPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnRowPointerReleased, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => HookSelection();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void HookSelection()
    {
        if (Vm is { } vm)
        {
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedPlaylistItem)) RestyleRows();
            };
        }
    }

    private static T? FindUp<T>(object? source, string? withClass = null) where T : StyledElement
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (visual is T match && (withClass is null || match.Classes.Contains(withClass))) return match;
            visual = visual.GetVisualParent();
        }
        return null;
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var row = FindUp<Border>(e.Source, "plrow");
        if (row?.DataContext is not PlaylistItemConfig item) return;

        vm.SelectedPlaylistItem = item;

        if (FindUp<Border>(e.Source, "plhandle") is not null)
        {
            _dragItem = item;
            _dragging = false;
            _pressPoint = e.GetPosition(PlaylistList);
            e.Pointer.Capture(PlaylistList);
            e.Handled = true;
        }
    }

    private void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Vm is not { } vm || _dragItem is null || !ReferenceEquals(e.Pointer.Captured, PlaylistList)) return;

        var pos = e.GetPosition(PlaylistList);
        if (!_dragging && Math.Abs(pos.Y - _pressPoint.Y) < 5) return;
        _dragging = true;

        // Re-order live: the item follows whichever row midpoint the pointer has crossed.
        var target = IndexUnder(pos.Y);
        if (target >= 0) vm.MovePlaylistItemTo(_dragItem, target);
        e.Handled = true;
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragItem is null) return;
        _dragItem = null;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    /// <summary>The row index under a Y position; outside the rows snaps to first/last.</summary>
    private int IndexUnder(double y)
    {
        var items = PlaylistList.ItemCount;
        for (var i = 0; i < items; i++)
        {
            if (PlaylistList.ContainerFromIndex(i) is not { } container) continue;
            var top = container.TranslatePoint(default, PlaylistList)?.Y ?? 0;
            if (y >= top && y <= top + container.Bounds.Height) return i;
        }
        return y < 0 ? 0 : items - 1;
    }

    private void RestyleRows()
    {
        if (Vm is not { } vm) return;
        for (var i = 0; i < PlaylistList.ItemCount; i++)
        {
            if (PlaylistList.ContainerFromIndex(i) is not { } container) continue;
            var row = container.FindDescendantOfType<Border>();
            if (row is null || !row.Classes.Contains("plrow")) continue;
            var selected = ReferenceEquals(row.DataContext, vm.SelectedPlaylistItem);
            row.BorderThickness = new Thickness(selected ? 1.5 : 0);
            row.BorderBrush = selected ? new SolidColorBrush(Color.Parse("#3EC1F3")) : null;
        }
    }
}
