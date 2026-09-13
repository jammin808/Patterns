using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;

namespace Patterns.App.Views;

/// <summary>
/// The game's own window: the arcade's picture and nothing else, drawn from the loop's newest
/// buffer at the display's rate, windowed or filling a display. The keys are the pads (and 1–3
/// pick a game for the house), F11 fills the display or brings the window back, Esc brings the
/// window back and then closes it. Opened from the Arcade page, or by ARCADE WINDOW on the wire,
/// from a cue or the assistant — on a desk for a rig day's game on a spare display, on an arcade
/// node for the hub's screen with no page around it.
/// </summary>
public sealed class ArcadeWindow : Window
{
    private readonly ArcadeSurface _surface = new();
    private bool _full;

    public ArcadeWindow()
    {
        Title = "Patterns — arcade";
        Background = Brushes.Black;
        Width = 1280;
        Height = 720;
        MinWidth = 320;
        MinHeight = 180;
        Content = _surface;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        Opened += (_, _) => _surface.Focus();
        Closed += (_, _) => Arcade?.ReportWindow("off");
    }

    private ArcadeService? Arcade => (DataContext as IArcadePage)?.Arcade;

    /// <summary>Whether the window fills a display.</summary>
    public bool IsFull => _full;

    /// <summary>Fills a display: the one whose bounds are given, else the one the window is on.</summary>
    public void Fill(PixelRect? bounds)
    {
        if (bounds is { } b)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(b.X, b.Y);
        }
        SystemDecorations = SystemDecorations.None;
        WindowState = WindowState.FullScreen;
        _full = true;
        Arcade?.ReportWindow("full");
        _surface.Focus();
    }

    /// <summary>A window again, with its frame.</summary>
    public void Windowed()
    {
        WindowState = WindowState.Normal;
        SystemDecorations = SystemDecorations.Full;
        _full = false;
        Arcade?.ReportWindow("on");
        _surface.Focus();
    }

    /// <summary>A key as the window would receive it — the tests' hand on the keyboard.</summary>
    public void PressKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
        => OnPreviewKeyDown(this, new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = this });

    public void ReleaseKey(Key key)
        => OnPreviewKeyUp(this, new KeyEventArgs { RoutedEvent = KeyUpEvent, Key = key, Source = this });

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            if (_full) Windowed(); else Fill(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_full) Windowed(); else Close();
            e.Handled = true;
            return;
        }
        if (Arcade is { } arcade && ArcadeKeys.Press(arcade, e.Key, e.KeyModifiers, down: true)) e.Handled = true;
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (Arcade is { } arcade) ArcadeKeys.Press(arcade, e.Key, e.KeyModifiers, down: false);
    }
}

/// <summary>
/// Opens, fills and closes the one <see cref="ArcadeWindow"/> a desk or a node has, for the page's
/// buttons and for ARCADE WINDOW: the service asks it through <see cref="ArcadeService.WindowHost"/>
/// and it answers with the words. A display is named by its number on the Screens page.
/// </summary>
public sealed class ArcadeWindowHost
{
    private readonly IArcadePage _page;
    private readonly Func<int, ScreenInfo?> _display;
    private ArcadeWindow? _window;

    public ArcadeWindowHost(IArcadePage page, Func<int, ScreenInfo?> display)
    {
        _page = page;
        _display = display;
    }

    /// <summary>The window while it is open.</summary>
    public ArcadeWindow? Window => _window;

    public string? Handle(ArcadeWindowMode mode, int display)
    {
        if (!UiThread.CheckAccess()) return UiThread.InvokeAsync(() => Handle(mode, display)).GetTask().GetAwaiter().GetResult();
        try
        {
            if (mode == ArcadeWindowMode.Off)
            {
                if (_window is null) return "The arcade window was not open.";
                _window.Close();
                _window = null;
                return "The arcade window is closed.";
            }
            if (_window is null)
            {
                var window = new ArcadeWindow { DataContext = _page };
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_window, window)) _window = null;
                };
                _window = window;
                window.Show();
            }
            else
            {
                _window.Activate();
            }
            if (mode == ArcadeWindowMode.Full)
            {
                var screen = display > 0 ? _display(display) : null;
                _window.Fill(screen?.Bounds);
                return screen is null
                    ? (display > 0 ? $"No display {display} — the arcade fills the display it is on; Esc or F11 brings the window back." : "The arcade fills its display — Esc or F11 brings the window back.")
                    : $"The arcade fills {screen.Label} — Esc or F11 brings the window back.";
            }
            _window.Windowed();
            return "The arcade window is open — F11 fills the display, Esc closes it.";
        }
        catch (Exception ex)
        {
            Core.Services.Log.Warn("The arcade window could not be opened.", ex);
            return null;
        }
    }
}
