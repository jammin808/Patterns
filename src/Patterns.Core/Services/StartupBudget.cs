using System.Diagnostics;

namespace Patterns.Core.Services;

/// <summary>
/// How long the app took to become a desk, in phases: the runtime before Main (the host, the
/// bundle, the runtime's own start), the settings read, the graphics choices, Avalonia's own
/// start, the services built, the view model, the window's pages, the window opened, the first
/// frame drawn. A show machine that starts in two seconds is one an operator can restart between
/// sessions without a word; one that takes twenty is waiting on a device, a disk or a network
/// share, and the phase names which. The Machine page and the super-check read it; a test fences it.
/// </summary>
public sealed class StartupBudget
{
    /// <summary>A start past this is one the operator notices: the amber line.</summary>
    public const double SlowSeconds = 8;

    /// <summary>A start past this waited on something: the red line.</summary>
    public const double StuckSeconds = 20;

    /// <summary>Before Main: the exe's host, the single-file bundle, the runtime's own start.</summary>
    public const string Runtime = "runtime";

    /// <summary>The one settings read before the desk (Main), or the desk's own when there was no Main.</summary>
    public const string Settings = "settings";

    /// <summary>The GPU enumerated and chosen, the direct-output decision (Main, before Avalonia).</summary>
    public const string Graphics = "graphics";

    /// <summary>Avalonia's platform, Skia and the app's XAML, from Main to the first service.</summary>
    public const string Avalonia = "avalonia";

    public const string Services = "services";

    /// <summary>The desk's view model: its commands, the library, the first reconcile.</summary>
    public const string ViewModel = "view model";

    /// <summary>The window's XAML built — the shell, the switcher, the rail; the pages themselves come after the first frame.</summary>
    public const string Pages = "pages";

    /// <summary>The window opened: the screens attached, the side effects applied.</summary>
    public const string Window = "window";

    public const string FirstFrame = "first frame";

    private static readonly object EarlyGate = new();
    private static readonly List<(string Phase, long At)> Early = new();
    private static long _processStartedAt;
    private static double _runtimeMsBeforeMain;

    /// <summary>
    /// The first thing Main does, so the runtime's own start counts: the time from the process's
    /// start to this call is the <see cref="Runtime"/> phase. A second call is ignored.
    /// </summary>
    public static void MarkProcessStart()
    {
        if (_processStartedAt != 0) return;
        _processStartedAt = Stopwatch.GetTimestamp();
        try
        {
            using var me = Process.GetCurrentProcess();
            _runtimeMsBeforeMain = Math.Max(0, (DateTime.Now - me.StartTime).TotalMilliseconds);
        }
        catch
        {
            _runtimeMsBeforeMain = 0;   // a platform that keeps the start time to itself: the phase is left out
        }
    }

    /// <summary>The timestamp Main marked, or 0 when this process never went through Main (the tests).</summary>
    public static long ProcessStartedAt => _processStartedAt;

    /// <summary>Milliseconds the process ran before Main, when known.</summary>
    public static double RuntimeMsBeforeMain => _runtimeMsBeforeMain;

    /// <summary>A phase that ended in Main, before the desk's budget exists: folded in, in order, when the budget begins.</summary>
    public static void MarkEarly(string phase)
    {
        lock (EarlyGate)
        {
            Early.Add((phase, Stopwatch.GetTimestamp()));
        }
    }

    /// <summary>Forgets Main's marks (tests that stand in for Main).</summary>
    public static void ResetEarly()
    {
        lock (EarlyGate)
        {
            Early.Clear();
        }
        _processStartedAt = 0;
        _runtimeMsBeforeMain = 0;
    }

    private readonly List<(string Phase, double Ms)> _phases = new();
    private readonly object _gate = new();
    private long _origin;
    private long _last;
    private bool _begun;

    /// <summary>
    /// Starts the clock at a moment (the process start when known), once. With Main's timestamp
    /// the phases before the budget existed come first: the runtime before Main, then what Main
    /// marked (<see cref="MarkEarly"/>), in order.
    /// </summary>
    public void Begin(long originTimestamp = 0)
    {
        lock (_gate)
        {
            if (_begun) return;
            _begun = true;
            _origin = originTimestamp != 0 ? originTimestamp : Stopwatch.GetTimestamp();
            _last = _origin;
            if (originTimestamp == 0) return;
            if (_runtimeMsBeforeMain > 0) _phases.Add((Runtime, _runtimeMsBeforeMain));
            (string Phase, long At)[] early;
            lock (EarlyGate)
            {
                early = Early.ToArray();
            }
            foreach (var (phase, at) in early)
            {
                if (at < _last) continue;
                _phases.Add((phase, Stopwatch.GetElapsedTime(_last, at).TotalMilliseconds));
                _last = at;
            }
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

    /// <summary>"Start-up 1.8 s: runtime 420 ms · settings 40 ms · graphics 90 ms · avalonia 300 ms · services 310 ms · view model 80 ms · pages 210 ms · window 160 ms · first frame 190 ms".</summary>
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
