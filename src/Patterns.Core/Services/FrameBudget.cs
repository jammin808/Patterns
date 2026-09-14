using Patterns.Core.Model;
using Patterns.Core.Rendering;

namespace Patterns.Core.Services;

/// <summary>
/// One sink's frame budget read at a moment: the frames it drew, the ones past the slow line,
/// the last minute's average and worst frame with the stage that took it, and the frame rate
/// the last minute's complete seconds measured.
/// </summary>
/// <param name="P95Ms">The frame time 95% of the last minute's frames came in under, from the buckets' histograms; -1 with no frame.</param>
/// <param name="Missed">The presentation slots the last minute missed — frames the room did not get — as the pacer counted them.</param>
/// <param name="Faults">Frames of the last minute whose draw threw; the last good picture was drawn in each one's place.</param>
/// <param name="ConsecutiveFaults">Faults in a row up to now on this sink — 0 once a frame draws whole again.</param>
public sealed record FrameBudgetReading(SinkKind Kind, int SinkIndex, string Label, long Frames, long SlowFrames,
                                        int FramesInWindow, double AverageMs, double WorstMs, string WorstStage, double Fps,
                                        double LastSecondWorstMs = -1, double P95Ms = -1, int Missed = 0, double LagMs = -1, double LagAverageMs = -1,
                                        int Faults = 0, int ConsecutiveFaults = 0, string LastFault = "")
{
    /// <summary>"Preview", "Output 1 (Main)", "Monitor PGM".</summary>
    public string Name => Kind switch
    {
        SinkKind.Preview => "Preview",
        SinkKind.Output => Label.Length > 0 && !Label.StartsWith("Output", StringComparison.OrdinalIgnoreCase) ? $"Output {SinkIndex} ({Label})" : Label.Length > 0 ? Label : $"Output {SinkIndex}",
        SinkKind.Monitor => Label.Length > 0 ? $"Monitor {Label}" : "Monitor",
        _ => Label.Length > 0 ? Label : Kind.ToString(),
    };

    /// <summary>"Output 1 (Main) 4.1 ms avg at 60 fps · worst 31.2 ms (the Fractal pattern)".</summary>
    public string Words
    {
        get
        {
            var stage = WorstStage.Length > 0 ? $" ({FrameStage.Words(WorstStage)})" : "";
            var fps = Fps >= 0 ? $" at {Fps:0} fps" : "";
            var p95 = P95Ms >= 0 ? $" · p95 {P95Ms:0.0} ms" : "";
            var missed = Missed > 0 ? $" · {Missed} slots missed" : "";
            var lag = LagMs >= 0 ? $" · publish to first drawn frame worst {LagMs:0} ms" : "";
            var faults = Faults > 0 ? $" · {Faults} fault{(Faults == 1 ? "" : "s")}" : "";
            return $"{Name} {AverageMs:0.0} ms avg{fps}{p95} · worst {WorstMs:0.0} ms{stage}{missed}{lag}{faults}";
        }
    }
}

/// <summary>
/// The engine's frame budget for one sink — the render-side twin of the desk's
/// <see cref="TickBudget"/>. Every frame an output, the preview or a monitor draws is recorded
/// with how long it took and the slowest stage inside it; the last minute is kept as sixty
/// one-second buckets (frames, the sum, the worst and its stage, the slow count), so the worst
/// frame of the last minute and what took it are one read away, and nothing is allocated once
/// warm. The render thread writes, the desk reads once a second: one short lock.
/// </summary>
public sealed class FrameBudget
{
    /// <summary>A frame past this is a hitch the room can see at show frame rates: the amber line.</summary>
    public const double SlowMs = 25;

    /// <summary>A frame past this is a stutter: the red line.</summary>
    public const double StutterMs = 50;

    /// <summary>Faults in a row that make a sink one drawing nothing whole: the red line of the Render faults row.</summary>
    public const int FaultRun = 3;

    /// <summary>The window the worst and the average are read over, in seconds.</summary>
    public const int Window = 60;

