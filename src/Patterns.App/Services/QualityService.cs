using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Drives the engine's quality ladder: the Machine page's mode into it, and once a second the
/// worst output sink's last complete second from the frame budgets (the preview stands in when
/// no output draws — the desk in prep still shows the ladder working). A level change is logged
/// and raised so the page's line and STATE follow at once.
/// </summary>
public sealed class QualityService
{
    private readonly AppServices _s;

    public QualityService(AppServices s)
    {
        _s = s;
        Ladder.Reset();
        Ladder.SetMode(s.State.Admin.Quality);
        s.State.Admin.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AdminConfig.Quality)) return;
            if (Ladder.SetMode(s.State.Admin.Quality))
            {
                Log.Info($"Quality ladder: {Ladder.Describe()}");
                Changed?.Invoke();
            }
        };
    }

    /// <summary>The engine's one ladder — every renderer reads it.</summary>
    public QualityLadder Ladder => QualityLadder.Shared;

    /// <summary>A level change (a step, or the mode from the Machine page).</summary>
    public event Action? Changed;

    /// <summary>The sink and the worst frame the last tick judged; "" / -1 before the first.</summary>
    public string LastSink { get; private set; } = "";

    public double LastWorstMs { get; private set; } = -1;

    /// <summary>Once a second from the desk's poll. Tests hand in readings and a clock.</summary>
    public void Tick(IReadOnlyList<FrameBudgetReading>? readings = null, DateTime? nowUtc = null)
    {
        readings ??= FrameBudgets.Readings(ShowClock.Seconds);
        FrameBudgetReading? judge = null;
        foreach (var r in readings)
        {
            if (r.Kind != SinkKind.Output || r.LastSecondWorstMs < 0) continue;
            if (judge is null || r.LastSecondWorstMs > judge.LastSecondWorstMs) judge = r;
        }
        if (judge is null)
        {
            foreach (var r in readings)
            {
                if (r.Kind == SinkKind.Preview && r.LastSecondWorstMs >= 0)
                {
                    judge = r;
                    break;
                }
            }
        }
        if (judge is null) return;   // nothing drew last second: no verdict, no step
        LastSink = judge.Name;
        LastWorstMs = judge.LastSecondWorstMs;
        if (Ladder.Observe(judge.LastSecondWorstMs, judge.Name, nowUtc ?? DateTime.UtcNow))
        {
            Log.Info($"Quality ladder: {Ladder.Describe()}");
            Changed?.Invoke();
        }
    }

    public string Describe() => Ladder.Describe();
}
