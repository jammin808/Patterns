using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    private static void Attach(Control control)
    {
        if (GetKind(control) is not { Length: > 0 })
        {
            control.ContextFlyout = null;
            return;
        }
        if (control.ContextFlyout is Flyout { Content: DeskMenuControl }) return;
        var flyout = new Flyout { Placement = PlacementMode.Pointer, ShowMode = FlyoutShowMode.Standard };
        flyout.FlyoutPresenterClasses.Add("deskMenu");
        control.ContextFlyout = flyout;
        // The right-click: the menu is built here, before the flyout opens at the pointer; a control
        // the host has no menu for swallows the request and nothing opens.
        control.ContextRequested += (_, e) =>
        {
            if (!Prepare(control)) e.Handled = true;
        };
    }

    /// <summary>
    /// Builds the menu for a control's kind and subject into its flyout: true when there is one
    /// (the flyout may open), false when the host has none. The right-click calls this; a test or
    /// a key can call it and then show the flyout itself.
    /// </summary>
    public static bool Prepare(Control control)
    {
        if (control.ContextFlyout is not Flyout flyout) return false;
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