    /// <summary>Half-millisecond bins to 64 ms and one for everything past it: a p95 a walk away, nothing allocated per frame.</summary>
    public const int Bins = 129;
    public const double BinMs = 0.5;

    private struct Bucket
    {
        public long Second;
        public int Frames;
        public double SumMs;
        public double WorstMs;
        public string? WorstStage;
        public int Slow;
        public int Missed;
        public int[]? Hist;
        public double LagWorstMs;
        public double LagSum;
        public int LagCount;
        public int Faults;
    }

    /// <summary>Publishes remembered with the clock of the first frame that showed each: what the GO's clock reads.</summary>
    public const int ShownKept = 32;

    private long _lastShownVersion = -1;
    private readonly (long Version, double Clock)[] _shown = new (long, double)[ShownKept];
    private int _shownNext;
    private int _shownCount;

    private readonly Bucket[] _buckets = new Bucket[Window];
    private readonly object _gate = new();

    public FrameBudget(SinkKind kind, int sinkIndex, string label)
    {
        Kind = kind;
        SinkIndex = sinkIndex;
        Label = label;
    }

    public SinkKind Kind { get; private set; }
    public int SinkIndex { get; private set; }
    public string Label { get; private set; }

    /// <summary>Frames recorded this session.</summary>
    public long Frames { get; private set; }

    /// <summary>Frames past <see cref="SlowMs"/> this session.</summary>
    public long SlowFrames { get; private set; }

    /// <summary>The last frame, ms; -1 before the first.</summary>
    public double LastMs { get; private set; } = -1;

    /// <summary>The show clock of the last frame drawn; -1 before the first. A sink that stopped drawing is not one a GO waits for.</summary>
    public double LastFrameClock { get; private set; } = -1;

    /// <summary>Whose publishes this sink draws — the bus — so a GO on one bus never waits for a sink drawing another's (one bus per desk; the tests run several in a process).</summary>
    public object? Scope { get; set; }

    /// <summary>The slowest frame this session, ms, and the stage that took it.</summary>
    public double WorstEverMs { get; private set; } = -1;

    public string WorstEverStage { get; private set; } = "";

    /// <summary>Presentation slots missed this session — frames the room did not get.</summary>
    public long Missed { get; private set; }

    /// <summary>The worst lag from a publish to the frame that first showed it this session, ms; -1 before one reached this sink.</summary>
    public double WorstLagMs { get; private set; } = -1;

    /// <summary>Render faults this session: frames whose draw threw. Each is counted as a frame, noted here, and the last good world is drawn in its place.</summary>
    public long Faults { get; private set; }

    /// <summary>Faults in a row up to now — 0 once a frame draws whole again. <see cref="FaultRun"/> in a row is a sink drawing nothing whole.</summary>
    public int ConsecutiveFaults { get; private set; }

    /// <summary>The last fault's words — the exception's kind and message — "" before one.</summary>
    public string LastFault { get; private set; } = "";

    public DateTime? LastFaultUtc { get; private set; }

    /// <summary>The version of the last frame drawn whole; -1 before one. What the room is looking at while frames fault.</summary>
    public long LastGoodVersion { get; private set; } = -1;

    /// <summary>
    /// The frame drawn carried a snapshot: when its version is new to this sink a publish has
    /// reached the glass, and the lag from the publish to this frame — the frame's show clock less
    /// the snapshot's — goes on the second, and the version with its clock onto the ring the GO's
    /// clock reads. Called every frame; only a new version costs anything.
    /// </summary>
    public void RecordShown(long version, double publishedClock, double clockSeconds)
    {
        lock (_gate)
        {
            if (version == _lastShownVersion) return;
            _lastShownVersion = version;
            var lag = Math.Max(0, (clockSeconds - publishedClock) * 1000.0);
            if (lag > WorstLagMs) WorstLagMs = lag;
            var second = (long)Math.Floor(clockSeconds);
            ref var b = ref _buckets[(int)(((second % Window) + Window) % Window)];
            if (b.Second != second)
            {
                var hist = b.Hist;
                b = default;
                b.Second = second;
                b.Hist = hist;
                if (hist is not null) Array.Clear(hist);
            }
            if (lag > b.LagWorstMs) b.LagWorstMs = lag;
            b.LagSum += lag;
            b.LagCount++;
            _shown[_shownNext] = (version, clockSeconds);
            _shownNext = (_shownNext + 1) % ShownKept;
            if (_shownCount < ShownKept) _shownCount++;
        }
    }

