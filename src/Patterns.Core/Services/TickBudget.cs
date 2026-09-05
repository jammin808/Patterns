namespace Patterns.Core.Services;

/// <summary>
/// The desk's tick budget. The once-a-second poll runs on the UI thread, so every millisecond it
/// takes is a millisecond a click, a slider or a GO waits — and a probe that blocks (a name
/// resolver, a device, a disk) is felt at the desk before it shows anywhere else. This keeps the
/// last minute of ticks — how long each took and the slowest area inside it — the worst and the
/// average over that minute, the count of ticks past a desk frame this session, and the areas
/// that threw and were carried past. Pure; a ring of sixty samples, nothing allocated once warm.
/// </summary>
public sealed class TickBudget
{
    /// <summary>A tick past this is a dropped desk frame at 60 Hz: the amber line.</summary>
    public const double SlowMs = 16;

    /// <summary>A tick past this is a stutter the operator feels: the red line.</summary>
    public const double StutterMs = 50;

    /// <summary>The window the worst and the average are read over — one tick a second, so a minute.</summary>
    public const int Window = 60;

    private readonly double[] _ms = new double[Window];
    private readonly string[] _area = new string[Window];
    private int _next;
    private int _count;

    /// <summary>Ticks recorded this session.</summary>
    public long Ticks { get; private set; }

    /// <summary>Ticks past <see cref="SlowMs"/> this session.</summary>
    public long SlowTicks { get; private set; }

    /// <summary>Areas that threw and were carried past, this session (one per area per tick).</summary>
    public long Faults { get; private set; }

    /// <summary>The last tick, ms; -1 before the first.</summary>
    public double LastMs { get; private set; } = -1;

    /// <summary>The slowest tick this session, ms, and the slowest area inside it.</summary>
    public double WorstEverMs { get; private set; } = -1;

    public string WorstEverArea { get; private set; } = "";

    /// <summary>One tick done: how long it took and which area took the longest inside it.</summary>
    public void Record(double ms, string slowestArea = "", double slowestAreaMs = -1)
    {
        if (ms < 0) ms = 0;
        Ticks++;
        LastMs = ms;
        if (ms > SlowMs) SlowTicks++;
        var area = slowestArea.Length > 0 && slowestAreaMs >= 0 ? slowestArea : "";
        if (ms > WorstEverMs)
        {
            WorstEverMs = ms;
            WorstEverArea = area;
        }
        _ms[_next] = ms;
        _area[_next] = area;
        _next = (_next + 1) % Window;
        if (_count < Window) _count++;
    }

    /// <summary>An area threw inside a tick and the tick carried on.</summary>
    public void RecordFault() => Faults++;

    /// <summary>The worst tick in the window, ms; -1 with none.</summary>
    public double WorstMs
    {
        get
        {
            var worst = -1.0;
            for (var i = 0; i < _count; i++)
            {
                if (_ms[i] > worst) worst = _ms[i];
            }
            return worst;
        }
    }

    /// <summary>The slowest area of the worst tick in the window; "" when unknown.</summary>
    public string WorstArea
    {
        get
        {
            var worst = -1.0;
            var area = "";
            for (var i = 0; i < _count; i++)
            {
                if (_ms[i] > worst)
                {
                    worst = _ms[i];
                    area = _area[i] ?? "";
                }
            }
            return area;
        }
    }

    /// <summary>The average tick in the window, ms; -1 with none.</summary>
    public double AverageMs
    {
        get
        {
            if (_count == 0) return -1;
            var sum = 0.0;
            for (var i = 0; i < _count; i++) sum += _ms[i];
            return sum / _count;
        }
    }

    /// <summary>How many ticks the window holds right now (up to <see cref="Window"/>).</summary>
    public int InWindow => _count;

    /// <summary>
    /// The STABILITY line: "Desk tick 1.2 ms · worst 4.1 ms (tallies) in the last minute · 0 past
    /// 16 ms this session" — and what failed, when something did.
    /// </summary>
    public string Describe()
    {
        if (Ticks == 0) return "Desk tick: not measured yet.";
        var worst = WorstMs;
        var area = WorstArea;
        var parts = new List<string>
        {
            $"Desk tick {AverageMs:0.0} ms",
            $"worst {worst:0.0} ms{(area.Length > 0 ? $" ({area})" : "")} in the last minute",
            $"{SlowTicks} past {SlowMs:0} ms this session",
        };
        if (Faults > 0) parts.Add($"{Faults} area{(Faults == 1 ? "" : "s")} failed and the tick carried on — see the log");
        return string.Join(" · ", parts);
    }

    public void Reset()
    {
        Array.Clear(_ms);
        Array.Clear(_area);
        _next = 0;
        _count = 0;
        Ticks = 0;
        SlowTicks = 0;
        Faults = 0;
        LastMs = -1;
        WorstEverMs = -1;
        WorstEverArea = "";
    }
}
