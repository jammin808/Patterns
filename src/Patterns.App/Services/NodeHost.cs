using Patterns.Audience;
using Patterns.Devices;
using Avalonia.Threading;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// A node's action layer: the verbs a node runs itself and "not on this node" for every verb
/// that is the desk's. The same vocabulary as the desk's, so a cue, Companion or the assistant
/// say the same words to either; the answer says whose the verb is. A caller or a stage timer in
/// step with a desk calls the desk: every verb but a note runs there, journaled there as this
/// node's. Alone, a caller rehearses its stack on paper and a timer keeps its own clock; the
/// arcade runs its games and the room on any node.
/// </summary>
public sealed class NodeActions : IActionLayer
{
    private readonly NodeHost _host;

    public NodeActions(NodeHost host) => _host = host;

    /// <summary>Raised on the UI thread after every action, whatever its outcome.</summary>
    public event Action<ShowAction, ActionOrigin, ActionResult>? Performed;

    private ServiceKernel Kernel => _host.Kernel;

    public ActionResult Execute(ShowAction action, ActionOrigin origin)
    {
        ActionResult result;
        // In step with a desk, this node calls it: the verb runs there as this node's own. A note
        // is the caller's pad and stays here; a receipt goes where the message came from.
        if (_host.Twin is { IsLinkedToDesk: true } twin && action.Kind != ShowActionKind.Note)
        {
            result = twin.Forward(action, origin);
            Kernel.Journal.Record(origin.Label, action.Kind.ToString(), action.Target, result.Status.ToString(), result.Message);
            Performed?.Invoke(action, origin, result);
            return result;
        }
        try
        {
            result = Run(action, origin);
        }
        catch (Exception ex)
        {
            Log.Error($"Action {action} failed on the node.", ex);
            result = ActionResult.Failed(ex.Message);
        }
        if (action.Kind is not (ShowActionKind.Note or ShowActionKind.CueStandby or ShowActionKind.StageAck))
        {
            Kernel.Journal.Record(origin.Label, action.Kind.ToString(), action.Target, result.Status.ToString(), result.Message);
        }
        Performed?.Invoke(action, origin, result);
        return result;
    }

    private ActionResult Run(ShowAction a, ActionOrigin origin)
    {
        var kind = _host.Kind;
        if (ArcadeService.IsArcadeKind(a.Kind)) return kind == NodeKind.Arcade ? _host.Arcade.Run(a) : ActionResult.Refused(NotHere(a.Kind, "the arcade node's"));
        if (a.Kind is ShowActionKind.AudienceOn or ShowActionKind.AudienceOff) return _host.Play.RunAudience(a);
        if (PlayService.IsPlayKind(a.Kind)) return _host.Play.Run(a);
        if (a.Kind == ShowActionKind.Note) return ActionResult.Done("Noted.");
        if (IsCueKind(a.Kind)) return kind == NodeKind.Caller ? RunCue(a, origin) : ActionResult.Refused(NotHere(a.Kind, "the caller's"));
        if (IsStageKind(a.Kind)) return _host.IsFollower ? RunStage(a, origin) : ActionResult.Refused(NotHere(a.Kind));
        if (IsCountdownKind(a.Kind)) return _host.IsFollower ? RunCountdown(a) : ActionResult.Refused(NotHere(a.Kind));
        if (a.Kind == ShowActionKind.UpdateApply) return _host.Updates.Apply(a.Target, origin);
        if (a.Kind == ShowActionKind.Restart) return Restart(a, origin);
        return ActionResult.Refused(NotHere(a.Kind));
    }

    /// <summary>RESTART with the passcode: the machine's own verb on a node as on a desk — the watchdog brings it back.</summary>
    private ActionResult Restart(ShowAction a, ActionOrigin origin)
    {
        if (!Kernel.Gate.Check(Kernel.State.Install.AdminPasscode, a.Target, DateTime.UtcNow)) return ActionResult.Refused($"Restart refused — {Kernel.Gate.Reason}.");
        return _host.RestartInPlace(origin);
    }

    /// <summary>The stack's own verbs — the ones a caller runs alone, on paper — the plan's slip among them.</summary>
    public static bool IsCueKind(ShowActionKind kind) => kind is ShowActionKind.CueGo or ShowActionKind.CueStandby or ShowActionKind.CueHoldOn or ShowActionKind.CueHoldOff
        or ShowActionKind.CueFire or ShowActionKind.ListArm or ShowActionKind.ListDisarm or ShowActionKind.ListGo or ShowActionKind.ListBack or ShowActionKind.ListReset
        or ShowActionKind.PlanShift or ShowActionKind.PlanResume or ShowActionKind.PlanCatchUp;

