using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// What every edge that follows the show asks of the process it runs in: the show, and a word
/// when a snapshot or the runtime moved. The desk and the kernel provide it; a test hands in a
/// state and raises the events itself.
/// </summary>
public interface IShowHost
{
    ShowState State { get; }

    event Action? SnapshotPublished;

    event Action? RuntimeChanged;
}

/// <summary>The cue stack's own event — its runtime is not in the snapshot: STANDBY, ARM and HOLD push on this.</summary>
public interface ICueStackEvents
{
    event Action? Changed;
}

/// <summary>
/// What the device transports ask of the desk: the show and its moves, the wire's router (a
/// device's trigger is a wire line, and its feedback is STATE's facts), and the execution in
/// hand, so a receipt settles the cue that caused the send.
/// </summary>
public interface IDeviceHost : IShowHost
{
    IRouter Router { get; }

    /// <summary>The cue execution whose actions are running now, "" between cues.</summary>
    string ExecutionInHand { get; }
}

/// <summary>What the OSC port asks of the desk: the show and its moves, the wire's router, and the cue stack's own event once the stack exists.</summary>
public interface IOscHost : IShowHost
{
    IRouter Router { get; }

    ICueStackEvents? CueStackEvents { get; }
}

/// <summary>Who this process is on the network: the beacon's name for the machine and the instance minted at this start.</summary>
public interface IBeaconIdentity
{
    string MachineName { get; }

    string Instance { get; }
}

/// <summary>What the beacon asks of the process: the show, the role, what is on air and the link it offers.</summary>
public interface IBeaconHost
{
    ShowState State { get; }

    bool IsDesk { get; }

    NodeKind Profile { get; }

    IAirReport Air { get; }

    ILinkReport Link { get; }
}

/// <summary>What the DNS-SD responder asks of the process: the show, the role, the link, who the beacon says we are, and the build's version for the advert.</summary>
public interface IMdnsHost
{
    ShowState State { get; }

    NodeKind Profile { get; }

    ILinkReport Link { get; }

    IBeaconIdentity BeaconIdentity { get; }

    string AppVersion { get; }
}
