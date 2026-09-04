using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Services;

namespace Patterns.App.Views;

/// <summary>
/// The Run surface as a second window for a caller's own monitor. Its GO key works only while
/// this window has focus (a window only sees its own keys), with the same latch as the desk.
/// </summary>
public partial class RunWindow : Window
{
    private readonly HashSet<Key> _down = new();

    public RunWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => _down.Remove(e.Key), RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _down.Clear();
        Opened += (_, _) => WarnIfOnAnOutput();
    }

    private bool Latch(Key key)
    {
        if (_down.Contains(key)) return false;
        _down.Add(key);
        return true;
    }

    /// <summary>A pop-out on the display an output covers would sit under the audience surface.</summary>
    private void WarnIfOnAnOutput()
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            var here = Screens.ScreenFromWindow(this);
            if (here is null) return;
            foreach (var window in vm.Services.Outputs.Windows)
            {
                var info = vm.Services.Screens.All.FirstOrDefault(s => s.Id == window.TargetScreenId);
                if (info is not null && info.Bounds.Intersects(here.Bounds))
                {
                    vm.StatusMessage = "The Run window is on a display that carries an output — move it to the caller's monitor.";
                    return;
                }
            }
        }
        catch
        {
            // No screen information (headless) — nothing to warn about.
        }
    }

    /// <summary>The caller's keys, exactly as on the main window's Run surface.</summary>
    public void PressKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
        => OnPreviewKeyDown(this, new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = this });

    public void ReleaseKey(Key key) => _down.Remove(key);

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var typing = FocusManager?.GetFocusedElement() is TextBox or NumericUpDown or ComboBox or AutoCompleteBox;
        if (typing) return;

        if (e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key is Key.Return or Key.Enter)
            {
                e.Handled = true;
                if (Latch(e.Key)) vm.Run.Go(ActionOrigin.Keyboard);
                return;
            }
            if (e.Key is Key.Up or Key.Down)
            {
                e.Handled = true;
                if (Latch(e.Key)) vm.Services.CueStack.StandbyMove(e.Key == Key.Up ? -1 : +1);
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (Latch(e.Key)) vm.Run.EscapePressed();
                return;
            }
            if (e.Key == Key.Space)
            {
                e.Handled = true;
                if (Latch(e.Key)) vm.Services.Actions.Execute(Patterns.Core.Services.ShowActionKind.BlackoutToggle, ActionOrigin.Keyboard);
                return;
            }
            if (e.Key == Key.D)
            {
                e.Handled = true;
                if (Latch(e.Key)) vm.Services.Actions.Execute(Patterns.Core.Services.ShowActionKind.DuckToggle, ActionOrigin.Keyboard);
            }
        }
    }
}
