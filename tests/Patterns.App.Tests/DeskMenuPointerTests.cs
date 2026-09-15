using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 62: the right-click itself. Round 60's test built the menu by calling Prepare and showed
/// the flyout by hand, so the one path an operator uses — the pointer's right button through
/// Avalonia's own context request — was never driven. On the desk it opened nothing: with a
/// ContextFlyout set, the platform's handler is subscribed first, shows the flyout with the
/// content it has (none yet) and marks the request handled, so the desk's builder never ran.
/// These tests press the right button through the headless window's input pipeline, and the
/// menu key, and read what opened.
/// </summary>
public class DeskMenuPointerTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
    };

    private static void Rig(TestApp.Booted b)
    {
        var fakes = ThreeScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.RebuildSwitcherTiles(fakes);
        Dispatcher.UIThread.RunJobs();
    }

    private static (Window Window, Border Border) Stage(MainViewModel vm, object subject, string kind)
    {
        var border = new Border { Width = 240, Height = 80, Background = Brushes.DimGray, DataContext = subject, Focusable = true };
        Menus.SetKind(border, kind);
        var window = new Window { Width = 600, Height = 400, DataContext = vm, Content = new StackPanel { Children = { border } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, border);
    }

    private static void RightClick(Window window, Control on)
    {
        var at = on.TranslatePoint(new Point(20, 20), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ARightClickThroughTheInputPipelineOpensTheBuiltMenuAndAChoiceClosesIt()
    {
        var b = TestApp.Boot();
        Window? window = null;
        try
        {
            var vm = b.Vm;
            Rig(b);
            var tile = vm.SwitcherTiles.First(t => t.TargetId == "b");
            Border border;
            (window, border) = Stage(vm, tile, "tile");

            RightClick(window, border);

            var flyout = Menus.FlyoutOf(border);
            Assert.NotNull(flyout);
            Assert.True(flyout!.IsOpen, "the right-click opened nothing");
            var view = Assert.IsType<DeskMenuControl>(flyout.Content);
            var menu = Assert.IsType<DeskMenuVm>(view.DataContext);
            Assert.Equal("screen", menu.Menu.Kind);

            // A choice runs and the flyout closes; the next right-click builds the menu afresh.
            menu.Find("go.looks")!.ChooseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(flyout.IsOpen);
            Assert.Equal(Shell.IndexOf("Looks"), vm.SelectedPageIndex);

            RightClick(window, border);
            Assert.True(flyout.IsOpen);
            Assert.NotSame(menu, ((DeskMenuControl)flyout.Content!).DataContext);
        }
        finally
        {
            window?.Close();
            b.Window.Close();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheMenuKeyOpensItAtTheControlAndAThingWithNoMenuOpensNothing()
    {
        var b = TestApp.Boot();
        Window? window = null;
        try
        {
            var vm = b.Vm;
            Rig(b);
            var tile = vm.SwitcherTiles.First(t => t.TargetId == "b");
            Border border;
            (window, border) = Stage(vm, tile, "tile");

            // The keyboard's context key, on the focused thing: the same menu, at the control.
            window.Activate();
            Assert.True(border.Focus(), "the border took focus");
            Assert.True(border.IsFocused);
            // The context key acts on its release, as Avalonia's own ContextMenu does.
            window.KeyPress(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
            window.KeyRelease(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
            Dispatcher.UIThread.RunJobs();
            var flyout = Menus.FlyoutOf(border)!;
            Assert.True(flyout.IsOpen, "the menu key opened nothing");
            Assert.IsType<DeskMenuControl>(flyout.Content);
            flyout.Hide();
            Dispatcher.UIThread.RunJobs();

            // A thing the host has no menu for swallows the right-click and opens nothing.
            var other = new Border { Width = 240, Height = 80, Background = Brushes.DimGray, DataContext = "nothing the desk has a menu for" };
            Menus.SetKind(other, "tile");
            ((StackPanel)window.Content!).Children.Add(other);
            Dispatcher.UIThread.RunJobs();
            RightClick(window, other);
            Assert.False(Menus.FlyoutOf(other)!.IsOpen);
            Assert.Null(Menus.FlyoutOf(other)!.Content);
        }
        finally
        {
            window?.Close();
            b.Window.Close();
            b.Dispose();
        }
    }
}
