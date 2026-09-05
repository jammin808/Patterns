using Patterns.Core.Model;

namespace Patterns.App.Services;

/// <summary>Where a list is right now. Never saved with the show; reset when a show loads.</summary>
public sealed class StackRuntime : Observable
{
    private bool _armed;
    private bool _hold;
    private bool _executing;
    private int _currentIndex = -1;
    private string? _lastCueId;
    private string _lastOutcome = "";
    private string? _standbyCueId;
    private string? _confirmPendingCueId;
    private DateTime? _confirmDeadlineUtc;
    private DateTime? _lastGoUtc;
    private DateTime? _followDueUtc;
    private string? _followCueId;
    private long _seq;

    /// <summary>A runtime chip: the list answers its keys / GO only while armed. Always off at launch.</summary>
    public bool Armed { get => _armed; set => Set(ref _armed, value); }

    /// <summary>The cue last run from this list (-1 = not started).</summary>
    public int CurrentIndex { get => _currentIndex; set => Set(ref _currentIndex, value); }

    public string? LastCueId { get => _lastCueId; set => Set(ref _lastCueId, value); }

    public string LastOutcome { get => _lastOutcome; set => Set(ref _lastOutcome, value); }

    /// <summary>A latched GO inhibit and nothing else: GO from any origin is refused with "held" until released.</summary>
    public bool Hold { get => _hold; set => Set(ref _hold, value); }

    /// <summary>A cue is running right now; a second GO is dropped, never queued.</summary>
    public bool Executing { get => _executing; set => Set(ref _executing, value); }

    /// <summary>The cue GO fires next. Selecting it changes no output.</summary>
    public string? StandbyCueId { get => _standbyCueId; set => Set(ref _standbyCueId, value); }

    /// <summary>A cue that asks for confirmation waits here for four seconds after the first GO.</summary>
    public string? ConfirmPendingCueId { get => _confirmPendingCueId; set => Set(ref _confirmPendingCueId, value); }

    public DateTime? ConfirmDeadlineUtc { get => _confirmDeadlineUtc; set => Set(ref _confirmDeadlineUtc, value); }

    public DateTime? LastGoUtc { get => _lastGoUtc; set => Set(ref _lastGoUtc, value); }

    /// <summary>An auto-follow is pending: the standby cue GOes by itself at this time, unless the caller moves standby, holds or disarms first.</summary>
    public DateTime? FollowDueUtc { get => _followDueUtc; set => Set(ref _followDueUtc, value); }

    /// <summary>The cue the pending follow expects on standby — a different one there cancels the follow.</summary>
    public string? FollowCueId { get => _followCueId; set => Set(ref _followCueId, value); }

    /// <summary>Bumps on every runtime change; remotes long-poll on it.</summary>
    public long Seq { get => _seq; set => Set(ref _seq, value); }
}

/// <summary>
/// Runtime state for every cue list, keyed by stack id, owned beside the sandbox service so
/// nothing runtime ever lives in the show state (a flag there would republish the whole show
/// to every sink on each press, and survive a show load unreset).
/// </summary>
public sealed class CueRuntime
{
    private readonly Dictionary<string, StackRuntime> _byStack = new(StringComparer.Ordinal);

    /// <summary>Raised on the UI thread whenever any list's runtime changes.</summary>
    public event Action? Changed;

    public StackRuntime For(CueStackConfig stack) => For(stack.Id);

    public StackRuntime For(string stackId)
    {
        if (_byStack.TryGetValue(stackId, out var rt)) return rt;
        rt = new StackRuntime();
        rt.PropertyChanged += (_, _) => Changed?.Invoke();
        _byStack[stackId] = rt;
        return rt;
    }

    /// <summary>A show load: every list starts over, disarmed.</summary>
    public void Reset()
    {
        foreach (var rt in _byStack.Values)
        {
            rt.Armed = false;
            rt.Hold = false;
            rt.Executing = false;
            rt.CurrentIndex = -1;
            rt.LastCueId = null;
            rt.LastOutcome = "";
            rt.StandbyCueId = null;
            rt.ConfirmPendingCueId = null;
            rt.ConfirmDeadlineUtc = null;
            rt.LastGoUtc = null;
            rt.FollowDueUtc = null;
            rt.FollowCueId = null;
        }
        Changed?.Invoke();
    }
}
