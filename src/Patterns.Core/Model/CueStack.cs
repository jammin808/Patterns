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
/// What a cue marks in the day, so the caller's estimates know where the next break, lunch
/// and the end are. First member is the fallback for a value this build does not know.
/// </summary>
public enum CueMark
{
    None,
    Break,
    Lunch,
    End,
}

/// <summary>
/// One typed step of a cue: a show action as the desk, the wire and OSC send them — the same kind,
/// target and value, in the one vocabulary (<see cref="ShowActionKind"/>) — kept observable for the
/// editor. <see cref="ToAction"/> is the whole translation; a cue has no vocabulary of its own. A
/// kind this build does not know loads as <see cref="ShowActionKind.Unknown"/>: the checks say so
/// and it never runs.
/// </summary>
public sealed class CueActionConfig : Observable
{
    private ShowActionKind _kind = ShowActionKind.ApplyLook;
    private string _target = "";
    private string _value = "";

    public ShowActionKind Kind { get => _kind; set => Set(ref _kind, value); }
    public string Target { get => _target; set => Set(ref _target, value); }
    public string Value { get => _value; set => Set(ref _value, value); }

    /// <summary>The step as the action layer runs it — the desk's, the wire's and a cue's are the same thing.</summary>
    public ShowAction ToAction() => new(Kind, Target, Value);
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
    private string _plannedStart = "";
    private int? _followSeconds;
    private CueMark _mark;

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
    /// <summary>The planned length: drives the running row's elapsed / planned display and the day's estimates.</summary>
    public int? PlannedSeconds { get => _plannedSeconds; set => Set(ref _plannedSeconds, value is { } v ? Math.Max(0, v) : null); }

    /// <summary>The running order's clock time for this cue, "HH:mm" (empty = none); the estimates compare it with the clock.</summary>
    public string PlannedStart { get => _plannedStart; set => Set(ref _plannedStart, value ?? ""); }

    /// <summary>Auto-follow: after this cue fires, the next one GOes by itself this many seconds later (0 = at once; null = the caller presses GO).</summary>
    public int? FollowSeconds { get => _followSeconds; set => Set(ref _followSeconds, value is { } v ? Math.Clamp(v, 0, 86400) : null); }

    /// <summary>A break, lunch or the end of the day — what the caller's estimates count down to.</summary>
    public CueMark Mark { get => _mark; set => Set(ref _mark, value); }

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
