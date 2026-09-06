using System.Diagnostics;

namespace Patterns.Core.Services;

/// <summary>
/// How long the app took to become a desk, in phases: the runtime and the graphics set-up before
/// the services, the settings read, the services built, the window opened, the first frame drawn.
/// A show machine that starts in two seconds is one an operator can restart between sessions
/// without a word; one that takes twenty is waiting on a device, a disk or a network share, and
/// the phase names which. The Machine page and the super-check read it; a test fences it.
/// </summary>
public sealed class StartupBudget
{
    /// <summary>A start past this is one the operator notices: the amber line.</summary>
    public const double SlowSeconds = 8;

    /// <summary>A start past this waited on something: the red line.</summary>
    public const double StuckSeconds = 20;

    public const string Runtime = "runtime";
    public const string Settings = "settings";
    public const string Services = "services";
    public const string Window = "window";
    public const string FirstFrame = "first frame";

    private static long _processStartedAt;

    /// <summary>The first thing Main does, so the runtime's own start counts; a second call is ignored.</summary>
    public static void MarkProcessStart()
    {
        if (_processStartedAt == 0) _processStartedAt = Stopwatch.GetTimestamp();
    }

    /// <summary>The timestamp Main marked, or 0 when this process never went through Main (the tests).</summary>
    public static long ProcessStartedAt => _processStartedAt;

    private readonly List<(string Phase, double Ms)> _phases = new();
    private readonly object _gate = new();
    private long _origin;
    private long _last;
    private bool _begun;

    /// <summary>Starts the clock at a moment (the process start when known), once.</summary>
    public void Begin(long originTimestamp = 0)
    {
        lock (_gate)
        {
            if (_begun) return;
            _begun = true;
            _origin = originTimestamp != 0 ? originTimestamp : Stopwatch.GetTimestamp();
            _last = _origin;
        }
    }

    /// <summary>A phase ended now: its length is the time since the previous mark. A phase marked twice keeps its first mark.</summary>
    public bool Mark(string phase)
    {
        lock (_gate)
        {
            if (!_begun) Begin();
            foreach (var p in _phases)
            {
                if (p.Phase == phase) return false;
            }
            var now = Stopwatch.GetTimestamp();
            _phases.Add((phase, Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds));
            _last = now;
            return true;
        }
    }

    public IReadOnlyList<(string Phase, double Ms)> Phases
    {
        get
        {
            lock (_gate)
            {
                return _phases.ToArray();
            }
        }
    }

    /// <summary>From the origin to the last mark, ms; -1 before the first mark.</summary>
    public double TotalMs
    {
        get
        {
            lock (_gate)
            {
                if (_phases.Count == 0) return -1;
                var sum = 0.0;
                foreach (var p in _phases) sum += p.Ms;
                return sum;
            }
        }
    }

    /// <summary>The first frame is drawn: the desk is up.</summary>
    public bool Complete
    {
        get
        {
            lock (_gate)
            {
                foreach (var p in _phases)
                {
                    if (p.Phase == FirstFrame) return true;
                }
                return false;
            }
        }
    }

    /// <summary>"Start-up 1.8 s: runtime 420 ms · settings 40 ms · services 610 ms · window 510 ms · first frame 190 ms".</summary>
    public string Describe()
    {
        var phases = Phases;
        if (phases.Count == 0) return "Start-up: measuring…";
        var total = TotalMs;
        var parts = new List<string>(phases.Count);
        foreach (var p in phases) parts.Add($"{p.Phase} {Span(p.Ms)}");
        var still = Complete ? "" : " (still starting)";
        return $"Start-up {Span(total)}{still}: {string.Join(" · ", parts)}";
    }

    /// <summary>"1.8 s" past a second, "420 ms" under it.</summary>
    public static string Span(double ms) => ms >= 1000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms";
}