    /// <summary>The stage's verbs: the timer's transport, a message, its receipt.</summary>
    public static bool IsStageKind(ShowActionKind kind) => kind is ShowActionKind.TimerPause or ShowActionKind.TimerResume or ShowActionKind.TimerAdd or ShowActionKind.TimerFlash
        or ShowActionKind.StageMessage or ShowActionKind.StageClear or ShowActionKind.StageAck;

    /// <summary>The countdown's verbs — the stage timer's clock, started, aimed, stopped, labelled.</summary>
    public static bool IsCountdownKind(ShowActionKind kind) => kind is ShowActionKind.CountdownStart or ShowActionKind.CountdownTo or ShowActionKind.CountdownStop
        or ShowActionKind.CountdownToggle or ShowActionKind.CountdownLabel or ShowActionKind.CountdownFollow;

    // ---- the stack, on paper ----------------------------------------------------------------

    private ActionResult RunCue(ShowAction a, ActionOrigin origin)
    {
        var stack = _host.CueStack;
        switch (a.Kind)
        {
            case ShowActionKind.CueGo:
                return stack.Go(origin, a.Target.Length == 0 ? null : a.Target);
            case ShowActionKind.CueStandby:
            {
                var word = a.Target.Trim();
                var next = word.Equals("next", StringComparison.OrdinalIgnoreCase);
                if (next || word.Equals("prev", StringComparison.OrdinalIgnoreCase))
                {
                    if (!stack.StandbyMove(next ? +1 : -1)) return ActionResult.Refused("no cue that way");
                    var at = stack.StandbyCue;
                    return ActionResult.Done(at is null ? "Standby moved." : $"Standby: {at.Number} {at.Name}");
                }
                var cue = stack.Stack.Cues.FirstOrDefault(c => c.Id == word)
                          ?? (CueNumber.Parse(word) is not null ? stack.Stack.Cues.FirstOrDefault(c => CueNumber.Compare(c.Number, word) == 0) : null)
                          ?? stack.Stack.Cues.FirstOrDefault(c => string.Equals(c.Name, word, StringComparison.OrdinalIgnoreCase));
                if (cue is null) return ActionResult.Refused($"no cue '{word}'");
                stack.Standby(cue.Id);
                return ActionResult.Done($"Standby: {cue.Number} {cue.Name}");
            }
            case ShowActionKind.CueHoldOn:
            case ShowActionKind.CueHoldOff:
            {
                var hold = a.Kind == ShowActionKind.CueHoldOn;
                stack.SetHold(hold, origin);
                return ActionResult.Done(hold ? "HOLD — GO is refused until released." : "HOLD released.");
            }
            case ShowActionKind.PlanShift:
                return CueTiming.ParseDelta(a.Value) is { } delta ? ActionResult.Done(stack.ShiftPlan(delta, origin)) : ActionResult.Refused($"'{a.Value}' is not a slip — +2:00, -0:30, +90.");
            case ShowActionKind.PlanResume:
                return ActionResult.Done(stack.ResumeNow(origin));
            case ShowActionKind.PlanCatchUp:
                return ActionResult.Done(stack.CatchUp(origin));
            case ShowActionKind.CueFire:
            {
                var found = CueStacks.FindCueByWord(Kernel.State, a.Target);
                if (found is null) return ActionResult.Refused($"No cue '{a.Target}'.");
                return ((ICueHost)_host).RunCue(found.Value.Stack, found.Value.Cue, origin);
            }
            default:
            {
                var list = CueStacks.Find(Kernel.State, a.Target);
                if (list is null) return ActionResult.Refused($"No cue list '{a.Target}'.");
                var rt = Kernel.Cues.For(list);
                switch (a.Kind)
                {
                    case ShowActionKind.ListArm:
                    case ShowActionKind.ListDisarm:
                    {
                        var arm = a.Kind == ShowActionKind.ListArm;
                        if (ReferenceEquals(list, stack.Stack))
                        {
                            stack.SetArmed(arm, origin);
                            return ActionResult.Done(arm ? "Cue stack armed — on paper: GO moves the stack and runs nothing." : "Cue stack disarmed.");
                        }
                        rt.Armed = arm;
                        return ActionResult.Done(arm ? $"{list.Name} armed." : $"{list.Name} disarmed.");
                    }
                    case ShowActionKind.ListReset:
                        rt.CurrentIndex = -1;
                        return ActionResult.Done($"{list.Name} reset to the start.");
                    case ShowActionKind.ListGo:
                        return StepList(list, +1, origin);
                    default:
                        return StepList(list, -1, origin);
                }
            }
        }
    }

