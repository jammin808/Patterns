using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;

namespace Patterns.App.Views;

/// <summary>A node's window: the keys are the pads, the tick keeps the words moving.</summary>
public partial class NodeWindow : Window
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public NodeWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        _tick.Tick += (_, _) => (DataContext as NodeViewModel)?.Poll();
        Opened += (_, _) => _tick.Start();
        Closed += (_, _) => _tick.Stop();
    }

    private bool Typing => FocusManager?.GetFocusedElement() is TextBox or NumericUpDown or ComboBox or AutoCompleteBox;

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is NodeViewModel vm && !Typing && ArcadeKeys.Press(vm.Arcade, e.Key, e.KeyModifiers, down: true)) e.Handled = true;
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is NodeViewModel vm && !Typing) ArcadeKeys.Press(vm.Arcade, e.Key, e.KeyModifiers, down: false);
    }
}
