namespace Patterns.Core.Services;

/// <summary>
/// Every verb the show understands, in one vocabulary shared by the desk, the keyboard,
/// the remote protocol, the Companion module and (later) the cue stack. Typed on purpose:
/// a kind can be validated, journaled and displayed; a free-form string cannot.
/// </summary>
public enum ShowActionKind
{
    /// <summary>A kind this build does not know (written by a newer build). Never executes.</summary>
    Unknown,
    Note,
    OutputsOn,
    OutputsOff,
    Identify,
    BlackoutOn,
    BlackoutOff,
    BlackoutToggle,
    /// <summary>Target = look name or id; Value = "cut", a fade in ms, or empty for the show default.</summary>
    ApplyLook,
    /// <summary>Target = look name or id; loads the look into the editors (the sandboxed preview).</summary>
    ApplyLookToPreview,
    /// <summary>Target = F-key slot 1–12.</summary>
    ApplyLookHotkey,
    PresenterNext,
    PresenterPrev,
    /// <summary>Target = screen number (arrangement order, 1-based) or a placement screen id.</summary>
    ScreenOn,
    ScreenOff,
    ScreenToggle,
    /// <summary>Target = canvas letter (A, B…) or a canvas member key.</summary>
    CanvasOn,
    CanvasOff,
    AudioPlay,
    AudioStop,
    ToneOn,
    ToneOff,
    /// <summary>Target = stinger number (1-based, Audio-tab order), name or id.</summary>
    StingerFire,
    StingerStop,
    /// <summary>Target = playlist part number (1-based) or name.</summary>
    PlaylistPart,
    StreamStart,
    StreamStop,
    /// <summary>The sandbox becomes the program on every screen, crossfading.</summary>
    Take,
    /// <summary>The sandbox becomes the program on every screen, instantly.</summary>
    Cut,
    /// <summary>Audio track, break music, stingers (a clip reverts) and the tone stop. Never outputs, never blackout, never the stream.</summary>
    StopAll,
    /// <summary>Value = minutes; a duration countdown starts now on air.</summary>
    CountdownStart,
    CountdownStop,
    /// <summary>Value = the text (empty keeps the current text).</summary>
    MessageOn,
    MessageOff,
    ClockOn,
    ClockOff,
    /// <summary>Target = a cue list (stack id or name): the clicker list or the caller's stack.</summary>
    ListArm,
    ListDisarm,
    ListGo,
    ListBack,
    ListReset,
    /// <summary>Target = cue id: run that cue's actions now, in order, stopping at the first failure.</summary>
    CueFire,
    /// <summary>GO on the caller's stack through the gate; Target = the standby id the sender saw ("" skips the fence).</summary>
    CueGo,
    /// <summary>The audio track's volume in percent (0–125), live — the drawer's SEND and a cue.</summary>
    AudioVolume,
    /// <summary>Break music (Spotify): Target = library entry number (1-based, Audio-page order),
    /// name or id; empty resumes, or plays the first saved entry.</summary>
    SpotifyPlay,
    /// <summary>Break music pauses (Spotify has no "stop"; the position is kept so play resumes).</summary>
    SpotifyPause,
    /// <summary>Break music skips to the next track in whatever is playing.</summary>
    SpotifyNext,
    /// <summary>The break-music level in percent (0–100 — the Spotify device's own volume), live.</summary>
    SpotifyVolume,
}

/// <summary>One thing to do to the show: a kind plus the target it acts on and an optional value.</summary>
public readonly record struct ShowAction(ShowActionKind Kind, string Target = "", string Value = "")
{
    public override string ToString()
        => Target.Length == 0 ? Kind.ToString() : Value.Length == 0 ? $"{Kind} {Target}" : $"{Kind} {Target} {Value}";
}

/// <summary>Where an action came from — the one fact a history row must never lose.</summary>
public enum OriginKind
{
    Desk,
    Keyboard,
    Clicker,
    Tcp,
    Http,
    Companion,
    Schedule,
    Playlist,
    Stinger,
    Recovery,
    Cue,
}

public sealed record ActionOrigin(OriginKind Kind, string Name = "", string Endpoint = "")
{
    public static readonly ActionOrigin Desk = new(OriginKind.Desk);
    public static readonly ActionOrigin Keyboard = new(OriginKind.Keyboard);
    public static readonly ActionOrigin Clicker = new(OriginKind.Clicker);
    public static readonly ActionOrigin Schedule = new(OriginKind.Schedule);
    public static readonly ActionOrigin Playlist = new(OriginKind.Playlist);
    public static readonly ActionOrigin Stinger = new(OriginKind.Stinger);
    public static readonly ActionOrigin Recovery = new(OriginKind.Recovery);

    /// <summary>"Companion FOH deck", "tcp 10.0.0.12:51234", "desk".</summary>
    public string Label
    {
        get
        {
            var kind = Kind.ToString().ToLowerInvariant();
            if (Name.Length > 0) return $"{kind} {Name}";
            if (Endpoint.Length > 0) return $"{kind} {Endpoint}";
            return kind;
        }
    }

    public override string ToString() => Label;
}

public enum ActionStatus
{
    /// <summary>Applied; the screens (or the service) reflect it now.</summary>
    Done,
    /// <summary>Asked for; a service confirms or fails it later (stream start, a clip decode).</summary>
    Requested,
    /// <summary>Tried and failed (file missing, look unreadable).</summary>
    Failed,
    /// <summary>Not attempted: the show's state forbids it (prep mode, no sandbox open, unknown target).</summary>
    Refused,
}

public sealed record ActionResult(ActionStatus Status, string Message = "")
{
    public bool Ok => Status is ActionStatus.Done or ActionStatus.Requested;

    public static ActionResult Done(string message = "") => new(ActionStatus.Done, message);
    public static ActionResult Requested(string message = "") => new(ActionStatus.Requested, message);
    public static ActionResult Failed(string message) => new(ActionStatus.Failed, message);
    public static ActionResult Refused(string message) => new(ActionStatus.Refused, message);
}
