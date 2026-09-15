using Patterns.Core.Model;

namespace Patterns.Core.Services;

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