    /// <summary>The show clock of the first frame that showed the version, or the nearest one past it; null while none has, or once it slid off the ring.</summary>
    public double? FirstShown(long version)
    {
        lock (_gate)
        {
            double? best = null;
            var bestVersion = long.MaxValue;
            for (var i = 0; i < _shownCount; i++)
            {
                var (v, clock) = _shown[i];
                if (v >= version && v < bestVersion)
                {
                    bestVersion = v;
                    best = clock;
                }
            }
            return best;
        }
    }

    /// <summary>The pacer found slots gone by unpresented: counted on the second of the show clock they were found at.</summary>
    public void RecordMissed(int missed, double clockSeconds)
    {
        if (missed <= 0) return;
        var second = (long)Math.Floor(clockSeconds);
        lock (_gate)
        {
            Missed += missed;
            ref var b = ref _buckets[(int)(((second % Window) + Window) % Window)];
            if (b.Second != second)
            {
                var hist = b.Hist;
                b = default;
                b.Second = second;
                b.Hist = hist;
                if (hist is not null) Array.Clear(hist);
            }
            b.Missed += missed;
        }
    }

    /// <summary>A frame's draw threw: counted on the session and on the second, the run of faults grown, the words kept.</summary>
    public void RecordFault(string words, DateTime utcNow, double clockSeconds)
    {
        var second = (long)Math.Floor(clockSeconds);
        lock (_gate)
        {
            Faults++;
            ConsecutiveFaults++;
            LastFault = words;
            LastFaultUtc = utcNow;
            ref var b = ref _buckets[(int)(((second % Window) + Window) % Window)];
            if (b.Second != second)
            {
                var hist = b.Hist;
                b = default;
                b.Second = second;
                b.Hist = hist;
                if (hist is not null) Array.Clear(hist);
            }
            b.Faults++;
        }
    }

    /// <summary>A frame drew whole: the run of faults is over, and this is the version the room has.</summary>
    public void RecordGood(long version)
    {
        lock (_gate)
        {
            ConsecutiveFaults = 0;
            if (version >= 0) LastGoodVersion = version;
        }
    }

    /// <summary>The sink's viewport can be re-described (a screen renamed, a window moved): the budget follows.</summary>
    public void Relabel(SinkKind kind, int sinkIndex, string label)
    {
        lock (_gate)
        {
            Kind = kind;
            SinkIndex = sinkIndex;
            Label = label;
        }
    }

    /// <summary>One frame done: how long it took, the slowest stage inside it, and the show clock it was drawn at.</summary>
    public void Record(double ms, string slowestStage, double clockSeconds)
    {
        if (ms < 0) ms = 0;
        var second = (long)Math.Floor(clockSeconds);
        lock (_gate)
        {
            Frames++;
            LastMs = ms;
            LastFrameClock = clockSeconds;
            if (ms > SlowMs) SlowFrames++;
            if (ms > WorstEverMs)
            {
                WorstEverMs = ms;
                WorstEverStage = slowestStage;
            }
            ref var b = ref _buckets[(int)(((second % Window) + Window) % Window)];
            if (b.Second != second)
            {
                var hist = b.Hist;                                   // the bins are kept, cleared, never reallocated
                b = default;
                b.Second = second;
                b.Hist = hist;
                if (hist is not null) Array.Clear(hist);
            }
            b.Hist ??= new int[Bins];
            b.Hist[Math.Min(Bins - 1, (int)(ms / BinMs))]++;
            b.Frames++;
            b.SumMs += ms;
            if (ms > b.WorstMs || b.WorstStage is null)
            {
                b.WorstMs = ms;
                b.WorstStage = slowestStage;
            }
            if (ms > SlowMs) b.Slow++;
        }
    }

