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
/// cue), the edit scopes, the cue runtime for the live word, the outputs to hold closed and the
/// show to put back after a takeover. Everything else the twin needs is the kernel's.
/// </summary>
public interface ITwinHost
{
    IActionLayer Actions { get; }
    string AirLabel { get; }
    CueStackService CueStack { get; }
    CueRuntime Cues { get; }
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

/// <summary>What the audience room asks of the desk: the edit scope, and the audience port's facts from the wire.</summary>
public interface IPlayHost
{
    void BulkEdit(Action edit);
    IReadOnlyList<string> AudienceUrls();
    bool AudienceListening { get; }
    int AudienceConnections { get; }
}
