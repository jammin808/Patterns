using Avalonia.Threading;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Composition root: owns the show state, snapshot bus, persistence, screens, outputs,
/// NDI and video, and turns state changes into side effects.
/// </summary>
public sealed class AppServices
{
    public static AppServices Instance { get; set; } = null!;

    public ShowState State { get; }
    public SnapshotBus Bus { get; }
    public SettingsStore Store { get; }
    public NdiSender Ndi { get; }
    public ScreenService Screens { get; }
    public OutputWindowManager Outputs { get; }
    public VideoEngine Video { get; }

    public MainWindow? MainWindow { get; private set; }

    /// <summary>Screen id the preview mirrors while editing an independent screen (null = program).</summary>
    public string? PreviewScreenId { get; set; }

    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _reapplyTimer;
    private int _bulkDepth;
    private bool _autosave = true;
    private Mutex? _instanceMutex;

    public AppServices(SettingsStore? store = null)
    {
        Store = store ?? new SettingsStore();
        Log.Init(Store.BaseDirectory);

        // Second instance on the same folder: run, but leave saving to the first one.
        try
        {
            _instanceMutex = new Mutex(true, "PatternsApp-" + Math.Abs(Store.BaseDirectory.GetHashCode()), out var first);
            if (!first)
            {
                _autosave = false;
                Log.Warn("Another instance owns this folder — autosave disabled here.");
            }
        }
        catch
        {
            // Mutex trouble must never stop startup.
        }

        State = Store.Load();
        State.Blackout = false;
        State.IdentifyUntilUtc = null;

        Bus = new SnapshotBus(State);
        Ndi = new NdiSender(Bus);
        Video = new VideoEngine();
        Screens = new ScreenService();
        Outputs = new OutputWindowManager(this);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };

        _reapplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _reapplyTimer.Tick += (_, _) =>
        {
            _reapplyTimer.Stop();
            if (Outputs.IsLive) Outputs.Apply();
        };

        _ = new ChangeTracker(State, OnStateChanged);

        Screens.Changed += () => Outputs.OnScreensChanged();
    }

    public void AttachMainWindow(MainWindow window)
    {
        MainWindow = window;
        window.Opened += (_, _) =>
        {
            Screens.Attach(window);
            ApplySideEffects();
        };
    }

    /// <summary>Group many model writes into one publish (preset/show/brand-kit loads).</summary>
    public void BulkEdit(Action edit)
    {
        _bulkDepth++;
        try
        {
            edit();
        }
        finally
        {
            _bulkDepth--;
            OnStateChanged();
        }
    }

    private void OnStateChanged()
    {
        if (_bulkDepth > 0) return;

        Bus.Publish(State);
        ApplySideEffects();

        Outputs.NotifySnapshot();
        SnapshotPublished?.Invoke();

        if (Outputs.IsLive)
        {
            _reapplyTimer.Stop();
            _reapplyTimer.Start();
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Raised on the UI thread after each publish (preview + status displays hook this).</summary>
    public event Action? SnapshotPublished;

    private void ApplySideEffects()
    {
        // NDI lifecycle.
        if (State.Ndi.Enabled && !Ndi.IsRunning) Ndi.Start();
        else if (!State.Ndi.Enabled && Ndi.IsRunning) Ndi.Stop();

        // Video decoder lifecycle.
        Video.Reconcile(Bus.Current);
    }

    public void Identify()
    {
        State.IdentifyUntilUtc = DateTime.UtcNow.AddSeconds(4);
    }

    public void SaveNow()
    {
        if (!_autosave) return;
        try
        {
            Store.Save(State);
        }
        catch (Exception ex)
        {
            Log.Error("Settings save failed.", ex);
        }
    }

    public void Shutdown()
    {
        try
        {
            Outputs.CloseAll();
            Ndi.Stop();
            Video.Dispose();
            SaveNow();
            _instanceMutex?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown cleanup failed.", ex);
        }
    }
}
