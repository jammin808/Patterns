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
    public NdiInputEngine NdiIn { get; }
    public PipEngine Pip { get; }
    public WebService Web { get; }
    public PlaylistService Playlist { get; }
    public FeedService Feeds { get; }
    public AudioService Audio { get; }
    public AudioPlayerService AudioPlayer { get; }
    public ControlService Control { get; }
    public StingerService Stingers { get; }
    public SandboxService Sandbox { get; }
    public StreamService Stream { get; }
    public RecoveryStore Recovery { get; }

    /// <summary>What the recovery file said at startup — read before anything can rewrite it.</summary>
    public RecoverySnapshot? PendingRecovery { get; }

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
        NdiIn = new NdiInputEngine();
        Pip = new PipEngine(Video);
        Web = new WebService();
        Screens = new ScreenService();
        Outputs = new OutputWindowManager(this);
        Playlist = new PlaylistService(this);
        Feeds = new FeedService(this);
        Audio = new AudioService(this);
        AudioPlayer = new AudioPlayerService(this);
        Control = new ControlService(this);
        Stingers = new StingerService(this);
        Sandbox = new SandboxService(this);
        Stream = new StreamService(this);
        Recovery = new RecoveryStore(Store.BaseDirectory);
        PendingRecovery = Recovery.Read();

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
        Outputs.LiveChanged += UpdateRecovery;
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

    /// <summary>Runs the full change pipeline now (sandbox enter/exit republish without a model edit).</summary>
    public void RepublishNow() => OnStateChanged();

    private void OnStateChanged()
    {
        if (_bulkDepth > 0) return;

        if (Sandbox.Active)
        {
            Sandbox.PublishBoth(); // outputs stay on the frozen program; preview follows the edits
        }
        else
        {
            Bus.Publish(State);
        }
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

        UpdateRecovery();
    }

    private (bool Live, bool Audio)? _recoveryWritten;

    /// <summary>Keeps the recovery sidecar current: present while something is live, gone otherwise.</summary>
    private void UpdateRecovery()
    {
        if (Bus.OutputsLive != Outputs.IsLive)
        {
            // GO/STOP don't touch the model, so push the tally change to sinks ourselves.
            Bus.OutputsLive = Outputs.IsLive;
            PublishRuntime();
        }

        var current = (Outputs.IsLive, State.AudioPlayer.Playing);
        if (_recoveryWritten == current) return;
        _recoveryWritten = current;
        if (current.Item1 || current.Item2)
        {
            Recovery.Write(current.Item1, current.Item2);
        }
        else
        {
            Recovery.Clear();
        }
    }

    /// <summary>After a watchdog relaunch (--recover): put back what was running at the crash.</summary>
    public void TryRecover(ViewModels.MainViewModel vm)
    {
        try
        {
            if (!State.Watchdog.AutoRestore) return;
            if (PendingRecovery is not { } was || !RecoveryStore.IsFresh(was, DateTime.UtcNow)) return;

            if (was.Live && !Outputs.IsLive) Outputs.Apply();
            if (was.AudioPlaying && File.Exists(State.AudioPlayer.Path)) State.AudioPlayer.Playing = true;

            var restored = was.Live || was.AudioPlaying;
            vm.StatusMessage = restored
                ? "Watchdog restarted the app — the show was put back on."
                : "Watchdog restarted the app.";
            Log.Info(vm.StatusMessage);
        }
        catch (Exception ex)
        {
            Log.Error("Recovery after restart failed.", ex);
        }
    }

    /// <summary>Raised on the UI thread after each publish (preview + status displays hook this).</summary>
    public event Action? SnapshotPublished;

    private void ApplySideEffects()
    {
        // NDI sender set follows the config.
        Ndi.Reconcile(Bus.Current);

        // Video decoder, NDI receiver and PiP lifecycles.
        Video.Reconcile(Bus.Current);
        NdiIn.Reconcile(Bus.Current);
        Pip.Reconcile(Bus.Current);

        // Remote control server follows its config.
        Control.Reconcile();
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
            Stream.Dispose();
            Stingers.Dispose();
            Control.Dispose();
            Web.Dispose();
            Ndi.StopAll();
            Pip.Dispose();
            NdiIn.Dispose();
            Audio.Dispose();
            AudioPlayer.Dispose();
            Playlist.Dispose();
            Feeds.Dispose();
            Video.Dispose();
            SaveNow();
            Recovery.Clear(); // a clean exit must never auto-restore
            _instanceMutex?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown cleanup failed.", ex);
        }
    }
}