    /// <summary>A list stepped on paper: the next enabled cue is read and the place moves; a disabled one is passed over.</summary>
    private ActionResult StepList(CueStackConfig list, int delta, ActionOrigin origin)
    {
        var rt = Kernel.Cues.For(list);
        var cues = list.Cues;
        var current = rt.CurrentIndex;
        for (var hops = 0; hops < cues.Count; hops++)
        {
            if (PresenterLogic.Advance(current, cues.Count, delta, list.LoopAtEnd) is not { } idx) return ActionResult.Refused($"No cue left to read in {list.Name}.");
            if (!cues[idx].Enabled)
            {
                current = idx;
                continue;
            }
            rt.CurrentIndex = idx;
            return ((ICueHost)_host).RunCue(list, cues[idx], origin);
        }
        return ActionResult.Refused($"No cue in {list.Name} can be read.");
    }

    // ---- the stage and the clock, this node's own while it follows no desk ------------------

    private ActionResult RunStage(ShowAction a, ActionOrigin origin)
    {
        var stage = _host.Stage;
        return a.Kind switch
        {
            ShowActionKind.TimerPause => stage.Pause(),
            ShowActionKind.TimerResume => stage.Resume(),
            ShowActionKind.TimerAdd => stage.Add(a.Value),
            ShowActionKind.TimerFlash => stage.Flash(),
            ShowActionKind.StageMessage => stage.Message(a.Target.Length > 0 ? a.Target : "speaker", a.Value, origin.Label),
            ShowActionKind.StageClear => stage.Clear(),
            _ => stage.Ack(a.Value.Trim()) is { } seen ? ActionResult.Done(seen) : ActionResult.Refused("no such message, or seen already"),
        };
    }

