using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Drives the engine's quality ladder: the Machine page's mode into it, and once a second the
/// output sink that pressed hardest against its own rate's budget, from the frame budgets (the
/// preview stands in when no output draws — the desk in prep still shows the ladder working).
/// Auto starts a session where the last one on this machine settled — the profile beside the
/// settings — or a level down on a small machine; the profile is written when the level moves
/// and when the desk ends. A level change is logged and raised so the page's line and STATE
/// follow at once.
/// </summary>
public sealed class QualityService
{
    private readonly AppServices _s;
    private readonly QualityProfileStore _profiles;
    private static string? _machine;

    /// <summary>The machine's memory in gigabytes, for the starting level: a small machine starts a level down. Tests pin it.</summary>
    public static double MachineGB { get; set; } = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);

    /// <summary>What this machine is, for the profile — the CPU, its threads, the memory, the best card: a profile from another machine says nothing about this one.</summary>
    public static string MachineKey() => _machine ??= $"{WinRegistry.ReadCpuName()} · {Environment.ProcessorCount} threads · {Math.Round(MachineGB)} GB · {GpuService.BestGpuName}";

    public QualityService(AppServices s)
    {
        _s = s;
        _profiles = new QualityProfileStore(s.Store.BaseDirectory);
        Ladder.Reset();
        Ladder.SetMode(s.State.Admin.Quality);
        var profile = _profiles.Read();
        var known = profile is not null && profile.Machine == MachineKey();
        var start = QualityLadder.StartingLevel(profile, MachineKey(), MachineGB);
        if (Ladder.Start(start, known ? "where the last session on this machine settled" : "a small machine starts a level down until thirty clean seconds prove it"))
        {
            StartedAt = start;
            Log.Info($"Quality ladder: {Ladder.Describe()}");
        }
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

    /// <summary>The level Auto began this session at — the profile's or a small machine's — 0 when it began at full.</summary>
    public int StartedAt { get; private set; }

    /// <summary>The sink the last tick judged; "" before the first.</summary>
    public string LastSink { get; private set; } = "";

    /// <summary>The second the last tick judged, as the ladder saw it.</summary>
    public FrameSecond LastSecond { get; private set; } = new(-1, -1, 0, 0);

    /// <summary>The worst frame of the second the last tick judged; -1 before the first.</summary>
    public double LastWorstMs => LastSecond.WorstMs;

    /// <summary>The second as the ladder judges it: the reading's last complete second against the sink's own rate.</summary>
    public static FrameSecond SecondOf(FrameBudgetReading r)
        => new(r.LastSecondP95Ms >= 0 ? r.LastSecondP95Ms : r.LastSecondWorstMs, r.LastSecondWorstMs, r.LastSecondMissed, r.TargetFps);

    /// <summary>How hard a sink's second pressed against its own budget — 1 is the line — so a 30 fps output and a 60 fps one each answer for their own slot, and the one that pressed hardest is judged.</summary>
    public static double PressureOf(FrameBudgetReading r)
    {
        var s = SecondOf(r);
        var p95 = Math.Max(0, s.P95Ms) / QualityLadder.BudgetMs(s.TargetFps);
        var missed = s.Missed / (double)QualityLadder.MissedSlotsToCount;
        var worst = Math.Max(0, s.WorstMs) / FrameBudget.StutterMs;
        return Math.Max(p95, Math.Max(missed, worst));
    }

    /// <summary>Once a second from the desk's poll. Tests hand in readings and a clock.</summary>
    public void Tick(IReadOnlyList<FrameBudgetReading>? readings = null, DateTime? nowUtc = null)
    {
        readings ??= FrameBudgets.Readings(ShowClock.Seconds);
        FrameBudgetReading? judge = null;
        var pressure = double.NegativeInfinity;
        foreach (var r in readings)
        {
            if (r.Kind != SinkKind.Output || r.LastSecondWorstMs < 0) continue;
            var p = PressureOf(r);
            if (judge is null || p > pressure)
            {
                judge = r;
                pressure = p;
            }
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
        LastSecond = SecondOf(judge);
        if (Ladder.Observe(LastSecond, judge.Name, nowUtc ?? DateTime.UtcNow))
        {
            Log.Info($"Quality ladder: {Ladder.Describe()}");
            Changed?.Invoke();
            SaveProfile();
        }
    }

    private QualityProfile Profile() => new(MachineKey(), Ladder.Mode == QualityMode.Auto ? Ladder.Level : 0, Ladder.StepsDown, DateTime.UtcNow);

    /// <summary>The profile onto the file lane: where Auto is now, for the next start on this machine.</summary>
    public void SaveProfile()
    {
        var profile = Profile();
        _s.QueueFileWork("quality profile", () => _profiles.Write(profile));
    }

    /// <summary>The profile written now, on this thread: the desk is ending.</summary>
    public void SaveProfileNow() => _profiles.Write(Profile());

    /// <summary>The Machine page's line: the ladder's words, then the last second judged against its sink's budget.</summary>
    public string Describe()
    {
        var words = Ladder.Describe();
        if (LastSink.Length == 0) return words;
        var s = LastSecond;
        var missed = s.Missed > 0 ? $", {s.Missed} slot{(s.Missed == 1 ? "" : "s")} missed" : "";
        return $"{words} Last second: {LastSink} p95 {Math.Max(0, s.P95Ms):0.#} ms, worst {Math.Max(0, s.WorstMs):0.#} ms{missed} against a {s.BudgetMs:0.#} ms budget at {s.JudgedFps} fps.";
    }
}
