using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// What is on air, as the kernel's services see it — the beacon packet, the nodes page. The desk
/// fills it in from its outputs, its cue stack and its metrics; a node has nothing on air and
/// says so; a test hands in whatever it likes.
/// </summary>
public interface IAirReport
{
    string AirLabel { get; }
    bool OutputsLive { get; }
    bool Armed { get; }
    string StandbyWords { get; }
    string LastCueNumber { get; }
    double Fps { get; }
    int Windows { get; }
}

/// <summary>Nothing on air: a node's answer, and the kernel's until the desk fills the slot.</summary>
public sealed class NothingOnAir : IAirReport
{
    public static readonly NothingOnAir Instance = new();
    public string AirLabel => "—";
    public bool OutputsLive => false;
    public bool Armed => false;
    public string StandbyWords => "";
    public string LastCueNumber => "";
    public double Fps => 0;
    public int Windows => 0;
}

/// <summary>The link this process offers or holds — the twin's port for callers and standbys, how many callers are on it. The twin fills it; a kernel without one offers none.</summary>
public interface ILinkReport
{
    int LinkPort { get; }
    int CallerCount { get; }
}

/// <summary>No link: the kernel's answer until a twin service fills the slot.</summary>
public sealed class NoLink : ILinkReport
{
    public static readonly NoLink Instance = new();
    public int LinkPort => 0;
    public int CallerCount => 0;
}

/// <summary>The action layer as a capability: every verb of the show's vocabulary, run and answered. The desk's is <see cref="ShowActions"/>; a node's is <see cref="NodeActions"/>, which runs its own kinds and refuses the desk's.</summary>
public interface IActionLayer
{
    ActionResult Execute(ShowAction action, ActionOrigin origin);
}

/// <summary>The wire's dispatcher as a capability: a command in, the protocol's reply out. The desk's is <see cref="CommandRouter"/>; a node's is <see cref="NodeRouter"/>.</summary>
public interface IRouter
{
    /// <summary>A revision the tablet long-polls on: bumped by the control service on every push-worthy change.</summary>
    Func<long>? Rev { get; set; }

    Task<string> ExecuteAsync(RemoteCommand cmd, ActionOrigin? origin = null);

    string StateJson();

    Task<string> StateJsonAsync();

    Task<string> CueListJsonAsync();
}

/// <summary>
/// What the twin asks of the desk it runs in: the action layer (a caller's verbs, the wall-switch
/// cue), the edit scopes, the cue stack for the live word, the outputs to hold closed and the
/// show to put back after a takeover. Everything else the twin needs is the kernel's — the show,
/// the runtime of its lists, the beacon, the journal.
/// </summary>
public interface ITwinHost
{
    IActionLayer Actions { get; }
    string AirLabel { get; }
    CueStackService CueStack { get; }
    bool OutputsLive { get; }
    string OutputsHeldBy { get; set; }
    void CloseOutputs();
    void BulkEdit(Action edit);
    void DeskEdit(Action edit);
    void Notify(string message);
    void NotifyShowMirrored(IReadOnlyCollection<string>? sections);
    void RecoverFromTwin(RecoverySnapshot? was, string head, string peer = "the main");
    event Action<RecoverySnapshot?>? RecoveryMoved;
    void SaveNow();

    /// <summary>A mark before the wall-switch cue fires, so the lines it sends to boxes can be waited for; a node, which sends to no box, marks nothing.</summary>
    long DeviceMark();

    /// <summary>How many lines went to boxes since the mark — their receipts landed or still to come; none means the cue asked nothing outside this machine.</summary>
    int DeviceSentSince(long mark);

    /// <summary>The receipts of every line sent since the mark, once each has landed or timed out.</summary>
    Task<IReadOnlyList<DeviceReceipt>> DeviceConfirmSince(long mark);
}

/// <summary>
/// What the wire asks of the desk: the action layer its verbs land on, the router it dispatches
/// through, the air its pages read, the services whose status and pages it serves. A node's
/// desk answers each of these too — with holds and "not on this node" where they apply.
/// </summary>
public interface IWireHost
{
    IActionLayer Actions { get; }
    ShowState AirState { get; }
    /// <summary>The running order — null on a node, which has none.</summary>
    CueStackService? CueStack { get; }
    /// <summary>The desk's install, management and updates, whose status the admin page shows — null on a node.</summary>
    InstallService? Install { get; }
    ManagementService? Management { get; }
    UpdateService? Updates { get; }
    OscService? Osc { get; }
    PlayService Play { get; }
    /// <summary>The games — built by the desk for a rig day and by every node, running only on the arcade node.</summary>
    ArcadeService Arcade { get; }
    /// <summary>The stage timer — null on a node that has none.</summary>
    StageService? Stage { get; }
    VideoReading? VideoOnAir();
    event Action? RuntimeChanged;
    event Action? SnapshotPublished;
    IRouter NewRouter();
}