    /// <summary>The last minute at a moment on the show clock. Averages and worsts are -1 with no frame in the window.</summary>
    public FrameBudgetReading Read(double clockSeconds)
    {
        var now = (long)Math.Floor(clockSeconds);
        var oldest = now - Window + 1;
        lock (_gate)
        {
            var frames = 0;
            var sum = 0.0;
            var worst = -1.0;
            var stage = "";
            var complete = 0;
            var completeFrames = 0;
            var lastSecondWorst = -1.0;
            var missed = 0;
            var faults = 0;
            var lagWorst = -1.0;
            var lagSum = 0.0;
            var lagCount = 0;
            Span<int> hist = stackalloc int[Bins];
            for (var i = 0; i < Window; i++)
            {
                ref var b = ref _buckets[i];
                if (b.Second < oldest || b.Second > now) continue;
                missed += b.Missed;
                faults += b.Faults;
                if (b.LagCount > 0)
                {
                    if (b.LagWorstMs > lagWorst) lagWorst = b.LagWorstMs;
                    lagSum += b.LagSum;
                    lagCount += b.LagCount;
                }
                if (b.Frames == 0) continue;
                if (b.Hist is { } h) for (var k = 0; k < Bins; k++) hist[k] += h[k];
                frames += b.Frames;
                sum += b.SumMs;
                if (b.WorstMs > worst)
                {
                    worst = b.WorstMs;
                    stage = b.WorstStage ?? "";
                }
                if (b.Second < now)
                {
                    complete++;
                    completeFrames += b.Frames;
                }
                if (b.Second == now - 1) lastSecondWorst = b.WorstMs;   // the last complete second: what the quality ladder judges
            }
            var fps = complete > 0 ? completeFrames / (double)complete : -1;
            var p95 = -1.0;
            if (frames > 0)
            {
                // The bin the 95th frame falls in, its upper edge: past the last bin it is "over 64 ms".
                var want = (long)Math.Ceiling(frames * 0.95);
                long seen = 0;
                for (var k = 0; k < Bins; k++)
                {
                    seen += hist[k];
                    if (seen >= want)
                    {
                        p95 = (k + 1) * BinMs;
                        break;
                    }
                }
            }
            return new FrameBudgetReading(Kind, SinkIndex, Label, Frames, SlowFrames, frames,
                frames > 0 ? sum / frames : -1, frames > 0 ? worst : -1, stage, fps, lastSecondWorst, p95, missed,
                lagCount > 0 ? lagWorst : -1, lagCount > 0 ? lagSum / lagCount : -1,
                faults, ConsecutiveFaults, LastFault);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_buckets);
            Frames = 0;
            SlowFrames = 0;
            Missed = 0;
            LastMs = -1;
            LastFrameClock = -1;
            WorstEverMs = -1;
            WorstEverStage = "";
            WorstLagMs = -1;
            Faults = 0;
            ConsecutiveFaults = 0;
            LastFault = "";
            LastFaultUtc = null;
            LastGoodVersion = -1;
            _lastShownVersion = -1;
            _shownNext = 0;
            _shownCount = 0;
        }
    }
}

/// <summary>
/// Every sink's budget in one place for the desk: the pipelines attach on creation and detach on
/// dispose, the Machine page, the super-check and STATE read the set once a second.
/// </summary>
public static class FrameBudgets
{
    private static readonly object Gate = new();
    private static readonly List<FrameBudget> All = new();

    public static void Attach(FrameBudget budget)
    {
        lock (Gate)
        {
            if (!All.Contains(budget)) All.Add(budget);
        }
    }

    public static void Detach(FrameBudget budget)
    {
        lock (Gate)
        {
            All.Remove(budget);
        }
    }

