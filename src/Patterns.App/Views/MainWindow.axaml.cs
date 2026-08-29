using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.App.ViewModels;

namespace Patterns.App.Views;

public partial class MainWindow : Window
{
    private RenderPipeline? _previewPipeline;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_previewPipeline is not null) return;
        if (DataContext is not MainViewModel vm) return;

        var services = vm.Services;
        _previewPipeline = new RenderPipeline(services.Bus, PipelineViewport.Preview)
        {
            ScreenIdOverride = () => services.PreviewScreenId,
        };
        PreviewCanvas.Pipeline = _previewPipeline;
        services.SnapshotPublished += () => PreviewCanvas.NotifyChanged();

        Closed += (_, _) => _previewPipeline?.Dispose();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Space toggles blackout — operator muscle memory — but never while typing.
        if (e.Key != Key.Space) return;
        var focused = FocusManager?.GetFocusedElement();
        if (focused is TextBox or NumericUpDown or ComboBox or AutoCompleteBox) return;
        if (DataContext is MainViewModel vm)
        {
            vm.State.Blackout = !vm.State.Blackout;
            e.Handled = true;
        }
    }
}
