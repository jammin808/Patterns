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

        // PREVIEW (bottom): follows the selected target (own pattern or program) and the
        // sandbox while it is open — a true miniature of that target, letterboxed to fit.
        var preview = new RenderPipeline(services.Bus, PreviewViewport(vm))
        {
            ScreenIdOverride = () => services.PreviewScreenId,
        };
        _previewPipeline = preview;
        PreviewCanvas.Pipeline = preview;

        // PROGRAM (top): what the audience sees on the selected target — never the sandbox.
        // A monitor sink, so it never wears an output's identify badge or counts as an output.
        var program = new RenderPipeline(services.Bus, ProgramViewport(vm));
        _programPipeline = program;
        PgmCanvas.Pipeline = program;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(MainViewModel.SelectedTargetSize) or nameof(MainViewModel.SelectedTargetId))) return;
            preview.Viewport = PreviewViewport(vm);
            program.Viewport = ProgramViewport(vm);
            PreviewCanvas.NotifyChanged();
            PgmCanvas.NotifyChanged();
        };

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

    private static PipelineViewport ProgramViewport(MainViewModel vm)
        => PipelineViewport.Monitor(vm.SelectedTargetId, vm.SelectedTargetSize, "PGM", previewSide: false);

    private static PipelineViewport PreviewViewport(MainViewModel vm)
        => PipelineViewport.Preview with { ReferenceSize = vm.SelectedTargetSize, FitReference = true };

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

        // The transport KeyBindings (Shift+F5–F8) fire on every repeat of a held key; the
        // first press is latched here and the repeats are swallowed before they reach them.
        if (e.KeyModifiers == KeyModifiers.Shift && e.Key is >= Key.F5 and <= Key.F8)
        {
            if (!Latch(e.Key)) e.Handled = true;
            return;
        }

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
        if (vm.ClickerArmed && e.KeyModifiers == KeyModifiers.None)
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
