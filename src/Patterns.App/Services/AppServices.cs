using Patterns.Audience;
using Patterns.Assistant;
using Patterns.Devices;
using Patterns.Rendering;
using Avalonia.Threading;
using Patterns.App.Views;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Ndi;
using Patterns.Core.Services;
using Patterns.Platform.Windows;

namespace Patterns.App.Services;

/// <summary>
/// The desk's composition root: the kernel first (<see cref="ServiceKernel"/> — the store and
/// the show, the log, the journal, the bus, the beacon, the nodes, the assistant, the arcade),
/// then the desk's own services on top of it — screens, outputs, NDI and video, the sandbox,
/// the cue stack — and it turns state changes into side effects. It is also what the kernel's
/// services see of the desk, through the capabilities it implements (<see cref="IAirReport"/>,
/// <see cref="ITwinHost"/>, <see cref="IWireHost"/>, <see cref="IStageHost"/>, <see cref="IPlayHost"/>).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The kernel ends in named shutdown phases (ADR-011); the instance mutex goes in the process phase.")]
public sealed class AppServices : IAirReport, ITwinHost, IWireHost, IStageHost, IPlayHost, IRunHost, IMachineHost, IDeviceHost, IOscHost
{
    public static AppServices Instance { get; set; } = null!;

    /// <summary>What every role stands on, built before anything of the desk.</summary>
    public ServiceKernel Kernel { get; }

    /// <summary>The rig's clock as this desk reads it — its own machine's: a desk is the frame.</summary>
    public RoomClock Clock => Kernel.Clock;

    public ShowState State => Kernel.State;
    public SnapshotBus Bus => Kernel.Bus;
    public SettingsStore Store => Kernel.Store;
    public NdiService Ndi { get; }
    public ScreenService Screens { get; }
    public OutputWindowManager Outputs { get; }
    public VideoEngine Video { get; }
    public NdiInputEngine NdiIn { get; }

    /// <summary>Web pages inside the engine — one browser per page the show references. Nothing opens outside Patterns.</summary>
    public WebEngine WebIn { get; }

    /// <summary>Round 68.6: a YouTube or Vimeo page's stream through the clip player, when the look asks and yt-dlp is on the machine.</summary>
    public WebVideoService WebVideo { get; }

    /// <summary>The audio graph: the routing matrix applied to the players, the mixer lanes and the NDI sends (Windows; a no-op elsewhere).</summary>
    public AudioGraphService? AudioGraph { get; private set; }

    /// <summary>PDF decks inside the engine — one per deck the show references, a page at a time.</summary>
    public DeckEngine DeckIn { get; }

    /// <summary>Round 69: the residency ledger — every held thing with the reason it stays, and the sweep that lets idle pictures go on a clock.</summary>
    public ResidencyService Residency { get; }

    /// <summary>This machine's arcade on the input bus while a picture shows it.</summary>
    public ArcadeInputEngine ArcadeIn { get; }
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
    public AssistantKeyStore AssistantKeys => Kernel.AssistantKeys;

    /// <summary>The assistant: a fenced model that drafts looks, cues, designs and a show plan as proposals the desk applies.</summary>
    public AssistantService Assistant => Kernel.Assistant;

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
    public AdminGate Gate => Kernel.Gate;

    /// <summary>How the app leaves with an exit code the watchdog reads — set by the desktop lifetime; null in a headless test.</summary>
    public Func<int, bool>? ExitRequest { get; set; }
    public BeaconService Beacon => Kernel.Beacon;

    /// <summary>The twin link: a second Patterns kept in step, on this machine or another, that can take the show.</summary>
    public TwinService Twin { get; }

    /// <summary>The show lock: the machine held off notifications, sounds, other apps' audio, the shortcut keys, sleep and the Windows key while the show runs.</summary>
    public ShowLockService ShowLock { get; }

    /// <summary>The camera calibration: patterns on the projectors, a camera watching, the rig solved from what it saw.</summary>
    public CalibrationService Calibration { get; }

    /// <summary>Every other Patterns heard on the beacon — desks, callers, arcades, timers — and what is linked to this one.</summary>
    public NodesService Nodes => Kernel.Nodes;

    /// <summary>The arcade: the engine's loop, the pads, the picture to NDI, the board — run here on an arcade node; a desk sends the verbs to the nodes it hears.</summary>
    /// <summary>The games: the desk's own engine for a rig day's toy on the show machine, idle until a verb asks for a picture.</summary>
    public ArcadeService Arcade { get; }

    /// <summary>Audience play: the room on the hub — polls, quizzes, the cloud, messages back, the queue, draughts and the path; the wall on the arcade's lane.</summary>
    public PlayService Play { get; }

    /// <summary>The desk builds the room and the arcade always (a rig day, the wall's board); a node asks its role (round 64).</summary>
    public bool HasRoom => true;
    public bool HasArcade => true;
    public string RoomJoinUrl => Play.JoinUrl;

    /// <summary>Rig day, gamified and opt-in: the show-ready bar, the alignment game, Blend Quest, the streak.</summary>
    public RigDayService RigDay { get; }

    /// <summary>The stage timer and the messages to stage, on the countdown's clock; the stage and timer pages read it.</summary>
    public StageService Stage { get; }

    /// <summary>Output hot-plug: a display unplugged, back, or new, and what the rig does about it.</summary>
    public HotPlugService HotPlug { get; }

    /// <summary>The God's Eye (round 66): the whole show as one graph, gathered from the services once a second.</summary>
    public EyeService Eye { get; }

    /// <summary>
    /// Why the outputs must stay closed whatever asks for them — "" when nothing holds them. A
    /// standby twin sets it: its screens open only when it takes the show. Runtime only, never
    /// saved, and read by OUTPUTS ON and by the window manager itself.
    /// </summary>
    public string OutputsHeldBy { get; set; } = "";
    public StingerService Stingers { get; }
    public SandboxService Sandbox { get; }
    public StreamService Stream { get; }
    public SystemMetricsService Metrics { get; }
    public AudioAnalyserService Analyser { get; }
    public RecoveryStore Recovery { get; }

    /// <summary>The show's files on one ordered lane: the autosaves, the recovery record, the final save (round 65.12).</summary>
    public PersistenceRuntime Persistence { get; private set; } = null!;

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
    public ShowLog Journal => Kernel.Journal;

    /// <summary>The desk's tick budget: what the once-a-second poll costs on the UI thread, its worst minute, the areas that failed.</summary>
    public TickBudget DeskTick { get; } = new();

    /// <summary>The page switch's budget: every press on the rail timed to the first frame drawn, the worst of the last sixty and where its time went.</summary>
    public SwitchBudget Switches { get; } = new();

    /// <summary>The one worker that draws the Library's thumbnails.</summary>
    public ThumbnailQueue Thumbnails { get; } = new();

    /// <summary>How long this start took to become a desk, phase by phase (the Machine page, the super-check).</summary>
    public StartupBudget Startup => Kernel.Startup;

    /// <summary>The effects' quality ladder: the Machine page's mode in, the frame budgets' worst second in, a level out for every renderer.</summary>
    public QualityService Quality { get; }

    /// <summary>The one way to do something to the show — see <see cref="ShowActions"/>.</summary>
    public ShowActions Actions { get; }

    /// <summary>The rig's edits that are logic rather than a binding: placements, planned screens, gaps, a feed's screen.</summary>
    public RigEditor RigEditor { get; }

    /// <summary>Is the look on air still what is on the screens, and where has a screen gone its own way — read by the page, the wire, OSC and Companion alike.</summary>
    public LookTally LookTally { get; }

    /// <summary>Which targets the next CUT / TAKE touches (all, unless un-armed on the wall).</summary>
    public TransitionArming Arming { get; } = new();

    /// <summary>Round 67.6: the transition or video sting the next TAKE alone arrives by.</summary>
    public NextTakeService NextTake { get; } = new();

    /// <summary>The wall's CUT / TAKE scope words as the picker has them (the desk sets it; a node has none), for STATE's take row.</summary>
    public Func<string>? TakeScopeWords { get; set; }

    /// <summary>
    /// The wall's focus — the tile clicked, by target id; null for the program tile — read by a
    /// scoped FADE (FOCUSED). Set by the desk's view model; unset (no desk) FOCUSED means the rig.
    /// </summary>
    public Func<string?>? FocusedTarget { get; set; }

