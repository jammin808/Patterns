using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// A node's action layer: the verbs a node runs itself — the arcade's, the audience room's, the
/// audience port's, a note — and "not on this node" for every verb that is the desk's. The same
/// vocabulary as the desk's, so a cue, Companion or the assistant say the same words to either;
/// the answer says whose the verb is.
/// </summary>
public sealed class NodeActions : IActionLayer
{
    private readonly ServiceKernel _kernel;
    private readonly PlayService _play;

    public NodeActions(ServiceKernel kernel, PlayService play)
    {
        _kernel = kernel;
        _play = play;
    }

    /// <summary>Raised on the UI thread after every action, whatever its outcome.</summary>
    public event Action<ShowAction, ActionOrigin, ActionResult>? Performed;

    public ActionResult Execute(ShowAction action, ActionOrigin origin)
    {
        ActionResult result;
        try
        {
            if (ArcadeService.IsArcadeKind(action.Kind)) result = _kernel.Arcade.Run(action);
            else if (action.Kind is ShowActionKind.AudienceOn or ShowActionKind.AudienceOff) result = _play.RunAudience(action);
            else if (PlayService.IsPlayKind(action.Kind)) result = _play.Run(action);
            else if (action.Kind == ShowActionKind.Note) result = ActionResult.Done("Noted.");
            else result = ActionResult.Refused(NotHere(action.Kind));
        }
        catch (Exception ex)
        {
            Log.Error($"Action {action} failed on the node.", ex);
            result = ActionResult.Failed(ex.Message);
        }
        if (action.Kind != ShowActionKind.Note) _kernel.Journal.Record(origin.Label, action.Kind.ToString(), action.Target, result.Status.ToString(), result.Message);
        Performed?.Invoke(action, origin, result);
        return result;
    }

    /// <summary>"Blackout on is the desk's — not on this arcade node; send it to the desk's wire."</summary>
    public string NotHere(ShowActionKind kind)
        => $"{ActionSpec.Label(kind)} is the desk's — not on this {NodeKinds.Label(_kernel.Profile).ToLowerInvariant()} node; send it to the desk's wire.";
}

/// <summary>
/// A node's wire dispatcher: PING, HELLO and STATUS; the arcade's, the room's and the nodes'
/// status; every action through the node's action layer; and for the queries that are the
/// desk's alone (the cue list, the twin, the stage, the assistant), an ERR that says so.
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
                return ControlProtocol.Ok(_host.Kernel.Arcade.StatusJson(cmd.Text));
            case RemoteCommandKind.PlayStatus:
                return ControlProtocol.Ok(_host.Play.StatusJson(cmd.Text));
            case RemoteCommandKind.NodesStatus:
                return ControlProtocol.Ok(_host.Kernel.Nodes.StatusJson());
            case RemoteCommandKind.Action:
            {
                var result = _host.Actions.Execute(cmd.Action, origin);
                return result.Ok ? ControlProtocol.Ok() : ControlProtocol.Err(result.Message);
            }
            default:
                return ControlProtocol.Err($"{cmd.Kind} is the desk's — not on this {NodeKinds.Label(_host.Kind).ToLowerInvariant()} node");
        }
    }

    /// <summary>What a node says of itself: its kind, its machine, its show, its ports, the arcade's words, the room's code.</summary>
    public string StateJson()
    {
        var s = _host.State;
        return JsonUtil.SerializeCompact(new
        {
            node = true,
            kind = NodeKinds.Wire(_host.Kind),
            machine = _host.Kernel.Beacon.MachineName,
            show = s.Name,
            rev = Rev?.Invoke() ?? 0,
            arcade = _host.Kernel.Arcade.Words,
            play = _host.Play.Code,
            audience = s.Control.AudienceEnabled ? s.Control.AudiencePort : 0,
            wire = s.Control.Enabled ? s.Control.TcpPort : 0,
            http = s.Control.Enabled ? s.Control.HttpPort : 0,
        });
    }

    public Task<string> StateJsonAsync() => UiThread.InvokeAsync(StateJson).GetTask();

    public Task<string> CueListJsonAsync() => Task.FromResult("[]");
}

