using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.Core.Model;

namespace Patterns.App.Views;

/// <summary>A node's window: the keys are the pads on the arcade and GO on a caller, the tick keeps the words moving, the file pickers are this window's.</summary>
public partial class NodeWindow : Window
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public NodeWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => { if (DataContext is NodeViewModel vm) vm.Window = this; };
        Opened += (_, _) => _tick.Start();
        Closed += (_, _) => _tick.Stop();
        _tick.Tick += (_, _) => (DataContext as NodeViewModel)?.Poll();
    }

    private bool Typing => FocusManager?.GetFocusedElement() is TextBox or NumericUpDown or ComboBox or AutoCompleteBox;

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not NodeViewModel vm || Typing) return;
        if (vm.HasArcade && ArcadeKeys.Press(vm.Arcade, e.Key, e.KeyModifiers, down: true))
        {
            e.Handled = true;
            return;
        }
        if (!vm.HasRun || vm.SelectedTab != NodeViewModel.RunTab) return;
        // The caller's keys, as on the desk: Enter is GO on the standby cue; Esc cancels a confirm, twice is STOP ALL.
        if (e.Key is Key.Return or Key.Enter)
        {
            vm.Run.Go(ActionOrigin.Keyboard);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.Run.EscapePressed();
            e.Handled = true;
        }
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is NodeViewModel { HasArcade: true } vm && !Typing) ArcadeKeys.Press(vm.Arcade, e.Key, e.KeyModifiers, down: false);
    }
}
