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
public sealed class TwinService : IDisposable, ILinkReport
{
    private readonly ServiceKernel _kernel;
    private readonly ITwinHost _services;
    private readonly object _gate = new();
    private bool _flushScheduled;
    private string _activeKey = "";
    private CancellationTokenSource? _cts;
    private TwinRole _role;

    // ---- the main's side ----
    private TcpListener? _listener;
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
    private DateTime _nextAutoTakeOverUtc;      // after a takeover by itself was refused: the next try
    private string _dialNonce = "";             // this dial's nonce: the main proves the key over it before this desk answers
    private TwinTransaction? _handover;         // the last handover run here, its stages and where it stopped
    private string _handoverNote = "";          // a take-back stopped at the wall switch: the words until it finishes
    private bool _handoverBusy;                 // a wall switch was asked and its answer is awaited: no second handover meanwhile
    private bool _keyBeingMade;

    // ---- callers ----
    private bool _hosting;                                                          // the listener is open for callers (a main, or a desk that accepts them)
    private readonly Dictionary<string, string> _echoSkip = new(StringComparer.Ordinal);   // section → the peer whose edit it was: not sent back to it
    private readonly Dictionary<string, string> _lastLanded = new(StringComparer.Ordinal); // section → the JSON that landed here last: not sent back as an edit
    private string? _myPlanJson;                                                    // a caller's own cues, kept before the desk's show lands, offered once
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
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) Log.Warn("Twin beat loop ended.", ex);
            }
        });
    }

    /// <summary>The pending lines go after a trailing 200 ms, edits inside it riding the same flush — the remote's own cadence.</summary>
    private void ScheduleFlush()
    {
        if (_flushScheduled) return;
        _flushScheduled = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
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
        });
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
                    lock (_gate)
                    {
                        beats = _standbys.Select(s => (s.IsCaller ? "caller " + s.Name : s.IsFollower ? "timer " + s.Name : s.Name, s.LastBeatUtc)).ToList();
                    }
                    return TwinWatch.DescribeMain(_kernel.State.Twin.Port, beats, _sectionsSent, now, _holder, _launcher.Words, _handoverNote);
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
                        : $"{held}caller{(callers.Count == 1 ? "" : "s")} {string.Join(", ", callers)} linked; GO, STANDBY and HOLD from there run here.";
                }
                case TwinRole.Off when IsFollowerNode:
                    return TwinWatch.DescribeCaller(TwinPhase.Off, "", null, 0, now, timer: _kernel.Profile == NodeKind.Timer);
                case TwinRole.Standby when IsFollowerNode:
                    return TwinWatch.DescribeCaller(_phase, _mainName, _lastHeardUtc, _sectionsApplied, now, _note, linked: _stream is not null, airLabel: _live?.AirLabel ?? "", timer: _kernel.Profile == NodeKind.Timer);
                case TwinRole.Standby:
                {
                    var cfg = _kernel.State.Twin;
                    var auto = cfg.AutoTakeOver && TwinWatch.AutoTakeOverBlocked(MainIsOnThisMachine(), cfg.TakeOverCue.Length > 0, DeviceConfirmation.FenceProblem(_kernel.State, cfg.TakeOverCue)) is null && !_note.StartsWith("not taken over", StringComparison.Ordinal);
                    return TwinWatch.DescribeStandby(_phase, _mainName, _lastHeardUtc, _sectionsApplied, auto, now, _note, linked: _stream is not null);
                }
                default:
                    return _holder.Length > 0 ? $"Twin off — but the standby {_holder} has the show; this desk's outputs are held closed until it ends, or Main and TAKE BACK." : "Twin off.";
            }
        }
    }

    /// <summary>The last handover run on this desk — its stages, and where it stopped when it did.</summary>
    public TwinTransaction? LastHandover => _handover;

    /// <summary>The words for the health line: the twin's line while it is not simply in step, "" otherwise.</summary>
    public string HealthWords => _holder.Length > 0 ? Status : _role == TwinRole.Off || Phase is TwinPhase.InStep or TwinPhase.Listening ? "" : Status;

    /// <summary>A plan a caller brought, waiting on the desk's APPLY.</summary>
    public sealed record PlanOffer(string Instance, string Caller, string Json, string Words, string Count, bool Same);

    /// <summary>TWIN STATUS's payload.</summary>
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
            handover = _handover is null ? null : new
            {
                kind = _handover.Kind == HandoverKind.TakeBack ? "takeBack" : "takeOver",
                shape = _handover.Shape == HandoverShape.AcrossMachines ? "acrossMachines" : "sameMachine",
                stage = TwinTransaction.Label(_handover.Stage),
                complete = _handover.IsComplete,
                stopped = _handover.Reason,
                trail = _handover.Trail,
            },
        });
    }

    // ---- the settings ---------------------------------------------------------------

    /// <summary>Opens or closes the listener or the link to match the settings (UI thread, on every publish).</summary>
    public void Reconcile()
    {
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
                    _activeKey = ""; // retried on the next change
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
        try { _listener?.Stop(); } catch { /* already down */ }
        _listener = null;
        _pendingSections.Clear();
        _pendingWhole = false;
        _pendingAir = false;
        _sectionsSent = 0;
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
        try
        {
            var now = Clock();
            if (_hosting && _role != TwinRole.Main)
            {
                // Hosting callers with the twin off: the beats and the live word go out; nothing else of a main's.
                HookLive();
                var beat = TwinMessage.Format(TwinWord.Beat, Interlocked.Increment(ref _beat).ToString());
                List<Standby> callers;
                lock (_gate)
                {
                    callers = _standbys.ToList();
                }
                if (callers.Count > 0) _ = Task.Run(() => { foreach (var s in callers) if (!s.TryWrite(beat)) Drop(s); });
                SendLive();
                return;
            }
            switch (_role)
            {
                case TwinRole.Main:
                {
                    HookLive();
                    var line = TwinMessage.Format(TwinWord.Beat, Interlocked.Increment(ref _beat).ToString());
                    List<Standby> standbys;
                    lock (_gate)
                    {
                        standbys = _standbys.ToList();
                    }
                    if (standbys.Count > 0) _ = Task.Run(() => { foreach (var s in standbys) if (!s.TryWrite(line)) Drop(s); });
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
                    if (blocked is not null && _note.Length == 0)
                    {
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
                        var line = TwinMessage.Format(TwinWord.Beat, Interlocked.Increment(ref _beat).ToString());
                        _ = Task.Run(() => TryWriteToMain(line));
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

    // ---- the main: what goes out ------------------------------------------------------

    /// <summary>A publish named its sections (UI thread): they go on the next flush; unnamed means the whole show.</summary>
    private void OnBuilt(ShowState state, HashSet<string>? dirty)
    {
        if (!ReferenceEquals(state, _kernel.State)) return;
        if (IsFollowerNode)
        {
            if (_kernel.Profile == NodeKind.Caller) SendMyEdits(dirty);                  // a timer owns nothing of the show
            return;
        }
        if (_role != TwinRole.Main && !_hosting) return;
        if (dirty is null) _pendingWhole = true;
        else foreach (var s in TwinSync.Mirrored(dirty)) _pendingSections.Add(s);
        ScheduleFlush();
    }

    /// <summary>
    /// A caller's own edit — a cue, a note, the pad — goes to the desk as the section it landed in.
    /// A section that is exactly what landed here from the desk is the desk's own edit coming
    /// back through the publish, not the caller's, and stays where it is.
    /// </summary>
    private void SendMyEdits(HashSet<string>? dirty)
    {
        if (_stream is null || _phase != TwinPhase.InStep) return;
        var sections = dirty is null ? TwinSync.CallerSections : TwinSync.CallerSections.Where(dirty.Contains).ToList();
        foreach (var section in sections)
        {
            string json;
            try
            {
                json = TwinSync.SectionJson(_kernel.State, section);
            }
            catch (Exception ex)
            {
                Log.Warn($"Twin: the caller's {section} could not be written.", ex);
                continue;
            }
            if (_lastLanded.TryGetValue(section, out var landed) && landed == json) continue;
            _lastLanded[section] = json;
            var line = TwinMessage.Format(TwinWord.Section, json, section);
            _ = Task.Run(() => TryWriteToMain(line));
        }
    }

    /// <summary>A caller's verb to the desk: GO, STANDBY, HOLD, a message to stage — run there, journaled there as the caller's.</summary>
    public ActionResult Forward(ShowAction action, ActionOrigin origin)
    {
        if (!IsLinkedToDesk) return ActionResult.Refused("Not linked to a desk — LINK on the Nodes page.");
        var line = TwinMessage.Format(TwinWord.Act, JsonUtil.SerializeCompact(action));
        var sent = TryWriteToMain(line);
        return sent
            ? ActionResult.Requested($"{ActionSpec.Label(action.Kind)} — sent to {_mainName}.")
            : ActionResult.Failed($"{ActionSpec.Label(action.Kind)} could not be sent to {_mainName}.");
    }

    /// <summary>OFFER PLAN on a caller: the cues as they stand here, to the desk's Nodes page, for APPLY there.</summary>
    public ActionResult OfferPlan()
    {
        if (_kernel.Profile != NodeKind.Caller) return ActionResult.Refused("Only a caller node offers a plan.");
        if (_stream is null) return ActionResult.Refused("Not linked to a desk.");
        var json = CuePlan.Json(_kernel.State);
        var ok = TryWriteToMain(TwinMessage.Format(TwinWord.Plan, json));
        _planOffered = true;
        return ok ? ActionResult.Done($"Plan offered to {_mainName}: {CuePlan.Count(_kernel.State.Stacks)} — APPLY is the desk's press.") : ActionResult.Failed("The plan could not be sent.");
    }

    /// <summary>The whole show landed here: the caller's sections as the desk sent them, so the publish that follows is not sent back as an edit.</summary>
    private void RememberLanded(string showJson)
    {
        if (_kernel.Profile != NodeKind.Caller) return;
        try
        {
            var incoming = JsonUtil.Deserialize<ShowState>(showJson);
            if (incoming is null) return;
            foreach (var section in TwinSync.CallerSections) _lastLanded[section] = TwinSync.SectionJson(incoming, section);
        }
        catch (Exception)
        {
            // a show this build cannot read lands nowhere either
        }
    }

    private void HookLive()
    {
        if (_liveHooked || _services.CueStack is null) return;
        _liveHooked = true;
        _services.CueStack.Changed += SendLive;
    }

    /// <summary>The caller's stack as it runs here, to every caller on the link — once a second from the tick, at once on a change.</summary>
    private void SendLive()
    {
        if (_services.CueStack is null) return;
        List<Standby> callers;
        lock (_gate)
        {
            callers = _standbys.Where(s => s.IsFollower).ToList();
        }
        if (callers.Count == 0) return;
        string line;
        try
        {
            var stack = _services.CueStack;
            var rt = stack.Runtime;
            var live = new TwinLive(rt.StandbyCueId ?? "", rt.LastCueId ?? "", rt.Armed, rt.Hold, rt.Executing, _services.AirLabel, _services.OutputsLive, _kernel.State.Blackout, stack.Timing().OffsetText, Interlocked.Increment(ref _liveSeq));
            line = TwinMessage.Format(TwinWord.Live, live.ToJson());
        }
        catch (Exception ex)
        {
            Log.Warn("Twin: the live word could not be written.", ex);
            return;
        }
        _ = Task.Run(() => { foreach (var c in callers) if (!c.TryWrite(line)) Drop(c); });
    }

    /// <summary>A line from a caller on the link (UI thread): its edit lands, its verb runs, its plan is kept for APPLY.</summary>
    private void OnCallerLine(Standby caller, TwinMessage msg)
    {
        switch (msg.Word)
        {
            case TwinWord.Section:
            {
                if (!caller.IsCaller || !TwinSync.IsCallerSection(msg.Name))
                {
                    Log.Warn($"Twin: the {(caller.IsCaller ? "caller" : "timer")} {caller.Name} sent '{msg.Name}', which it does not own — ignored.");
                    return;
                }
                _echoSkip[msg.Name] = caller.Instance;                                // its own edit is not sent back to it
                var ok = false;
                _services.BulkEdit(() => ok = TwinSync.ApplySection(_kernel.State, msg.Name, msg.Payload));
                if (!ok) Log.Warn($"Twin: the caller {caller.Name}'s {msg.Name} could not land.");
                break;
            }
            case TwinWord.Act:
            {
                ShowAction action;
                try
                {
                    action = JsonUtil.Deserialize<ShowAction>(msg.Payload);
                }
                catch (Exception)
                {
                    Log.Warn($"Twin: the caller {caller.Name} sent a verb this build could not read.");
                    return;
                }
                if (action.Kind == ShowActionKind.Unknown) return;
                _services.Actions.Execute(action, new ActionOrigin(OriginKind.Caller, caller.Name));
                break;
            }
            case TwinWord.Plan:
            {
                if (!caller.IsCaller) return;                                          // a plan is a caller's to offer
                var plan = CuePlan.Parse(msg.Payload);
                if (plan is null)
                {
                    Log.Warn($"Twin: the caller {caller.Name} sent a plan this build could not read.");
                    return;
                }
                var diff = CuePlan.Diff(_kernel.State.Stacks, plan);
                var existing = Plans.FirstOrDefault(p => p.Instance == caller.Instance);
                if (existing is not null) Plans.Remove(existing);
                Plans.Add(new PlanOffer(caller.Instance, caller.Name, msg.Payload, diff.Words, CuePlan.Count(plan), diff.IsEmpty));
                var words = diff.IsEmpty
                    ? $"Nodes: the caller {caller.Name} brought {CuePlan.Count(plan)} — the same cues this desk has."
                    : $"Nodes: the caller {caller.Name} brought a plan — {diff.Words}. APPLY on the Nodes page lands it.";
                Log.Info("Twin: " + words);
                _services.Notify(words);
                break;
            }
        }
    }

    /// <summary>APPLY on the desk's Nodes page: a version of the show kept first, then the caller's stacks onto this desk's — mirrored on to everyone on the link.</summary>
    public ActionResult ApplyPlan(PlanOffer offer)
    {
        var plan = CuePlan.Parse(offer.Json);
        if (plan is null) return ActionResult.Refused("That plan could not be read.");
        _services.SaveNow();                                                         // the show as it was, kept as a version
        var stacks = 0;
        _services.BulkEdit(() => stacks = CuePlan.Merge(_kernel.State, plan));
        Plans.Remove(offer);
        var words = $"The caller {offer.Caller}'s plan landed: {offer.Count}, {stacks} stack{(stacks == 1 ? "" : "s")} — the show as it was is under EARLIER VERSIONS.";
        _kernel.Journal.Record($"caller {offer.Caller}", "PlanApply", "", "Done", words);
        _services.Notify(words);
        return ActionResult.Done(words);
    }

    public void DismissPlan(PlanOffer offer) => Plans.Remove(offer);

    /// <summary>A caller: the desk's stack as it runs there onto this desk's runtime, so the Run surface here reads the desk's standby, ARM and HOLD.</summary>
    private void AdoptLive(string json)
    {
        var live = TwinLive.Parse(json);
        if (live is null) return;
        _live = live;
        var stack = CueStacks.Caller(_kernel.State);
        var rt = _kernel.Cues.For(stack);
        _services.DeskEdit(() =>
        {
            rt.StandbyCueId = live.Standby.Length > 0 ? live.Standby : null;
            rt.LastCueId = live.Last.Length > 0 ? live.Last : null;
            rt.Armed = live.Armed;
            rt.Hold = live.Hold;
            rt.Executing = live.Executing;
        });
    }

    /// <summary>The recovery record moved (UI thread): what is on air, the caller's place — a standby needs it to take over.</summary>
    private void OnRecoveryMoved(RecoverySnapshot? record)
    {
        _air = record;
        if (_role != TwinRole.Main) return;
        _pendingAir = true;
        ScheduleFlush();
    }

    /// <summary>The pending lines, built on the UI thread (the show is read here) and written on a worker.</summary>
    private void Flush()
    {
        if (_role != TwinRole.Main && !_hosting) return;
        List<Standby> standbys;
        lock (_gate)
        {
            standbys = _standbys.ToList();
        }
        var lines = new List<string>();
        var origins = new List<string?>();                                            // per section line: the peer whose edit it was, or null
        try
        {
            if (_pendingWhole)
            {
                lines.Add(TwinMessage.Format(TwinWord.Show, TwinSync.WireJson(_kernel.State, _kernel.State.Twin.SendSecrets)));
                origins.Add(null);
                _sectionsSent += TwinSync.MirroredSections.Count;
            }
            else
            {
                foreach (var section in TwinSync.Mirrored(_pendingSections))
                {
                    lines.Add(TwinMessage.Format(TwinWord.Section, TwinSync.WireSectionJson(_kernel.State, section, _kernel.State.Twin.SendSecrets), section));
                    origins.Add(_echoSkip.TryGetValue(section, out var from) ? from : null);
                    _sectionsSent++;
                }
            }
            if (_pendingAir) lines.Add(AirLine());
        }
        catch (Exception ex)
        {
            Log.Warn("Twin: a section could not be written.", ex);
        }
        _pendingWhole = false;
        _pendingSections.Clear();
        _pendingAir = false;
        _echoSkip.Clear();
        if (lines.Count == 0 || standbys.Count == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var s in standbys)
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    if (i < origins.Count && origins[i] is { } from && from == s.Instance) continue;   // its own edit: it has it
                    if (s.TryWrite(lines[i])) continue;
                    Drop(s);
                    break;
                }
            }
        });
    }

    private string AirLine() => TwinMessage.Format(TwinWord.Air, _air is null ? "null" : JsonUtil.SerializeCompact(_air));

    private void Drop(Standby s)
    {
        lock (_gate)
        {
            _standbys.Remove(s);
        }
        s.Dispose();
        Log.Info($"Twin: the standby {s.Name} left.");
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleStandby(client, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            _ = ex;
        }
        catch (Exception ex)
        {
            Log.Warn("Twin accept loop ended.", ex);
        }
    }

    private async Task HandleStandby(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        Standby? standby = null;
        try
        {
            var stream = client.GetStream();
            var reader = new BoundedLineReader(stream, JoinLineBytes);     // a JOIN is a few hundred bytes; the ceiling rises once the key is right
            using var joinWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            joinWait.CancelAfter(TimeSpan.FromSeconds(5));
            var first = TwinMessage.Parse(await reader.ReadLineAsync(joinWait.Token));
            var join = first.Word == TwinWord.Join ? TwinJoin.Parse(first.Payload) : null;
            var key = _kernel.State.Twin.Key;
            string? refused = join is null ? "the first line was not a JOIN"
                : join.Proto != TwinMessage.Proto ? $"another version of the link (yours {join.Proto}, mine {TwinMessage.Proto})"
                : key.Length == 0 ? "this main has no key yet"
                : join.Nonce.Length == 0 ? "the JOIN carried no nonce to prove the key over"
                : join.Instance == Instance ? "that is this very desk"
                : !join.IsFollower && _role != TwinRole.Main ? "this desk is not a twin main — it links callers and stage timers only"
                : join.IsFollower && !_kernel.IsDesk ? "a node does not host callers or stage timers"
                : null;
            if (refused is null)
            {
                // The key, proved and never read off the wire: this desk answers the joiner's nonce
                // first — a stranger listening on the port could otherwise hand a standby a show —
                // then the joiner answers this desk's, and a wrong answer is a wrong key.
                var serverNonce = TwinAuth.NewNonce();
                var challenge = new TwinChallenge(serverNonce, TwinAuth.Proof(key, join!.Nonce, serverNonce));
                await WriteLineAsync(stream, TwinMessage.Format(TwinWord.Challenge, challenge.ToJson()), ct);
                using var proofWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                proofWait.CancelAfter(TimeSpan.FromSeconds(5));
                var answer = TwinMessage.Parse(await reader.ReadLineAsync(proofWait.Token));
                refused = answer.Word != TwinWord.Proof ? "the line after the challenge was not a PROOF"
                    : !TwinAuth.Verify(key, serverNonce, join.Instance, answer.Payload) ? "wrong key"
                    : null;
            }
            if (refused is not null)
            {
                await WriteLineAsync(stream, TwinMessage.Format(TwinWord.Refused, refused), ct);
                Log.Warn($"Twin: refused a standby ({refused}).");
                client.Dispose();
                return;
            }
            standby = new Standby(client, stream, join!.Name.Length > 0 ? join.Name : join.Machine, Clock()) { HoldsShow = join.TookOver && !join.IsFollower, IsCaller = join.IsCaller, IsFollower = join.IsFollower, Instance = join.Instance, Machine = join.Machine };
            // The welcome and the whole show, read on the UI thread — the show is its own. A standby
            // that ran the show while this desk was away gets the welcome and nothing to mirror: its
            // show is the newer one, and this desk holds its outputs until TAKE BACK.
            var (welcome, show, air) = await UiThread.InvokeAsync(() =>
            {
                var w = new TwinWelcome(Name, Environment.MachineName, Instance, Environment.ProcessId, ProcessStartTicks(), Environment.ProcessPath ?? "", _kernel.State.Name);
                if (standby.HoldsShow) Hold(standby.Name, null, linked: true);
                return (w.ToJson(), standby.HoldsShow ? "" : TwinSync.WireJson(_kernel.State, _kernel.State.Twin.SendSecrets), AirLine());
            });
            var welcomed = standby.TryWrite(TwinMessage.Format(TwinWord.Welcome, welcome))
                           && (standby.HoldsShow || (standby.TryWrite(TwinMessage.Format(TwinWord.Show, show)) && standby.TryWrite(air)))
                           && standby.TryWrite(TwinMessage.Format(TwinWord.Beat, Interlocked.Increment(ref _beat).ToString()));
            if (!welcomed)
            {
                standby.Dispose();
                return;
            }
            reader.MaxLineBytes = LinkLineBytes;                            // proved: a show's worth of JSON may travel on one line
            lock (_gate)
            {
                _standbys.Add(standby);
            }
            Log.Info(standby.IsCaller ? $"Twin: the caller {standby.Name} linked and has the show."
                : standby.IsFollower ? $"Twin: the stage timer {standby.Name} linked and follows the clock."
                : standby.HoldsShow
                ? $"Twin: the standby {standby.Name} joined and HAS THE SHOW — this desk's outputs are held until TAKE BACK."
                : $"Twin: the standby {standby.Name} joined and has the show.");
            if (standby.IsFollower)
            {
                // Off the accept thread: the desk's words and the live word are the UI thread's.
                UiThread.Post(() =>
                {
                    _services.Notify(standby.IsCaller
                        ? $"Nodes: the caller {standby.Name} linked — its GO, STANDBY and HOLD run here as its own."
                        : $"Nodes: the stage timer {standby.Name} linked — it shows this desk's clock and messages, and its ACKs land here.");
                    SendLive();
                });
            }
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                var msg = TwinMessage.Parse(line);
                if (msg.Word == TwinWord.Beat) standby.LastBeatUtc = Clock();
                else if (msg.Word == TwinWord.Bye) break;
                else if (standby.IsFollower) await UiThread.InvokeAsync(() => OnCallerLine(standby, msg));
                else if (standby.HoldsShow && msg.Word is TwinWord.Show or TwinWord.Air)
                {
                    // What the standby has: kept for TAKE BACK, never applied on its own.
                    await UiThread.InvokeAsync(() =>
                    {
                        if (msg.Word == TwinWord.Show) _heldShowJson = msg.Payload;
                        else _heldAir = msg.Payload == "null" ? null : ReadAir(msg.Payload);
                        _heldLines++;
                    });
                }
            }
        }
        catch (Exception ex)
        {
            // A standby that went away is routine; anything else on its line is said, so a peer that is dropped is never dropped silently.
            if (ex is not (IOException or ObjectDisposedException or OperationCanceledException or SocketException))
            {
                Log.Warn($"Twin: the line from {standby?.Name ?? "a peer"} ended on a fault.", ex);
            }
        }
        finally
        {
            if (standby is not null)
            {
                bool listed;
                lock (_gate)
                {
                    listed = _standbys.Remove(standby);
                }
                if (listed) Log.Info($"Twin: the standby {standby.Name} left.");
                standby.Dispose();
                if (standby.HoldsShow)
                {
                    await UiThread.InvokeAsync(() =>
                    {
                        if (!_holderLinked) return;
                        _holderLinked = false;
                        // Its process is still up by the marker: the marker decides, and what it sent
                        // is kept for this desk to put back should that process die with the show.
                        if (_holderMarked) return;
                        _heldShowJson = null;
                        _heldAir = null;
                        Release("left the link");
                    });
                }
            }
            else
            {
                client.Dispose();
            }
        }
    }

    // ---- the main: a standby that has the show ------------------------------------------------

    /// <summary>
    /// The marker a standby on this machine writes when it takes the show, read from the folder this
    /// desk launches one into: while that process lives this desk's outputs are held — two desks
    /// driving the same screens is the one failure worse than one being down. Once a second from the
    /// desk's poll (any role: a main restarted by its watchdog reads it before its first window),
    /// at once when forced.
    /// </summary>
    private void CheckMarker(bool force)
    {
        var now = Clock();
        if (!force && now - _markerCheckedUtc < TimeSpan.FromSeconds(2)) return;
        _markerCheckedUtc = now;
        TwinTookOverMarker? marker;
        try
        {
            marker = TwinHandover.Read(TwinHandover.StandbyHome(_kernel.Store.BaseDirectory));
        }
        catch (Exception)
        {
            return;
        }
        var holds = marker is not null && TwinHandover.Holds(marker, Probe.Look);   // a process that cannot be read still holds: a fence, not an absence
        if (holds && !_holderMarked)
        {
            _holderMarked = true;
            Hold(marker!.Standby, marker.AtUtc, linked: _holderLinked);
        }
        else if (!holds && _holderMarked)
        {
            _holderMarked = false;
            if (marker is not null) HolderDied(marker.Standby);       // the marker stands but its process is gone: it died with the show
            else if (!_holderLinked) Release("ended");                // the marker was cleared: it stood by again on purpose
        }
    }

    /// <summary>
    /// The standby on this machine died with the show — its marker's process is gone. The show it
    /// sent while it ran, if any, lands here, and what it had on air goes back on here the way a
    /// restart puts it back: a room left dark until somebody presses OUTPUTS ON is the very thing
    /// a standby was there to prevent. A standby elsewhere that merely leaves the link is not this:
    /// a link that dropped cannot say whether that desk still runs the show.
    /// </summary>
    private void HolderDied(string name)
    {
        var who = _holder.Length > 0 ? _holder : name.Length > 0 ? name : "the standby";
        var head = $"The standby {who} died with the show at {Clock().ToLocalTime():HH:mm:ss} — put back on here.";
        var notes = new List<string>();
        if (_heldShowJson is { } json)
        {
            var ok = false;
            _services.BulkEdit(() => ok = TwinSync.ApplyShow(_kernel.State, json));
            notes.Add(ok ? "its show landed here" : "its show could not be read — this desk's show stands");
            if (!ok) Log.Warn("Twin: the standby's show could not be read after it died.");
        }
        var air = _heldAir ?? _air;
        _holder = "";
        _holderSinceUtc = null;
        _holderLinked = false;
        _heldShowJson = null;
        _heldAir = null;
        try
        {
            TwinHandover.Clear(TwinHandover.StandbyHome(_kernel.Store.BaseDirectory));
        }
        catch (Exception)
        {
            // a marker whose process is gone holds nothing either way
        }
        if (_role != TwinRole.Standby) _services.OutputsHeldBy = "";
        var words = head + (notes.Count > 0 ? " " + string.Join(", ", notes) + "." : "");
        Log.Warn($"Twin: {words}");
        _services.Notify(words);
        _services.RecoverFromTwin(air, head, peer: who);
    }

    /// <summary>From the desk's poll, once a second, whatever the role.</summary>
    public void Poll() => CheckMarker(force: false);

    /// <summary>A standby has the show: this desk's outputs are held (and closed, were they open) until TAKE BACK.</summary>
    private void Hold(string standby, DateTime? sinceUtc, bool linked)
    {
        var fresh = _holder.Length == 0;
        _holder = standby.Length > 0 ? standby : "the standby";
        _holderSinceUtc ??= sinceUtc;
        if (linked) _holderLinked = true;
        if (_role == TwinRole.Standby) return; // a standby's own hold has its own words
        _services.OutputsHeldBy = TwinHandover.HoldWords(_holder, _holderSinceUtc);
        if (_services.OutputsLive)
        {
            _services.CloseOutputs();
            Log.Warn($"Twin: the standby {_holder} has the show — this desk's outputs are closed.");
        }
        if (fresh) _services.Notify($"Twin: the standby {_holder} has the show — this desk's outputs are held closed. TAKE BACK (Machine page, TWIN) puts the show back here.");
    }

    /// <summary>The standby no longer has the show: this desk's outputs are its own again — OUTPUTS ON is the operator's press.</summary>
    private void Release(string reason)
    {
        var who = _holder.Length > 0 ? _holder : "the standby";
        _holder = "";
        _holderSinceUtc = null;
        _holderLinked = false;
        _holderMarked = false;
        _heldShowJson = null;
        _heldAir = null;
        if (_role != TwinRole.Standby) _services.OutputsHeldBy = "";
        Log.Info($"Twin: the standby {who} {reason} — this desk's outputs are its own again.");
        _services.Notify($"Twin: the standby {who} {reason} — this desk's outputs are its own again; OUTPUTS ON puts the show on here.");
    }

    /// <summary>
    /// The main takes the show back from a standby that ran it: the standby's show — the edits made
    /// while it ran — lands here first, what it had on air goes on here the way a restart puts it
    /// back, the standby is told to close its outputs and follow again, and the whole show goes back
    /// over the link so it is in step from here.
    /// </summary>
    public ActionResult TakeBack(ActionOrigin origin)
    {
        if (_role != TwinRole.Main) return ActionResult.Refused("This desk is not the main twin — Machine page, TWIN.");
        if (_handoverBusy) return ActionResult.Refused("A hand-back is in progress — the wall switch was asked and its answer is awaited.");
        if (_holder.Length == 0) return ActionResult.Refused("No standby has the show — nothing to take back.");
        Standby? holder;
        lock (_gate)
        {
            holder = _standbys.FirstOrDefault(s => s.HoldsShow);
        }
        if (holder is null) return ActionResult.Refused($"The standby {_holder} has the show but is not on the link — TAKE BACK once it is, or OUTPUTS ON if it is gone.");
        var now = Clock();
        var cue = _kernel.State.Twin.TakeBackCue;
        var sameMachine = holder.Machine.Length > 0 && string.Equals(holder.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        var tx = new TwinTransaction(HandoverKind.TakeBack, sameMachine ? HandoverShape.SameMachine : HandoverShape.AcrossMachines, cue.Length > 0, now);
        _handover = tx;
        var head = $"TOOK BACK from {holder.Name} at {now.ToLocalTime():HH:mm:ss}.";
        var notes = new List<string>();
        // The standby's show — the edits made while it ran — lands here first, once: a second
        // press after a stopped route finds it landed already.
        if (_heldShowJson is { } json)
        {
            var ok = false;
            _services.BulkEdit(() => ok = TwinSync.ApplyShow(_kernel.State, json));
            notes.Add(ok ? "its show landed here" : "its show could not be read — this desk's show stands");
            if (!ok) Log.Warn("Twin: the standby's show could not be read on TAKE BACK.");
            _heldShowJson = null;
        }
        var air = _heldAir;
        var words = head + (notes.Count > 0 ? " " + string.Join(", ", notes) + "." : "");

        if (tx.Shape == HandoverShape.AcrossMachines)
        {
            // The room looks at one of two machines through its switcher. This desk's picture goes
            // up first, on displays the room is not yet looking at; the room is pointed at it and
            // the switch answers; only then is the standby, whose picture the room was watching,
            // told to let go. A switch that fails leaves the standby up and the room on it.
            _services.OutputsHeldBy = "";
            _services.RecoverFromTwin(air, head, peer: holder.Name);
            tx.Reached(HandoverStage.TargetReady);
            if (tx.HasRoute)
            {
                var wall = FireWallSwitch(cue);
                tx.Reached(HandoverStage.RouteRequested);
                if (!wall.Ok) return StopTakeBack(tx, holder, cue, $"could not fire ({wall.Reason})", origin);
                words += " " + wall.Words;
                if (wall.Receipts is { } receipts)
                {
                    // The boxes answer on their own time: the standby is released when they have,
                    // and not at all when one of them says no or says nothing.
                    _handoverBusy = true;
                    var pendingWords = words;
                    receipts.ContinueWith(t => UiThread.Post(() =>
                    {
                        _handoverBusy = false;
                        if (tx.Stopped || tx.IsComplete) return;
                        var (ok, said) = ReadReceipts(t);
                        if (!ok)
                        {
                            StopTakeBack(tx, holder, cue, $"was not confirmed — {said}", origin);
                            return;
                        }
                        tx.Reached(HandoverStage.RouteConfirmed);
                        var done = FinishTakeBack(holder, tx, pendingWords + " " + said, origin);
                        _services.Notify(done);
                    }), TaskScheduler.Default);
                    return ActionResult.Requested($"TAKE BACK: the show is on here and the room was asked to look at this desk — the standby {holder.Name} is released once the switch answers.");
                }
                tx.Reached(HandoverStage.RouteConfirmed);
            }
            return ActionResult.Done(FinishTakeBack(holder, tx, words, origin));
        }
        // One machine: the standby's windows are these displays. It lets go first — HANDBACK
        // closes them — and this desk's picture goes up after: a dark instant, never two sets.
        CommitTakeBack(holder, tx);
        if (!holder.TryWrite(TwinMessage.Format(TwinWord.HandBack))) Drop(holder);
        tx.Reached(HandoverStage.OldOwnerReleased);
        _services.OutputsHeldBy = "";
        _services.RecoverFromTwin(air, head, peer: holder.Name);
        tx.Reached(HandoverStage.TargetReady);
        if (cue.Length > 0) words += " " + FireWallSwitch(cue).Words;   // a switch on one machine is the operator's own to have set; fired, never a fence
        _heldAir = null;
        _handoverNote = "";
        _pendingWhole = true;
        _pendingAir = true;
        ScheduleFlush();
        tx.Reached(HandoverStage.Complete);
        Log.Warn($"Twin: {words} ({origin.Label}) — {tx.Trail}");
        return ActionResult.Done(words);
    }

    /// <summary>Across machines, the route confirmed: this desk is the main again, the standby is released, the whole show goes back over the link.</summary>
    private string FinishTakeBack(Standby holder, TwinTransaction tx, string words, ActionOrigin origin)
    {
        CommitTakeBack(holder, tx);
        if (!holder.TryWrite(TwinMessage.Format(TwinWord.HandBack))) Drop(holder);
        tx.Reached(HandoverStage.OldOwnerReleased);
        _heldAir = null;
        _handoverNote = "";
        _pendingWhole = true;
        _pendingAir = true;
        ScheduleFlush();
        tx.Reached(HandoverStage.Complete);
        Log.Warn($"Twin: {words} ({origin.Label}) — {tx.Trail}");
        return words;
    }

    /// <summary>The route did not move the room: the standby stays up and the holder, the words say what finishes it.</summary>
    private ActionResult StopTakeBack(TwinTransaction tx, Standby holder, string cue, string reason, ActionOrigin origin)
    {
        tx.Stop(reason);
        _handoverNote = TwinTransaction.RouteStoppedWords(holder.Name, cue, reason);
        Log.Warn($"Twin: {_handoverNote} ({origin.Label}) — {tx.Trail}");
        _services.Notify(_handoverNote);
        return ActionResult.Failed(_handoverNote);
    }

    /// <summary>This desk is the main again: the holder's state cleared, the marker gone. The word to the standby follows, on the caller's thread, so it reads them in that order.</summary>
    private void CommitTakeBack(Standby holder, TwinTransaction tx)
    {
        holder.HoldsShow = false;
        _holderLinked = false;
        _holderMarked = false;
        _holder = "";
        _holderSinceUtc = null;
        try
        {
            TwinHandover.Clear(TwinHandover.StandbyHome(_kernel.Store.BaseDirectory));
        }
        catch (Exception)
        {
            // the standby clears its own; the marker holds nothing once the process stands by again
        }
        tx.Reached(HandoverStage.AuthorityCommitted);
    }

    /// <summary>
    /// What firing the wall-switch cue came to: no cue set, fired, or not — with the reason and the
    /// words for the line — and, when the cue sent lines to boxes that answer, the receipts still
    /// to come: the route is confirmed by them, never by the cue having fired.
    /// </summary>
    private readonly record struct WallSwitch(bool Ok, string Reason, string Words, Task<IReadOnlyList<DeviceReceipt>>? Receipts = null);

    /// <summary>
    /// The wall-switch cue — the room's own fence. A cue that puts this machine's input on the wall
    /// (a switcher's HTTP or OSC verb, a PJLink input, a matrix route), so whichever desk the room
    /// shows is the one running the show. Ok with no words when there is none.
    /// </summary>
    private WallSwitch FireWallSwitch(string cue)
    {
        if (cue.Length == 0) return new WallSwitch(true, "", "");
        var mark = _services.DeviceMark();
        var result = _services.Actions.Execute(new ShowAction(ShowActionKind.CueFire, cue), ActionOrigin.Recovery);
        var words = result.Ok ? $"Wall switch: cue '{cue}' fired." : $"Wall switch: cue '{cue}' could not fire — {result.Message}";
        if (result.Ok) Log.Info($"Twin: {words}");
        else Log.Warn($"Twin: {words}");
        if (!result.Ok) return new WallSwitch(false, result.Message, words);
        // Fired is dispatched. A cue that sent lines to boxes is confirmed by their receipts, up
        // to each device's own timeout; a cue with no box to answer is the operator's own switch.
        var receipts = _services.DevicePendingSince(mark) > 0 ? _services.DeviceConfirmSince(mark) : null;
        return new WallSwitch(true, "", words, receipts);
    }

    /// <summary>"Wall switch confirmed: Switcher: POST /route — accepted (200 OK)" or the first receipt that failed, for the words.</summary>
    private static (bool Ok, string Words) ReadReceipts(Task<IReadOnlyList<DeviceReceipt>> task)
    {
        if (!task.IsCompletedSuccessfully) return (false, "the receipts could not be read");
        var receipts = task.Result;
        var bad = receipts.FirstOrDefault(r => !r.Ok);
        return bad is not null
            ? (false, bad.Line)
            : (true, "Wall switch confirmed: " + string.Join("; ", receipts.Select(r => r.Line)) + ".");
    }

    private bool MainIsOnThisMachine() => _welcome is { } w && string.Equals(w.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>LINK on a caller node's Nodes page: follow that desk — its address and link port onto the twin settings; the link dials on the publish.</summary>
    public string LinkTo(NodeCard card)
    {
        if (!IsFollowerNode) return "Only a caller or a stage timer node links to a desk this way — the desk's own twin is set on the Machine page.";
        if (card.Kind != NodeKind.Desk) return $"{card.KindLabel} {card.Name} is not a desk to follow.";
        if (card.Address is null || card.LinkPort <= 0) return $"{card.Name} is not linking callers (its Nodes page: Accept caller nodes).";
        var cfg = _kernel.State.Twin;
        _services.BulkEdit(() =>
        {
            cfg.MainHost = card.Address.ToString();
            cfg.Port = card.LinkPort;
        });
        return $"Linking to {card.Name} at {card.Address}:{card.LinkPort} — the desk's twin key must be entered here (Machine page, TWIN) if it refuses.";
    }

    /// <summary>UNLINK on a caller node: leave the desk and plan on with the show as it stands here.</summary>
    public string Unlink()
    {
        if (!IsFollowerNode) return "Not a caller or a stage timer node.";
        _services.BulkEdit(() => _kernel.State.Twin.MainHost = "");
        return _kernel.Profile == NodeKind.Timer ? "Unlinked — the clock is this node's own again." : "Unlinked — planning on with the show as it stands here.";
    }

    private static long ProcessStartTicks()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; }
        catch { return 0; }
    }

    // ---- the standby: the link ---------------------------------------------------------

    /// <summary>Where the main is: the address the operator gave, else the one whose beacon says it is a twin main.</summary>
    private (string Host, int Port)? MainAddress()
    {
        var cfg = _kernel.State.Twin;
        if (cfg.MainHost.Length > 0) return (cfg.MainHost, cfg.Port);
        var beacon = _kernel.Beacon.LastBeacon;
        var from = _kernel.Beacon.LastFrom;
        if (beacon is { Twin: > 0 } && from is not null && beacon.Instance != _kernel.Beacon.Instance) return (from.Address.ToString(), beacon.Twin);
        return null;
    }

    private void Dial()
    {
        if (_dialling || _stream is not null || _phase is TwinPhase.Refused or TwinPhase.Off) return;
        var now = Clock();
        if (now - _lastDialUtc < TwinWatch.BeatEvery) return;
        _lastDialUtc = now;
        var address = MainAddress();
        if (address is null) return; // waiting for a beacon: the line says so
        var (host, port) = address.Value;
        if (_mainName.Length == 0) _mainName = host;
        var cts = _cts;
        if (cts is null) return;
        _dialling = true;
        _dialNonce = TwinAuth.NewNonce();
        var join = new TwinJoin(Name, Environment.MachineName, Instance, "", TookOver: _phase == TwinPhase.TookOver, Kind: IsFollowerNode ? NodeKinds.Wire(_kernel.Profile) : "standby", Nonce: _dialNonce).ToJson();
        _ = Task.Run(async () =>
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient { NoDelay = true };
                using var connectWait = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                connectWait.CancelAfter(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(host, port, connectWait.Token);
                var stream = client.GetStream();
                await WriteLineAsync(stream, TwinMessage.Format(TwinWord.Join, join), cts.Token);
                await UiThread.InvokeAsync(() =>
                {
                    _client = client;
                    _stream = stream;
                    _dialling = false;
                    _dialFailures = 0;
                });
                await ReadLoop(client, stream, cts.Token);
            }
            catch (Exception ex)
            {
                // The first failure is said, then one in thirty: a main that is down for an hour is one line a half-minute, not one a second.
                var failures = Interlocked.Increment(ref _dialFailures);
                if (!cts.IsCancellationRequested && (failures == 1 || failures % 30 == 0)) Log.Info($"Twin: could not reach the main at {host}:{port} — {ex.Message}");
                client?.Dispose();
                UiThread.Post(() =>
                {
                    _dialling = false;
                    if (ReferenceEquals(_client, client)) CloseLink();
                });
            }
        });
    }

    private async Task ReadLoop(TcpClient client, NetworkStream stream, CancellationToken ct)
    {
        var reader = new BoundedLineReader(stream, JoinLineBytes);         // the main's first word is short; the show that follows is not
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                reader.MaxLineBytes = LinkLineBytes;
                var msg = TwinMessage.Parse(line);
                if (msg.Word == TwinWord.Unknown) continue;
                await UiThread.InvokeAsync(() => OnLine(client, msg));
                if (msg.Word is TwinWord.Refused or TwinWord.Bye) break;
            }
        }
        catch (Exception)
        {
            // The main went away: the silence is counted below.
        }
        UiThread.Post(() =>
        {
            if (!ReferenceEquals(_client, client)) return; // an older link; a newer one is up
            CloseLink();
            if (_phase == TwinPhase.InStep)
            {
                _phase = TwinPhase.MainSilent;
                Log.Warn($"Twin: the link to the main {_mainName} dropped.");
            }
        });
    }

    /// <summary>One line from the main, on the UI thread: the show and its sections land here, the beats are counted here.</summary>
    private void OnLine(TcpClient client, TwinMessage msg)
    {
        if (!ReferenceEquals(_client, client)) return;
        var now = Clock();
        switch (msg.Word)
        {
            case TwinWord.Challenge:
            {
                // The main proves the key over this dial's nonce before this desk proves anything —
                // a main that cannot is a stranger on the port, and gets no proof, no show and no hold.
                var challenge = TwinChallenge.Parse(msg.Payload);
                var key = _kernel.State.Twin.Key;
                if (key.Length == 0)
                {
                    // Nothing to prove with: said here, in this desk's own words, not as a "wrong key" from the main.
                    _phase = TwinPhase.Refused;
                    _note = "this desk has no key — the main's key goes on the Machine page, TWIN";
                    Log.Warn("Twin: this desk has no key to prove to the main; the link is closed.");
                    CloseLink();
                    break;
                }
                if (challenge is null || !TwinAuth.Verify(key, _dialNonce, challenge.Nonce, challenge.Proof))
                {
                    _phase = TwinPhase.Refused;
                    _note = "the main did not prove the key — is its key the same as this desk's?";
                    Log.Warn($"Twin: the main at {_mainName} could not prove the key; the link is closed.");
                    CloseLink();
                    break;
                }
                if (!TryWriteToMain(TwinMessage.Format(TwinWord.Proof, TwinAuth.Proof(key, challenge.Nonce, Instance)))) CloseLink();
                break;
            }
            case TwinWord.Welcome:
                _welcome = TwinWelcome.Parse(msg.Payload);
                if (_welcome is not null && _welcome.Name.Length > 0) _mainName = _welcome.Name;
                _lastHeardUtc = now;
                _note = "";
                if (_phase == TwinPhase.TookOver) SendWhatIHave();
                // A caller's own cues, planned before the desk's show lands over them: kept, and
                // offered once the show is here — the desk decides.
                if (_kernel.Profile == NodeKind.Caller && !_planOffered && _kernel.State.Stacks.Any(s => s.Cues.Count > 0)) _myPlanJson = CuePlan.Json(_kernel.State);
                break;
            case TwinWord.Live:
                _lastHeardUtc = now;
                if (IsFollowerNode) AdoptLive(msg.Payload);
                break;
            case TwinWord.Refused:
                _phase = TwinPhase.Refused;
                _note = msg.Payload;
                Log.Warn($"Twin: the main {_mainName} refused the link — {msg.Payload}.");
                break;
            case TwinWord.Show when _phase == TwinPhase.TookOver:
            case TwinWord.Section when _phase == TwinPhase.TookOver:
            case TwinWord.Air when _phase == TwinPhase.TookOver:
                // This desk has the show: nothing the main sends lands until it takes the show back.
                _lastHeardUtc = now;
                break;
            case TwinWord.HandBack:
                HandedBack(now);
                break;
            case TwinWord.Show:
            {
                var ok = false;
                RememberLanded(msg.Payload);
                _services.BulkEdit(() => ok = TwinSync.ApplyShow(_kernel.State, msg.Payload));
                if (ok)
                {
                    _sectionsApplied += TwinSync.MirroredSections.Count;
                    var first = _phase != TwinPhase.InStep;
                    _phase = TwinPhase.InStep;
                    _lastHeardUtc = now;
                    _services.NotifyShowMirrored(null);
                    if (first)
                    {
                        if (IsFollowerNode)
                        {
                            Log.Info($"Twin: this {NodeKinds.Wire(_kernel.Profile)} node is in step with {_mainName}.");
                            _services.Notify(_kernel.Profile == NodeKind.Timer
                                ? $"Nodes: in step with {_mainName} — its clock and its messages show here."
                                : $"Nodes: in step with {_mainName} — its show is here, and GO from here runs there.");
                            if (_kernel.Profile == NodeKind.Caller && _myPlanJson is { } plan)
                            {
                                _myPlanJson = null;
                                _planOffered = true;
                                var offer = TwinMessage.Format(TwinWord.Plan, plan);
                                _ = Task.Run(() => TryWriteToMain(offer));
                            }
                        }
                        else
                        {
                            Log.Info($"Twin: in step with the main {_mainName}; outputs held closed.");
                            _services.Notify($"Twin: in step with {_mainName} — the show is mirrored here and the outputs are held closed.");
                        }
                    }
                }
                else
                {
                    Log.Warn("Twin: the main sent a show this build could not read.");
                }
                break;
            }
            case TwinWord.Section:
            {
                var ok = false;
                if (TwinSync.IsCallerSection(msg.Name)) _lastLanded[msg.Name] = msg.Payload;   // the desk's own: not an edit of this caller's to send back
                _services.BulkEdit(() => ok = TwinSync.ApplySection(_kernel.State, msg.Name, msg.Payload));
                if (ok)
                {
                    _sectionsApplied++;
                    _lastHeardUtc = now;
                    if (_phase == TwinPhase.MainSilent) { _phase = TwinPhase.InStep; _note = ""; }
                    _services.NotifyShowMirrored(new[] { msg.Name });
                }
                else
                {
                    Log.Warn($"Twin: the section '{msg.Name}' from the main could not land.");
                }
                break;
            }
            case TwinWord.Air:
                _mirroredAir = msg.Payload == "null" ? null : ReadAir(msg.Payload);
                _lastHeardUtc = now;
                break;
            case TwinWord.Beat:
                _lastHeardUtc = now;
                if (_phase == TwinPhase.MainSilent) { _phase = TwinPhase.InStep; _note = ""; }
                break;
            case TwinWord.Bye:
                // A main leaving on purpose (its role changed, a clean exit) is not a main that died: nothing is taken over.
                CloseLink();
                if (_phase != TwinPhase.TookOver)
                {
                    _phase = TwinPhase.Connecting;
                    _lastHeardUtc = null;
                    _note = "";
                }
                Log.Info($"Twin: the main {_mainName} said goodbye.");
                break;
        }
    }

    /// <summary>A standby that has the show, welcomed back by the main: its show and its air go to the main, for TAKE BACK.</summary>
    private void SendWhatIHave()
    {
        string show;
        string air;
        try
        {
            show = TwinMessage.Format(TwinWord.Show, TwinSync.WireJson(_kernel.State, _kernel.State.Twin.SendSecrets));
            air = AirLine();
        }
        catch (Exception ex)
        {
            Log.Warn("Twin: this desk's show could not be written for the main.", ex);
            return;
        }
        _ = Task.Run(() =>
        {
            TryWriteToMain(show);
            TryWriteToMain(air);
        });
    }

    /// <summary>The main took the show back: the outputs close and are held again, the marker goes, the show that follows the word puts this desk in step.</summary>
    private void HandedBack(DateTime now)
    {
        if (_phase != TwinPhase.TookOver) return;
        var main = _mainName.Length > 0 ? _mainName : "the main";
        _services.OutputsHeldBy = "this desk is the standby twin";
        if (_services.OutputsLive) _services.CloseOutputs();
        try
        {
            TwinHandover.Clear(_kernel.Store.BaseDirectory);
        }
        catch (Exception)
        {
            // a folder that would not take the marker did not take one
        }
        _phase = TwinPhase.Connecting;
        _note = "";
        _lastHeardUtc = now;
        _mirroredAir = null;
        Log.Warn($"Twin: {main} took the show back; this desk stands by again.");
        _services.Notify($"Twin: {main} took the show back — this desk's outputs are held closed and it follows again.");
    }

    private static RecoverySnapshot? ReadAir(string json)
    {
        try { return JsonUtil.Deserialize<RecoverySnapshot>(json); }
        catch (JsonException ex)
        {
            Log.Warn("Twin: the air record could not be read.", ex);
            return null;
        }
    }

    private void CloseLink()
    {
        var client = _client;
        _client = null;
        _stream = null;
        try { client?.Dispose(); } catch { /* already down */ }
    }

    private bool TryWriteToMain(string line)
    {
        var stream = _stream;
        if (stream is null) return false;
        try
        {
            lock (_writeGate)
            {
                stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- the standby: the show ---------------------------------------------------------------

    /// <summary>
    /// The standby runs the show from here. The fence first, before anything is let go of: the
    /// takeover is marked on disk for a main on this machine that comes back, and a main on this
    /// very machine that is still up but stopped beating — hung; its windows would play on under
    /// nobody's hand and ours would open behind them — is ended, and only once it is provably gone
    /// does the hold on the outputs lift and the air record the main sent last go back on, the way
    /// a watchdog restart puts it back. Either fence failing, nothing is taken by itself: two desks
    /// on one set of screens is the one failure worse than one being down. A press can override
    /// (TAKE OVER ANYWAY, TWIN TAKEOVER FORCE), and says so in its words.
    /// </summary>
    public ActionResult TakeOver(ActionOrigin origin, bool force = false)
    {
        if (IsFollowerNode) return ActionResult.Refused($"A {NodeKinds.Label(_kernel.Profile).ToLowerInvariant()} node never takes the show over — it has no outputs to take it onto.");
        if (_role != TwinRole.Standby) return ActionResult.Refused("This desk is not a standby twin — Machine page, TWIN.");
        if (_phase == TwinPhase.TookOver) return ActionResult.Done("This desk already took the show over.");
        if (_handoverBusy) return ActionResult.Refused("A takeover is in progress — the wall switch was asked and its answer is awaited.");
        if (_welcome is null) return ActionResult.Refused("Nothing to take over: no main has been joined yet.");
        var now = Clock();
        var main = _mainName.Length > 0 ? _mainName : "the main";
        var notes = new List<string>();
        var w = _welcome;
        var localMain = MainIsOnThisMachine();
        var tx = new TwinTransaction(HandoverKind.TakeOver, localMain ? HandoverShape.SameMachine : HandoverShape.AcrossMachines, _kernel.State.Twin.TakeOverCue.Length > 0, now);
        _handover = tx;

        // The marker: said on disk, in this desk's own folder, so a main on this machine that comes
        // back reads it before its first window opens and holds its outputs while this process
        // lives. Only such a main ever reads it; one elsewhere loses nothing by its absence.
        string? fence = null;
        var marked = false;
        try
        {
            TwinHandover.Write(_kernel.Store.BaseDirectory, new TwinTookOverMarker(Name, Environment.MachineName, Environment.ProcessId, ProcessStartTicks(), Environment.ProcessPath ?? "", now, main));
            marked = true;
        }
        catch (Exception ex)
        {
            if (localMain) fence = $"the takeover could not be marked on disk ({ex.Message})";
            else Log.Warn("Twin: the takeover could not be marked on disk — the main is on another machine and nothing reads it there.", ex);
        }

        // The hung main on this very machine: ended, and only a process that is provably the one
        // the welcome named and provably Patterns — a pid is never enough to end something by. A
        // process that is gone, or whose id was handed out again, holds nothing; one that is up
        // but cannot be read from here is a fence, not an absence: it may well still have the
        // screens, and "could not see it" is never "it is gone".
        if (fence is null && localMain && w.Pid > 0 && w.Pid != Environment.ProcessId)
        {
            var sight = Probe.Look(w.Pid);
            if (sight.IsGoneOrReused(w.StartedAtUtcTicks))
            {
                // gone with its windows, or another program wearing its id: nothing to end
            }
            else if (sight.IsUnreadable)
            {
                fence = $"{main}'s process (pid {w.Pid}) is still up but cannot be read from here (another user's, or elevated?), so it cannot be ended";
            }
            else if (!OutputTakeover.IsPatterns(sight.ExePath, w.ExePath))
            {
                fence = $"pid {w.Pid} is up with {main}'s start time but is not Patterns ({sight.ExePath}), so it is not ended";
            }
            else if (Probe.Kill(w.Pid))
            {
                notes.Add($"ended {main}'s process (pid {w.Pid}) — it had stopped answering");
            }
            else
            {
                fence = $"{main}'s process (pid {w.Pid}) is still up and could not be ended";
            }
        }

        if (fence is not null && !force)
        {
            tx.Stop(fence);
            return RefuseTakeOver(fence, marked, origin);
        }
        if (fence is not null) notes.Add(fence + " — taken over anyway");

        // The wall switch — the room's own fence, between two machines — fires before a single
        // output opens here, so the room is looking at this machine by the time its picture is
        // up. Taken by itself from another machine, a cue that could not fire refuses the
        // takeover: the room could not be told which desk to show, so no desk is changed; the
        // next try comes after the usual pause. A press goes ahead and carries the failure in its
        // words — the operator can switch the wall by hand.
        var wall = FireWallSwitch(_kernel.State.Twin.TakeOverCue);
        if (tx.HasRoute && !localMain) tx.Reached(HandoverStage.RouteRequested);
        if (!wall.Ok && !localMain && origin.Kind == OriginKind.Recovery && !force)
        {
            var why = $"the wall switch cue '{_kernel.State.Twin.TakeOverCue}' could not fire ({wall.Reason}), so the room could not be told to show this desk";
            tx.Stop(why);
            return RefuseTakeOver(why, marked, origin);
        }
        if (wall.Ok && wall.Receipts is { } receipts && !localMain)
        {
            // The switcher answers on its own time: the outputs open here once it has said yes. By
            // itself, a switch that said no or nothing refuses the takeover and the hold stays; a
            // press goes on and carries the answer in its words.
            _handoverBusy = true;
            var wallWords = wall.Words;
            receipts.ContinueWith(t => UiThread.Post(() =>
            {
                _handoverBusy = false;
                if (_phase == TwinPhase.TookOver || _role != TwinRole.Standby) return;
                var (ok, said) = ReadReceipts(t);
                if (!ok && origin.Kind == OriginKind.Recovery && !force)
                {
                    var why = $"the wall switch cue '{_kernel.State.Twin.TakeOverCue}' was not confirmed ({said}), so the room may not be showing this desk";
                    tx.Stop(why);
                    _nextAutoTakeOverUtc = Clock() + TwinWatch.RetryAfterRefusal;
                    RefuseTakeOver(why, marked, origin);
                    return;
                }
                if (!ok) notes.Add($"the wall switch was not confirmed ({said}) — taken over anyway; switch the wall by hand");
                tx.Reached(HandoverStage.RouteConfirmed);
                _services.Notify(CompleteTakeOver(tx, main, notes, ok ? wallWords + " " + said : wallWords, origin, now));
            }), TaskScheduler.Default);
            return ActionResult.Requested($"Taking over from {main}: the wall switch was asked — the outputs open here once it answers.");
        }
        if (tx.HasRoute && !localMain) tx.Reached(HandoverStage.RouteConfirmed);   // a press carries a failed switch in its words and goes on: the operator switches by hand
        return ActionResult.Done(CompleteTakeOver(tx, main, notes, wall.Words, origin, now));
    }

    /// <summary>The show is this desk's: the link closed, the phase moved, the hold lifted, the air record put back — and said.</summary>
    private string CompleteTakeOver(TwinTransaction tx, string main, List<string> notes, string wallWords, ActionOrigin origin, DateTime now)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource(); // the redial never restarts on its own
        CloseLink();
        _phase = TwinPhase.TookOver;
        _note = $"at {now.ToLocalTime():HH:mm:ss}";
        _services.OutputsHeldBy = "";
        tx.Reached(HandoverStage.AuthorityCommitted);
        // The beat goes on: this desk keeps dialling, so the main, once it is back, can take the show back over the link.
        StartBeating(_cts);
        _lastDialUtc = DateTime.MinValue;
        var head = $"TOOK OVER from {main} {_note}" + (notes.Count > 0 ? " — " + string.Join(", ", notes) : "") + ".";
        _services.RecoverFromTwin(_mirroredAir, head);
        tx.Reached(HandoverStage.TargetReady);
        tx.Reached(HandoverStage.Complete);
        Log.Warn($"Twin: {head} ({origin.Label}) — {tx.Trail}");
        return wallWords.Length > 0 ? head + " " + wallWords : head;
    }

    /// <summary>A takeover a fence stopped: the marker taken back (one that says this desk has the show would be a lie), the reason on the line, said once, and refused.</summary>
    private ActionResult RefuseTakeOver(string fence, bool marked, ActionOrigin origin)
    {
        if (marked) TwinHandover.Clear(_kernel.Store.BaseDirectory);
        var refusal = $"Not taken over: {fence}. The outputs stay held closed — TAKE OVER ANYWAY (Machine page, TWIN) or TWIN TAKEOVER FORCE overrides, by hand only.";
        var first = _note != "not taken over: " + fence;
        _note = "not taken over: " + fence;
        Log.Warn($"Twin: {refusal} ({origin.Label})");
        if (first) _services.Notify(refusal);
        return ActionResult.Refused(refusal);
    }

    /// <summary>After a takeover: the outputs held again, the link dialled again, the show mirrored again.</summary>
    public ActionResult StandByAgain(ActionOrigin origin)
    {
        if (_role != TwinRole.Standby) return ActionResult.Refused("This desk is not a standby twin — Machine page, TWIN.");
        if (_phase != TwinPhase.TookOver) return ActionResult.Done("This desk is standing by already.");
        _services.OutputsHeldBy = "this desk is the standby twin";
        if (_services.OutputsLive) _services.CloseOutputs();
        try
        {
            TwinHandover.Clear(_kernel.Store.BaseDirectory);
        }
        catch (Exception)
        {
            // nothing marked
        }
        CloseLink(); // a link dialled after the takeover said TookOver: the next join says standby
        _phase = TwinPhase.Connecting;
        _note = "";
        _lastHeardUtc = null;
        _mirroredAir = null;
        _lastDialUtc = DateTime.MinValue;
        Log.Info($"Twin: standing by again for {_mainName} ({origin.Label}).");
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        StartBeating(_cts);
        Dial();
        return ActionResult.Done($"Standing by again — the outputs are held closed and the link to {(_mainName.Length > 0 ? _mainName : "the main")} is being dialled.");
    }

    private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
    }

    public void Dispose()
    {
        Stop(sayGoodbye: true);
        // A clean exit ends the standby process it started — unless that process has the show, or this is a restart and the next desk adopts it.
        _launcher.End(standbyHoldsShow: _holder.Length > 0 || _holderMarked || KeepStandbyOnExit);
        _kernel.Bus.SectionsPublished -= OnBuilt;
        _services.RecoveryMoved -= OnRecoveryMoved;
    }

    /// <summary>One joined standby on the main's side: its socket, its name, when it last beat. Writes are serialised per standby.</summary>
    private sealed class Standby : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly object _writeGate = new();
        private long _lastBeatTicks;

        public Standby(TcpClient client, NetworkStream stream, string name, DateTime nowUtc)
        {
            _client = client;
            _stream = stream;
            Name = name;
            LastBeatUtc = nowUtc;
        }

        public string Name { get; }

        /// <summary>It ran the show while this desk was away: nothing is mirrored to it, and this desk's outputs wait on TAKE BACK.</summary>
        public bool HoldsShow { get; set; }

        /// <summary>A caller node: follows the show and calls it, sends its cues back, never holds an output.</summary>
        public bool IsCaller { get; init; }

        /// <summary>A follower — a caller or a stage timer: mirrored to and sent the live word, never a standby that could take the show.</summary>
        public bool IsFollower { get; init; }

        /// <summary>The peer's own instance id, so its own edits are not echoed back to it.</summary>
        public string Instance { get; init; } = "";

        /// <summary>The computer it runs on — the same as this one means its windows are these displays.</summary>
        public string Machine { get; init; } = "";

        public DateTime LastBeatUtc
        {
            get => new(Interlocked.Read(ref _lastBeatTicks), DateTimeKind.Utc);
            set => Interlocked.Exchange(ref _lastBeatTicks, value.Ticks);
        }

        public bool TryWrite(string line)
        {
            try
            {
                lock (_writeGate)
                {
                    _stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            try { _client.Dispose(); } catch { /* already down */ }
        }
    }
}
