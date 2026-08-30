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
    public NdiService Ndi { get; }
    public ScreenService Screens { get; }
    public OutputWindowManager Outputs { get; }
    public VideoEngine Video { get; }
    public PlaylistService Playlist { get; }
    public FeedService Feeds { get; }
    public AudioService Audio { get; }

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
        // (string.GetHashCode is randomized per process — a stable hash is required here.)
        try
        {
            _instanceMutex = new Mutex(true, "PatternsApp-" + StableFolderKey(Store.BaseDirectory), out var first);
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
        State.Tone.Enabled = false; // a tone must never auto-start with the app

        Bus = new SnapshotBus(State);
        Ndi = new NdiService(Bus);
        Video = new VideoEngine();
        Screens = new ScreenService();
        Outputs = new OutputWindowManager(this);
        Playlist = new PlaylistService(this);
        Feeds = new FeedService(this);
        Audio = new AudioService(this);

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
        // NDI sender set follows the config.
        Ndi.Reconcile(Bus.Current);

        // Video decoder lifecycle.
        Video.Reconcile(Bus.Current);
    }

    /// <summary>Stable across processes and case-insensitive, unlike string.GetHashCode.</summary>
    public static string StableFolderKey(string path)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant());
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash.AsSpan(0, 12));
    }

    public void Identify()
    {
        Bus.IdentifyUntilUtc = DateTime.UtcNow.AddSeconds(4);
        OnStateChanged();
    }

    /// <summary>
    /// Publishes a snapshot for a runtime-only change (playlist item, tone indicator, feed
    /// text) — sinks refresh, but the settings save timer is left alone.
    /// </summary>
    public void PublishRuntime()
    {
        if (_bulkDepth > 0) return;
        Bus.Publish(State);
        Outputs.NotifySnapshot();
        SnapshotPublished?.Invoke();
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
            Ndi.StopAll();
            Audio.Dispose();
            Playlist.Dispose();
            Feeds.Dispose();
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