    private ActionResult RunCountdown(ShowAction a)
    {
        var countdown = Kernel.State.Countdown;
        switch (a.Kind)
        {
            case ShowActionKind.CountdownStart when a.Value.Trim().Length > 0:
            {
                if (!double.TryParse(a.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
                {
                    return ActionResult.Refused("A countdown needs a number of minutes above zero.");
                }
                _host.EditAir(air =>
                {
                    air.Countdown.TargetKind = CountdownTargetKind.Duration;
                    air.Countdown.DurationMinutes = minutes;
                    air.Countdown.ArmedAtUtc = DateTime.UtcNow;
                    air.Countdown.Enabled = true;
                });
                return ActionResult.Done($"Countdown running: {minutes:0.#} min.");
            }
            case ShowActionKind.CountdownStop:
                _host.EditAir(air => air.Countdown.Enabled = false);
                return ActionResult.Done("Countdown off.");
            case ShowActionKind.CountdownFollow:
            {
                var follow = !a.Value.Equals("off", StringComparison.OrdinalIgnoreCase);
                _host.EditAir(air => air.Countdown.FollowPlan = follow);
                return ActionResult.Done(follow ? "Countdown follows the running order — the standby cue's planned start is its target." : "Countdown no longer follows the running order.");
            }
            case ShowActionKind.CountdownToggle when countdown.Enabled:
                _host.EditAir(air => air.Countdown.Enabled = false);
                return ActionResult.Done("Countdown off.");
            case ShowActionKind.CountdownTo:
            {
                if (!CountdownService.TryParseTime(a.Value, out var timeOfDay)) return ActionResult.Refused($"'{a.Value}' is not a time of day — HH:mm, 24-hour.");
                var target = $"{(int)timeOfDay.TotalHours:00}:{timeOfDay.Minutes:00}";
                _host.EditAir(air =>
                {
                    air.Countdown.TargetKind = CountdownTargetKind.TimeOfDay;
                    air.Countdown.TargetTime = target;
                    air.Countdown.Enabled = true;
                });
                return ActionResult.Done($"Countdown to {target}.");
            }
            case ShowActionKind.CountdownLabel:
            {
                var label = a.Value.Trim();
                _host.EditAir(air => air.Countdown.Label = label);
                return ActionResult.Done(label.Length > 0 ? $"Countdown label: '{label}'." : "Countdown label cleared.");
            }
            default:
            {
                // A bare START, or the toggle's on half: the countdown as it is set up — the time it points at, else its duration from now.
                if (countdown.TargetKind == CountdownTargetKind.TimeOfDay && CountdownService.TryParseTime(countdown.TargetTime, out _))
                {
                    var to = countdown.TargetTime;
                    _host.EditAir(air => air.Countdown.Enabled = true);
                    return ActionResult.Done($"Countdown to {to}.");
                }
                var configured = countdown.DurationMinutes;
                _host.EditAir(air =>
                {
                    air.Countdown.TargetKind = CountdownTargetKind.Duration;
                    air.Countdown.ArmedAtUtc = DateTime.UtcNow;
                    air.Countdown.Enabled = true;
                });
                return ActionResult.Done($"Countdown running: {configured:0.#} min.");
            }
        }
    }

    /// <summary>"Blackout on is the desk's — not on this arcade node; send it to the desk's wire." — or, on a follower, where LINK would take it.</summary>
    public string NotHere(ShowActionKind kind, string whose = "the desk's")
        => $"{ActionSpec.Label(kind)} is {whose} — not on this {NodeKinds.Label(_host.Kind).ToLowerInvariant()} node; "
           + (whose == "the desk's" && _host.IsFollower ? "LINK to a desk on the Nodes page and it runs there." : "send it to the desk's wire.");
}

/// <summary>
/// A node's wire dispatcher: PING, HELLO and STATUS; the arcade's, the room's, the nodes', the
/// stage's and the link's status; the cue list of a caller; every action through the node's
/// action layer; and for the queries that are the desk's alone (the show lock, the calibration,
/// rig day, the assistant), an ERR that says so.
/// </summary>
public sealed class NodeRouter : IRouter
{
    private readonly NodeHost _host;

    public NodeRouter(NodeHost host) => _host = host;

    public Func<long>? Rev { get; set; }

    public async Task<string> ExecuteAsync(RemoteCommand cmd, ActionOrigin? origin = null)
    {
        try
        {
            return await UiThread.InvokeAsync(() => Execute(cmd, origin ?? new ActionOrigin(OriginKind.Tcp)));
        }
        catch (Exception ex)
        {
            Log.Error("Node command failed.", ex);
            return ControlProtocol.Err(ex.Message);
        }
    }

    private string Execute(RemoteCommand cmd, ActionOrigin origin)
    {
        switch (cmd.Kind)
        {
            case RemoteCommandKind.Ping:
                return ControlProtocol.Ok("PONG");
            case RemoteCommandKind.Hello:
                return ControlProtocol.Ok();
            case RemoteCommandKind.Status:
                return ControlProtocol.Ok(StateJson());
            case RemoteCommandKind.Unknown:
                return ControlProtocol.Err($"unknown command '{cmd.Text}'");
            case RemoteCommandKind.ArcadeStatus:
                return ControlProtocol.Ok(_host.Arcade.StatusJson(cmd.Text));
            case RemoteCommandKind.PlayStatus:
                return ControlProtocol.Ok(_host.Play.StatusJson(cmd.Text));
            case RemoteCommandKind.NodesStatus:
                return ControlProtocol.Ok(_host.Kernel.Nodes.StatusJson());
            case RemoteCommandKind.StageStatus when _host.IsFollower:
                return ControlProtocol.Ok(_host.Stage.StatusJson());
            case RemoteCommandKind.CueList when _host.Kind == NodeKind.Caller:
                return ControlProtocol.Ok(CueListJson());
            case RemoteCommandKind.TwinStatus when _host.Twin is { } twin:
                return ControlProtocol.Ok(twin.StatusJson());
            case RemoteCommandKind.Action:
            {
                var result = _host.Actions.Execute(cmd.Action, origin);
                return result.Ok ? ControlProtocol.Ok() : ControlProtocol.Err(result.Message);
            }
            default:
                return ControlProtocol.Err($"{cmd.Kind} is the desk's — not on this {NodeKinds.Label(_host.Kind).ToLowerInvariant()} node");
        }
    }

    /// <summary>What a node says of itself: its kind, its machine, its show, its ports, the arcade's words, the room's code — and, on a follower, its link and its place in the stack.</summary>
    public string StateJson()
    {
        var s = _host.State;
        var stack = _host.CueStack;
        var rt = stack.Runtime;
        return JsonUtil.SerializeCompact(new
        {
            node = true,
            kind = NodeKinds.Wire(_host.Kind),
            machine = _host.Kernel.Beacon.MachineName,
            show = s.Name,
            rev = Rev?.Invoke() ?? 0,
            version = AppVersion.Current,
            decks = _host.Control.Decks.Select(d => new { name = d.Name, module = d.Module, address = d.Address }).ToArray(),
            nodes = _host.Kernel.Nodes.Rows(),
            twin = _host.Twin?.DeckBlock(),
            stage = _host.Stage?.Block(),
            arcade = _host.Arcade.Words,
            play = _host.Play.Code,
            audience = s.Control.AudienceEnabled ? s.Control.AudiencePort : 0,
            wire = s.Control.Enabled ? s.Control.TcpPort : 0,
            http = s.Control.Enabled ? s.Control.HttpPort : 0,
            link = _host.Twin?.Status ?? "",
            linked = _host.Twin?.IsLinkedToDesk ?? false,
            airLabel = _host.AirLabel,
            armed = rt.Armed,
            hold = rt.Hold,
            standby = stack.StandbyCue is { } sb ? new { id = sb.Id, number = sb.Number, name = sb.Name } : null,
            last = stack.LastCue is { } last ? new { id = last.Id, number = last.Number, name = last.Name } : null,
            stageRev = _host.Stage?.Rev ?? 0,
        });
    }

    /// <summary>A caller's whole list with notes, as the desk's wire gives it — GET /api/cues and CUE LIST.</summary>
    public string CueListJson()
    {
        var stack = _host.CueStack;
        var report = CueValidator.Validate(_host.State, stack.Stack, _host.ValidationContext);
        var rows = stack.Stack.Cues.Select(c => new
        {
            id = c.Id,
            number = c.Number,
            name = c.Name,
            enabled = c.Enabled,
            requireConfirm = c.RequireConfirm,
            ready = c.Ready,
            track = c.Track,
            notes = c.Notes,
            plannedStart = c.PlannedStart,
            plannedSeconds = c.PlannedSeconds,
            followSeconds = c.FollowSeconds,
            mark = c.Mark == CueMark.None ? "" : c.Mark.ToString().ToLowerInvariant(),
            summary = CueSummary.Describe(_host.State, c),
            broken = report.ReasonFor(c.Id),
        }).ToArray();
        var listRev = string.Join("|", stack.Stack.Cues.Select(c => $"{c.Id}:{c.Number}:{c.Name}:{c.Enabled}:{c.Notes.Length}")).GetHashCode();
        return System.Text.Json.JsonSerializer.Serialize(new { name = stack.Stack.Name, listRev, cues = rows });
    }

    public Task<string> StateJsonAsync() => UiThread.InvokeAsync(StateJson).GetTask();

    public Task<string> CueListJsonAsync() => UiThread.InvokeAsync(CueListJson).GetTask();
}

/// <summary>
/// A node built from the kernel alone: the composition root for a process that is not the desk.
/// The kernel (the store and the show, the log, the journal, the bus, the runtime of the lists,
/// the beacon, the nodes registry, the assistant client, the arcade), the audience room, the cue
/// stack rehearsed on paper, the stage, the follower's link to a desk (a caller's, a stage
/// timer's), the node's action layer and the wire — and nothing of the desk: no outputs, no
/// engines, no sandbox. What the wire, the room, the twin, the stage and the stack's pages ask of
/// their host, the node answers itself, with null where the desk would have had a service.
/// </summary>
public sealed class NodeHost : IWireHost, IPlayHost, ITwinHost, IStageHost, IRunHost, IMachineHost, IDisposable
{
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _save;
    private readonly ScreenService _screens;
    private bool _shutDown;
    private int _bulkDepth;
    private int _deskDepth;
    private string _airLabel = "";
    private long _lastRev = -1;
    private int _ticks;

    public ServiceKernel Kernel { get; }

    /// <summary>The rig's clock as this node reads it: the machine's moved to the desk's frame while the link measures it, its own alone.</summary>
    public RoomClock Clock => Kernel.Clock;

    public ShowState State => Kernel.State;

    public NodeKind Kind => Kernel.Profile;

    /// <summary>A caller or a stage timer: a node that follows a desk's show and never holds an output.</summary>
    public bool IsFollower => Kind is NodeKind.Caller or NodeKind.Timer;

    public PlayService Play { get; }

    /// <summary>The games: every node builds the engine (a page may ask it for a picture), only the arcade node runs it from its first frame.</summary>
    public ArcadeService Arcade { get; }

    /// <summary>The follower's link to a desk — a caller's, a stage timer's; null on the arcade, which the desk finds.</summary>
    public TwinService? Twin { get; }

    /// <summary>The stack as it stands here: the desk's, mirrored, while linked; this node's own, rehearsed on paper, alone.</summary>
    public CueStackService CueStack { get; }

    /// <summary>The stage — the timer and the messages — over the show as it stands here.</summary>
    public StageService Stage { get; }

    public NodeActions Actions { get; }

    public ControlService Control { get; }

    /// <summary>The updates folder beside this node's settings: a package dropped there is applied through the watchdog, as on a desk.</summary>
    public UpdateService Updates { get; }

    /// <summary>The site's check-in with its management server — a node is one more machine the fleet sees.</summary>
    public ManagementService Management { get; }

    /// <summary>The way out for a restart or an update: set by the app, null headless.</summary>
    public Func<int, bool>? ExitRequest { get; set; }

    public ChangeTracker StateWatch { get; }

    /// <summary>The node from the launch and its settings folder — the store Main read, or one of its own.</summary>
    public static NodeHost Build(NodeKind kind, SettingsStore? store = null, ShowState? preloaded = null) => new(kind, store, preloaded);

    private NodeHost(NodeKind kind, SettingsStore? store, ShowState? preloaded)
    {
        Kernel = ServiceKernel.Build(kind, store, preloaded);
        OutputsHeldBy = NodeKinds.HoldWords(kind);
        _screens = new ScreenService { PlannedProvider = PlannedScreens };
        _screens.Refresh();
        Play = new PlayService(Kernel, this);
        Arcade = new ArcadeService(Kernel);
        Arcade.Board = Play.DrawWall;                                     // the room's wall rides the arcade's picture lane
        CueStack = new CueStackService(Kernel, this);
        Stage = new StageService(this);
        if (IsFollower)
        {
            Twin = new TwinService(Kernel, this);
            Kernel.Link = Twin;
        }
        Actions = new NodeActions(this);
        Control = new ControlService(Kernel, this);
        Updates = new UpdateService(Kernel, this);
        Management = new ManagementService(Kernel, this, Updates);
        StateWatch = new ChangeTracker(State, OnStateChanged, trackSections: true, onRuntimeOnlyChanged: () => RuntimeChanged?.Invoke());   // a runtime flag moves the wire's STATE, never a publish
        _save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            SaveNow();
        };
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Tick();
    }

    /// <summary>The listeners up, the beacon out, the link dialling, the game running, the tick on: the node is on the network.</summary>
    public void Start()
    {
        Control.Reconcile();
        Kernel.Beacon.Reconcile();
        Kernel.Mdns.Reconcile();
        Twin?.Reconcile();
        if (Kind == NodeKind.Arcade) Arcade.Start();
        _tick.Start();
        Kernel.Startup.Mark(StartupBudget.Services);
        Log.Info(Modules.Words());                                                              // what this node actually loaded: the lightness claim, on record at every start
    }

    /// <summary>Once a second, on the UI thread: the room's clock, the stack's settling, the nodes heard — and the wire's revision when anything a page shows moved.</summary>
    public void Tick()
    {
        Play.Tick();
        Kernel.Nodes.Poll();
        CueStack.Poll();
        if (++_ticks % 5 == 0) Updates.Scan();
        Updates.TickWindow(DateTime.Now);
        Management.Tick(DateTime.UtcNow);
        var rev = Play.Rev * 31 + CueStack.Runtime.Seq * 7 + Stage.Rev;
        if (rev != _lastRev)
        {
            _lastRev = rev;
            RuntimeChanged?.Invoke();
        }
    }

    private void OnStateChanged()
    {
        if (_bulkDepth > 0 || _deskDepth > 0) return;
        // The publish is what tells the link which sections moved: a caller's edit travels from here.
        Kernel.Bus.Publish(State, StateWatch);
        _screens.Refresh();
        Control.Reconcile();
        Kernel.Beacon.Reconcile();
        Kernel.Mdns.Reconcile();
        Twin?.Reconcile();
        SnapshotPublished?.Invoke();
        _save.Stop();
        _save.Start();
    }

    public void SaveNow()
    {
        try
        {
            Kernel.Store.Save(State);
        }
        catch (Exception ex)
        {
            Log.Error("The node's settings save failed.", ex);
        }
    }

    /// <summary>The rig as the show describes it — every placement as a planned screen — for the cue editor's pickers and the validator.</summary>
    private IEnumerable<ScreenInfo> PlannedScreens()
    {
        foreach (var p in State.Output.Placements)
        {
            yield return new ScreenInfo(
                p.ScreenId,
                p.CustomLabel.Length > 0 ? p.CustomLabel : p.IsVirtual ? p.VirtualKind : "Screen",
                new Avalonia.PixelRect(p.X, p.Y, Math.Max(1, p.PlannedWidth), Math.Max(1, p.PlannedHeight)),
                1.0, false, 0, IsPlanned: true, IsVirtual: p.IsVirtual);
        }
    }

    // ---- the edit scopes ----

    /// <summary>Many writes, one publish: a mirrored show landing, a plan merged, a cue sheet imported.</summary>
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

    /// <summary>A runtime-only edit — the desk's live word onto this node's runtime: nothing of the show moves, nothing publishes.</summary>
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

    /// <summary>The stage's edits and the countdown's: the show as it stands here, published once.</summary>
    public void EditAir(Action<ShowState> edit) => BulkEdit(() => edit(State));

    // ---- IPlayHost ----

    public void StartWall() => Arcade.Start();

    public IReadOnlyList<string> AudienceUrls() => Control.AudienceUrls();

    public bool AudienceListening => Control.AudienceListening;

    public int AudienceConnections => Control.AudienceConnections;

    // ---- ITwinHost: a follower holds nothing, takes nothing, and the show it mirrors is the desk's ----

    IActionLayer ITwinHost.Actions => Actions;

    // A node sends to no box: a wall switch fired here has nothing to wait for.
    long ITwinHost.DeviceMark() => 0;

    int ITwinHost.DeviceSentSince(long mark) => 0;

    /// <summary>The glance line on a node: the link to the desk it follows, and nothing of a rig it does not have.</summary>
    public string GlanceWords => Twin?.GlanceWords ?? "";

    Task<IReadOnlyList<DeviceReceipt>> ITwinHost.DeviceConfirmSince(long mark) => Task.FromResult((IReadOnlyList<DeviceReceipt>)Array.Empty<DeviceReceipt>());

    /// <summary>What is on air by name: the desk's, while a link is in step; the cue last rehearsed on paper alone.</summary>
    public string AirLabel
    {
        get => Twin is { IsLinkedToDesk: true, Live.AirLabel.Length: > 0 } ? Twin.Live!.AirLabel : _airLabel;
        set
        {
            if (_airLabel == value) return;
            _airLabel = value;
            AirLabelChanged?.Invoke();
        }
    }

    public bool OutputsLive => false;

    /// <summary>Why this node's outputs are held: the node's own words, never lifted.</summary>
    public string OutputsHeldBy { get; set; }

    public void CloseOutputs()
    {
        // a node has none
    }

    public void NotifyShowMirrored(IReadOnlyCollection<string>? sections) => ShowMirrored?.Invoke(sections);

    /// <summary>The desk's show landed here, whole or a section of it: the pages read again.</summary>
    public event Action<IReadOnlyCollection<string>?>? ShowMirrored;

    public void RecoverFromTwin(RecoverySnapshot? was, string head, string peer = "the main")
    {
        // a node never takes the show over, so nothing of it is put back
    }

#pragma warning disable CS0067 // a node writes no recovery record, so the twin's subscription never fires
    public event Action<RecoverySnapshot?>? RecoveryMoved;
#pragma warning restore CS0067

    // ---- IStageHost ----

    public ShowState AirState => State;

    CueStackService IStageHost.CueStack => CueStack;

    CueStackService ITwinHost.CueStack => CueStack;

    // ---- ICueHost / IRunHost: the stack on paper ----

    /// <summary>A cue rehearsed on paper: its steps are read and counted, none runs; the stack moves, the history records it, the clock reads from it.</summary>
    public ActionResult RunCue(CueStackConfig stack, RunCueConfig cue, ActionOrigin origin)
    {
        var rt = Kernel.Cues.For(stack);
        if (!cue.Enabled)
        {
            rt.LastOutcome = "Refused";
            return ActionResult.Refused($"{cue.Number} {cue.Name} is disabled.");
        }
        rt.LastOutcome = "Done";
        var steps = cue.Actions.Count;
        return ActionResult.Done($"{cue.Number} {cue.Name} — rehearsed on paper: {steps} step{(steps == 1 ? "" : "s")} read, none run. This {NodeKinds.Label(Kind).ToLowerInvariant()} node has no outputs, engines or devices; in step with a desk, GO runs there.");
    }

    public void WriteRunPlace()
    {
        // a node keeps no recovery sidecar: a relaunch starts from the show as saved
    }

    public IEnumerable<string> WatchedStatuses() => Array.Empty<string>();

    public void RecordGo(TimeSpan? offset)
    {
        // rig day's games are the desk's
    }

    public CueRuntime Cues => Kernel.Cues;

    IActionLayer IRunHost.Actions => Actions;

    /// <summary>What the validator may assume here: a desk's decoder and its music are the desk's to have, so neither is doubted; the presets are the ones beside this node's show.</summary>
    public CueValidationContext ValidationContext => new()
    {
        VideoDecoderAvailable = true,
        MusicReady = true,
        Presets = Kernel.Store.PresetNames().ToList(),
    };

    public IReadOnlyList<ScreenInfo> Screens => _screens.All;

    public event Action? AirLabelChanged;

    public VideoReading? VideoOnAir() => null;

    public string DeckSignature() => $"{Kernel.Nodes.Rev}|{Twin?.Phase}|{Stage?.DeckSignature()}|{Control.Decks.Count}";

    public IReadOnlyList<PreRoll.State> PreRollStates(IReadOnlyList<MediaLocator.WantedInput> wants) => Array.Empty<PreRoll.State>();

    public string BreakMusicWords => "";

    public (bool Holding, string Name) StingHold => (false, "");

    // ---- IWireHost ----

    IActionLayer IWireHost.Actions => Actions;

    CueStackService? IWireHost.CueStack => CueStack;

    // The desk's services the wire may ask for, and a node has not: null, and not on the node's own surface.
    InstallService? IWireHost.Install => null;

    ManagementService? IWireHost.Management => Management;

    UpdateService? IWireHost.Updates => Updates;

    OscService? IWireHost.Osc => null;

    StageService? IWireHost.Stage => IsFollower ? Stage : null;                 // the stage pages are a follower's; the arcade has no display to serve

    public event Action? RuntimeChanged;

    public event Action? SnapshotPublished;

    public IRouter NewRouter() => new NodeRouter(this);

    // ---- IMachineHost ----

    IActionLayer IMachineHost.Actions => Actions;

    /// <summary>A deliberate exit: the show as it stands saved; the code the watchdog reads, 0 when this node runs without one.</summary>
    public int PrepareRestart(bool forUpdate = false)
    {
        SaveNow();
        if (!Updates.Supervised) return 0;
        return forUpdate ? SupervisorPolicy.UpdateRequestExitCode : SupervisorPolicy.RestartRequestExitCode;
    }

    /// <summary>A restart in place — from this node's own window, or from the wire past the gate: the show saved, the watchdog brings the node back; refused in words without one.</summary>
    public ActionResult RestartInPlace(ActionOrigin origin)
    {
        if (!Updates.Supervised) return ActionResult.Refused("A restart in place needs the watchdog — start Patterns normally, with the watchdog on.");
        if (ExitRequest is null) return ActionResult.Refused("No way to restart in this session.");
        var code = PrepareRestart();
        Log.Info($"Restart requested from {origin.Label} on the {NodeKinds.Wire(Kind)} node.");
        return ExitRequest(code) ? ActionResult.Requested("Restarting — the watchdog brings this node back in a moment.") : ActionResult.Failed("The app did not accept the exit request.");
    }

    /// <summary>A line for the operator's strip on the node's window, through the kernel's notifier.</summary>
    public void Notify(string message) => Kernel.Notify(message);

    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        _tick.Stop();
        _save.Stop();
        SaveNow();
        Twin?.Dispose();
        Management.Dispose();
        Control.Dispose();
        Play.Dispose();
        Arcade.Dispose();
        Kernel.Dispose();
    }

    public void Dispose() => Shutdown();
}