    /// <summary>The wall's ticked tiles, by target id, read by a scoped FADE (TICKED, GROUPS). Set by the desk's view model; unset (no desk) nothing is ticked.</summary>
    public Func<IReadOnlyList<string>>? TickedTargets { get; set; }

    /// <summary>Where each cue list is (armed, current cue). Runtime only; reset when a show loads.</summary>
    /// <summary>Where every list is right now — the kernel's, read by the desk and by a node alike.</summary>
    public CueRuntime Cues => Kernel.Cues;

    /// <summary>The caller's stack at show time: standby, GO, HOLD, history, the sidecar's place.</summary>
    public CueStackService CueStack { get; }

    private CommandRouter? _edgeRouter;

    /// <summary>The wire's router as the device transports and the OSC port see it: one, made on first ask.</summary>
    public IRouter Router => _edgeRouter ??= new CommandRouter(this);

    string IDeviceHost.ExecutionInHand => Actions.ExecutionInHand;

    ICueStackEvents? IOscHost.CueStackEvents => CueStack;

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
        // A preset is a file beside the show rather than part of it, so the pure checks cannot see
        // one: the desk hands the list in. A cue that names a preset this machine has not got is
        // worth a look, not a refusal — the folder may simply not have travelled yet.
        Presets = Store.PresetNames().ToList(),
    };

    /// <summary>Tests only: stand in for "is libVLC present" so a video-stinger cue can run headless.</summary>
    public Func<bool>? ValidationVideoOverride { get; set; }

    /// <summary>What the recovery file said at startup — read before anything can rewrite it.</summary>
    public RecoverySnapshot? PendingRecovery { get; }

    /// <summary>The supervisor's note of how the last run ended (a crash or a hang), consumed at this start; null after a clean run.</summary>
    public CrashNote? LastCrash => Kernel.LastCrash;

    /// <summary>The run right after a native fault: clips decode in software unless the Machine page says Hardware.</summary>
    public bool SafeRun => Kernel.SafeRun;

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

    /// <summary>What this process is, as the launch said (<c>--node caller</c>); the desk unless told otherwise. Read by the constructor.</summary>
    public static NodeKind LaunchProfile { get; set; } = NodeKind.Desk;

    /// <summary>What this process is: the desk, or a node that boots a fraction of it and never opens an output.</summary>
    public NodeKind Profile { get; }

    public bool IsDesk => Profile == NodeKind.Desk;

    /// <summary>Every desk made, held weakly: <see cref="Alive"/> counts the ones a collection could not reclaim — a closed desk still counted is a rooted desk (round 64's census).</summary>
    private static readonly List<WeakReference<AppServices>> Desks = new();

    /// <summary>Desks alive right now — read after a full collection to prove a closed desk is gone.</summary>
    public static int Alive
    {
        get
        {
            lock (Desks)
            {
                Desks.RemoveAll(w => !w.TryGetTarget(out _));
                return Desks.Count;
            }
        }
    }

    /// <summary>The pipeline hook this desk set for its start-up budget, cleared on shutdown so the static holds nothing of a closed desk.</summary>
    private Action? _firstPreviewFrame;

    public AppServices(SettingsStore? store = null, ShowState? preloaded = null, NodeKind? profile = null)
    {
        lock (Desks) Desks.Add(new WeakReference<AppServices>(this));
        RenderingModule.Register();                                                         // the desk draws: the render side reports its bytes and reads pictures for the assistant
        Profile = profile ?? LaunchProfile;
        if (store is null && Preloaded is { } pre)
        {
            store = pre.Store;
            preloaded ??= pre.State;
            Preloaded = null;
        }
        store ??= new SettingsStore();
        Log.Init(store.BaseDirectory);
        MachineProbe.BuildVersion = () => UpdateService.RunningVersion;   // round 65.12: the platform assembly knows no update service
        // What Main found on the screens before Avalonia started, taken once so a second desk in
        // the same process never inherits the first one's story.
        Takeover = OutputTakeover.Consume();
        // A fault on the UI thread is contained from here on: logged, counted, the desk kept up.
        UiFaults.Install();

        // Second instance on the same folder: run, but leave saving to the first one.
        // (string.GetHashCode is randomized per process — a stable hash is required here.)
        try
        {
            _instanceMutex = new Mutex(true, "PatternsApp-" + StableFolderKey(store.BaseDirectory), out var first);
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

        // The kernel: the show, the log, the journal, the last run's notes, the bus and the
        // services every role has — built before a single desk service, and the only thing those
        // services are written against.
        Kernel = ServiceKernel.Build(Profile, store, preloaded);
        Kernel.Notifier = Notify;
        Kernel.Facts = GatherFacts;
        // The show's files on one lane (round 65.12), built before the first save below can ask for it.
        Recovery = new RecoveryStore(Store.BaseDirectory);
        Persistence = new PersistenceRuntime(Store, Recovery, Files) { Autosave = _autosave };
        if (Kernel.Migrated)
        {
            // An upgraded file is written back once so the ids minted for its looks and
            // stingers are the same ids next time (cues and the journal refer to them).
            SaveNow();
        }

        // This start found the last run's render windows still playing and took them back (or was
        // told not to): the health line carries it, so "the screens are this desk's" is a fact the
        // operator can read rather than infer.
        if (Takeover.Words.Length > 0) HealthMonitor.WatchdogNote = Takeover.Words;
        // A supervisor that stood down last time left a note: it goes on the health line, once.
        var standDown = Kernel.StandDownNote;
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
        if (LastCrash is { } crash)
        {
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
        Ndi = new NdiService(Bus);
        Video = new VideoEngine
        {
            HardwareDecoding = () => HardwareDecoding,
            // The programme's sound goes where the room's sound goes; the operator's own
            // monitoring goes on their own wire. A programme with several outputs cannot be
            // resolved to one device id — a decoder plays to one — so it stays on the default,
            // which is what a single-interface rig wants anyway.
            DeviceFor = where => where switch
            {
                AudioDestination.Monitor => AudioPlayerService.DeviceIdFor(State.Monitor.Device),
                AudioDestination.Program when State.AudioPlayer.Devices.Count == 1
                    => AudioPlayerService.DeviceIdFor(State.AudioPlayer.Devices[0]),
                _ => null,
            },
        };
        Quality = new QualityService(this);
        var video = Video;
        _videoDecoder = new Lazy<bool>(() => video.EnsureAvailable());
        NdiIn = new NdiInputEngine();
        WebIn = new WebEngine(Store.BaseDirectory);
        WebIn.HardwareDecoding = () => HardwareDecoding;
        AudioGraph = new AudioGraphService(this);
        DeckIn = new DeckEngine(Store.BaseDirectory);
        Residency = new ResidencyService(this);
        DeckIn.Converter.ConfiguredPath = () => State.Admin.LibreOfficePath;
        // A conversion lands on a background thread; the swap to the PDF happens with the inputs, on the UI thread.
        DeckIn.Changed = () => UiThread.Post(() =>
        {
            if (DeckIn.IsDisposed) return;
            ReconcileInputs();
            PublishRuntime();
        });
        Screens = new ScreenService();
        WebIn.PlanFor = (w, fps) =>
        {
            // A page is captured no larger than the largest surface in the rig — so a routing change never
            // restarts its capture — and smaller still on a small machine once the ladder has stepped.
            var (vw, vh) = WebEngine.ParseSize(w.Format);
            var geo = Rig.Geometry(State, Screens.All);
            int dw = 0, dh = 0;
            foreach (var target in geo.Targets)
            {
                var size = geo.SizeOf(target);
                dw = Math.Max(dw, size.Width);
                dh = Math.Max(dh, size.Height);
            }
            return Patterns.Core.Media.WebCapturePolicy.Plan(vw, vh, dw, dh,
                Patterns.Core.Services.MemoryBudget.ClassOf(Patterns.Core.Services.MemoryBudget.MachineMB), Patterns.Core.Services.QualityLadder.Shared.Level, fps);
        };
        // The native-player path: the tool's answers land on a worker; the inputs reconcile on the UI thread, and the
        // locator hands a resolved page to the clip engine under the page's own key.
        WebVideo = new WebVideoService { ConfiguredPath = () => State.Admin.YtDlpPath };
        WebVideo.Changed = () => UiThread.Post(() =>
        {
            if (WebVideo.IsDisposed) return;
            ReconcileInputs();
        });
        MediaLocator.WebResolver = WebVideo.Rewrite;
        Outputs = new OutputWindowManager(this);
        Playlist = new PlaylistService(this);
        Feeds = new FeedService(this);
        Weather = new WeatherService(this);
        Audio = new AudioService(this);
        AudioPlayer = new AudioPlayerService(this);
        MusicDuckSource = () => AudioPlayer.VogSoundPlaying;
        SpotifyCredentials = new SpotifyCredentialStore(Store.BaseDirectory);
        Spotify = new SpotifyService(this, SpotifyCredentials);
        Control = new ControlService(Kernel, this);
        Osc = new OscService(this);
        Devices = new DeviceService(this);
        // A line to a box is journaled as dispatched when it goes; its receipt — what the box made
        // of it, or its silence — is journaled when it lands, so the log says what happened, not what was asked.
        Devices.Receipt += r => Kernel.Journal.Record($"device {r.Device}", "DeviceReceipt", r.Device, r.Ok ? "Done" : "Failed", r.Line);
        Install = new InstallService(this);
        Updates = new UpdateService(Kernel, this) { NotNow = () => LivePolicy.Refusal(ShowActionKind.UpdateApply, CueStack?.Armed ?? false, OutputsLive) };
        Management = new ManagementService(Kernel, this, Updates);
        Twin = new TwinService(Kernel, this);
        Kernel.Link = Twin;
        ShowLock = new ShowLockService(this);
        Calibration = new CalibrationService(this);
        Play = new PlayService(Kernel, this);
        RigDay = new RigDayService(this);
        Arcade = new ArcadeService(Kernel);
        Arcade.Board = Play.DrawWall;
        ArcadeIn = new ArcadeInputEngine(Arcade);
        Stingers = new StingerService(this);
        Sandbox = new SandboxService(this);
        Stream = new StreamService(this);
        Metrics = new SystemMetricsService(this);
        Analyser = new AudioAnalyserService(this);
        Ownership = new OutputOwnershipService(this);
        PendingRecovery = Recovery.Read();
        // The record on disk belongs to the previous run until this one has either acted on it
        // or written its own. Until then the ordinary bookkeeping must not delete it as "nothing
        // live" — this start has not lit an output yet, and a second fault inside the same start
        // would find nothing at all, which is the one failure the record exists for.
        _recoveryPending = PendingRecovery is not null;
        Actions = new ShowActions(this);
        // Round 65.11: a box that carries a screen says what its input receives — held against the screen's contract on the desk's thread.
        Devices.InputReported += report => UiThread.Post(() => Actions.ReceiveFromDevice(report));
        RigEditor = new RigEditor(this);
        HotPlug = new HotPlugService(this);
        LookTally = new LookTally(this);
        Eye = new EyeService(this);
        // A waiting step runs through the same action layer its cue's immediate steps went
        // through, and is journaled with its cue's name and its place in it.
        Tail.Run = step =>
        {
            var mapped = step.Action.ToAction();
            // Follow: the origin a cue's own later step already has — it was not pressed, the cue
            // said it would happen, and the gate that refuses a remote's GO must not refuse this.
            // The step runs in its cue's hand: a device line it sends carries the cue's execution.
            Actions.CueInHand = step.Label;
            Actions.ExecutionInHand = step.ExecutionId;
            ActionResult result;
            try { result = Actions.Execute(mapped, ActionOrigin.Follow); }
            finally
            {
                Actions.CueInHand = "";
                Actions.ExecutionInHand = "";
            }
            Journal.Record(ActionOrigin.Follow.Label, mapped.Kind.ToString(), mapped.Target, result.Status.ToString(),
                $"{step.Label}: step {step.Number} of {step.Of} — {result.Message}");
            Notify($"{step.Label}: step {step.Number} of {step.Of} — {result.Message}");
            // The step's outcome onto its cue's row: a receipt awaited, or a step that failed after the GO.
            CueStack?.TailStep(step.ExecutionId, step.Number, step.Of, result, mapped.Kind == ShowActionKind.DeviceSend);
        };
        CueStack = new CueStackService(Kernel, this);
        // A box's receipt settles the cue that sent the line — that row and no other.
        Devices.Receipt += CueStack.OnDeviceReceipt;
        // Standby moved (or the cue's look was edited): the pool opens the new standby's clips now, not at GO.
        CueStack.Changed += ReconcileInputs;
        CueStack.Changed += FollowPlan;
        Stage = new StageService(this);
        Kernel.Air = this;                                       // the beacon packet and the nodes page read the desk's air from here on
        GpuService.RecordAppliedPath(State);

        _saveTimer = global::Patterns.App.Services.DeskTimers.Make(TimeSpan.FromMilliseconds(900));
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            if (_shutDown) return;
            SaveInBackground();
        };

        _reapplyTimer = global::Patterns.App.Services.DeskTimers.Make(TimeSpan.FromMilliseconds(250));
        _reapplyTimer.Tick += (_, _) =>
        {
            _reapplyTimer.Stop();
            if (Outputs.IsLive) Outputs.Apply();
        };

        // The watch on the live show tracks which sections each edit lands in, so a publish
        // copies those and shares the rest with the snapshot before (see SnapshotClone). A write to
        // the desk's own chrome — every [JsonIgnore] property: a tally, a status line, a device's
        // counters — is told apart and never publishes (see OnRuntimeOnlyChanged).
        StateWatch = new ChangeTracker(State, OnStateChanged, trackSections: true, onRuntimeOnlyChanged: OnRuntimeOnlyChanged);
        // The sections a publish of the live show named dirty, kept for the side effects that follow it.
        Bus.SectionsPublished += (root, dirty) =>
        {
            if (!ReferenceEquals(root, State)) return;
            _dirtyPublished = dirty;
            _dirtyCaptured = true;
        };

        Screens.PlannedProvider = PlannedScreens;
        Screens.Changed += () =>
        {
            HotPlug.OnScreensChanged();    // first: a display that re-indexed keeps its screen, one unplugged leaves its screen waiting
            var moved = SyncDisplays();
            Outputs.OnScreensChanged();
            if (moved) PublishRuntime();   // a hot-plug moves no model: push the new shapes ourselves
        };
        Outputs.LiveChanged += UpdateRecovery;
        Outputs.LiveChanged += ShowLock.OnOutputsLiveChanged;   // the machine held with the outputs, released with them
        // The screens change hands the moment they open or close, not at the next poll: a start a
        // second later must never read a record for windows that are already gone.
        Outputs.LiveChanged += Ownership.OnLiveChanged;
        // A record that cannot be kept is said on the status line and the health line as it starts and as it clears.
        Ownership.TroubleChanged += words =>
        {
            if (words.Length > 0)
            {
                Notify(words);
                HealthMonitor.WatchdogNote = HealthMonitor.WatchdogNote.Length > 0 ? HealthMonitor.WatchdogNote + " · " + words : words;
            }
            else
            {
                Notify("The screens' ownership record is being kept again.");
            }
        };
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
        // A node never opens an output: the hold the window manager and OUTPUTS ON both honour is on from the first second.
        if (!IsDesk) OutputsHeldBy = NodeKinds.HoldWords(Profile);
        if (Profile == NodeKind.Arcade) Arcade.Start();          // the game is the node's window from its first frame
        Screens.Refresh(); // planned screens exist before any display is attached
        Startup.Mark(StartupBudget.Services);
        Log.Info(Modules.Words());                                                              // what the desk loaded, on record at every start
        // The desk's first frame is the budget's last mark; a pipeline tells it once.
        _firstPreviewFrame = () => Startup.Mark(StartupBudget.FirstFrame);
        Rendering.RenderPipeline.FirstPreviewFrame = _firstPreviewFrame;
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
        ShowLock.RestoreAfterCrash();
        MainWindow = window;
        Startup.Mark(StartupBudget.Pages);   // the window's XAML is built by now
        window.Opened += (_, _) =>
        {
            Startup.Mark(StartupBudget.Window);
            Screens.Attach(window);
            ApplySideEffects(null);       // the boot: everything follows the show as loaded
            _windowOpened = true;
            if (_recoverVm is { } vm)
            {
                _recoverVm = null;
                // The screens are known and the side effects applied: the show goes back on as
                // soon as the desk has drawn, not after a timer's guess at how long that takes.
                UiThread.Post(() => TryRecover(vm), DispatcherPriority.Background);
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
            UiThread.Post(() => TryRecover(vm), DispatcherPriority.Background);
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
    /// <summary>A publish asked for outright — a rig change, the sandbox opening or closing, a capture format picked: everything is published and every side effect runs, as before there was a mask.</summary>
    public void RepublishNow()
    {
        _forceAllEffects = true;
        try
        {
            OnStateChanged();
        }
        finally
        {
            _forceAllEffects = false;
        }
    }

    /// <summary>What the side effects cost: passes, the systems that ran and skipped, the worst. The Machine page's line and the assistant's brief read it.</summary>
    public ReconcileBudget Reconciles { get; } = new();

    private HashSet<string>? _dirtyPublished;
    private bool _dirtyCaptured;
    private bool _forceAllEffects;

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
        if (!IsDesk) return;                                   // a node has no preview to keep safe
        if (State.Switcher.EditSafeByDefault && !Sandbox.Active)
        {
            Sandbox.Enter();
        }
    }

    /// <summary>
    /// A runtime-only property moved — the audio player's playing flag, a device's line counters,
    /// a look's tally text, a status line. None of it reaches a snapshot, so nothing publishes: a
    /// publish for it used to hand every sink a picture identical to the one it had, run every
    /// side effect, restart the autosave, and — the real harm — spend the snapshot version a
    /// look's own wipe or fade was riding on, so a device streaming readings after a recall turned
    /// the recall's transition into a plain switch. What such a change does move is the recovery
    /// record (the audio and the stream are in it) and the remotes' STATE, which read the live show.
    /// </summary>
    private void OnRuntimeOnlyChanged()
    {
        if (_bulkDepth > 0 || _deskDepth > 0) return;
        UpdateRecovery();
        RaiseSafely(RuntimeChanged, "a runtime-change listener");
    }

    /// <summary>
    /// Raised on the UI thread after a runtime-only property moved without a publish — the
    /// feedback surfaces (the wire's STATE, OSC, the devices) listen to this beside
    /// <see cref="SnapshotPublished"/>, because their state text reads the live show and a flag
    /// like "audio playing" is in it.
    /// </summary>
    public event Action? RuntimeChanged;

    private void OnStateChanged()
    {
        // A closed desk publishes nothing: a queued edit that lands after Shutdown (a key made
        // for the twin, a device's last line) would otherwise re-open what Shutdown closed —
        // the twin's listener, its beat — and root the desk for good (round 64's census).
        if (_shutDown) return;
        if (_bulkDepth > 0 || _deskDepth > 0) return;

        SyncDisplays();
        _dirtyPublished = null;
        _dirtyCaptured = false;
        if (Sandbox.Active)
        {
            Sandbox.PublishBoth(); // outputs stay on the frozen program; preview follows the edits
        }
        else
        {
            Bus.Publish(State, StateWatch);
        }
        // The sections this publish named, or everything when it could not name them (the first
        // publish, a show copied in whole, a republish asked for outright).
        var dirty = _forceAllEffects || !_dirtyCaptured ? null : _dirtyPublished;
        ApplySideEffects(dirty);

        Outputs.NotifySnapshot();
        RaiseSafely(SnapshotPublished, "a snapshot listener");

        if (Outputs.IsLive)
        {
            ArmReapply();
        }

        ArmSave();

        UpdateRecovery();
    }

    private (bool Live, bool Audio, bool Sandboxed, long Air)? _recoveryWritten;
    private bool _restartRequested;
    private volatile bool _handedOver;
    private bool _recoveryPending;
    private long _airVersion;
    private ChangeTracker? _airWatch;

    /// <summary>The watch on the live show: what each edit touched, for the publish that follows it.</summary>
    internal ChangeTracker StateWatch { get; }

    /// <summary>The watch on the frozen program while EDIT SAFE is open (null otherwise): what each air edit touched.</summary>
    internal ChangeTracker? AirWatch => _airWatch;

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
        _airWatch = program is null ? null : new ChangeTracker(program, () => _airVersion++, trackSections: true, onRuntimeOnlyChanged: static () => { });
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
        Persistence.AwaitPending();     // a record on the lane must not land over the one written here
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
            QueueRecoveryWrite(place);
            _recoveryPending = false; // the file is this run's now
        }
        else if (!_recoveryPending)
        {
            Persistence.ClearRecovery();                                 // behind the writes, in order: a clear never races a write
            RaiseSafely(() => RecoveryMoved?.Invoke(null), "the recovery record's listener");
        }
    }

    /// <summary>
    /// The recovery record made and written on the file lane. The desk's thread takes what will
    /// not change under a worker — the frozen program the room is seeing, the pinned look's JSON,
    /// the flags, the lists copied here — and the worker clones the air, applies the pin,
    /// serialises the record and writes it whole. It used to serialise a whole show three times
    /// on the desk's thread on every GO. A record a newer one overtakes is skipped; the twin is
    /// told the record moved once it is on the disk.
    /// </summary>
    private void QueueRecoveryWrite(RunPlace? place)
    {
        var live = Outputs.IsLive;
        var audio = State.AudioPlayer.Playing;
        var sandboxed = Sandbox.Active;
        var pinned = _pinnedAirLook;
        var airSource = sandboxed || pinned is { Length: > 0 } ? Bus.Current.State : null;   // the program as published: what the audience is seeing, whole
        var black = Bus.BlackTargets.Count == 0 ? null : Bus.BlackTargets.ToList();
        var streaming = State.Stream.Active;
        var airLabel = AirLabel;
        var airLookId = AirLookId;
        var previousAirLookId = PreviousAirLookId;
        var previewLookId = PreviewLookId;
        var files = Files;
        Persistence.WriteRecovery(() =>
        {
            var t = System.Diagnostics.Stopwatch.GetTimestamp();
            ShowState? air = null;
            if (airSource is not null)
            {
                try
                {
                    air = JsonUtil.Clone(airSource);
                    if (pinned is { Length: > 0 }) LookService.Apply(pinned, air);
                }
                catch (Exception ex)
                {
                    Log.Warn("Air capture for recovery failed.", ex);
                }
            }
            var record = new RecoverySnapshot(live, audio, DateTime.UtcNow, AirLook: null, Run: place, Sandboxed: sandboxed, Air: air,
                BlackTargets: black, Streaming: streaming, AirLabel: airLabel, AirLookId: airLookId, PreviousAirLookId: previousAirLookId, PreviewLookId: previewLookId);
            var json = RecoveryStore.Serialize(record);
            files.Record(FileBudget.RecoverySerialise, MsSince(t));
            return (record, json);
        }, record => UiThread.Post(() => RaiseSafely(() => RecoveryMoved?.Invoke(record), "the recovery record's listener")));
    }

    /// <summary>
    /// The recovery record as it was last written, or null as it was cleared — on the UI thread,
    /// only when it moved. The twin link sends it to a standby: what is on air, whether the desk is
    /// split, the caller's place — everything a takeover puts back.
    /// </summary>
    public event Action<RecoverySnapshot?>? RecoveryMoved;

    /// <summary>
    /// The twin mirrored the show, or the sections named, onto this desk's state in place (UI
    /// thread, after the publish). The desk refreshes the lists that read those sections.
    /// </summary>
    public event Action<IReadOnlyCollection<string>?>? ShowMirrored;

    public void NotifyShowMirrored(IReadOnlyCollection<string>? sections)
        => RaiseSafely(() => ShowMirrored?.Invoke(sections), "the mirror's listener");

    /// <summary>The caller's place goes to the sidecar on every GO, atomically, live or not.</summary>
    /// <summary>The caller's place onto the record, on the file lane: a GO used to clone and serialise the whole show on the desk's thread before the cue's own work was over.</summary>
    public void WriteRunPlace()
    {
        if (_restartRequested || _handedOver) return;
        _recoveryWritten = RecoveryKey();
        QueueRecoveryWrite(CueStack.Place());
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
            // The record of who has the screens could not be read: a fence. A restart that put the
            // show back by itself could open a second set of windows behind a desk still playing,
            // so nothing opens here; the operator has the words and OUTPUTS ON.
            if (Takeover.Uncertain)
            {
                vm.StatusMessage = Takeover.Words;
                Log.Warn($"Recovery held: {Takeover.Words}");
                return;
            }
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

            RestoreRecord(was, took, vm);
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
    /// The record back on this desk: the split as it was, the program on the outputs, the screens
    /// faded on their own, the audio, what the desk calls the picture, the caller's place. The
    /// watchdog's relaunch, a start that took the screens back, and a twin taking the show over all
    /// end here — one way of putting a show back, so none of them can put back a different one.
    /// </summary>
    private void RestoreRecord(RecoverySnapshot was, bool took, ViewModels.MainViewModel vm, string? headOverride = null)
    {
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
            var head = headOverride ?? (Takeover.TookOver
                ? Takeover.Words
                : restored
                    ? $"{who} — the show was put back on."
                    : $"{who}.");
            vm.StatusMessage = head + streamNote;
            if (was.Run is { } place)
            {
                // The caller's place: disarmed, pointing at the next cue, nothing fired. A
                // takeover's words lead the banner — the one sentence saying the windows in the
                // room are this desk's now, not the ghost's, must not be pushed off the strip by
                // the surface the recovery itself opens.
                var lead = Takeover.TookOver || headOverride is not null ? head + " " : "";
                RecoveryBanner = lead + CueStack.RestorePlace(place) + streamNote;
                vm.StatusMessage = RecoveryBanner;
                vm.IsRunLayout = true;
            }
        Log.Info(vm.StatusMessage);
    }

    /// <summary>
    /// The standby twin took the show: the air record the main sent last goes back on here, the way
    /// a restart puts it back — or, with none (nothing was live at the main), the outputs stay closed
    /// and the desk says so.
    /// </summary>
    public void RecoverFromTwin(RecoverySnapshot? was, string head, string peer = "the main")
    {
        var vm = MainWindow?.DataContext as ViewModels.MainViewModel;
        if (was is null || vm is null)
        {
            var words = head + $" Nothing was on air at {peer} — the outputs stay closed until OUTPUTS ON.";
            Notify(words);
            Log.Info(words);
            return;
        }
        try
        {
            RestoreRecord(was, took: false, vm, head);
        }
        catch (Exception ex)
        {
            Log.Error("Taking the show over from the twin failed.", ex);
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

    // ---- the capabilities the kernel's services see of the desk -------------------------------

    /// <summary>
    /// The desk's states for the brief, read from the services at the moment of the ask: EDIT
    /// SAFE and the two states, the outputs, the canvases and what every screen shows, the cue
    /// on standby, the inputs mounted (by nickname and kind, never a path), the library's names,
    /// the sound, the lower third on air, the sends and the stream. Never throws: a service that
    /// cannot answer leaves its line at the default, and the ask goes out with the rest.
    /// </summary>
    public ShowFacts GatherFacts()
    {
        var s = this;
        var state = s.State;
        var air = s.AirState;
        var canvases = new List<CanvasFact>();
        var shows = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var geo = Rig.Geometry(state, s.Screens.All);
            var infos = new Dictionary<string, ScreenInfo>(StringComparer.Ordinal);
            foreach (var info in s.Screens.All) infos[info.Id] = info;
            string LabelOf(string id)
            {
                var placement = state.Output.Placements.FirstOrDefault(p => p.ScreenId == id);
                return placement is null ? id : Rig.LabelFor(placement, infos.GetValueOrDefault(id));
            }
            foreach (var target in geo.Targets)
            {
                if (!ContentTargets.IsCanvasKey(target)) continue;
                var members = geo.MembersOf(target);
                var size = geo.SizeOf(target);
                var name = state.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == target)?.Name ?? "";
                canvases.Add(new CanvasFact(geo.LetterOf(target), name, size.Width, size.Height, members.ToList(), members.Select(LabelOf).ToList()));
            }
            foreach (var p in state.Output.Placements)
            {
                string words;
                if (p.MirrorOf.Length > 0) words = "a repeater of " + (ContentTargets.IsCanvasKey(p.MirrorOf) ? "canvas " + geo.LetterOf(p.MirrorOf) : LabelOf(p.MirrorOf));
                else if (!p.Enabled) words = "nothing (off)";
                else
                {
                    var target = geo.TargetOf(p.ScreenId);
                    var own = ContentTargets.UsesOwnPattern(air, target) ? air.Independent.FirstOrDefault(a => a.ScreenId == target)?.Pattern : null;
                    var picture = own is null ? "the program" : "its own picture: " + ShowBrief.PatternWords(own);
                    words = ContentTargets.IsCanvasKey(target) ? $"canvas {geo.LetterOf(target)} with {picture}" : picture;
                }
                shows[p.ScreenId] = words;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The assistant's brief could not read the rig.", ex);
        }

        var inputs = new List<string>();
        try
        {
            foreach (var key in InputBus.Keys)
            {
                var kind = key.StartsWith("cap:", StringComparison.Ordinal) ? "a capture input"
                    : key.StartsWith("ndi:", StringComparison.Ordinal) ? "an NDI feed"
                    : key.StartsWith("web:", StringComparison.Ordinal) ? "a web page"
                    : key.StartsWith("deck:", StringComparison.Ordinal) ? "a deck"
                    : key.StartsWith("vid:", StringComparison.Ordinal) ? "a clip"
                    : "a source";
                var label = state.InputLabel(key, "");
                var held = Residency.ReasonWords(key);                                          // round 69: why it is in memory — on air, the preview's, pre-rolled, armed
                var reason = held.Length > 0 ? ", " + held : "";
                inputs.Add(label.Length > 0 ? $"{label} ({kind[2..]}{reason})" : kind + reason);   // the nickname and the kind, never the key's path or address
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The assistant's brief could not read the inputs.", ex);
        }

        var standby = s.CueStack.StandbyCue;
        var last = s.CueStack.LastCue;
        var audioNow = "";
        try
        {
            audioNow = s.AudioPlayer.NowPath.Length > 0 ? $"playing '{s.AudioPlayer.CurrentName}'" : state.AudioPlayer.Items.Count + state.AudioPlayer.Folders.Count > 0 ? "stopped" : "";
        }
        catch (Exception ex)
        {
            Log.Warn("The assistant's brief could not read the audio player.", ex);
        }
        var lowerThird = air.LowerThirds.IsShowing ? air.LowerThirds.Active?.Name ?? "" : "";
        var (health, attention) = DeskHealthWords();
        return new ShowFacts
        {
            EditSafeOpen = s.Sandbox.Active,
            Air = s.Sandbox.Active ? air : null,
            AirLabel = s.AirLabel == "—" ? "" : s.AirLabel,   // the strip's dash is "no look named", not a name
            PreviewLook = s.PreviewLookId.Length > 0 ? LookService.Find(state, s.PreviewLookId)?.Name ?? "" : "",
            EditingTarget = Assistant.EditingTarget,
            OutputsLive = s.Outputs.IsLive,
            OutputWindows = s.Outputs.Windows.Count,
            ShowLock = s.ShowLock.Status,
            Canvases = canvases,
            ScreenShows = shows,
            StackArmed = s.CueStack.Armed,
            StandbyCue = standby is null ? "" : $"{standby.Number} {standby.Name}".Trim(),
            LastCue = last is null ? "" : $"{last.Number} {last.Name}".Trim(),
            InputsMounted = inputs,
            MediaFiles = state.MediaLibrary.Count,
            MediaNames = state.MediaLibrary.Where(m => m.Name.Trim().Length > 0).Select(m => m.Name.Trim()).ToList(),
            AudioNow = audioNow,
            VogOnAir = s.Stingers.VogOnAir,
            StingOnAir = s.Stingers.StingOnAir,
            LowerThirdOnAir = lowerThird,
            NdiSendsRunning = s.Ndi.ActiveCount,
            StreamStatus = s.Stream.Status,
            Health = health,
            Attention = attention,
            Signals = SignalLinesForBrief(),
            Machine = MachineLinesForBrief(),
            Commissioning = CommissioningLinesForBrief(),
            Eye = EyeLinesForBrief(),
        };
    }

    /// <summary>The commissioning flow's lines for the brief; nothing when the desk cannot read them.</summary>
    private IReadOnlyList<string> CommissioningLinesForBrief()
    {
        try
        {
            return Actions.CommissioningBriefLines();
        }
        catch (Exception ex)
        {
            Log.Warn("The assistant's brief could not read the commissioning flow.", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>The God's Eye's lines for the brief (round 66) — the desk's picture, never a node's; nothing when the desk cannot read it.</summary>
    private IReadOnlyList<string> EyeLinesForBrief()
    {
        if (!IsDesk) return Array.Empty<string>();
        try
        {
            return Eye.BriefLines();
        }
        catch (Exception ex)
        {
            Log.Warn("The assistant's brief could not read the God's Eye.", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>The machine's lines and the known-good verdict for the brief; nothing when the desk cannot read them.</summary>
    private IReadOnlyList<string> MachineLinesForBrief()
    {
        try
        {
            return Actions.MachineBriefLines();
        }
        catch (Exception ex)
        {
            Log.Warn("The assistant's brief could not read the machine.", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>The screens' signal lines for the brief; nothing when the desk cannot read them.</summary>
    private IReadOnlyList<string> SignalLinesForBrief()
    {
        try
        {
            return Actions.SignalBriefLines();
        }
        catch (Exception ex)
        {
            Log.Warn("The assistant's brief could not read the signal truth.", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// How this desk is doing, in the words its Machine page reads: the health line with the
    /// glance's facts, the twin's line when it has something to say, the desk tick, the page
    /// switch, the GO to frame, the render frame — and the super-check's rows that are not green,
    /// with the advice each carries. For the assistant's brief; nothing that names the machine.
    /// </summary>
    public (IReadOnlyList<string> Health, IReadOnlyList<string> Attention) DeskHealthWords()
    {
        var health = new List<string>();
        var attention = new List<string>();
        try
        {
            var glance = GlanceWords;
            health.Add("Health: " + HealthMonitor.Summary(DateTime.UtcNow) + (glance.Length > 0 ? " · " + glance : ""));
            var twin = Twin.HealthWords;
            if (twin.Length > 0) health.Add("Twin: " + twin);
            health.Add(DeskTick.Describe());
            health.Add(Reconciles.Describe());
            health.Add(Files.Describe());
            health.Add("Memory: " + Metrics.MemoryCeilingLine());
            health.Add(Switches.Describe());
            health.Add(CueStack.GoClock.Describe());
            health.Add(FrameBudgets.Describe(ShowClock.Seconds));
            var report = SuperCheck.Run(Metrics.GatherFacts());
            foreach (var row in report.Rows)
            {
                if (row.Light is not (CheckLight.Amber or CheckLight.Red)) continue;      // grey is unknown, not a warning
                attention.Add($"{row.Item}: {row.Value}{(row.Note.Length > 0 ? " — " + row.Note : "")}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The assistant's brief could not read the desk's health.", ex);
        }
        return (health, attention);
    }


    /// <summary>The outputs are open: the beacon's LIVE, the twin's hold.</summary>
    public bool OutputsLive => Outputs.IsLive;

    /// <summary>The glance line's facts from this desk's services: the twin, the screens the room is short, the last box that said no, the lock.</summary>
    public string GlanceWords
    {
        get
        {
            var parts = new List<string>();
            var twin = Twin.GlanceWords;
            if (twin.Length > 0) parts.Add(twin);
            var lost = HotPlug.LostScreens.Count;
            if (lost > 0) parts.Add(lost == 1 ? "1 SCREEN MISSING" : $"{lost} SCREENS MISSING");
            var device = Devices.HealthWords;
            if (device.Length > 0) parts.Add(device);
            if (Outputs.IsLive) parts.Add(ShowLock.Locked ? "LOCK ON" : "LOCK OFF");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The countdown that follows the running order: on every move of the stack — a GO, a standby
    /// moved, a slip, a resume, a catch-up — its target is the standby cue's planned start, as a time
    /// of day, so the speaker timer, the stage display and the info screen keep the caller's one
    /// clock. A standby cue with no planned start leaves the countdown where it was.
    /// </summary>
    /// <summary>COUNTDOWN FOLLOW ON: the target from this moment, not the next move of the stack.</summary>
    public void FollowPlanNow() => FollowPlan();

    private void FollowPlan()
    {
        var countdown = AirState.Countdown;
        if (!countdown.FollowPlan) return;
        var start = CueStack.StandbyCue?.PlannedStart ?? "";
        if (!CountdownService.TryParseTime(start, out _)) return;
        if (countdown.TargetKind == CountdownTargetKind.TimeOfDay && countdown.TargetTime == start && countdown.Enabled) return;
        EditAir(air =>
        {
            air.Countdown.TargetKind = CountdownTargetKind.TimeOfDay;
            air.Countdown.TargetTime = start;
            air.Countdown.Enabled = true;
        });
    }

    /// <summary>The twin's hold: every output window closed, whatever they were showing.</summary>
    public void CloseOutputs() => Outputs.CloseAll();

    /// <summary>The stack is armed — for the beacon packet.</summary>
    public bool Armed => CueStack?.Runtime.Armed ?? false;

    /// <summary>"03.020 Five-minute call": the standby cue, for the beacon packet; "" with none.</summary>
    public string StandbyWords => CueStack?.StandbyCue is { } standby ? $"{standby.Number} {standby.Name}".Trim() : "";

    /// <summary>The last cue's number, for the beacon packet; "" with none.</summary>
    public string LastCueNumber => CueStack?.LastCue?.Number ?? "";

    /// <summary>The picture's rate: the outputs' when they are open, else the preview's — for the beacon packet.</summary>
    public double Fps => Metrics.Current is { } m ? Math.Round(m.OutputWindows > 0 ? m.OutputFps : m.PreviewFps, 1) : 0;

    /// <summary>How many output windows are open — for the beacon packet.</summary>
    public int Windows => Metrics.Current?.OutputWindows ?? 0;

    /// <summary>The audience port's addresses, from the wire.</summary>
    public IReadOnlyList<string> AudienceUrls() => Control.AudienceUrls();

    public bool AudienceListening => Control.AudienceListening;

    public int AudienceConnections => Control.AudienceConnections;

    /// <summary>A router over this desk's action layer: every wire (TCP, HTTP, OSC, a device, the management server) dispatches through one.</summary>
    public CommandRouter NewRouter() => new(this);

    // The contracts' view of the desk's own members: the same objects, typed as the capability.
    IActionLayer ITwinHost.Actions => Actions;

    long ITwinHost.DeviceMark() => Devices.Mark();

    int ITwinHost.DeviceSentSince(long mark) => Devices.SentSince(mark);

    Task<IReadOnlyList<DeviceReceipt>> ITwinHost.DeviceConfirmSince(long mark) => Devices.ConfirmSince(mark);
    IActionLayer IWireHost.Actions => Actions;
    IActionLayer IRunHost.Actions => Actions;
    IReadOnlyList<ScreenInfo> IRunHost.Screens => Screens.All;

    // The cue stack's host: a cue's steps run for real here, through the action layer; the sidecar
    // services it watches for a late failure; rig day's streak while the games are on.
    ActionResult ICueHost.RunCue(CueStackConfig stack, RunCueConfig cue, ActionOrigin origin) => Actions.RunCue(stack, cue, origin);

    IEnumerable<string> ICueHost.WatchedStatuses() => new[] { Stream.Status, AudioPlayer.Status, Stingers.Status, Spotify.CommandFailure };

    void ICueHost.RecordGo(TimeSpan? offset)
    {
        if (RigDay.Enabled) RigDay.RecordGo(offset);
    }

    IReadOnlyList<PreRoll.State> IRunHost.PreRollStates(IReadOnlyList<MediaLocator.WantedInput> wants) => Video.PreRollStates(wants);

    string IRunHost.BreakMusicWords => Spotify.NowPlaying.Length > 0
        ? Spotify.NowPlaying + (Spotify.DeviceLabel.Length > 0 ? " — " + Spotify.DeviceLabel : "")
        : "";

    (bool Holding, string Name) IRunHost.StingHold => (Stingers.Holding, Stingers.HoldName);

    void IPlayHost.StartWall() => Arcade.Start();

    IRouter IMachineHost.NewRouter() => NewRouter();
    IActionLayer IMachineHost.Actions => Actions;
    CueStackService? IWireHost.CueStack => CueStack;
    InstallService? IWireHost.Install => Install;
    ManagementService? IWireHost.Management => Management;
    UpdateService? IWireHost.Updates => Updates;
    StageService? IWireHost.Stage => Stage;
    IRouter IWireHost.NewRouter() => NewRouter();

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
            var missing = HotPlugWatch.IsLost(p);
            yield return new ScreenInfo(
                p.ScreenId,
                p.CustomLabel.Length > 0 ? p.CustomLabel : p.IsVirtual ? p.VirtualKind : missing ? HotPlugWatch.LostName(p) : "Planned screen",
                new Avalonia.PixelRect(p.X, p.Y, p.PlannedWidth, p.PlannedHeight),
                1.0, false, 0, IsPlanned: true, IsVirtual: p.IsVirtual, IsMissing: missing);
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

    /// <summary>
    /// The systems that follow the show, each run only when a section it reads moved — the
    /// change mask (<see cref="SideEffectDomains"/>): a lower third's text does not make the
    /// room's boxes, the wire, the beacon or the twin look at themselves, and a box's address
    /// does not make the decoders. Null is everything. Each run is timed into the budget; a
    /// skip is counted too, so the Machine page says what the mask saved.
    /// </summary>
    private void ApplySideEffects(IReadOnlySet<string>? dirty)
    {
        var passStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var ran = 0;
        var skipped = 0;
        Effect("rig", SideEffectDomains.Rig, dirty, SyncPlannedScreens, ref ran, ref skipped);

        if (IsDesk)
        {
            // NDI sender set follows the config.
            Effect("ndi out", SideEffectDomains.NdiOut, dirty, () => Ndi.Reconcile(Bus.Current), ref ran, ref skipped);

            // The live-input pool follows everything the program (and sandbox) references.
            Effect("inputs", SideEffectDomains.Inputs, dirty, ReconcileInputs, ref ran, ref skipped);
            Effect("audio graph", SideEffectDomains.AudioGraph, dirty, () => AudioGraph?.Reconcile(), ref ran, ref skipped);
            // The room's boxes and OSC are the desk's to drive.
            Effect("osc", SideEffectDomains.Osc, dirty, Osc.Reconcile, ref ran, ref skipped);
            Effect("devices", SideEffectDomains.Devices, dirty, Devices.Reconcile, ref ran, ref skipped);
        }
        // A node keeps the wire (its own pages and verbs), the beacon (so it is found) and the
        // twin (its link to the desk); never a sender, a decoder or a device — a mirrored show's
        // clips are the desk's to open, not a caller's.
        Effect("wire", SideEffectDomains.Wire, dirty, Control.Reconcile, ref ran, ref skipped);
        Effect("beacon", SideEffectDomains.Beacon, dirty, Beacon.Reconcile, ref ran, ref skipped);
        Effect("mdns", SideEffectDomains.Mdns, dirty, Kernel.Mdns.Reconcile, ref ran, ref skipped);
        Effect("twin", SideEffectDomains.Twin, dirty, Twin.Reconcile, ref ran, ref skipped);
        Reconciles.Pass(System.Diagnostics.Stopwatch.GetElapsedTime(passStart).TotalMilliseconds, ran, skipped, SideEffectDomains.Words(dirty), full: dirty is null);
    }

    /// <summary>One system of the pass: run and timed when a section it reads moved, counted as skipped otherwise.</summary>
    private void Effect(string name, IReadOnlyList<string> reads, IReadOnlySet<string>? dirty, Action run, ref int ran, ref int skipped)
    {
        if (!SideEffectDomains.Touches(dirty, reads))
        {
            Reconciles.Skipped(name);
            skipped++;
            return;
        }
        var at = System.Diagnostics.Stopwatch.GetTimestamp();
        run();
        Reconciles.Ran(name, System.Diagnostics.Stopwatch.GetElapsedTime(at).TotalMilliseconds);
        ran++;
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
        // The web pages the cues ahead ask to play from a point — the caller's standby cue and the
        // clicker's next step — open early, prepared at their mark, so the take lands on the frame.
        WebIn.Reconcile(Bus.Current, Bus.Sandbox, CueStack is null ? null : PreRoll.WebPagesFor(State, CueStack.StandbyCue, ClickerNextCue()));
        DeckIn.Reconcile(Bus.Current, Bus.Sandbox);
        ArcadeIn.Reconcile(Bus.Current, Bus.Sandbox);
        AudioGraph?.Reconcile();   // the taps may have come or gone with the mounts: the plan follows them now, not at the next second
    }

    /// <summary>The clicker list's next step — the cue NEXT would run — or null at the end (or with no list).</summary>
    public RunCueConfig? ClickerNextCue()
    {
        // Found, never made: this runs on every publish, and making the list here would edit the
        // show inside its own change event — and on a standby, edit a section the main owns.
        var clicker = State.Stacks.FirstOrDefault(s => s.IsClicker);
        if (clicker is null || clicker.Cues.Count == 0) return null;
        var rt = Cues.For(clicker);
        var next = PresenterLogic.Advance(rt.CurrentIndex, clicker.Cues.Count, +1, clicker.LoopAtEnd);
        return next is { } idx && idx >= 0 && idx < clicker.Cues.Count ? clicker.Cues[idx] : null;
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
    /// <summary>What a deck's keys read that no publish carries: the nodes heard, the twin's phase and holder, the stage's waiting messages, the decks connected.</summary>
    public string DeckSignature() => $"{Nodes.Rev}|{Twin.Phase}|{Twin.Holder}|{Stage.DeckSignature()}|{Control.Decks.Count}";

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
            Bus.Publish(State, StateWatch);
        }
        Outputs.NotifySnapshot();
        RaiseSafely(SnapshotPublished, "a snapshot listener");
    }

    /// <summary>
    /// The show to disk now, on this thread: a restart, the exit, the write-back after a migration
    /// — and the tests. Any autosave still on its way to the disk lands first, so the file always
    /// ends on the newest show.
    /// </summary>
    public void SaveNow()
    {
        if (!_autosave) return;
        // A clip on the screens (or a sting holding them) is a momentary event, never what the show
        // is. Writing now would reopen the file on a dead clip; the revert — or Shutdown's
        // Stingers.Dispose(), which stops first — writes the real content a moment later.
        // Null-safe on purpose: SaveNow also runs at startup, before Stingers exists.
        if (Stingers is { OwnsScreens: true })
        {
            ArmSave();
            return;
        }
        Persistence.SaveNow(State);
    }

    /// <summary>What the show's files cost, phase by phase, and the saves a newer one made unnecessary. The Machine page's line and the assistant's brief read it.</summary>
    public FileBudget Files { get; } = new();

    /// <summary>One step on the show's file lane (<see cref="Persistence"/>), behind everything queued before it: the quality profile's write rides it too.</summary>
    public void QueueFileWork(string what, Action work) => Persistence.Queue(what, work);

    private static double MsSince(long timestamp) => PersistenceRuntime.MsSince(timestamp);

    /// <summary>
    /// The autosave, off the frame budget: nothing of it runs on the desk's thread but taking the
    /// show the last publish froze — immutable by construction, the very object the sinks draw —
    /// so no worker ever reads the live model. The serialising and the disk go to the file lane,
    /// and a save a newer save overtakes before it runs is skipped: five edits in a second are
    /// one serialisation and one write, of the latest show. A show file on a USB stick or a
    /// network share holds nobody.
    /// </summary>
    public void SaveInBackground()
    {
        if (!_autosave) return;
        if (Stingers is { OwnsScreens: true })
        {
            ArmSave();
            return;
        }
        var at = System.Diagnostics.Stopwatch.GetTimestamp();
        var frozen = Bus.Sandbox?.State ?? Bus.Current.State;         // the show as last published: the sandbox's edits while one is open, the program otherwise
        Persistence.SaveInBackground(frozen, MsSince(at));
    }

    /// <summary>The autosaves still on their way to the disk — complete when the file holds the last of them.</summary>
    public Task PendingSaves => Persistence.Pending;

    private bool _shutDown;

    /// <summary>The way out, once: Avalonia raises ShutdownRequested and then Exit, and the exit used to do all of this twice.</summary>
    /// <summary>
    /// The view models built on this desk, weakly: Shutdown stops their timers whether or not a
    /// window was ever attached — a view model without a window (a headless test's) would
    /// otherwise keep its timers running, and they it (round 64's census).
    /// </summary>
    private readonly List<WeakReference<ViewModels.MainViewModel>> _viewModels = new();

    internal void RegisterViewModel(ViewModels.MainViewModel vm)
    {
        lock (_viewModels) _viewModels.Add(new WeakReference<ViewModels.MainViewModel>(vm));
    }

    private ViewModels.MainViewModel[] ViewModelsAlive()
    {
        lock (_viewModels)
        {
            var alive = new List<ViewModels.MainViewModel>();
            foreach (var w in _viewModels) if (w.TryGetTarget(out var vm)) alive.Add(vm);
            return alive.ToArray();
        }
    }

    /// <summary>
    /// Arms the autosave debounce — never on a closed desk. A change that lands after Shutdown
    /// (a listener's last line, a page's last edit, a device's receipt) must not leave a timer
    /// running: a running timer roots the whole desk, and its tick would save a desk that is
    /// gone (round 64's census).
    /// </summary>
    private void ArmSave()
    {
        if (_shutDown) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Arms the live re-apply debounce — never on a closed desk (see <see cref="ArmSave"/>).</summary>
    private void ArmReapply()
    {
        if (_shutDown) return;
        _reapplyTimer.Stop();
        _reapplyTimer.Start();
    }

    /// <summary>
    /// The shutdown's phases, in this order (round 65): what the room sees and hears ends first;
    /// the desk's authority over outputs, wires and peers next; the machine's own state is put
    /// back; the workers stop; the show is persisted; the recovery record is resolved; the
    /// ownership record is resolved; the process's own handles go last. Every step runs on its own
    /// guard and is written to <see cref="ShutdownReport"/>: a step that throws is logged and the
    /// phases go on, so the critical steps at the end — the show lock, the final save, recovery,
    /// ownership, the mutex — run whatever failed before them.
    /// </summary>
    public static readonly IReadOnlyList<string> ShutdownPhases = new[] { "audience", "authority", "machine", "workers", "persist", "recovery", "ownership", "process" };

    /// <summary>Every step of the last shutdown and how it ended — ran, failed (with the reason), skipped (with the reason) — in the order taken. The fault-injection test reads it; so can the support ticket of a desk that came down badly.</summary>
    public IReadOnlyList<ShutdownStep> ShutdownReport
    {
        get { lock (_shutdownReport) return _shutdownReport.ToArray(); }
    }

    private readonly List<ShutdownStep> _shutdownReport = new();

    /// <summary>Tests: the step named here throws instead of running — the proof that every step after it still runs.</summary>
    public static string? FailShutdownStep { get; set; }

    /// <summary>How long the exit waits for the final save to reach the disk before it goes on without it (round 65).</summary>
    public static TimeSpan ExitSaveWait { get; set; } = TimeSpan.FromSeconds(10);

    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        lock (_shutdownReport) _shutdownReport.Clear();
        // The statics that pointed at this desk let go of it: a closed desk is reclaimed whole (the census counts it).
        if (ReferenceEquals(Rendering.RenderPipeline.FirstPreviewFrame, _firstPreviewFrame)) Rendering.RenderPipeline.FirstPreviewFrame = null;
        if (ReferenceEquals(Instance, this)) Instance = null!;
        foreach (var vm in ViewModelsAlive()) vm.OnWindowClosed();   // the desk's own timers and hooks, whether or not a window was attached or closed first

        // 1. What the room sees and hears ends first: the outputs' pictures, a calibration pattern
        //    on a projector, the stream, NDI, a sting, the music, the room, the games.
        Step("audience", "outputs", Outputs.CloseAll);
        Step("audience", "calibration", Calibration.Shutdown);   // a run in flight ends with the desk: the structured light is process-wide
        Step("audience", "stream", Stream.Dispose);
        Step("audience", "ndi", Ndi.StopAll);
        Step("audience", "stingers", Stingers.Dispose);
        Step("audience", "spotify", Spotify.Dispose);
        Step("audience", "play", Play.Dispose);
        Step("audience", "arcade in", ArcadeIn.Dispose);
        Step("audience", "arcade", Arcade.Dispose);
        // 2. The desk's authority: the standby told, the wire and OSC closed, the devices let go,
        //    the management server down, the beacon's and mDNS's goodbyes said.
        Twin.KeepStandbyOnExit = _restartRequested; // RESTART and UPDATE APPLY bring this desk back in seconds: the standby waits for it
        Step("authority", "twin", Twin.Dispose);
        Step("authority", "control", Control.Dispose);
        Step("authority", "osc", Osc.Dispose);
        Step("authority", "devices", Devices.Dispose);
        Step("authority", "management", Management.Dispose);
        Step("authority", "beacon", Beacon.Dispose);
        Step("authority", "mdns", Kernel.Mdns.Dispose);   // its goodbye and its timer: a running timer roots the kernel, and the kernel this desk (round 64's census)
        // 3. The machine as it was: everything the show lock changed goes back before anything else can fail.
        Step("machine", "show lock", ShowLock.Dispose);
        // 4. The workers: the inputs, the decoders, the audio, the files, the metrics.
        Step("workers", "ndi in", NdiIn.Dispose);
        Step("workers", "web video", () =>
        {
            if (ReferenceEquals(MediaLocator.WebResolver?.Target, WebVideo)) MediaLocator.WebResolver = null;   // this desk's hook, never another's (the tests boot several)
            WebVideo.Dispose();
        });
        Step("workers", "web in", WebIn.Dispose);
        Step("workers", "audio graph", () => AudioGraph?.Dispose());
        Step("workers", "deck in", DeckIn.Dispose);
        Step("workers", "audio", Audio.Dispose);
        Step("workers", "audio player", AudioPlayer.Dispose);
        Step("workers", "playlist", Playlist.Dispose);
        Step("workers", "feeds", Feeds.Dispose);
        Step("workers", "weather", Weather.Dispose);
        Step("workers", "video", Video.Dispose);
        Step("workers", "thumbnails", Thumbnails.Dispose);
        Step("workers", "metrics", Metrics.Dispose);
        Step("workers", "analyser", Analyser.Dispose);
        Step("workers", "tail", Tail.Dispose);
        // 5. Persist: the ladder's profile, then the show — on the file lane, bounded — and the
        //    timers that could arm a save after this stopped.
        Step("persist", "quality profile", Quality.SaveProfileNow);   // where the ladder settled on this machine: next start begins there
        var saved = false;
        Step("persist", "final save", () => saved = SaveAtExit(ExitSaveWait));
        _saveTimer.Stop();      // a save armed by the last publish would fire after the desk is gone — and a running timer roots it (round 64's census)
        _reapplyTimer.Stop();
        // 6. Recovery: a clean exit must never auto-restore — unless the show could not be written,
        //    when the record is the only copy of the last state and stays for the next start.
        if (_restartRequested) Skip("recovery", "recovery cleared", "a restart: the record puts the show back");
        else if (saved) Step("recovery", "recovery cleared", Recovery.Clear);
        else Step("recovery", "recovery kept", () => WatchdogMarker.Write(Store.BaseDirectory, $"The show file could not be written at exit at {DateTime.Now:HH:mm} — the recovery record was kept; the last state of the show is in it (patterns.log has the reason)."));
        // 7. Ownership: the windows went with the outputs above, so the record must go too, or the
        //    next start would hunt for screens that are not playing.
        Step("ownership", "ownership", Ownership.Shutdown);
        // 8. The process's own handles.
        Step("process", "instance mutex", () => _instanceMutex?.Dispose());
    }

    /// <summary>
    /// The final save, at exit, on the file lane and nowhere else (round 65). The show is
    /// serialised here, on the desk's thread, and the write is queued behind whatever autosave
    /// is still on its way; the exit waits a bounded time for it. A save stuck on a dead share used
    /// to be followed by a second, synchronous save on the same lock — an exit that could never
    /// end. Now the exit ends either way: true when the show reached the disk, false when it did
    /// not — and then the recovery record stays, so the next start puts the show back from it.
    /// </summary>
    public bool SaveAtExit(TimeSpan wait) => Persistence.SaveAtExit(State, wait);

    /// <summary>One shutdown step on its own guard: its failure logged, written to the report, and the next step still taken. A test can name a step to fail.</summary>
    private void Step(string phase, string what, Action step)
    {
        try
        {
            if (string.Equals(FailShutdownStep, what, StringComparison.Ordinal)) throw new InvalidOperationException("injected by a test");
            step();
            Record(phase, what, "ran");
        }
        catch (Exception ex)
        {
            Log.Error($"Shutdown: {what} failed.", ex);
            Record(phase, what, "failed: " + ex.Message);
        }
    }

    private void Skip(string phase, string what, string why) => Record(phase, what, "skipped: " + why);

    private void Record(string phase, string what, string outcome)
    {
        lock (_shutdownReport) _shutdownReport.Add(new ShutdownStep(phase, what, outcome));
    }
}

/// <summary>One step of a shutdown as the report tells it (round 65): its phase, its name, and how it ended — "ran", "failed: …" or "skipped: …".</summary>
public readonly record struct ShutdownStep(string Phase, string Step, string Outcome);
