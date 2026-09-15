using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Patterns.App.ViewModels;

namespace Patterns.App.Views.Controls;

/// <summary>
/// The right-click menus, attached in XAML: <c>ctl:Menus.Kind="tile"</c> on a tile, a row, a
/// chip, a pane — and <c>ctl:Menus.Subject</c> when the thing is a word rather than the row's
/// object ("clock", "layer2"). A context flyout of the desk's own opens at the pointer, built
/// on opening from the host up the tree (the window's view model), and closes when a choice
/// runs. Nothing is built until the first right-click, and a control the host has no menu for
/// opens nothing.
/// </summary>
public static class Menus
{
    public static readonly AttachedProperty<string?> KindProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Kind", typeof(Menus));

    public static readonly AttachedProperty<object?> SubjectProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("Subject", typeof(Menus));

    static Menus()
    {
        KindProperty.Changed.AddClassHandler<Control>((control, _) => Attach(control));
    }

    public static string? GetKind(Control c) => c.GetValue(KindProperty);
    public static void SetKind(Control c, string? value) => c.SetValue(KindProperty, value);
    public static object? GetSubject(Control c) => c.GetValue(SubjectProperty);
    public static void SetSubject(Control c, object? value) => c.SetValue(SubjectProperty, value);

    /// <summary>The control's flyout — the desk's, never <see cref="Control.ContextFlyout"/> (see <see cref="Attach"/>).</summary>
    private static readonly AttachedProperty<Flyout?> FlyoutProperty =
        AvaloniaProperty.RegisterAttached<Control, Flyout?>("Flyout", typeof(Menus));

    private static void Attach(Control control)
    {
        if (GetKind(control) is not { Length: > 0 })
        {
            control.SetValue(FlyoutProperty, null);
            return;
        }
        if (control.GetValue(FlyoutProperty) is not null) return;
        var flyout = new Flyout { Placement = PlacementMode.Pointer, ShowMode = FlyoutShowMode.Standard };
        flyout.FlyoutPresenterClasses.Add("deskMenu");
        control.SetValue(FlyoutProperty, flyout);
        // The desk opens the flyout itself on the context request — the right button's release, or
        // the menu key — after building the menu, and never through Control.ContextFlyout: the
        // platform's handler for that property is subscribed before any other, shows the flyout with
        // whatever content it has and marks the request handled, so a menu built on the request never
        // ran and the desk saw an empty flyout (round 62). A thing with no menu swallows the request.
        control.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
    }

    private static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || sender is not Control control) return;
        e.Handled = true;
        if (!Prepare(control)) return;
        var flyout = FlyoutOf(control)!;
        var atPointer = e.TryGetPosition(null, out _);
        flyout.Placement = atPointer ? PlacementMode.Pointer : PlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(control, atPointer);
    }

    /// <summary>
    /// Builds the menu for a control's kind and subject into its flyout: true when there is one
    /// (the flyout may open), false when the host has none. The context request calls this; a test
    /// or a key can call it and then show the flyout itself.
    /// </summary>
    public static bool Prepare(Control control)
    {
        if (FlyoutOf(control) is not { } flyout) return false;
        var kind = GetKind(control);
        var host = HostOf(control);
        var vm = kind is null || host is null ? null : host.MenuFor(kind, GetSubject(control) ?? control.DataContext);
        if (vm is null) return false;
        if (flyout.Content is not DeskMenuControl view)
        {
            view = new DeskMenuControl();
            flyout.Content = view;
        }
        view.DataContext = vm;
        vm.Chosen += () => flyout.Hide();
        return true;
    }

    /// <summary>The control's menu flyout, or null when it has no kind.</summary>
    public static Flyout? FlyoutOf(Control control) => control.GetValue(FlyoutProperty);

    /// <summary>The nearest view model up the tree that builds menus — the window's, whatever the row or the pop-out binds.</summary>
    public static IDeskMenuHost? HostOf(Control control)
    {
        for (ILogical? node = control; node is not null; node = node.LogicalParent)
        {
            if (node is StyledElement { DataContext: IDeskMenuHost host }) return host;
        }
        return TopLevel.GetTopLevel(control)?.DataContext as IDeskMenuHost;
    }
}