    /// <summary>The sinks that drew a frame in the last minute, in the order they attached (the preview first, then the outputs).</summary>
    public static IReadOnlyList<FrameBudgetReading> Readings(double clockSeconds)
    {
        FrameBudget[] budgets;
        lock (Gate)
        {
            budgets = All.ToArray();
        }
        var readings = new List<FrameBudgetReading>(budgets.Length);
        foreach (var b in budgets)
        {
            var r = b.Read(clockSeconds);
            if (r.FramesInWindow > 0) readings.Add(r);
        }
        return readings;
    }

    /// <summary>For the GO's clock: every sink that drew in the last minute, with the show clock of the first frame it showed the version (or one past it) at — null while it has not.</summary>
    public static IReadOnlyList<(SinkKind Kind, int SinkIndex, double? Clock)> FirstFrames(long version, double clockSeconds, object? scope = null)
    {
        FrameBudget[] budgets;
        lock (Gate)
        {
            budgets = All.ToArray();
        }
        var list = new List<(SinkKind, int, double?)>(budgets.Length);
        foreach (var b in budgets)
        {
            // Drawing now, not merely in the last minute: a window closed or a sink gone idle is not one the GO waits for; nor is a sink drawing another bus.
            if (b.LastFrameClock < 0 || clockSeconds - b.LastFrameClock > GoLatency.GiveUpSeconds) continue;
            if (scope is not null && b.Scope is not null && !ReferenceEquals(b.Scope, scope)) continue;
            list.Add((b.Kind, b.SinkIndex, b.FirstShown(version)));
        }
        return list;
    }

    /// <summary>The sink with the worst frame in the last minute; null with none drawn.</summary>
    public static FrameBudgetReading? Worst(IReadOnlyList<FrameBudgetReading> readings)
    {
        FrameBudgetReading? worst = null;
        foreach (var r in readings)
        {
            if (worst is null || r.WorstMs > worst.WorstMs) worst = r;
        }
        return worst;
    }

    /// <summary>Frames past the slow line this session, every sink together.</summary>
    public static long SlowFrames(IReadOnlyList<FrameBudgetReading> readings)
    {
        long slow = 0;
        foreach (var r in readings) slow += r.SlowFrames;
        return slow;
    }

    /// <summary>
    /// The STABILITY line: "Render frame worst 31.2 ms (the Fractal pattern) on Output 1 (Main) in
    /// the last minute · Preview 2.0 ms avg at 60 fps · Output 1 (Main) 4.1 ms avg at 60 fps · 3
    /// past 25 ms this session".
    /// </summary>
    public static string Describe(IReadOnlyList<FrameBudgetReading> readings)
    {
        var worst = Worst(readings);
        if (worst is null) return "Render frame: not measured yet — the preview and the outputs report as they draw.";
        var stage = worst.WorstStage.Length > 0 ? $" ({FrameStage.Words(worst.WorstStage)})" : "";
        var parts = new List<string> { $"Render frame worst {worst.WorstMs:0.0} ms{stage} on {worst.Name} in the last minute" };
        foreach (var r in readings)
        {
            parts.Add($"{r.Name} {r.AverageMs:0.0} ms avg{(r.Fps >= 0 ? $" at {r.Fps:0} fps" : "")}{(r.P95Ms >= 0 ? $", p95 {r.P95Ms:0.0} ms" : "")}{(r.Missed > 0 ? $", {r.Missed} slots missed" : "")}{(r.Faults > 0 ? $", {r.Faults} fault{(r.Faults == 1 ? "" : "s")}" : "")}");
        }
        parts.Add($"{SlowFrames(readings)} past {FrameBudget.SlowMs:0} ms this session");
        var missed = readings.Sum(r => (long)r.Missed);
        if (missed > 0) parts.Add($"{missed} presentation slot{(missed == 1 ? "" : "s")} missed in the last minute");
        var faults = readings.Sum(r => (long)r.Faults);
        if (faults > 0) parts.Add($"{faults} render fault{(faults == 1 ? "" : "s")} in the last minute — the last good frame drawn again each time; the log has the stack");
        return string.Join(" · ", parts);
    }

    public static string Describe(double clockSeconds) => Describe(Readings(clockSeconds));

    /// <summary>Tests: forget every sink.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            All.Clear();
        }
    }
}
