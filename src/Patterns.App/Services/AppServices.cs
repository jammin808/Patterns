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

    /// <summary>Web pages inside the engine — one browser per page the show references.</summary>
    public WebEngine WebIn { get; }
    public PlaylistService Playlist { get; }
    public FeedService Feeds { get; }
    public AudioService Audio { get; }
    public AudioPlayerService AudioPlayer { get; }

    /// <summary>The Spotify sign-in for this machine, beside the settings file — never in a show.</summary>
    public SpotifyCredentialStore SpotifyCredentials { get; }

    /// <summary>Break music: Patterns drives Spotify, Spotify makes the sound.</summary>
    public SpotifyService Spotify { get; }

    public ControlService Control { get; }
    public OscService Osc { get; }
    public BeaconService Beacon { get; }
    public StingerService Stingers { get; }
    public SandboxService Sandbox { get; }
    public StreamService Stream { get; }
    public SystemMetricsService Metrics { get; }
    public AudioAnalyserService Analyser { get; }
    public RecoveryStore Recovery { get; }

    /// <summary>The show journal: every air change with its origin, on disk beside the settings.</summary>
    public ShowLog Journal { get; }

    /// <summary>The one way to do something to the show — see <see cref="ShowActions"/>.</summary>
    public ShowActions Actions { get; }

    /// <summary>Which targets the next CUT / TAKE touches (all, unless un-armed on the wall).</summary>
    public TransitionArming Arming { get; } = new();

    /// <summary>Where each cue list is (armed, current cue). Runtime only; reset when a show loads.</summary>
    public CueRuntime Cues { get; } = new();

    /// <summary>The caller's stack at show time: standby, GO, HOLD, history, the sidecar's place.</summary>
    public CueStackService CueStack { get; }

    private string _airLabel = "—";

    /// <summary>
    /// What is on air, by name: a look, "03.020 Five-minute call", "VOG: name", "STING: name",
    /// "STING HOLD: name", "PART: Main", or "MODIFIED — last …" after a sandbox send. Set inside
    /// every air-seam path; the LIVE strip, the STATE json and a Companion variable read this one string.
    /// </summary>
    public string AirLabel
    {
        get => _airLabel;
        set
        {
            if (_airLabel == value) return;
            _airLabel = value;
            AirLabelChanged?.Invoke();
        }
    }

    public event Action? AirLabelChanged;

    /// <summary>
    /// The look last put on air, by id ("" = none recorded): a recall from anywhere sets it, a
    /// playlist part clears it, a TAKE carries the preview's over. The desk's tally lights that
    /// look — exactly, or "edited" once the program no longer matches its picture — and with
    /// nothing recorded (a fresh start) lights whichever look the picture matches.
    /// </summary>
    private string _airLookId = "";

    public string AirLookId
    {
        get => _airLookId;
        set
        {
            if (value == _airLookId) return;
            // The look before this one, for LOOK BACK: a recall keeps what it replaced; a part or
            // a stinger that takes the picture clears the air look but not the way back.
            if (_airLookId.Length > 0) PreviousAirLookId = _airLookId;
            _airLookId = value;
        }
    }

    /// <summary>The look that was on air before the current one, by id ("" = none yet) — what LOOK BACK returns to.</summary>
    public string PreviousAirLookId { get; private set; } = "";

    /// <summary>The look loaded into the sandboxed preview, by id ("" = none): set by → PVW, cleared when the sandbox closes.</summary>
    public string PreviewLookId { get; set; } = "";

    /// <summary>
    /// What makes background music duck — a VOG announcement playing over it. One source, read by
    /// every gain rule (the music players, a stinger sound, a clip's soundtrack) so they duck
    /// together. A Func so a headless test can drive it without a voice.
    /// </summary>
    public Func<bool> MusicDuckSource { get; set; } = () => false;

    public bool MusicDuckActive => MusicDuckSource();

    private readonly Lazy<bool> _videoDecoder;

    /// <summary>
    /// What the cue validator may ask this machine: files on disk, the video runtime, whether break
    /// music can actually run tonight. Built per call — a Spotify connection arrives after startup,
    /// so a cached record would keep saying "not connected" all night. The libVLC probe stays lazy.
    /// </summary>
    public CueValidationContext ValidationContext => new()
    {
        VideoDecoderAvailable = ValidationVideoOverride is { } video ? video() : _videoDecoder.Value,
        MusicReady = Spotify.Connected,
    };

    /// <summary>Tests only: stand in for "is libVLC present" so a video-stinger cue can run headless.</summary>
    public Func<bool>? ValidationVideoOverride { get; set; }

    /// <summary>What the recovery file said at startup — read before anything can rewrite it.</summary>
    public RecoverySnapshot? PendingRecovery { get; }

    public MainWindow? MainWindow { get; private set; }

    /// <summary>Screen id the preview mirrors while editing an independent screen (null = program).</summary>
    public string? PreviewScreenId { get; set; }

    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _reapplyTimer;
    private int _bulkDepth;
    private bool _autosave = true;
    private bool _primaryInstance = true;
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
                _primaryInstance = false;
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
        if (Store.LastLoadMigrated)
        {
            // An upgraded file is written back once so the ids minted for its looks and
            // stingers are the same ids next time (cues and the journal refer to them).
            SaveNow();
        }

        Journal = new ShowLog(Store.BaseDirectory);
        // A supervisor that stood down last time left a note: it goes on the health line, once.
        var standDown = WatchdogMarker.ReadAndClear(Store.BaseDirectory);
        if (standDown.Length > 0)
        {
            HealthMonitor.WatchdogNote = standDown;
            Log.Warn(standDown);
        }
        Bus = new SnapshotBus(State);
        Ndi = new NdiService(Bus);
        Video = new VideoEngine();
        var video = Video;
        _videoDecoder = new Lazy<bool>(() => video.EnsureAvailable());
        NdiIn = new NdiInputEngine();
        Web = new WebService();
        WebIn = new WebEngine(Store.BaseDirectory);
        Screens = new ScreenService();
        Outputs = new OutputWindowManager(this);
        Playlist = new PlaylistService(this);
        Feeds = new FeedService(this);
        Audio = new AudioService(this);
        AudioPlayer = new AudioPlayerService(this);
        MusicDuckSource = () => AudioPlayer.VogSoundPlaying;
        SpotifyCredentials = new SpotifyCredentialStore(Store.BaseDirectory);
        Spotify = new SpotifyService(this, SpotifyCredentials);
        Control = new ControlService(this);
        Osc = new OscService(this);
        Beacon = new BeaconService(this);
        Stingers = new StingerService(this);
        Sandbox = new SandboxService(this);
        Stream = new StreamService(this);
        Metrics = new SystemMetricsService(this);
        Analyser = new AudioAnalyserService(this);
        Recovery = new RecoveryStore(Store.BaseDirectory);
        PendingRecovery = Recovery.Read();
        Actions = new ShowActions(this);
        CueStack = new CueStackService(this);
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
        Screens.Changed += () =>
        {
            var moved = SyncDisplays();
            Outputs.OnScreensChanged();
            if (moved) PublishRuntime();   // a hot-plug moves no model: push the new shapes ourselves
        };
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

    /// <summary>False in a second Patterns window on the same folder: it must not fight over the music.</summary>
    /// <summary>Tests only: behave as a second window on the same folder.</summary>
    public Func<bool>? PrimaryInstanceOverride { get; set; }

    public bool IsPrimaryInstance => PrimaryInstanceOverride?.Invoke() ?? _primaryInstance;

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
            return;
        }
        // Air moved without the live state moving — the recovery sidecar must follow, or a
        // crash would put the untaken preview back instead of what was on the screens.
        _airDirty = true;
        UpdateRecovery();
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

        SyncDisplays();
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
        Stingers.Stop(); // a deliberate restart comes back to the show, not to a clip
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
        if (_recoveryWritten == current && !_airDirty) return;
        _recoveryWritten = current;
        _airDirty = false;
        var place = CueStack?.Runtime.LastCueId is null && CueStack?.Runtime.StandbyCueId is null ? null : CueStack?.Place();
        if (current.Item1 || current.Item2 || place is not null)
        {
            Recovery.Write(current.Item1, current.Item2, CaptureAirLook(), place);
        }
        else
        {
            Recovery.Clear();
        }
    }

    /// <summary>The caller's place goes to the sidecar on every GO, atomically, live or not.</summary>
    public void WriteRunPlace()
    {
        if (_restartRequested) return;
        _recoveryWritten = (Outputs.IsLive, State.AudioPlayer.Playing);
        _airDirty = false;
        Recovery.Write(Outputs.IsLive, State.AudioPlayer.Playing, CaptureAirLook(), CueStack.Place());
    }

    private bool _airDirty;
    private string? _pinnedAirLook;

    /// <summary>
    /// While a clip owns the screens, the recovery sidecar must hold the content to come back to —
    /// not the clip. A watchdog relaunch mid-sting puts the show back, never a dead frame.
    /// </summary>
    public void PinAirLook(string? json)
    {
        if (_pinnedAirLook == json) return;
        _pinnedAirLook = json;
        _airDirty = true;
        UpdateRecovery();
    }

    /// <summary>
    /// The content the audience is seeing, but only while it differs from the live state —
    /// unsandboxed, the settings file already is the air content and capturing would be waste.
    /// </summary>
    private string? CaptureAirLook()
    {
        if (_pinnedAirLook is { Length: > 0 }) return _pinnedAirLook;
        if (!Sandbox.Active) return null;
        try
        {
            return LookService.Capture(AirState);
        }
        catch (Exception ex)
        {
            Log.Warn("Air look capture for recovery failed.", ex);
            return null;
        }
    }

    /// <summary>After a watchdog relaunch (--recover): put back what was running at the crash.</summary>
    public void TryRecover(ViewModels.MainViewModel vm)
    {
        try
        {
            if (!State.Watchdog.AutoRestore) return;
            if (PendingRecovery is not { } was || !RecoveryStore.IsFresh(was, DateTime.UtcNow)) return;

            // Put back what the audience was seeing, not the preview that was being built.
            // EDIT SAFE is already armed by the time this runs (StartDefaultSandbox precedes
            // the recovery timer), so the air look has to land on the frozen program via the
            // air seam — applying it to State would restore it into the preview and leave the
            // outputs on the untaken edit the settings file holds.
            if (was.AirLook is { Length: > 0 } airLook)
            {
                EditAir(air => LookService.Apply(airLook, air));
                vm.RefreshAfterRecovery();
            }

            if (was.Live && !Outputs.IsLive) Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Recovery);
            if (was.AudioPlaying && File.Exists(State.AudioPlayer.Path)) State.AudioPlayer.Playing = true;

            var restored = was.Live || was.AudioPlaying;
            vm.StatusMessage = restored
                ? "Watchdog restarted the app — the show was put back on."
                : "Watchdog restarted the app.";
            if (was.Run is { } place)
            {
                // The caller's place: disarmed, pointing at the next cue, nothing fired.
                RecoveryBanner = CueStack.RestorePlace(place);
                vm.StatusMessage = RecoveryBanner;
                vm.IsRunLayout = true;
            }
            Log.Info(vm.StatusMessage);
        }
        catch (Exception ex)
        {
            Log.Error("Recovery after restart failed.", ex);
        }
    }

    /// <summary>"Restored after restart — last GO 03.020 at 19:41:58 — press ARM to continue", until dismissed.</summary>
    public string RecoveryBanner { get; set; } = "";

    /// <summary>Raised on the UI thread after each publish (preview + status displays hook this).</summary>
    public event Action? SnapshotPublished;

    /// <summary>A line for the desk's status strip — the place confirmations belong, never the audience surface.</summary>
    public void Notify(string message)
    {
        if (MainWindow?.DataContext is ViewModels.MainViewModel vm) vm.StatusMessage = message;
        else Log.Info(message);
    }

    /// <summary>Synthetic screens for the placements the operator planned without hardware.</summary>
    private IEnumerable<ScreenInfo> PlannedScreens()
    {
        foreach (var p in State.Output.Placements)
        {
            if (!p.Planned) continue;
            yield return new ScreenInfo(
                p.ScreenId,
                p.CustomLabel.Length > 0 ? p.CustomLabel : p.IsVirtual ? p.VirtualKind : "Planned screen",
                new Avalonia.PixelRect(p.X, p.Y, p.PlannedWidth, p.PlannedHeight),
                1.0, false, 0, IsPlanned: true, IsVirtual: p.IsVirtual);
        }
    }

    private string _displayKey = "";

    /// <summary>
    /// Hands the bus the measured display sizes and names so every snapshot can resolve target
    /// geometry. Runs before each publish: a snapshot must never carry a stale display table.
    /// Returns true when they changed, so a caller with nothing else to publish can push one.
    /// </summary>
    private bool SyncDisplays()
    {
        var key = new System.Text.StringBuilder();
        foreach (var s in Screens.All)
        {
            key.Append(s.Id).Append('\u001f').Append(s.Bounds.Width).Append('x')
               .Append(s.Bounds.Height).Append('\u001f').Append(s.Label).Append('\u001e');
        }
        var k = key.ToString();
        if (k == _displayKey) return false;
        _displayKey = k;
        Bus.Displays = Rig.DisplaysOf(Screens.All.ToList());   // a fresh dictionary, assigned whole
        return true;
    }

    private string _plannedKey = "";

    /// <summary>Re-merges planned screens when their set, size or label changed.</summary>
    private void SyncPlannedScreens()
    {
        var key = string.Join('|', State.Output.Placements
            .Where(p => p.Planned)
            .Select(p => $"{p.ScreenId}:{p.PlannedWidth}x{p.PlannedHeight}:{p.CustomLabel}:{p.Virtual}"));
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

        // Remote control server follows its config; OSC and the beacon beside it.
        Control.Reconcile();
        Osc.Reconcile();
        Beacon.Reconcile();
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
        WebIn.Reconcile(Bus.Current, Bus.Sandbox);
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
        SyncDisplays();
        if (Sandbox.Active)
        {
            // A runtime publish must respect the freeze exactly like a model edit — otherwise
            // the next playlist item or tone tick would push the operator's private edit to air.
            Sandbox.PublishBoth();
        }
        else
        {
            Bus.Publish(State);
        }
        Outputs.NotifySnapshot();
        SnapshotPublished?.Invoke();
    }

    public void SaveNow()
    {
        if (!_autosave) return;
        // A clip on the screens (or a sting holding them) is a momentary event, never what the show
        // is. Writing now would reopen the file on a dead clip; the revert — or Shutdown's
        // Stingers.Dispose(), which stops first — writes the real content a moment later.
        // Null-safe on purpose: SaveNow also runs at startup, before Stingers exists.
        if (Stingers is { OwnsScreens: true })
        {
            _saveTimer.Stop();
            _saveTimer.Start();
            return;
        }
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
            Spotify.Dispose();
            Control.Dispose();
            Osc.Dispose();
            Beacon.Dispose();
            Web.Dispose();
            Ndi.StopAll();
            NdiIn.Dispose();
            WebIn.Dispose();
            Audio.Dispose();
            AudioPlayer.Dispose();
            Playlist.Dispose();
            Feeds.Dispose();
            Video.Dispose();
            Metrics.Dispose();
            Analyser.Dispose();
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