/// <summary>What the stage timer asks of the desk: the air to read and edit, the running order, the status strip.</summary>
public interface IStageHost
{
    ShowState AirState { get; }
    CueStackService CueStack { get; }
    void EditAir(Action<ShowState> edit);
    void Notify(string message);
    event Action? SnapshotPublished;
}

/// <summary>
/// What the machine's own services — the updates folder, the management check-in — ask of the
/// process they run in, whatever its role: the way out for a restart that the watchdog brings
/// back, the router the management server's lines dispatch through, and the action layer. Every
/// node is a machine somebody has to keep current and can reach from the fleet's server; none of
/// that is the desk's alone.
/// </summary>
public interface IMachineHost
{
    /// <summary>The way out: the app's exit with a code the watchdog reads — null in a session that cannot restart.</summary>
    Func<int, bool>? ExitRequest { get; }

    /// <summary>What is saved and marked before a deliberate exit; the exit code the watchdog acts on, 0 when it is not there.</summary>
    int PrepareRestart(bool forUpdate = false);

    IRouter NewRouter();

    IActionLayer Actions { get; }
}

/// <summary>What the audience room asks of the desk: the edit scope, and the audience port's facts from the wire.</summary>
public interface IPlayHost
{
    void BulkEdit(Action edit);
    /// <summary>The wall went on: the picture lane it rides (the arcade's loop) runs from here.</summary>
    void StartWall();
    IReadOnlyList<string> AudienceUrls();
    bool AudienceListening { get; }
    int AudienceConnections { get; }
}

/// <summary>
/// What the cue stack asks of the process it runs in. The desk runs a cue's steps through its
/// action layer, writes the caller's place to the recovery sidecar, watches its sidecar services
/// for a late failure and feeds rig day's streak; a node rehearses a cue on paper and has none of
/// the rest. The show, the runtime of the lists and the journal are the kernel's.
/// </summary>
public interface ICueHost
{
    /// <summary>Runs one cue's steps — the desk's action layer, or a node's rehearsal on paper — and says what became of them.</summary>
    ActionResult RunCue(CueStackConfig stack, RunCueConfig cue, ActionOrigin origin);

    /// <summary>What is on air by name; the stack sets it after a GO that landed.</summary>
    string AirLabel { get; set; }

    /// <summary>The caller's place for a relaunch, written on every GO — the desk's sidecar; a node writes none.</summary>
    void WriteRunPlace();

    /// <summary>The status lines of the services a Requested cue may fail in later (the stream, the tracks, the stingers, break music); none on a node.</summary>
    IEnumerable<string> WatchedStatuses();

    /// <summary>A person's GO against the running order, for rig day's streak while the games are on; nothing on a node.</summary>
    void RecordGo(TimeSpan? offset);

    void BulkEdit(Action edit);
}

/// <summary>
/// What the stack's pages — the Run surface and the Cues page — ask beyond the stack: the show and
/// the runtime, the action layer for a row's verbs, the validation context, the rig's screens for
/// the target pickers, the events they refresh on, and the LIVE strip's chips a desk has and a
/// node has not (a clip's clock, the pre-roll, break music, a stinger's hold).
/// </summary>
public interface IRunHost : ICueHost
{
    ShowState State { get; }
    CueRuntime Cues { get; }
    CueStackService CueStack { get; }
    IActionLayer Actions { get; }
    CueValidationContext ValidationContext { get; }
    IReadOnlyList<ScreenInfo> Screens { get; }
    event Action? SnapshotPublished;
    event Action? AirLabelChanged;
    /// <summary>The clip on air and where it is — null with none, and always null on a node.</summary>
    VideoReading? VideoOnAir();
    /// <summary>The standby cue's clips in the pool — empty on a node, which opens nothing.</summary>
    IReadOnlyList<PreRoll.State> PreRollStates(IReadOnlyList<MediaLocator.WantedInput> wants);
    /// <summary>"Track — device" while break music plays here; "" otherwise, and on a node.</summary>
    string BreakMusicWords { get; }
    /// <summary>A stinger holding the screens for the caller's take; never on a node.</summary>
    (bool Holding, string Name) StingHold { get; }
}

/// <summary>The one-line verbs on any action layer — a kind with its target and value, one cue fired — as the desk's own layer always offered them.</summary>
public static class ActionLayerVerbs
{
    public static ActionResult Execute(this IActionLayer actions, ShowActionKind kind, ActionOrigin origin, string target = "", string value = "")
        => actions.Execute(new ShowAction(kind, target, value), origin);

    /// <summary>One cue, wherever it is: the row's GO THIS CUE NOW, the Cues page's FIRE.</summary>
    public static ActionResult FireCue(this IActionLayer actions, RunCueConfig cue, ActionOrigin origin)
        => actions.Execute(new ShowAction(ShowActionKind.CueFire, cue.Id), origin);
}
