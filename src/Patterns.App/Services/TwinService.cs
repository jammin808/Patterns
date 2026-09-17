using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The twin link. As the main: a port the standbys join; the whole show on welcome, then each
/// section the publish names dirty (trailing 200 ms, like the remote's pushes), the air record
/// whenever it moves, and a beat every second. As the standby: dials the main — the address the
/// operator gave, or the one whose beacon this machine hears — joins with the key, copies what
/// comes onto its own show in place with the outputs held closed, beats back, and takes the show
/// when the main has been silent past the limit and the operator said it may, or when they
/// press TAKE OVER. Sockets on workers, the show on the UI thread, like every wire the desk has;
/// the timing and the words are <see cref="TwinWatch"/>'s, tested without a socket.
/// </summary>
public sealed partial class TwinService : IDisposable, ILinkReport
{
    private readonly ServiceKernel _kernel;
    private readonly ITwinHost _services;
    private readonly object _gate = new();
    private bool _flushScheduled;
    private string _activeKey = "";
    private CancellationTokenSource? _cts;
    private TwinRole _role;

    /// <summary>Round 77: the last attempt to open the twin's port failed (held by the desk this one replaces, usually); the poll asks again.</summary>
    public bool BindFailed { get; private set; }

    // ---- the main's side ----
    private TcpListener? _listener;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "End decides the standby process's fate at exit; Dispose would end it whatever the show's state.")]
    private readonly TwinLauncher _launcher;
    private string _holder = "";            // the standby that has the show — on the link, or marked on disk
    private bool _holderLinked;             // …and on the link: its SHOW and AIR can land here
    private bool _holderMarked;             // …by the marker in the twin-standby folder, its process alive
    private DateTime? _holderSinceUtc;
    private string? _heldShowJson;
    private RecoverySnapshot? _heldAir;
    private int _heldLines;
    private DateTime _markerCheckedUtc;
    private readonly List<Standby> _standbys = new();
    private readonly HashSet<string> _pendingSections = new(StringComparer.Ordinal);
    private bool _pendingWhole;
    private bool _pendingAir;
    private RecoverySnapshot? _air;
    private long _sectionsSent;

    // ---- the standby's side ----
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly object _writeGate = new();
    private TwinPhase _phase;
    private string _mainName = "";
    private TwinWelcome? _welcome;
    private DateTime? _lastHeardUtc;
    private long _sectionsApplied;
    private string _note = "";
    private RecoverySnapshot? _mirroredAir;
    private bool _dialling;
    private DateTime _lastDialUtc;
    private int _dialFailures;
    private long _beat;
    private LinkClock _linkClock = new();        // the main's clock against this machine's, from the beats' stamps; new with every link
    private long _lastMainSentTicks;             // the main's last beat: its stamp, and when it was heard here — echoed in this desk's next beat
    private long _lastMainReceivedTicks;
    private DateTime _nextAutoTakeOverUtc;      // after a takeover by itself was refused: the next try
    private string _dialNonce = "";             // this dial's nonce: the main proves the key over it before this desk answers
    private TwinTransaction? _handover;         // the last handover run here, its stages and where it stopped
    private string _handoverNote = "";          // a take-back stopped at the wall switch: the words until it finishes
    private bool _handoverBusy;                 // a wall switch was asked, or the standby was told to let go, and the answer is awaited: no second handover meanwhile
    private ReleaseWait? _releaseWait;          // a hand-back sent: the standby's RELEASED awaited, or overdue and kept for the next press, its re-dial or its marker
    private SwitchByHand? _switchByHand;        // a take-back across machines whose route is the operator's own: the show is up here, the second press releases
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TakenBack> _takenBack = new(StringComparer.Ordinal);   // instance → the takeover this desk took back and the hand-back that ended it
    private bool _keyBeingMade;

    // ---- callers ----
    private bool _hosting;                                                          // the listener is open for callers (a main, or a desk that accepts them)
    private readonly Dictionary<string, string> _echoSkip = new(StringComparer.Ordinal);   // section → the peer whose edit it was: not sent back to it
    private string? _landing;                                                       // the peer whose edit is landing on the show right now: the publish it raises is its own
    private readonly Dictionary<string, string> _lastLanded = new(StringComparer.Ordinal); // section → the JSON that landed here last: not sent back as an edit
    private string? _myPlanJson;                                                    // a caller's own cues, kept before the desk's show lands, offered once
    private bool _stackHooked;                                                      // the stack's Changed is watched: a disarm lands what waited
    private bool _landingQueued;                                                    // the queue is landing now: a publish it raises must not land it again
    private readonly Dictionary<(string Instance, string Section), (string Caller, string Json)> _queuedSections = new();   // a caller's live edit that arrived while the stack was armed: the last one, landed on DISARM
    private bool _planOffered;
    private TwinLive? _live;                                                        // a caller: the desk's stack as it runs there
    private long _liveSeq;
    private bool _liveHooked;

    /// <summary>The clock the watch reads; the tests move it.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>How a process is seen from outside; the tests answer without a process tree.</summary>
    public IProcessProbe Probe { get; set; } = new SystemProcessProbe();

    /// <summary>The most bytes the first line of a link may run to — a JOIN or a WELCOME — before the peer has proved itself.</summary>
    private const int JoinLineBytes = 64 * 1024;

    /// <summary>The most bytes a line may run to once it has: a whole show as JSON, with room.</summary>
    private const int LinkLineBytes = 64 * 1024 * 1024;

    public TwinService(ServiceKernel kernel, ITwinHost host)
    {
        _kernel = kernel;
        _services = host;
        _launcher = new TwinLauncher(() => Clock());
        _kernel.Bus.SectionsPublished += OnBuilt;
        _services.RecoveryMoved += OnRecoveryMoved;
        EnsureStackHook();
        // Before a window opens: a standby on this machine that took the show while this desk was
        // away still has the screens — this desk's outputs wait on TAKE BACK.
        CheckMarker(force: true);
    }

    /// <summary>The standby process a main runs on this machine; the tests hand it a fake spawner.</summary>
    public TwinLauncher Launcher => _launcher;

    /// <summary>The port a caller node may link on: the twin's, while this desk listens; 0 when it does not.</summary>
    public int LinkPort => _listener is null ? 0 : _kernel.State.Twin.Port;

    /// <summary>The plans callers brought, for the desk's Nodes page: APPLY lands one, DISMISS forgets it.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PlanOffer> Plans { get; } = new();

    /// <summary>This process is a node that follows a desk's show without ever holding an output: a caller, or a stage timer.</summary>
    private bool IsFollowerNode => _kernel.Profile is NodeKind.Caller or NodeKind.Timer;

    /// <summary>This caller or stage timer node is on the link and in step: its verbs go to the desk.</summary>
    public bool IsLinkedToDesk => IsFollowerNode && _phase == TwinPhase.InStep && _stream is not null;

    /// <summary>The desk's stack as it runs there, as a caller last heard it.</summary>
    public TwinLive? Live => _live;

    /// <summary>Follower nodes on the link right now — callers and stage timers.</summary>
    public int CallerCount
    {
        get
        {
            lock (_gate)
            {
                return _standbys.Count(s => s.IsFollower);
            }
        }
    }

    /// <summary>The standby that has the show — "" when none; this desk's outputs are held while it is set.</summary>
    public string Holder => _holder;

    /// <summary>How many SHOW and AIR lines a standby that has the show sent this desk (the tests wait on it).</summary>
    public int HeldLines => _heldLines;

    /// <summary>A restart, not an exit: the standby process stays up and the desk that comes back adopts it — set by the shutdown that knows.</summary>
    public bool KeepStandbyOnExit { get; set; }

    /// <summary>
    /// The beat, once a second for as long as the role's session lasts: a worker waits the second
    /// and asks the UI thread to tick, so a desk whose UI thread has stopped answering stops
    /// beating — which is what the standby is listening for — while the waiting itself never
    /// rides a dispatcher timer.
    /// </summary>
    private void StartBeating(CancellationTokenSource cts)
    {
        _ = Task.Run(async () =>
        {
            var ct = cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TwinWatch.BeatEvery, ct);
                    await UiThread.InvokeAsync(Tick);
                }
            }
            catch (OperationCanceledException)
            {
                // stopped: the token was cancelled
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) Log.Warn("Twin beat loop ended.", ex);
            }
        }, CancellationToken.None);                                                          // the loop reads cts itself; a cancelled source ends it at once, as before
    }

    /// <summary>The pending lines go after a trailing 200 ms, edits inside it riding the same flush — the remote's own cadence.</summary>
    private void ScheduleFlush()
    {
        if (_flushScheduled) return;
        _flushScheduled = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(200, CancellationToken.None);
            try
            {
                await UiThread.InvokeAsync(() =>
                {
                    _flushScheduled = false;
                    Flush();
                });
            }
            catch (Exception ex)
            {
                _flushScheduled = false;
                Log.Warn("Twin flush failed.", ex);
            }
        }, CancellationToken.None);
    }

    /// <summary>A random id per process, so a standby never joins itself through its own beacon.</summary>
    public string Instance { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>How this desk names itself on the link: the beacon's name, else the computer's.</summary>
    public string Name => _kernel.Beacon.MachineName;

    public TwinRole Role => _role;

    /// <summary>Where the link stands (UI thread).</summary>
    public TwinPhase Phase => _role switch
    {
        TwinRole.Off => TwinPhase.Off,
        TwinRole.Main => _listener is null ? TwinPhase.Off : TwinPhase.Listening,
        _ => _phase,
    };

    /// <summary>The main's name as a standby knows it; "" before the first welcome.</summary>
    public string MainName => _mainName;

    /// <summary>
    /// The twin in a few words for the glance line: a main with its standbys and their silence, a
    /// standby with the main and when it was heard, a caller or a timer with the desk it follows;
    /// "" with the twin off and nothing linked.
    /// </summary>
    public string GlanceWords
    {
        get
        {
            var now = Clock();
            switch (_role)
            {
                case TwinRole.Main:
                {
                    if (_listener is null) return "";
                    if (_holder.Length > 0) return $"TWIN main · {_holder} HAS THE SHOW";
                    List<(string Name, DateTime Beat)> standbys;
                    lock (_gate)
                    {
                        standbys = _standbys.Where(s => !s.IsFollower).Select(s => (s.Name, s.LastBeatUtc)).ToList();
                    }
                    if (standbys.Count == 0) return "TWIN main · no standby";
                    var silent = standbys.Where(s => TwinWatch.IsSilent(s.Beat, now)).Select(s => s.Name).ToList();
                    var apart = ClockApartWords.Length > 0 ? " · CLOCKS APART" : "";
                    return (silent.Count > 0 ? $"TWIN main · {string.Join(", ", silent)} SILENT" : $"TWIN main · {standbys.Count} standby in step") + apart;
                }
                case TwinRole.Standby when !IsFollowerNode:                    // a follower links as a standby does, and its words below are its own
                    return _phase switch
                    {
                        TwinPhase.TookOver => "TWIN standby · TOOK OVER",
                        TwinPhase.MainSilent => $"TWIN standby · {(_mainName.Length > 0 ? _mainName : "the main")} SILENT",
                        TwinPhase.InStep => $"TWIN standby · {(_mainName.Length > 0 ? _mainName : "the main")} heard {TwinWatch.Age(_lastHeardUtc, now)}" + GlanceClock("the main's"),
                        TwinPhase.Refused => "TWIN standby · REFUSED",
                        _ => "TWIN standby · dialling",
                    };
                default:
                    if (IsFollowerNode)
                    {
                        return _phase switch
                        {
                            TwinPhase.InStep when _stream is not null => $"LINKED {(_mainName.Length > 0 ? _mainName : "the desk")} · heard {TwinWatch.Age(_lastHeardUtc, now)}" + GlanceClock("the desk's"),
                            TwinPhase.MainSilent => $"{(_mainName.Length > 0 ? _mainName : "the desk")} SILENT",
                            TwinPhase.Connecting => "LINKING",
                            _ => "ALONE",
                        };
                    }
                    return ClockApartWords.Length > 0 ? "NODES · CLOCKS APART" : "";      // a desk hosting callers with the twin off: only the one thing worth the line
            }
        }
    }

    /// <summary>The standbys a main has, by name (UI thread or not).</summary>
    public IReadOnlyList<string> StandbyNames
    {
        get
        {
            lock (_gate)
            {
                return _standbys.Select(s => s.Name).ToList();
            }
        }
    }

    /// <summary>The Machine page's line (UI thread).</summary>
    public string Status
    {
        get
        {
            var now = Clock();
            switch (_role)
            {
                case TwinRole.Main:
                    if (_listener is null) return _note.Length > 0 ? _note : "Twin off.";
                    List<(string, DateTime)> beats;
                    List<(string, TimeSpan)> clocks;
                    lock (_gate)
                    {
                        beats = _standbys.Select(s => (PeerLabel(s), s.LastBeatUtc)).ToList();
                        clocks = _standbys.Where(s => s.Clock.Known).Select(s => (PeerLabel(s), s.Clock.Offset)).ToList();
                    }
                    return TwinWatch.DescribeMain(_kernel.State.Twin.Port, beats, _sectionsSent, now, _holder, _launcher.Words, _handoverNote, clocks);
                case TwinRole.Off when _hosting:
                {
                    List<string> callers;
                    lock (_gate)
                    {
                        callers = _standbys.Where(s => s.IsFollower).Select(s => s.IsCaller ? s.Name : "timer " + s.Name).ToList();
                    }
                    if (IsFollowerNode) return TwinWatch.DescribeCaller(TwinPhase.Off, "", null, 0, now, timer: _kernel.Profile == NodeKind.Timer);
                    var held = _holder.Length > 0 ? $"Twin off — but the standby {_holder} has the show; this desk's outputs are held closed until it ends, or Main and TAKE BACK. " : "Twin off — ";
                    return callers.Count == 0
                        ? $"{held}callers may link on port {_kernel.State.Twin.Port}; none linked."
                        : $"{held}caller{(callers.Count == 1 ? "" : "s")} {string.Join(", ", callers)} linked; GO, STANDBY and HOLD from there run here." + PeerClockWords();
                }
                case TwinRole.Off when IsFollowerNode:
                    return TwinWatch.DescribeCaller(TwinPhase.Off, "", null, 0, now, timer: _kernel.Profile == NodeKind.Timer);
                case TwinRole.Standby when IsFollowerNode:
                    return TwinWatch.DescribeCaller(_phase, _mainName, _lastHeardUtc, _sectionsApplied, now, _note, linked: _stream is not null, airLabel: _live?.AirLabel ?? "", timer: _kernel.Profile == NodeKind.Timer, clock: LinkNote("the desk's"));
                case TwinRole.Standby:
                {
                    var cfg = _kernel.State.Twin;
                    var auto = cfg.AutoTakeOver && TwinWatch.AutoTakeOverBlocked(MainIsOnThisMachine(), cfg.TakeOverCue.Length > 0, DeviceConfirmation.FenceProblem(_kernel.State, cfg.TakeOverCue)) is null && !_note.StartsWith("not taken over", StringComparison.Ordinal);
                    return TwinWatch.DescribeStandby(_phase, _mainName, _lastHeardUtc, _sectionsApplied, auto, now, _note, linked: _stream is not null, clock: LinkNote("the main's"));
                }
                default:
                    return _holder.Length > 0 ? $"Twin off — but the standby {_holder} has the show; this desk's outputs are held closed until it ends, or Main and TAKE BACK." : "Twin off.";
            }
        }
    }

    /// <summary>The last handover run on this desk — its stages, and where it stopped when it did.</summary>
    public TwinTransaction? LastHandover => _handover;

    /// <summary>The words for the health line: the twin's line while it is not simply in step, "" otherwise — and the clocks apart, on either side, whatever the phase.</summary>
    public string HealthWords
    {
        get
        {
            var words = _holder.Length > 0 ? Status : _role == TwinRole.Off || Phase is TwinPhase.InStep or TwinPhase.Listening ? "" : Status;
            var apart = ClockApartWords;
            return apart.Length == 0 ? words : words.Length == 0 ? apart : apart + " · " + words;
        }
    }

    /// <summary>The glance line's word on the clock: " · the desk's clock 0.8 s ahead", or "" within half a second.</summary>
    private string GlanceClock(string whose)
    {
        var note = LinkNote(whose);
        return note.Length > 0 ? " · " + note : "";
    }

    /// <summary>A plan a caller brought, waiting on the desk's APPLY.</summary>
    /// <param name="Queued">APPLY pressed while the desk's stack was armed: kept, and landed on DISARM.</param>
    public sealed record PlanOffer(string Instance, string Caller, string Json, string Words, string Count, bool Same, bool Queued = false);

    /// <summary>TWIN STATUS's payload.</summary>
    /// <summary>The twin as a deck's key reads it: role, phase, the words, the names, who holds the show, the clocks — the block STATE carries.</summary>
    public object DeckBlock()
    {
        var phase = Phase.ToString();
        return new
        {
            role = _role.ToString().ToLowerInvariant(),
            phase = char.ToLowerInvariant(phase[0]) + phase[1..],
            words = Status,
            main = _mainName,
            standbys = StandbyNames,
            holder = _holder,
            clocks = PeerClockWords().Trim(),
            apart = ClockApartWords.Length > 0,
        };
    }

    public string StatusJson()
    {
        var phase = Phase.ToString();
        return JsonSerializer.Serialize(new
        {
            role = _role.ToString().ToLowerInvariant(),
            phase = char.ToLowerInvariant(phase[0]) + phase[1..],
            words = Status,
            main = _mainName,
            standbys = StandbyNames,
            holder = _holder,
            launcher = _launcher.Words,
            takeOverCue = _kernel.State.Twin.TakeOverCue,
            takeBackCue = _kernel.State.Twin.TakeBackCue,
            sectionsSent = _sectionsSent,
            sectionsMirrored = _sectionsApplied,
            clock = _linkClock.Known ? new
            {
                offsetMs = Math.Round(_linkClock.Offset.TotalMilliseconds, 1),
                roundTripMs = Math.Round(_linkClock.Delay.TotalMilliseconds, 1),
                samples = _linkClock.Samples,
                apart = LinkClock.Apart(_linkClock.Offset),
                followed = IsFollowerNode,
                roomOffsetMs = Math.Round(_kernel.Clock.Offset.TotalMilliseconds, 1),
            } : null,
            clocks = PeerClocks.Select(c => new { name = c.Name, offsetMs = Math.Round(c.Offset.TotalMilliseconds, 1), apart = LinkClock.Apart(c.Offset) }).ToArray(),
            handover = _handover is null ? null : new
            {
                id = _handover.Id,
                kind = _handover.Kind == HandoverKind.TakeBack ? "takeBack" : "takeOver",
                shape = _handover.Shape == HandoverShape.AcrossMachines ? "acrossMachines" : "sameMachine",
                stage = TwinTransaction.Label(_handover.Stage),
                complete = _handover.IsComplete,
                stopped = _handover.Reason,
                awaiting = _releaseWait is { } wait ? (wait.Overdue ? "the standby's answer to the hand-back — overdue; TAKE BACK again tells it again" : "the standby's answer to the hand-back")
                    : _switchByHand is not null ? "the operator's switch — TAKE BACK again releases the standby" : "",
                trail = _handover.Trail,
            },
        });
    }

    // ---- the settings ---------------------------------------------------------------

    /// <summary>Opens or closes the listener or the link to match the settings (UI thread, on every publish).</summary>
    public void Reconcile()
    {
        if (_disposed) return;                                              // a closed desk opens no port and starts no beat
        var cfg = _kernel.State.Twin;
        var hostsCallers = _kernel.IsDesk && cfg.Role != TwinRole.Standby && cfg.AcceptCallers;
        if ((cfg.Role == TwinRole.Main || hostsCallers) && cfg.Key.Length == 0)
        {
            // A desk with no key would let any machine on the network join, hold its outputs closed
            // and hand it a show: it is given one, and said out loud, before its port opens.
            if (!_keyBeingMade)
            {
                _keyBeingMade = true;
                var made = TwinKeys.New();
                UiThread.Post(() =>
                {
                    _keyBeingMade = false;
                    var twin = _kernel.State.Twin;
                    if (twin.Key.Length == 0 && (twin.Role == TwinRole.Main || (twin.Role != TwinRole.Standby && twin.AcceptCallers))) _services.BulkEdit(() => twin.Key = made);
                });
                Log.Info("Twin: this desk had no key; one was made for it — the port opens once it is saved.");
                _services.Notify(cfg.Role == TwinRole.Main
                    ? $"Twin: this main was given the key {made} — the standby needs the same key (Machine page, TWIN)."
                    : $"Twin: this desk was given the key {made} — a caller node that links needs the same key (Machine page, TWIN).");
            }
            return;
        }
        var key = $"{cfg.Role}|{cfg.Port}|{cfg.MainHost}|{cfg.Key}|{cfg.LocalStandby}|{hostsCallers}|{_kernel.Profile}";
        if (key == _activeKey) return;
        _activeKey = key;
        BindFailed = false;
        Stop(sayGoodbye: true);
        _role = cfg.Role;
        _note = "";
        // The standby process this desk runs: wanted by a main that asked for one, ended otherwise — unless it has the show.
        _launcher.Want(cfg.Role == TwinRole.Main && cfg.LocalStandby ? TwinHandover.StandbyHome(_kernel.Store.BaseDirectory) : null, cfg.Port, cfg.Key, _holder.Length > 0);
        _hosting = false;
        // A follower node — a caller, a stage timer: whatever its file's twin role says, it follows
        // the desk LINK named — with the desk's key — and never holds, takes or opens anything. No
        // desk named: a caller plans alone, a timer keeps its own clock.
        if (IsFollowerNode)
        {
            if (cfg.MainHost.Length == 0) return;
            _role = TwinRole.Standby;                                                // a follower, on the standby's own paths
            _cts = new CancellationTokenSource();
            _phase = TwinPhase.Connecting;
            _mainName = cfg.MainHost;
            _planOffered = false;
            StartBeating(_cts);
            Dial();
            return;
        }
        if (cfg.Role != TwinRole.Main && hostsCallers)
        {
            // The desk's twin is off, but callers may link: the same port, the same protocol, the
            // same key — a standby that dials it is refused, a caller is welcomed.
            _hosting = true;
            _cts = new CancellationTokenSource();
            try
            {
                _listener = new TcpListener(IPAddress.Any, cfg.Port);
                _listener.Start();
                _ = AcceptLoop(_listener, _cts.Token);
                Log.Info($"Twin: listening for caller nodes on port {cfg.Port}.");
            }
            catch (Exception ex)
            {
                _listener = null;
                _note = $"Twin: the desk could not open port {cfg.Port} for callers — {ex.Message}";
                Log.Warn(_note, ex);
                _activeKey = "";
                BindFailed = true;
            }
            StartBeating(_cts);
            return;
        }
        switch (cfg.Role)
        {
            case TwinRole.Main:
                CheckMarker(force: true);
                _hosting = true;
                _cts = new CancellationTokenSource();
                try
                {
                    _listener = new TcpListener(IPAddress.Any, cfg.Port);
                    _listener.Start();
                    _ = AcceptLoop(_listener, _cts.Token);
                    Log.Info($"Twin: listening for a standby on port {cfg.Port}.");
                }
                catch (Exception ex)
                {
                    _listener = null;
                    _note = $"Twin: the main could not open port {cfg.Port} — {ex.Message}";
                    Log.Warn(_note, ex);
                    _activeKey = ""; // retried on the next change, and from the poll (round 77)
                    BindFailed = true;
                }
                StartBeating(_cts);
                break;
            case TwinRole.Standby:
                _cts = new CancellationTokenSource();
                _phase = TwinPhase.Connecting;
                _mainName = cfg.MainHost;
                _services.OutputsHeldBy = "this desk is the standby twin";
                if (_services.OutputsLive)
                {
                    _services.CloseOutputs();
                    _services.Notify("Twin: this desk is the standby now — its outputs are held closed until it takes over.");
                }
                StartBeating(_cts);
                Dial();
                break;
        }
    }

    private void Stop(bool sayGoodbye)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        List<Standby> standbys;
        lock (_gate)
        {
            standbys = _standbys.ToList();
            _standbys.Clear();
        }
        foreach (var s in standbys)
        {
            if (sayGoodbye) s.TryWrite(TwinMessage.Format(TwinWord.Bye));
            s.Dispose();
        }
        try { _listener?.Dispose(); } catch { /* already down */ }
        _listener = null;
        _pendingSections.Clear();
        _pendingWhole = false;
        _pendingAir = false;
        _sectionsSent = 0;
        _releaseWait = null;                        // a hand-back awaited goes with the role: the standby is told again if it claims the show of a main
        _switchByHand = null;
        _handoverBusy = false;
        _queuedSections.Clear();
        if (_holderLinked)
        {
            // The link to the standby that has the show went with the role; the marker, if any, still holds.
            _holderLinked = false;
            _heldShowJson = null;
            _heldAir = null;
            if (!_holderMarked) Release("is off the link");
        }
        if (_role == TwinRole.Standby)
        {
            if (sayGoodbye) TryWriteToMain(TwinMessage.Format(TwinWord.Bye));
            CloseLink();
            if (_kernel.IsDesk) _services.OutputsHeldBy = "";                       // a node's own hold is not the twin's to lift
        }
        _hosting = false;
        _live = null;
        _phase = TwinPhase.Off;
        _kernel.Clock.Reset();                                                   // alone by choice: the clock is this machine's own again
        _welcome = null;
        _mainName = "";
        _lastHeardUtc = null;
        _sectionsApplied = 0;
        _mirroredAir = null;
        _role = TwinRole.Off;
    }

    /// <summary>Once a second (UI thread): the beats out, the silence counted, the redial, the takeover a standby was told it may make. Public so a test can move the clock and tick.</summary>
    public void Tick()
    {
        if (_disposed) return;                                              // a beat that lands after the close does nothing
        try
        {
            var now = Clock();
            if (_hosting && _role != TwinRole.Main)
            {
                // Hosting callers with the twin off: the beats and the live word go out; nothing else of a main's.
                HookLive();
                var seq = Interlocked.Increment(ref _beat);
                List<Standby> callers;
                lock (_gate)
                {
                    callers = _standbys.ToList();
                }
                if (callers.Count > 0) _ = Task.Run(() => { foreach (var s in callers) if (!s.TryWrite(s.BeatLine(seq, Clock().Ticks))) Drop(s); }, CancellationToken.None);
                SendLive();
                return;
            }
            switch (_role)
            {
                case TwinRole.Main:
                {
                    HookLive();
                    var seq = Interlocked.Increment(ref _beat);
                    List<Standby> standbys;
                    lock (_gate)
                    {
                        standbys = _standbys.ToList();
                    }
                    if (standbys.Count > 0) _ = Task.Run(() => { foreach (var s in standbys) if (!s.TryWrite(s.BeatLine(seq, Clock().Ticks))) Drop(s); }, CancellationToken.None);
                    SendLive();
                    _launcher.Tick(standbyHoldsShow: _holder.Length > 0);
                    CheckMarker(force: false);
                    break;
                }
                case TwinRole.Standby:
                {
                    var cfg = _kernel.State.Twin;
                    if (_phase == TwinPhase.InStep && TwinWatch.IsSilent(_lastHeardUtc, now))
                    {
                        _phase = TwinPhase.MainSilent;
                        Log.Warn($"Twin: the main {_mainName} has been silent for {TwinWatch.SilentAfter.TotalSeconds:0} s.");
                    }
                    // By itself only where it is safe: a main on this machine (the hung one is ended first) or,
                    // from another machine, with the wall-switch cue that makes this desk the one the room shows.
                    var blocked = IsFollowerNode ? null                                           // a follower never takes anything over: the desk is silent, and the cues stay here
                        : cfg.AutoTakeOver && _phase == TwinPhase.MainSilent ? TwinWatch.AutoTakeOverBlocked(MainIsOnThisMachine(), cfg.TakeOverCue.Length > 0, DeviceConfirmation.FenceProblem(_kernel.State, cfg.TakeOverCue)) : null;
                    if (blocked is not null && _note != blocked)
                    {
                        // The fence's words as they stand now: a cue built since, or a box changed, changes them.
                        _note = blocked;
                        Log.Warn($"Twin: the main {_mainName} is silent; {blocked}.");
                        _services.Notify($"Twin: the main {_mainName} is silent — {blocked}.");
                    }
                    if (!IsFollowerNode && blocked is null && now >= _nextAutoTakeOverUtc && TwinWatch.ShouldTakeOver(cfg.AutoTakeOver, _phase, _lastHeardUtc, now))
                    {
                        if (!TakeOver(ActionOrigin.Recovery).Ok) _nextAutoTakeOverUtc = now + TwinWatch.RetryAfterRefusal;
                        break;
                    }
                    if (_stream is not null)
                    {
                        var seq = Interlocked.Increment(ref _beat);
                        _ = Task.Run(() => TryWriteToMain(TwinMessage.Format(TwinWord.Beat, new TwinBeat(seq, Clock().Ticks, Interlocked.Read(ref _lastMainSentTicks), Interlocked.Read(ref _lastMainReceivedTicks)).Format())), CancellationToken.None);
                    }
                    else if (_phase is TwinPhase.Connecting or TwinPhase.MainSilent or TwinPhase.TookOver)
                    {
                        Dial(); // after a takeover too: the main, once it is back, takes the show back over this link
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Twin tick failed.", ex);
        }
    }


    private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
    }

    /// <summary>Set by Dispose: nothing reconciles, beats or posts for this desk again.</summary>
    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        Stop(sayGoodbye: true);
        // A clean exit ends the standby process it started — unless that process has the show, or this is a restart and the next desk adopts it.
        _launcher.End(standbyHoldsShow: _holder.Length > 0 || _holderMarked || KeepStandbyOnExit);
        _kernel.Bus.SectionsPublished -= OnBuilt;
        _services.RecoveryMoved -= OnRecoveryMoved;
        if (_stackHooked && _services.CueStack is { } stack) stack.Changed -= OnStackChanged;
    }
}
