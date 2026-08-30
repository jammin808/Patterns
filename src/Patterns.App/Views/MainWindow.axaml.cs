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
        if (DataContext is not MainViewModel vm) return;

        // Plain F1–F12 recall looks (transport lives on Shift+F5–F8).
        if (e.Key is >= Key.F1 and <= Key.F12 && e.KeyModifiers == KeyModifiers.None)
        {
            if (vm.ApplyLookHotkey(e.Key - Key.F1 + 1))
            {
                e.Handled = true;
            }
            return;
        }

        var typing = FocusManager?.GetFocusedElement() is TextBox or NumericUpDown or ComboBox or AutoCompleteBox;

        // Presenter clicker: USB presentation remotes send Page Down / Page Up (and often
        // the arrow keys — those only count when the operator isn't in a control).
        if (vm.State.Presenter.Armed && e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key is Key.PageDown || (!typing && e.Key is Key.Right))
            {
                if (vm.PresenterAdvance(+1)) e.Handled = true;
                return;
            }
            if (e.Key is Key.PageUp || (!typing && e.Key is Key.Left))
            {
                if (vm.PresenterAdvance(-1)) e.Handled = true;
                return;
            }
        }

        // Space toggles blackout — operator muscle memory — but never while typing.
        if (e.Key != Key.Space || typing) return;
        vm.State.Blackout = !vm.State.Blackout;
        e.Handled = true;
    }
}
