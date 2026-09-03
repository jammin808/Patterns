using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Services;

namespace Patterns.App.Views;

public partial class MainWindow : Window
{
    private RenderPipeline? _previewPipeline;
    private RenderPipeline? _programPipeline;
    private readonly HashSet<Key> _down = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _down.Clear(); // a key-up missed during Alt+Tab must not jam a key
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_previewPipeline is not null) return;
        if (DataContext is not MainViewModel vm) return;

        var services = vm.Services;

        // PREVIEW (bottom): follows the edit target, and the sandbox while it is open.
        _previewPipeline = new RenderPipeline(services.Bus, PipelineViewport.Preview)
        {
            ScreenIdOverride = () => services.PreviewScreenId,
        };
        PreviewCanvas.Pipeline = _previewPipeline;

        // PROGRAM (top): always what the audience sees — never the sandbox.
        _programPipeline = new RenderPipeline(services.Bus,
            new PipelineViewport(Patterns.Core.Model.SinkKind.Output, default, default, null, 0, "PGM"));
        PgmCanvas.Pipeline = _programPipeline;

        services.SnapshotPublished += () =>
        {
            PreviewCanvas.NotifyChanged();
            PgmCanvas.NotifyChanged();
        };

        Closed += (_, _) =>
        {
            _previewPipeline?.Dispose();
            _programPipeline?.Dispose();
        };
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e) => _down.Remove(e.Key);

    /// <summary>
    /// One physical press, one action: Avalonia reports no repeat flag, so a held key arrives
    /// as a stream of KeyDowns. Only the keys this handler acts on are latched.
    /// </summary>
    private bool Latch(Key key)
    {
        if (_down.Contains(key)) return false;
        _down.Add(key);
        return true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var actions = vm.Services.Actions;

        // Plain F1–F12 recall looks (transport lives on Shift+F5–F8).
        if (e.Key is >= Key.F1 and <= Key.F12 && e.KeyModifiers == KeyModifiers.None)
        {
            if (!Latch(e.Key))
            {
                e.Handled = true;
                return;
            }
            if (actions.ApplyLookHotkey(e.Key - Key.F1 + 1, ActionOrigin.Keyboard)) e.Handled = true;
            return;
        }

        var typing = FocusManager?.GetFocusedElement() is TextBox or NumericUpDown or ComboBox or AutoCompleteBox;

        // Presenter clicker: USB presentation remotes send Page Down / Page Up (and often
        // the arrow keys — those only count when the operator isn't in a control).
        if (vm.State.Presenter.Armed && e.KeyModifiers == KeyModifiers.None)
        {
            var forward = e.Key is Key.PageDown || (!typing && e.Key is Key.Right);
            var back = e.Key is Key.PageUp || (!typing && e.Key is Key.Left);
            if (forward || back)
            {
                if (!Latch(e.Key))
                {
                    e.Handled = true;
                    return;
                }
                if (actions.PresenterAdvance(forward ? +1 : -1, ActionOrigin.Clicker)) e.Handled = true;
                return;
            }
        }

        // Space toggles blackout — operator muscle memory — but never while typing.
        if (e.Key != Key.Space || typing) return;
        e.Handled = true;
        if (!Latch(e.Key)) return;
        actions.Execute(ShowActionKind.BlackoutToggle, ActionOrigin.Keyboard);
    }
}
