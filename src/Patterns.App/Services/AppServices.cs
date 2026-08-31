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
    public WebService Web { get; }
    public PlaylistService Playlist { get; }
    public FeedService Feeds { get; }
    public AudioService Audio { get; }
    public AudioPlayerService AudioPlayer { get; }
    public ControlService Control { get; }
    public StingerService Stingers { get; }
    public SandboxService Sandbox { get; }
    public StreamService Stream { get; }
    public SystemMetricsService Metrics { get; }
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
        Metrics = new SystemMetricsService(this);
        Recovery = new RecoveryStore(Store.BaseDirectory);
        PendingRecovery = Recovery.Read();
        GpuService.RecordAppliedPath(State);

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

        Screens.PlannedProvider = PlannedScreens;
        Screens.Changed += () => Outputs.OnScreensChanged();
        Outputs.LiveChanged += UpdateRecovery;
        Screens.Refresh(); // planned screens exist before any display is attached
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

    /// <summary>What the audience is seeing: the frozen program while the sandbox is open, else the live state.</summary>
    public ShowState AirState => Sandbox.ProgramState ?? State;

    /// <summary>
    /// Runs an air-targeted edit — a cue, a look recall, a stinger override, a playlist-part
    /// switch. While the sandbox is open it lands on the frozen program (the operator's
    /// in-progress edits stay untouched); otherwise it is a normal live edit.
    /// </summary>
    public void EditAir(Action<ShowState> edit)
    {
        if (!Sandbox.EditProgram(edit))
        {
            BulkEdit(() => edit(State));
        }
    }

    /// <summary>App startup: arm EDIT SAFE when the show is configured to start sandboxed.</summary>
    public void StartDefaultSandbox()
    {
        if (State.Switcher.EditSafeByDefault && !Sandbox.Active)
        {
            Sandbox.Enter();
        }
    }

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
    private bool _restartRequested;

    /// <summary>
    /// Admin restart: freeze the recovery sidecar to the current live state so the relaunch
    /// puts the show back, and return the exit code to shut down with (the supervisor's
    /// restart-request code when supervised, 0 when not).
    /// </summary>
    public int PrepareRestart()
    {
        _restartRequested = true;
        Recovery.Write(Outputs.IsLive, State.AudioPlayer.Playing);
        SaveNow();
        return LaunchOptions.IsChild ? SupervisorPolicy.RestartRequestExitCode : 0;
    }

    /// <summary>Keeps the recovery sidecar current: present while something is live, gone otherwise.</summary>
    private void UpdateRecovery()
    {
        if (_restartRequested) return; // the sidecar is frozen for the relaunch to read
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

    /// <summary>Synthetic screens for the placements the operator planned without hardware.</summary>
    private IEnumerable<ScreenInfo> PlannedScreens()
    {
        foreach (var p in State.Output.Placements)
        {
            if (!p.Planned) continue;
            yield return new ScreenInfo(
                p.ScreenId,
                p.CustomLabel.Length > 0 ? p.CustomLabel : "Planned screen",
                new Avalonia.PixelRect(p.X, p.Y, p.PlannedWidth, p.PlannedHeight),
                1.0, false, 0, IsPlanned: true);
        }
    }

    private string _plannedKey = "";

    /// <summary>Re-merges planned screens when their set, size or label changed.</summary>
    private void SyncPlannedScreens()
    {
        var key = string.Join('|', State.Output.Placements
            .Where(p => p.Planned)
            .Select(p => $"{p.ScreenId}:{p.PlannedWidth}x{p.PlannedHeight}:{p.CustomLabel}"));
        if (key == _plannedKey) return;
        _plannedKey = key;
        Screens.Refresh();
    }

    private void ApplySideEffects()
    {
        SyncPlannedScreens();

        // NDI sender set follows the config.
        Ndi.Reconcile(Bus.Current);

        // The live-input pool follows everything the program (and sandbox) references.
        ReconcileInputs();

        // Remote control server follows its config.
        Control.Reconcile();
    }

    /// <summary>
    /// Mounts/unmounts decoders and NDI receivers to match the current program snapshot —
    /// and the sandbox snapshot while one is open, so the detached preview shows its inputs.
    /// Also called directly on playlist item changes (runtime publishes skip side effects).
    /// </summary>
    public void ReconcileInputs()
    {
        Video.Reconcile(Bus.Current, Bus.Sandbox);
        NdiIn.Reconcile(Bus.Current, Bus.Sandbox);
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
            NdiIn.Dispose();
            Audio.Dispose();
            AudioPlayer.Dispose();
            Playlist.Dispose();
            Feeds.Dispose();
            Video.Dispose();
            Metrics.Dispose();
            SaveNow();
            if (!_restartRequested)
            {
                Recovery.Clear(); // a clean exit must never auto-restore
            }
            _instanceMutex?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown cleanup failed.", ex);
        }
    }
}
