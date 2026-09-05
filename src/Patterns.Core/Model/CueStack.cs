using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Patterns.Core.Model;

/// <summary>Who runs a list: the caller (Enter, the GO button, CUE GO) or the speaker (page keys, NEXT / PREV).</summary>
public enum StackRole
{
    Caller,
    Clicker,
}

/// <summary>
/// What one cue action does. Typed, never free text, so the editor, the validator and the
/// executor read one spec table (<see cref="Services.CueActionSpec"/>). <c>Unknown</c> is
/// what a newer build's kind becomes here; validation reports it and it never executes.
/// </summary>
public enum CueActionKind
{
    Unknown,
    /// <summary>No execution — the cue exists for its notes.</summary>
    Note,
    /// <summary>Target = look id; Value = "cut", a fade in ms, or empty for the show default.</summary>
    ApplyLook,
    AudioPlay,
    AudioStop,
    /// <summary>Target = stinger id; stop reverts a clip.</summary>
    StingerFire,
    StingerStop,
    /// <summary>Target = playlist part name.</summary>
    PlaylistPart,
    StreamStart,
    StreamStop,
    BlackoutOn,
    BlackoutOff,
    /// <summary>Target = placement screen id.</summary>
    ScreenOn,
    ScreenOff,
    /// <summary>Target = canvas member key (a+b).</summary>
    CanvasOn,
    CanvasOff,
    /// <summary>Value = minutes.</summary>
    CountdownStart,
    CountdownStop,
    /// <summary>Value = the text.</summary>
    MessageOn,
    MessageOff,
    ClockOn,
    ClockOff,
    /// <summary>Target = stack id: hand the room to the clicker list and back.</summary>
    ListArm,
    ListDisarm,
    ListGo,
    ListBack,
    ListReset,
    /// <summary>The audio track's volume, 0–125 %, as the SHOW CONTROLS drawer sends it.</summary>
    AudioVolume,
    /// <summary>Target = break-music entry id (empty resumes); break music is sound only.</summary>
    SpotifyPlay,
    SpotifyPause,
    SpotifyNext,
    /// <summary>Value = the break-music level, 0–100 % (the Spotify device's own volume).</summary>
    SpotifyVolume,
    /// <summary>The live duck for an announcement from the room: everything but a VOG makes way, and comes back with DuckOff.</summary>
    DuckOn,
    DuckOff,
    /// <summary>Target = lower third id: it goes on air; hide takes the one on air off.</summary>
    LowerThirdShow,
    LowerThirdHide,
}

/// <summary>One typed step of a cue.</summary>
public sealed class CueActionConfig : Observable
{
    private CueActionKind _kind = CueActionKind.ApplyLook;
    private string _target = "";
    private string _value = "";

    public CueActionKind Kind { get => _kind; set => Set(ref _kind, value); }
    public string Target { get => _target; set => Set(ref _target, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
}

/// <summary>
/// A cue in a list. List order is the running order; the number is a label the caller reads
/// ("03.020"), auto-assigned on insert and editable as text, never used to sort.
/// </summary>
public sealed class RunCueConfig : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _number = "01.010";
    private string _name = "New cue";
    private bool _enabled = true;
    private string _track = "";
    private string _notes = "";
    private bool _requireConfirm;
    private bool _ready;
    private int? _plannedSeconds;

    /// <summary>Never shown; the runtime, the remote fence and the history use it.</summary>
    public string Id { get => _id; set => Set(ref _id, value); }
    public string Number { get => _number; set => Set(ref _number, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>The script's column: "Video", "VT", "Audio" — free text.</summary>
    public string Track { get => _track; set => Set(ref _track, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    /// <summary>GO asks for a second press within a few seconds.</summary>
    public bool RequireConfirm { get => _requireConfirm; set => Set(ref _requireConfirm, value); }
    /// <summary>Set by the visual operator: "this one is built". Not a gate.</summary>
    public bool Ready { get => _ready; set => Set(ref _ready, value); }
    /// <summary>Drives the running row's elapsed / planned display.</summary>
    public int? PlannedSeconds { get => _plannedSeconds; set => Set(ref _plannedSeconds, value); }

    public ObservableCollection<CueActionConfig> Actions { get; init; } = new();
}

/// <summary>A list of cues with one role. The show holds exactly two: the caller's stack and the clicker list.</summary>
public sealed class CueStackConfig : Observable
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "Cue stack";
    private StackRole _role = StackRole.Caller;
    private bool _loopAtEnd;
    private bool _suspendAutomationWhileArmed = true;

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public StackRole Role { get => _role; set => Set(ref _role, value); }
    /// <summary>After the last cue the list starts over (was Presenter.Loop).</summary>
    public bool LoopAtEnd { get => _loopAtEnd; set => Set(ref _loopAtEnd, value); }
    /// <summary>The daily schedule and playlist part start times wait while this stack is armed.</summary>
    public bool SuspendAutomationWhileArmed { get => _suspendAutomationWhileArmed; set => Set(ref _suspendAutomationWhileArmed, value); }

    public ObservableCollection<RunCueConfig> Cues { get; init; } = new();

    [JsonIgnore]
    public bool IsClicker => _role == StackRole.Clicker;
}
