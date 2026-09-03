using Patterns.Core.Model;

namespace Patterns.App.Services;

/// <summary>Where a list is right now. Never saved with the show; reset when a show loads.</summary>
public sealed class StackRuntime : Observable
{
    private bool _armed;
    private int _currentIndex = -1;
    private string? _lastCueId;
    private string _lastOutcome = "";

    /// <summary>A runtime chip: the list answers its keys / GO only while armed. Always off at launch.</summary>
    public bool Armed { get => _armed; set => Set(ref _armed, value); }

    /// <summary>The cue last run from this list (-1 = not started).</summary>
    public int CurrentIndex { get => _currentIndex; set => Set(ref _currentIndex, value); }

    public string? LastCueId { get => _lastCueId; set => Set(ref _lastCueId, value); }

    public string LastOutcome { get => _lastOutcome; set => Set(ref _lastOutcome, value); }
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
            rt.CurrentIndex = -1;
            rt.LastCueId = null;
            rt.LastOutcome = "";
        }
        Changed?.Invoke();
    }
}
