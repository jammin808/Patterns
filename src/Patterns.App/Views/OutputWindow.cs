using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Patterns.App.Rendering;
using Patterns.App.Services;

namespace Patterns.App.Views;

/// <summary>
/// A borderless fullscreen pattern surface on one screen. Keys: Esc closes all outputs,
/// Space/B toggles blackout, I identifies screens.
/// </summary>
public sealed class OutputWindow : Window
{
    private readonly AppServices _services;
    private readonly SkiaCanvasControl _canvas;

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
        KeyDown += OnKeyDown;
    }

    public void ApplyOptions()
    {
        var output = _services.State.Output;
        Topmost = output.Topmost;
        Cursor = output.HideCursor ? new Cursor(StandardCursorType.None) : Cursor.Default;
    }

    public void NotifySnapshot() => _canvas.NotifyChanged();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _services.Outputs.CloseAll();
                e.Handled = true;
                break;
            case Key.Space:
            case Key.B:
                _services.State.Blackout = !_services.State.Blackout;
                e.Handled = true;
                break;
            case Key.I:
                _services.Identify();
                e.Handled = true;
                break;
        }
    }
}
