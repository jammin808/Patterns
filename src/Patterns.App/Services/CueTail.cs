using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>A step of a cue still to run, and the moment it is due.</summary>
public sealed record PendingStep(string StackId, string CueId, string Label, int Number, int Of, CueActionConfig Action, double DueClock);

/// <summary>
/// The part of a cue that has not happened yet.
///
/// A cue's steps run inside one edit and one publish — that is what makes a cue one visible change
/// — and a step with a wait cannot. So the waiting ones live here: a small list and one timer, on
/// the desk's own thread, each step run through the same action layer the immediate ones went
/// through and journaled with its cue's name and its place in it.
///
/// What matters on a show is not that they run but that they can never surprise: the next GO on
/// the same list drops that list's pending steps (the show has moved on and the caller can see
/// it), STOP ALL drops every one of them, and so does loading a show or closing the desk. A step
/// left over from a cue two cues ago landing on the audience is the failure this exists to make
/// impossible, so the rule is the narrow one — a cue's tail belongs to that cue.
/// </summary>
public sealed class CueTail : IDisposable
{
    private readonly List<PendingStep> _pending = new();
    private readonly DispatcherTimer _timer;
    private readonly Func<double> _clock;

    /// <summary>How often the tail is looked at: close enough that a step lands on its second, cheap enough to leave running.</summary>
    public static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(50);

    public CueTail(Func<double>? clock = null)
    {
        _clock = clock ?? (() => ShowClock.Seconds);
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = Beat };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Runs one step: the same action layer the cue's immediate steps went through.</summary>
    public Action<PendingStep>? Run { get; set; }

    /// <summary>Told when the list changes, so the desk's line and the Run surface follow it.</summary>
    public event Action? Changed;

    /// <summary>How many steps are still to run.</summary>
    public int Count
    {
        get
        {
            lock (_pending) return _pending.Count;
        }
    }

    /// <summary>Seconds until the next step, or null when nothing is waiting.</summary>
    public double? NextInSeconds
    {
        get
        {
            lock (_pending)
            {
                if (_pending.Count == 0) return null;
                var soonest = double.MaxValue;
                foreach (var p in _pending) soonest = Math.Min(soonest, p.DueClock);
                return Math.Max(0, soonest - _clock());
            }
        }
    }

    /// <summary>"2 steps to come, next in 4 s" — the line the desk and the caller's surface read; "" when nothing waits.</summary>
    public string Words
    {
        get
        {
            PendingStep[] rest;
            lock (_pending)
            {
                if (_pending.Count == 0) return "";
                rest = _pending.ToArray();
            }
            var soonest = rest.Min(p => p.DueClock);
            var inSeconds = Math.Max(0, soonest - _clock());
            var next = rest.First(p => Math.Abs(p.DueClock - soonest) < 0.0001);
            var count = rest.Length == 1 ? "1 step to come" : $"{rest.Length} steps to come";
            return $"{next.Label}: {count}, next in {inSeconds:0.#} s";
        }
    }

    /// <summary>
    /// The waiting steps of one cue. Anything already pending for that cue's list goes first —
    /// the next GO on a list takes the list over.
    /// </summary>
    public void Schedule(string stackId, RunCueConfig cue, string label, IReadOnlyList<CueStep> steps, double now)
    {
        DropStack(stackId, quiet: true);
        if (steps.Count == 0)
        {
            Changed?.Invoke();
            return;
        }
        var total = cue.Actions.Count;
        lock (_pending)
        {
            foreach (var s in steps)
            {
                _pending.Add(new PendingStep(stackId, cue.Id, label, s.Index + 1, total, s.Action, now + s.AtSeconds));
            }
        }
        if (!_timer.IsEnabled) _timer.Start();
        Changed?.Invoke();
    }

    /// <summary>The next GO on a list takes it over: that list's waiting steps go, quietly.</summary>
    public int DropStack(string stackId, bool quiet = false)
    {
        int gone;
        lock (_pending)
        {
            gone = _pending.RemoveAll(p => p.StackId == stackId);
            if (_pending.Count == 0) _timer.Stop();
        }
        if (gone > 0 && !quiet) Changed?.Invoke();
        return gone;
    }

    /// <summary>STOP ALL, a show loaded, the desk closing: nothing is left to land on the audience.</summary>
    public int DropAll()
    {
        int gone;
        lock (_pending)
        {
            gone = _pending.Count;
            _pending.Clear();
            _timer.Stop();
        }
        if (gone > 0) Changed?.Invoke();
        return gone;
    }

    /// <summary>The timer body, callable directly — the tests drive it without waiting on the clock.</summary>
    public void Poll()
    {
        var now = _clock();
        List<PendingStep>? due = null;
        lock (_pending)
        {
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                // A show clock that went backwards (it cannot, but a test's can) must not strand a
                // step for ever: anything more than its own wait in the future is due now.
                if (_pending[i].DueClock > now) continue;
                (due ??= new List<PendingStep>()).Add(_pending[i]);
                _pending.RemoveAt(i);
            }
            if (_pending.Count == 0) _timer.Stop();
        }
        if (due is null) return;
        // Oldest first: two steps due in the same beat run in the order they were written.
        due.Sort((a, b) => a.DueClock == b.DueClock ? a.Number.CompareTo(b.Number) : a.DueClock.CompareTo(b.DueClock));
        foreach (var step in due)
        {
            try
            {
                Run?.Invoke(step);
            }
            catch (Exception ex)
            {
                // A step that throws is one step, never the desk and never the rest of the tail.
                Log.Error($"{step.Label}: step {step.Number} of {step.Of} failed.", ex);
            }
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
        DropAll();
    }
}