/// <summary>
/// A node built from the kernel alone: the composition root for a process that is not the desk.
/// The kernel (the store and the show, the log, the journal, the bus, the beacon, the nodes
/// registry, the assistant client, the arcade), the audience room, the node's action layer, the
/// wire — and nothing of the desk: no outputs, no engines, no sandbox, no cue stack. What the wire
/// and the room ask of their host, the node answers itself (<see cref="IWireHost"/>,
/// <see cref="IPlayHost"/>), with null where the desk would have had a service. The arcade node
/// is the first role built this way; the caller and the timer still boot the desk's composition.
/// </summary>
public sealed class NodeHost : IWireHost, IPlayHost, IDisposable
{
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _save;
    private bool _shutDown;

    public ServiceKernel Kernel { get; }

    public ShowState State => Kernel.State;

    public NodeKind Kind => Kernel.Profile;

    public PlayService Play { get; }

    public NodeActions Actions { get; }

    public ControlService Control { get; }

    public ChangeTracker StateWatch { get; }

    /// <summary>The node from the launch and its settings folder — the store Main read, or one of its own.</summary>
    public static NodeHost Build(NodeKind kind, SettingsStore? store = null, ShowState? preloaded = null) => new(kind, store, preloaded);

    private NodeHost(NodeKind kind, SettingsStore? store, ShowState? preloaded)
    {
        Kernel = ServiceKernel.Build(kind, store, preloaded);
        Play = new PlayService(Kernel, this);
        Kernel.Arcade.Board = Play.DrawWall;                              // the room's wall rides the arcade's picture lane
        Actions = new NodeActions(Kernel, Play);
        Control = new ControlService(Kernel, this);
        StateWatch = new ChangeTracker(State, OnStateChanged);
        _save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            SaveNow();
        };
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Tick();
    }

    /// <summary>The listeners up, the beacon out, the game running, the tick on: the node is on the network.</summary>
    public void Start()
    {
        Control.Reconcile();
        Kernel.Beacon.Reconcile();
        if (Kind == NodeKind.Arcade) Kernel.Arcade.Start();
        _tick.Start();
        Kernel.Startup.Mark(StartupBudget.Services);
    }

    private long _lastRev = -1;

    /// <summary>Once a second, on the UI thread: the room's clock, the nodes heard — and the wire's revision when the room moved.</summary>
    public void Tick()
    {
        Play.Tick();
        Kernel.Nodes.Poll();
        var rev = Play.Rev;
        if (rev != _lastRev)
        {
            _lastRev = rev;
            RuntimeChanged?.Invoke();
        }
    }

    private void OnStateChanged()
    {
        Control.Reconcile();
        Kernel.Beacon.Reconcile();
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

    // ---- IPlayHost ----

    /// <summary>An edit on a node is an edit: the tracker sees each write, and nothing publishes a picture.</summary>
    public void BulkEdit(Action edit) => edit();

    public IReadOnlyList<string> AudienceUrls() => Control.AudienceUrls();

    public bool AudienceListening => Control.AudienceListening;

    public int AudienceConnections => Control.AudienceConnections;

    // ---- IWireHost ----

    IActionLayer IWireHost.Actions => Actions;

    public ShowState AirState => State;

    // The desk's services the wire may ask for, and a node has not: null, and not on the node's own surface.
    CueStackService? IWireHost.CueStack => null;

    InstallService? IWireHost.Install => null;

    ManagementService? IWireHost.Management => null;

    UpdateService? IWireHost.Updates => null;

    OscService? IWireHost.Osc => null;

    StageService? IWireHost.Stage => null;

    VideoReading? IWireHost.VideoOnAir() => null;

    public event Action? RuntimeChanged;

    public event Action? SnapshotPublished;

    public IRouter NewRouter() => new NodeRouter(this);

    /// <summary>A line for the operator's strip on the node's window, through the kernel's notifier.</summary>
    public void Notify(string message) => Kernel.Notify(message);

    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        _tick.Stop();
        _save.Stop();
        SaveNow();
        Control.Dispose();
        Play.Dispose();
        Kernel.Dispose();
    }

    public void Dispose() => Shutdown();
}
