using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.Core.Services;

namespace Patterns.App.Views;

/// <summary>
/// A borderless fullscreen pattern surface on one screen. Keys: Esc twice within a second
/// closes all outputs (a single Esc must never blank the room), Space/B toggles blackout,
/// I identifies screens, F1–F12 recall looks, Page keys drive the presenter when armed.
/// Every key acts once per physical press: the OS auto-repeat is ignored.
/// </summary>
public sealed class OutputWindow : Window
{
    /// <summary>How long a first Esc stays armed for the second one.</summary>
    public static readonly TimeSpan EscConfirmWindow = TimeSpan.FromSeconds(1);

    private readonly AppServices _services;
    private readonly SkiaCanvasControl _canvas;
    private readonly HashSet<Key> _down = new();
    private DateTime _escArmedUtc = DateTime.MinValue;

    public RenderPipeline Pipeline { get; }
    public string TargetScreenId { get; }

    public OutputWindow(AppServices services, ScreenInfo screen, PipelineViewport viewport)
    {
        _services = services;
        TargetScreenId = screen.Id;
        Pipeline = new RenderPipeline(services.Bus, viewport);

        Title = $"Patterns output — {screen.Label}";
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Black;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(screen.Bounds.X, screen.Bounds.Y);
        Focusable = true;

        _canvas = new SkiaCanvasControl { Pipeline = Pipeline };
        Content = _canvas;

        Opened += (_, _) =>
        {
            WindowState = WindowState.FullScreen;
            ApplyOptions();
            Focus();
        };
        Closed += (_, _) => Pipeline.Dispose();
        Deactivated += (_, _) => _down.Clear(); // a key-up missed during Alt+Tab must not jam a key
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
    }

    public void ApplyOptions()
    {
        var output = _services.State.Output;
        Topmost = output.Topmost;
        Cursor = output.HideCursor ? new Cursor(StandardCursorType.None) : Cursor.Default;
    }

    public void NotifySnapshot() => _canvas.NotifyChanged();

    /// <summary>A key press as the window would receive it (tests drive the guards through here).</summary>
    public void PressKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
        => OnKeyDown(this, new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = this });

    public void ReleaseKey(Key key)
        => OnKeyUp(this, new KeyEventArgs { RoutedEvent = KeyUpEvent, Key = key, Source = this });

    private void OnKeyUp(object? sender, KeyEventArgs e) => _down.Remove(e.Key);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Avalonia reports no repeat flag: a held key arrives as a stream of KeyDowns.
        if (!_down.Add(e.Key))
        {
            e.Handled = true;
            return;
        }

        // Look hotkeys work here too — the operator may be standing at an output screen.
        if (e.Key is >= Key.F1 and <= Key.F12 && e.KeyModifiers == KeyModifiers.None)
        {
            if (_services.Actions.ApplyLookHotkey(e.Key - Key.F1 + 1, ActionOrigin.Keyboard)) e.Handled = true;
            return;
        }

        // Presenter clicker on the output too — presenters click at the screen they see.
        if (_services.Cues.For(Patterns.Core.Services.CueStacks.Clicker(_services.State)).Armed && e.KeyModifiers == KeyModifiers.None &&
            e.Key is Key.PageDown or Key.PageUp or Key.Right or Key.Left)
        {
            var forward = e.Key is Key.PageDown or Key.Right;
            if (_services.Actions.PresenterAdvance(forward ? +1 : -1, ActionOrigin.Clicker)) e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
            {
                // One Esc on the audience surface must never put the desktop in front of the
                // room; the confirmation shows on the desk, never on the output.
                var now = DateTime.UtcNow;
                if (now - _escArmedUtc <= EscConfirmWindow)
                {
                    _escArmedUtc = DateTime.MinValue;
                    _services.Actions.Execute(ShowActionKind.OutputsOff, ActionOrigin.Keyboard);
                }
                else
                {
                    _escArmedUtc = now;
                    _services.Notify("Press Esc again within a second to close the outputs.");
                }
                e.Handled = true;
                break;
            }
            case Key.Space:
            case Key.B:
                _services.Actions.Execute(ShowActionKind.BlackoutToggle, ActionOrigin.Keyboard);
                e.Handled = true;
                break;
            case Key.I:
                _services.Actions.Execute(ShowActionKind.Identify, ActionOrigin.Keyboard);
                e.Handled = true;
                break;
        }
    }
}
