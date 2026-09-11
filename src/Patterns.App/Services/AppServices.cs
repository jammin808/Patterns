using Avalonia.Threading;
using Patterns.App.Views;
using Patterns.Core.Media;
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

    /// <summary>Web pages inside the engine — one browser per page the show references. Nothing opens outside Patterns.</summary>
    public WebEngine WebIn { get; }

    /// <summary>PDF decks inside the engine — one per deck the show references, a page at a time.</summary>
    public DeckEngine DeckIn { get; }
    public PlaylistService Playlist { get; }
    public FeedService Feeds { get; }

    /// <summary>The venue's forecast for the weather overlay, fetched on the show's interval and carried on the snapshot.</summary>
    public WeatherService Weather { get; }
    public AudioService Audio { get; }
    public AudioPlayerService AudioPlayer { get; }

    /// <summary>The Spotify sign-in for this machine, beside the settings file — never in a show.</summary>
    public SpotifyCredentialStore SpotifyCredentials { get; }

    /// <summary>Break music: Patterns drives Spotify, Spotify makes the sound.</summary>
    public SpotifyService Spotify { get; }

    /// <summary>The assistant's key for this machine, beside the settings file — never in a show.</summary>
    public AssistantKeyStore AssistantKeys { get; }

    /// <summary>The assistant: a fenced model that drafts looks, cues, designs and a show plan as proposals the desk applies.</summary>
    public AssistantService Assistant { get; }

    public ControlService Control { get; }
    public OscService Osc { get; }

    /// <summary>The Interactive area: Arduinos over serial, Raspberry Pis and controllers over IP — commands in, the show back out.</summary>
    public DeviceService Devices { get; }

    /// <summary>A permanent install's clock: programmes, adverts and announcements through the action layer.</summary>
    public InstallService Install { get; }

    /// <summary>Update packages staged beside the settings, applied by the watchdog between two starts.</summary>
    public UpdateService Updates { get; }

    /// <summary>The site's check-in with its management server: commands back, updates down.</summary>
    public ManagementService Management { get; }

    /// <summary>The passcode gate in front of remote administration (the web remote's ADMIN page, RESTART, UPDATE APPLY).</summary>
    public AdminGate Gate { get; } = new();

    /// <summary>How the app leaves with an exit code the watchdog reads — set by the desktop lifetime; null in a headless test.</summary>
    public Func<int, bool>? ExitRequest { get; set; }
    public BeaconService Beacon { get; }
    public StingerService Stingers { get; }
    public SandboxService Sandbox { get; }
    public StreamService Stream { get; }
    public SystemMetricsService Metrics { get; }
    public AudioAnalyserService Analyser { get; }
    public RecoveryStore Recovery { get; }

    /// <summary>The part of a cue that has not happened yet — its steps with a wait on them.</summary>
    public CueTail Tail { get; } = new();

    /// <summary>
    /// Who has the screens: this desk writes and beats the ownership record while its outputs are
    /// live, and stands down when a newer desk asks for them. The other half — a start taking the
    /// screens back from a run that crashed or hung — is <see cref="OutputTakeover"/>, before Avalonia.
    /// </summary>
    public OutputOwnershipService Ownership { get; }

    /// <summary>What this start found on the screens: a previous run still playing, taken back or left alone.</summary>
    public TakeoverResult Takeover { get; }

    /// <summary>The show journal: every air change with its origin, on disk beside the settings.</summary>
    public ShowLog Journal { get; }

    /// <summary>The desk's tick budget: what the once-a-second poll costs on the UI thread, its worst minute, the areas that failed.</summary>
    public TickBudget DeskTick { get; } = new();

    /// <summary>How long this start took to become a desk, phase by phase (the Machine page, the super-check).</summary>
    public StartupBudget Startup { get; } = new();

    /// <summary>The effects' quality ladder: the Machine page's mode in, the frame budgets' worst second in, a level out for every renderer.</summary>
    public QualityService Quality { get; }

    /// <summary>The one way to do something to the show — see <see cref="ShowActions"/>.</summary>
    public ShowActions Actions { get; }

    /// <summary>Which targets the next CUT / TAKE touches (all, unless un-armed on the wall).</summary>
    public TransitionArming Arming { get; } = new();

    /// <summary>
    /// The wall's focus — the tile clicked, by target id; null for the program tile — read by a
    /// scoped FADE (FOCUSED). Set by the desk's view model; unset (no desk) FOCUSED means the rig.
    /// </summary>
    public Func<string?>? FocusedTarget { get; set; }

    /// <summary>The wall's ticked tiles, by target id, read by a scoped FADE (TICKED, GROUPS). Set by the desk's view model; unset (no desk) nothing is ticked.</summary>
    public Func<IReadOnlyList<string>>? TickedTargets { get; set; }

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
            RaiseSafely(AirLabelChanged, "an air-label listener");
        }
    }

    public event Action? AirLabelChanged;

    /// <summary>
    /// Listeners never break the thing they listen to: a strip, a wall tile or a feedback sender that
    /// throws is logged and the publish, the label or the tally carries on to the next listener's
    /// caller. Before this, one throwing binding could unwind a stinger's own step mid-clip.
    /// </summary>
    private static void RaiseSafely(Action? handlers, string what)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                Log.Warn($"Carried past {what} that failed.", ex);
            }
        }
    }

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

    /// <summary>
    /// A restart puts both ids back as they were. Straight onto the fields: going through the
    /// setter would treat the restore as a recall and shuffle the way back, so LOOK BACK would
    /// return to the look that is already on air.
    /// </summary>
    internal void RestoreLookIds(string airLookId, string previousAirLookId)
    {
        _airLookId = airLookId;
        PreviousAirLookId = previousAirLookId;
    }

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

    /// <summary>The supervisor's note of how the last run ended (a crash or a hang), consumed at this start; null after a clean run.</summary>
    public CrashNote? LastCrash { get; }

    /// <summary>The run right after a native fault: clips decode in software unless the Machine page says Hardware.</summary>
    public bool SafeRun { get; }

    /// <summary>Whether the next decoder opened uses the graphics card — the operator's choice against this run's fate.</summary>
    public bool HardwareDecoding => VideoDecodingChoice.UseHardware(State.Admin.VideoDecoding, SafeRun);

    /// <summary>The Machine page's line about the clips' decoding.</summary>
    public string VideoDecodingWords => VideoDecodingChoice.Words(State.Admin.VideoDecoding, SafeRun);

    public MainWindow? MainWindow { get; private set; }

    /// <summary>Screen id the preview mirrors while editing an independent screen (null = program).</summary>
    public string? PreviewScreenId { get; set; }

    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _reapplyTimer;
    private int _bulkDepth;
    private bool _autosave = true;
    private bool _primaryInstance = true;
    private Mutex? _instanceMutex;

    /// <summary>
    /// The settings Main read before Avalonia started, handed to the desk so it does not read
    /// them again: the store (its migration flag with it) and the state. Taken once, by the
    /// first services built without a store of their own.
    /// </summary>
    public static (SettingsStore Store, ShowState State)? Preloaded { get; set; }

    public AppServices(SettingsStore? store = null, ShowState? preloaded = null)
    {
        if (store is null && Preloaded is { } pre)
        {
            store = pre.Store;
            preloaded ??= pre.State;
            Preloaded = null;
        }
        Store = store ?? new SettingsStore();
        Log.Init(Store.BaseDirectory);
        // What Main found on the screens before Avalonia started, taken once so a second desk in
        // the same process never inherits the first one's story.
        Takeover = OutputTakeover.Consume();
        // A fault on the UI thread is contained from here on: logged, counted, the desk kept up.
        UiFaults.Install();

        // The start-up budget: from Main when this process went through it (the runtime before
        // Main, the settings read and the graphics choices come in as Main marked them, and
        // Avalonia's own start ends here), else from here.
        Startup.Begin(StartupBudget.ProcessStartedAt);
        if (StartupBudget.ProcessStartedAt != 0) Startup.Mark(StartupBudget.Avalonia);

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

        State = preloaded ?? Store.Load();
        Startup.Mark(StartupBudget.Settings);   // already marked by Main when it read them: kept as Main's
        State.Blackout = false;
        State.Tone.Enabled = false; // a tone must never auto-start with the app
        if (Store.LastLoadMigrated)
        {
            // An upgraded file is written back once so the ids minted for its looks and
            // stingers are the same ids next time (cues and the journal refer to them).
            SaveNow();
        }

        Journal = new ShowLog(Store.BaseDirectory);
        // This start found the last run's render windows still playing and took them back (or was
        // told not to): the health line carries it, so "the screens are this desk's" is a fact the
        // operator can read rather than infer.
        if (Takeover.Words.Length > 0) HealthMonitor.WatchdogNote = Takeover.Words;
        // A supervisor that stood down last time left a note: it goes on the health line, once.
        var standDown = WatchdogMarker.ReadAndClear(Store.BaseDirectory);
        if (standDown.Length > 0)
        {
            HealthMonitor.WatchdogNote = HealthMonitor.WatchdogNote.Length > 0
                ? HealthMonitor.WatchdogNote + " · " + standDown
                : standDown;
            Log.Warn(standDown);
        }
        // A crash restart left a note: what the last run ended in, once, on the health line and in the
        // log — and after a native fault this is a safe run: the clips decode in software, the decoder
        // being the first suspect on a laptop, unless the Machine page says Hardware regardless.
        LastCrash = CrashMarker.ReadAndClear(Store.BaseDirectory);
        if (LastCrash is { } crash)
        {
            SafeRun = crash.NativeFault;
            var note = crash.Sentence;
            if (SafeRun)
            {
                note += VideoDecodingChoice.UseHardware(State.Admin.VideoDecoding, safeRun: true)
                    ? " Video decoding stays on the card (the Machine page says Hardware)."
                    : " Video decoding is in software for this run (Machine page → Video decoding).";
                if (crash.NativeFaultsInARow >= 2) note += $" {crash.NativeFaultsInARow} native faults in a row: if the last run already decoded in software, the decoder is not the cause — send the support bundle.";
            }
            HealthMonitor.WatchdogNote = HealthMonitor.WatchdogNote.Length > 0 ? HealthMonitor.WatchdogNote + " · " + note : note;
            Log.Warn(note);
        }
        Bus = new SnapshotBus(State);
        Ndi = new NdiService(Bus);
        Video = new VideoEngine { HardwareDecoding = () => HardwareDecoding };
        Quality = new QualityService(this);
        var video = Video;
        _videoDecoder = new Lazy<bool>(() => video.EnsureAvailable());
        NdiIn = new NdiInputEngine();
        WebIn = new WebEngine(Store.BaseDirectory);
        DeckIn = new DeckEngine(Store.BaseDirectory);
        DeckIn.Converter.ConfiguredPath = () => State.Admin.LibreOfficePath;
        // A conversion lands on a background thread; the swap to the PDF happens with the inputs, on the UI thread.
        DeckIn.Changed = () => Dispatcher.UIThread.Post(() =>
        {
            if (DeckIn.IsDisposed) return;
            ReconcileInputs();
            PublishRuntime();
        });
        Screens = new ScreenService();
        Outputs = new OutputWindowManager(this);
        Playlist = new PlaylistService(this);
        Feeds = new FeedService(this);
        Weather = new WeatherService(this);
        Audio = new AudioService(this);
        AudioPlayer = new AudioPlayerService(this);
        MusicDuckSource = () => AudioPlayer.VogSoundPlaying;
        SpotifyCredentials = new SpotifyCredentialStore(Store.BaseDirectory);
        Spotify = new SpotifyService(this, SpotifyCredentials);
        AssistantKeys = new AssistantKeyStore(Store.BaseDirectory);
        Assistant = new AssistantService(this, AssistantKeys);
        Control = new ControlService(this);
        Osc = new OscService(this);
        Devices = new DeviceService(this);
        Install = new InstallService(this);
        Updates = new UpdateService(this);
        Management = new ManagementService(this);
        Beacon = new BeaconService(this);
        Stingers = new StingerService(this);
        Sandbox = new SandboxService(this);
        Stream = new StreamService(this);
        Metrics = new SystemMetricsService(this);
        Analyser = new AudioAnalyserService(this);
        Recovery = new RecoveryStore(Store.BaseDirectory);
        Ownership = new OutputOwnershipService(this);
        PendingRecovery = Recovery.Read();
        // The record on disk belongs to the previous run until this one has either acted on it
        // or written its own. Until then the ordinary bookkeeping must not delete it as "nothing
        // live" — this start has not lit an output yet, and a second fault inside the same start
        // would find nothing at all, which is the one failure the record exists for.
        _recoveryPending = PendingRecovery is not null;
        Actions = new ShowActions(this);
        // A waiting step runs through the same action layer its cue's immediate steps went
        // through, and is journaled with its cue's name and its place in it.
        Tail.Run = step =>
        {
            var mapped = step.Action.ToAction();
            // Follow: the origin a cue's own later step already has — it was not pressed, the cue
            // said it would happen, and the gate that refuses a remote's GO must not refuse this.
            var result = Actions.Execute(mapped, ActionOrigin.Follow);
            Journal.Record(ActionOrigin.Follow.Label, mapped.Kind.ToString(), mapped.Target, result.Status.ToString(),
                $"{step.Label}: step {step.Number} of {step.Of} — {result.Message}");
            Notify($"{step.Label}: step {step.Number} of {step.Of} — {result.Message}");
        };
        CueStack = new CueStackService(this);
        // Standby moved (or the cue's look was edited): the pool opens the new standby's clips now, not at GO.
        CueStack.Changed += ReconcileInputs;
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
        // The screens change hands the moment they open or close, not at the next poll: a start a
        // second later must never read a record for windows that are already gone.
        Outputs.LiveChanged += Ownership.OnLiveChanged;
        Ownership.StoodDown += words =>
        {
            HealthMonitor.WatchdogNote = words;
            Notify(words);
        };
        // On air the collector works in the background and never stops the world for a full
        // collection; off air the default comes back.
        Outputs.LiveChanged += () => ShowGc.Apply(Outputs.IsLive);
        Arming.Changed += () =>
        {
            // The wall's arming is not in the model: push it to the sinks ourselves, so the
            // multiview's NEXT / HELD badges follow the next TAKE's scope as it is set.
            Bus.UnarmedTargets = Arming.Unarmed.ToHashSet(StringComparer.Ordinal);
            PublishRuntime();
        };
        Screens.Refresh(); // planned screens exist before any display is attached
        Startup.Mark(StartupBudget.Services);
        // The desk's first frame is the budget's last mark; a pipeline tells it once.
        Rendering.RenderPipeline.FirstPreviewFrame = () => Startup.Mark(StartupBudget.FirstFrame);
        // The NDI runtime's first touch loads and initialises a native library: off the UI thread
        // now, so the desk's first poll (a second after the start) finds the answer cached instead
        // of loading it on the UI thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _ = NdiInterop.Available; }
            catch { /* the poll asks again and says what it found */ }
        });
    }

    private ViewModels.MainViewModel? _recoverVm;
    private bool _windowOpened;

    public void AttachMainWindow(MainWindow window)
    {
        MainWindow = window;
        Startup.Mark(StartupBudget.Pages);   // the window's XAML is built by now
        window.Opened += (_, _) =>
        {
            Startup.Mark(StartupBudget.Window);
            Screens.Attach(window);
            ApplySideEffects();
            _windowOpened = true;
            if (_recoverVm is { } vm)
            {
                _recoverVm = null;
                // The screens are known and the side effects applied: the show goes back on as
                // soon as the desk has drawn, not after a timer's guess at how long that takes.
                Dispatcher.UIThread.Post(() => TryRecover(vm), DispatcherPriority.Background);
            }
        };
    }

    /// <summary>
    /// After a watchdog relaunch: put the show back the moment the window has opened and the
    /// screens are attached (a timer waited 2.5 s for that before, on every restart).
    /// </summary>
    public void RecoverWhenReady(ViewModels.MainViewModel vm)
    {
        if (_windowOpened)
        {
            Dispatcher.UIThread.Post(() => TryRecover(vm), DispatcherPriority.Background);
            return;
        }
        _recoverVm = vm;
    }

    /// <summary>
    /// Writes to the desk's own chrome — the tally chips, the "ON AIR · 4 s" lines, the row that
    /// lights — which the show does not contain. Every one of them is [JsonIgnore]: they never
    /// reach a snapshot, so a publish for them clones the whole show to hand the sinks a picture
    /// identical to the one they already have.
    ///
    /// That waste is the smaller half. The version a snapshot carries is what a look's own fade or
    /// transition rides on, and it is claimed by whichever publish comes next — so three tally
    /// writes after a look recall used to strand the look's own arrival on a version no sink would
    /// ever draw, and the wipe the operator asked for quietly became the show's dissolve. Chrome
    /// does not get to spend the show's versions.
    /// </summary>
    public void DeskEdit(Action edit)
    {
        _deskDepth++;
        try
        {
            edit();
        }
        finally
        {
            _deskDepth--;
        }
    }

    private int _deskDepth;

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
    /// The lower third on air has been edited since it went there: EDIT SAFE holds the copy the
    /// audience sees, and the designer edits the show's own — UPDATE ON AIR (or AIR again) carries
    /// the edit across. False without the sandbox (the air's design is the edited one).
    /// </summary>
    public bool LowerThirdAirEdited()
    {
        if (!Sandbox.Active) return false;
        var air = AirState.LowerThirds;
        if (!air.IsShowing || air.Active is not { } onAir) return false;
        var edited = State.LowerThirds.Find(onAir.Id);
        return edited is not null && !Patterns.Core.LowerThirds.LowerThirdsConfig.SameDesign(edited, onAir);
    }

    /// <summary>
    /// A lower third is in the preview for a sign-off: EDIT SAFE open and the edited state showing
    /// a run of its own — not the program's run mirrored into it when the sandbox opened.
    /// </summary>
    public bool LowerThirdInPreview()
    {
        if (!Sandbox.Active) return false;
        var mine = State.LowerThirds;
        return mine.IsShowing && !mine.IsSameRunAs(AirState.LowerThirds);
    }

    /// <summary>
    /// Runs an air-targeted edit — a cue, a look recall, a stinger override, a playlist-part
    /// switch. While the sandbox is open it lands on the frozen program (the operator's
    /// in-progress edits stay untouched); otherwise it is a normal live edit.
    /// </summary>
    public void EditAir(Action<ShowState> edit)
    {
        // Changing what the audience is seeing is, by definition, a take — so the pictures it
        // changes transition. Marking the seam rather than each of its callers is the point: a
        // stinger putting the show back, a recovery restoring the air and every verb in the
        // action layer all come through here, and a new one cannot forget.
        using var take = Bus.Take();
        if (!Sandbox.EditProgram(edit))
        {
            BulkEdit(() => edit(State));
            return;
        }
        // Air moved without the live state moving — the recovery sidecar must follow, or a
        // crash would put the untaken preview back instead of what was on the screens. The
        // watch on the frozen program has already counted the change; this is the write.
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
        if (_bulkDepth > 0 || _deskDepth > 0) return;

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
        RaiseSafely(SnapshotPublished, "a snapshot listener");

        if (Outputs.IsLive)
        {
            _reapplyTimer.Stop();
            _reapplyTimer.Start();
        }

        _saveTimer.Stop();
        _saveTimer.Start();

        UpdateRecovery();
    }

    private (bool Live, bool Audio, bool Sandboxed, long Air)? _recoveryWritten;
    private bool _restartRequested;
    private volatile bool _handedOver;
    private bool _recoveryPending;
    private long _airVersion;
    private ChangeTracker? _airWatch;

    /// <summary>
    /// Watches the frozen program so the recovery record follows the air by construction. Every
    /// way the air can move while EDIT SAFE is open — a cue, a look recall, a stinger, a
    /// per-screen SEND, a lower third, the blackout — ends in a write to that clone, and this
    /// counts them all. The alternative, a flag every one of those paths has to remember to set,
    /// is exactly what put the untaken preview on the screens after a restart: the paths that
    /// forgot were the ones nobody thought of.
    /// </summary>
    internal void WatchAir(ShowState? program)
    {
        // The old clone and its handlers go together; nothing else holds either.
        _airWatch = program is null ? null : new ChangeTracker(program, () => _airVersion++);
        AirMoved(); // opening or closing the split is itself a move of the record
    }

    /// <summary>The air record changed for a reason the watch cannot see — a clip pinned over it, the split opening or closing.</summary>
    private void AirMoved() => _airVersion++;

    /// <summary>
    /// The screens have gone to another desk that is starting: the record on disk is that desk's
    /// to read, so this one stops writing it. Closing our own outputs would otherwise clear the
    /// very thing the incoming desk uses to put the room's picture back. It becomes ours again
    /// the moment this desk lights outputs of its own.
    /// </summary>
    public void HandOverRecovery() => _handedOver = true;

    /// <summary>What the sidecar would say right now; the record is rewritten when any of it moves.</summary>
    private (bool Live, bool Audio, bool Sandboxed, long Air) RecoveryKey()
        => (Outputs.IsLive, State.AudioPlayer.Playing, Sandbox.Active, _airVersion);

    /// <summary>The whole record as it stands: what is live, what the audience is seeing, and the caller's place.</summary>
    private RecoverySnapshot RecoveryRecord(RunPlace? place) => new(
        Outputs.IsLive,
        State.AudioPlayer.Playing,
        DateTime.UtcNow,
        AirLook: null,                 // builds before the state vehicle wrote a look here
        Run: place,
        Sandboxed: Sandbox.Active,
        Air: CaptureAir(),
        BlackTargets: Bus.BlackTargets.Count == 0 ? null : Bus.BlackTargets.ToList(),
        Streaming: State.Stream.Active,
        AirLabel: AirLabel,
        AirLookId: AirLookId,
        PreviousAirLookId: PreviousAirLookId,
        PreviewLookId: PreviewLookId);

    /// <summary>The caller's place, or null when nothing has been armed or fired — an unused stack must not force the Run layout on a restart.</summary>
    private RunPlace? PlaceForRecovery()
        => CueStack?.Runtime.LastCueId is null && CueStack?.Runtime.StandbyCueId is null ? null : CueStack?.Place();

    /// <summary>
    /// Admin restart: freeze the recovery sidecar to the current live state so the relaunch
    /// puts the show back, and return the exit code to shut down with (the supervisor's
    /// restart-request code when supervised — its update code when the restart is to apply a
    /// staged update — 0 when not).
    ///
    /// The record written here is the same one a crash would leave: the content the audience
    /// is seeing, whether the desk was split, and the caller's place. It used to be the
    /// two-argument one, which is how a restart the operator asked for lost more than one they
    /// did not — the untaken preview went to air and the cue stack came back at the top.
    /// </summary>
    public int PrepareRestart(bool forUpdate = false)
    {
        Stingers.Stop(); // a deliberate restart comes back to the show, not to a clip
        Recovery.Write(RecoveryRecord(PlaceForRecovery()));
        _restartRequested = true;
        SaveNow();
        if (!(Updates.Supervised)) return 0;
        return forUpdate ? SupervisorPolicy.UpdateRequestExitCode : SupervisorPolicy.RestartRequestExitCode;
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

        // The screens went to another desk that is starting: the record is theirs to read now.
        if (_handedOver)
        {
            if (!Outputs.IsLive) return;
            _handedOver = false; // this desk has screens of its own again
        }

        var current = RecoveryKey();
        if (_recoveryWritten == current) return;
        _recoveryWritten = current;
        var place = PlaceForRecovery();
        if (current.Live || current.Audio || place is not null)
        {
            Recovery.Write(RecoveryRecord(place));
            _recoveryPending = false; // the file is this run's now
        }
        else if (!_recoveryPending)
        {
            Recovery.Clear();
        }
    }

    /// <summary>The caller's place goes to the sidecar on every GO, atomically, live or not.</summary>
    public void WriteRunPlace()
    {
        if (_restartRequested || _handedOver) return;
        _recoveryWritten = RecoveryKey();
        Recovery.Write(RecoveryRecord(CueStack.Place()));
    }

    private string? _pinnedAirLook;

    /// <summary>
    /// While a clip owns the screens, the recovery sidecar must hold the content to come back to —
    /// not the clip. A watchdog relaunch mid-sting puts the show back, never a dead frame.
    /// </summary>
    public void PinAirLook(string? json)
    {
        if (_pinnedAirLook == json) return;
        _pinnedAirLook = json;
        AirMoved();
        UpdateRecovery();
    }

    /// <summary>
    /// The show to come back to, whole — but only while it differs from the live state:
    /// unsandboxed with nothing covering it, the settings file already is the air and capturing
    /// would be waste. While a clip owns the screens the frozen program shows the clip, so the
    /// pre-clip content goes back on before the record is written: a clip is a moment, never the
    /// show, and a relaunch must never come back to a dead frame.
    /// </summary>
    private ShowState? CaptureAir()
    {
        var pinned = _pinnedAirLook;
        if (!Sandbox.Active && pinned is not { Length: > 0 }) return null;
        try
        {
            var air = JsonUtil.Clone(AirState);
            if (pinned is { Length: > 0 }) LookService.Apply(pinned, air);
            return air;
        }
        catch (Exception ex)
        {
            Log.Warn("Air capture for recovery failed.", ex);
            return null;
        }
    }

    /// <summary>After a watchdog relaunch (--recover): put back what was running at the crash.</summary>
    public void TryRecover(ViewModels.MainViewModel vm)
    {
        try
        {
            var took = Takeover.TookOver;
            // Taking the screens back ended the picture the room was watching. Putting it straight
            // back is then a duty, not a preference: the AutoRestore choice is about a watchdog's
            // own restart, and it must never be the reason a takeover leaves a dark room behind it.
            if (!took && !State.Watchdog.AutoRestore) return;
            // A takeover is proof the run we took the screens from was alive seconds ago, so its
            // record is better evidence than the settings file whatever its timestamp says — a
            // desk that went live this morning and never moved the air wrote it this morning.
            // Off a takeover, an old record is an old day and is not acted on.
            var usable = PendingRecovery is { } found && (took || RecoveryStore.IsFresh(found, DateTime.UtcNow));
            if (!usable || PendingRecovery is not { } was)
            {
                if (!took) return;
                // The run we took them from left no record to read: the show as it was saved goes
                // back on those screens rather than nothing at all — but that is a guess made
                // from the settings file, which while EDIT SAFE was open is the untaken preview,
                // and the operator is told so rather than shown a guess dressed as a restoration.
                if (!Outputs.IsLive) Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Recovery);
                vm.StatusMessage = Takeover.Words + " There was no record of what was on air — the screens carry the show as last saved, which may be an untaken preview. Check PGM.";
                Log.Warn(vm.StatusMessage);
                return;
            }

            // The desk was split when it went down — EDIT SAFE open, the audience on one picture
            // and the operator building another. Put the split back before the content: the air
            // look then lands on the frozen program through the air seam and the settings file
            // stays the preview it was. Without this the two collapse into one and whichever the
            // settings file happened to hold — the untaken preview — goes to the audience.
            if (was.Sandboxed == true && !Sandbox.Active)
            {
                Sandbox.Enter();
                Log.Info("The desk was in EDIT SAFE when it went down — the preview and the program are put back apart.");
            }
            else if (was.Sandboxed == false && Sandbox.Active)
            {
                // …and it was not. The show's own default armed EDIT SAFE on this start, but the
                // operator was editing live: coming back split would swallow their next change
                // silently, and they would find out when the caller asked why nothing happened.
                Sandbox.Discard();
                Log.Info("The desk was editing live when it went down — EDIT SAFE is left off, as it was.");
            }

            // Put back what the audience was seeing, not the preview that was being built.
            // EDIT SAFE is armed by the time this runs (by the show's own default, or by the
            // line above), so the air look has to land on the frozen program via the air seam —
            // applying it to State would restore it into the preview and leave the outputs on
            // the untaken edit the settings file holds.
            if (was.Air is { } air)
            {
                if (StingerLibrary.IsClipOnAir(air))
                {
                    // The record held a VOG or stinger clip as the air content: a clip is a moment,
                    // never the show, and put back it would be a dead picture nothing owns.
                    Log.Warn("The recovery file held a clip as the content on air — not put back; the show comes back as saved.");
                }
                else
                {
                    RestoreAir(air);
                    vm.RefreshAfterRecovery();
                }
            }
            else if (was.AirLook is { Length: > 0 } airLook)
            {
                // A sidecar an older build left behind: a look, not a state. Put it back the way
                // that build would have — the picture is right even if the brand kit is not.
                if (StingerLibrary.IsClipLook(State, airLook))
                {
                    Log.Warn("The recovery file held a clip as the content on air — not put back; the show comes back as saved.");
                }
                else
                {
                    EditAir(state => LookService.Apply(airLook, state));
                    vm.RefreshAfterRecovery();
                }
            }

            // The screens the operator had faded out on their own stay out: they are part of the
            // picture, and a foyer wall darkened for the keynote must not come back lit. Set
            // before the outputs open, so they never flash the picture first.
            if (was.BlackTargets is { Count: > 0 } black)
            {
                Bus.BlackTargets = black.ToList();
                RepublishNow();
            }

            // A takeover is its own evidence that the screens were live — we just took them off a
            // run that was playing on them — whatever a sidecar written before the crash says.
            if ((was.Live || took) && !Outputs.IsLive) Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Recovery);
            if (was.AudioPlaying && AudioPlaylist.HasTracks(State.AudioPlayer)) State.AudioPlayer.Playing = true;

            // What the desk calls the picture, back with the picture: without these the wall is
            // right and the LIVE strip reads "—", no look row lights, and LOOK BACK refuses.
            if (was.AirLabel is { Length: > 0 } label) AirLabel = label;
            if (was.AirLookId is { } airLook2) RestoreLookIds(airLook2, was.PreviousAirLookId ?? "");
            if (Sandbox.Active && was.PreviewLookId is { } previewLook) PreviewLookId = previewLook;

            var restored = was.Live || took || was.AudioPlaying;
            // The stream is not a Program output and pushing to a public endpoint is the
            // operator's call, so a restart never starts one by itself — it says so instead.
            var streamNote = was.Streaming && !State.Stream.Active ? " The stream was live — press STREAM to put it back." : "";
            // A start that took the screens back from a run still playing on them says so: the
            // operator needs to know the windows in the room are this desk's now, not the ghost's.
            // Name the restart honestly: the watchdog's own relaunch says so, and the same path
            // now serves a restart the operator asked for, which is not the watchdog's doing.
            var who = HealthMonitor.Restarts > 0 ? "Restarted by the watchdog" : "Restarted";
            var head = Takeover.TookOver
                ? Takeover.Words
                : restored
                    ? $"{who} — the show was put back on."
                    : $"{who}.";
            vm.StatusMessage = head + streamNote;
            if (was.Run is { } place)
            {
                // The caller's place: disarmed, pointing at the next cue, nothing fired. A
                // takeover's words lead the banner — the one sentence saying the windows in the
                // room are this desk's now, not the ghost's, must not be pushed off the strip by
                // the surface the recovery itself opens.
                var lead = Takeover.TookOver ? head + " " : "";
                RecoveryBanner = lead + CueStack.RestorePlace(place) + streamNote;
                vm.StatusMessage = RecoveryBanner;
                vm.IsRunLayout = true;
            }
            Log.Info(vm.StatusMessage);
        }
        catch (Exception ex)
        {
            Log.Error("Recovery after restart failed.", ex);
        }
        finally
        {
            // The record has been read and acted on: ordinary bookkeeping owns the file again.
            _recoveryPending = false;
        }
    }

    /// <summary>
    /// The recorded program back on the outputs. Split, the record IS the frozen program and
    /// replaces it whole. Not split, the settings file is already the show and the only thing the
    /// record can be telling us is what a clip was covering, so the content goes back through the
    /// ordinary air seam.
    /// </summary>
    private void RestoreAir(ShowState air)
    {
        if (Sandbox.RestoreProgram(air)) return;
        EditAir(state => LookService.Apply(LookService.Capture(air), state));
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
        Devices.Reconcile();
        Beacon.Reconcile();
    }

    /// <summary>
    /// Mounts/unmounts decoders and NDI receivers to match the current program snapshot —
    /// and the sandbox snapshot while one is open, so the detached preview shows its inputs.
    /// Also called directly on playlist item changes (runtime publishes skip side effects).
    /// </summary>
    public void ReconcileInputs()
    {
        // The standby cue's clips ride behind the live wants: opened before GO, held on their first frame.
        var preRoll = CueStack is null ? null : PreRoll.WantedFor(State, CueStack.StandbyCue);
        Video.Reconcile(Bus.Current, Bus.Sandbox, preRoll: preRoll);
        NdiIn.Reconcile(Bus.Current, Bus.Sandbox);
        WebIn.Reconcile(Bus.Current, Bus.Sandbox);
        DeckIn.Reconcile(Bus.Current, Bus.Sandbox);
    }

    /// <summary>The deck the program shows — the click-through's pages — or null when none is on air (or still opening).</summary>
    public IDeckSource? DeckOnAir()
    {
        var wanted = MediaLocator.FindWantedInputs(Bus.Current).FirstOrDefault(w => w.Kind == MediaLocator.WantedKind.Deck);
        return wanted is null ? null : InputBus.For(wanted.Key) as IDeckSource;
    }

    /// <summary>
    /// The clip on air as the caller's clock reads it — the file, where it is, what is left, whether
    /// it will end — or null when nothing on air is a file whose decoder is open. Read every second
    /// by the desk, the Run strip, the remotes and OSC, so every surface counts the same seconds.
    /// </summary>
    public VideoReading? VideoOnAir()
        => VideoClock.Read(Bus.Current, InputBus.For, Stingers is { ClipActive: true });

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
        if (_bulkDepth > 0 || _deskDepth > 0) return;
        // The show moving on by itself — a playlist reaching its next item, a clip ending — is a
        // take: the picture changed because the show changed, not because anyone is editing it.
        using var take = Bus.Take();
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
        RaiseSafely(SnapshotPublished, "a snapshot listener");
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

    private bool _shutDown;

    /// <summary>The way out, once: Avalonia raises ShutdownRequested and then Exit, and the exit used to do all of this twice.</summary>
    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        try
        {
            Outputs.CloseAll();
            Stream.Dispose();
            Stingers.Dispose();
            Spotify.Dispose();
            Control.Dispose();
            Osc.Dispose();
            Devices.Dispose();
            Management.Dispose();
            Beacon.Dispose();
            Ndi.StopAll();
            NdiIn.Dispose();
            WebIn.Dispose();
            DeckIn.Dispose();
            Audio.Dispose();
            AudioPlayer.Dispose();
            Playlist.Dispose();
            Feeds.Dispose();
            Weather.Dispose();
            Video.Dispose();
            Metrics.Dispose();
            Analyser.Dispose();
            Tail.Dispose();
            SaveNow();
            if (!_restartRequested)
            {
                Recovery.Clear(); // a clean exit must never auto-restore
            }
            // The windows went with CloseAll above: the record must go too, or the next start
            // would hunt for screens that are not playing.
            Ownership.Shutdown();
            _instanceMutex?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown cleanup failed.", ex);
        }
    }
}
