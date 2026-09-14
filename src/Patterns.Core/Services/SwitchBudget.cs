using System.Diagnostics;

namespace Patterns.Core.Services;

/// <summary>
/// The page switch's budget. A press on the rail is the one thing an operator does a hundred
/// times a night, and it has to be the frame and nothing else: the view model's handler, the
/// page's own build when it is built on entry (the first seconds after a start, before the idle
/// warm-up reached it), and the frame the window draws after. Each switch is timed from the
/// press to that first frame, the last sixty are kept with the worst and the average over them,
/// the switches past a desk frame are counted for the session, and the worst ever is remembered.
/// Pure; a ring, nothing allocated once warm. Timestamps are <see cref="Stopwatch"/> ticks.
/// </summary>
public sealed class SwitchBudget
{
    /// <summary>A switch past this skipped a desk frame at 60 Hz: the amber line.</summary>
    public const double SlowMs = 16;

    /// <summary>A switch past this the operator felt: the red line.</summary>
    public const double StutterMs = 100;

    /// <summary>The window the worst and the average are read over.</summary>
    public const int Window = 60;

    /// <summary>One switch: the page, the handler's whole span, the page's build inside it (0 when it was built already), the frame after the handler (-1 when none was seen).</summary>
    public readonly record struct Switch(string Page, double HandlerMs, double BuildMs, double FrameMs)
    {
        /// <summary>The handler less the build: the view model's own work.</summary>
        public double HandlerNetMs => Math.Max(0, HandlerMs - BuildMs);

        public double TotalMs => HandlerMs + Math.Max(0, FrameMs);

        /// <summary>"Machine 60 ms (30 ms building the page, 12 ms handler, 18 ms to the frame)".</summary>
        public string Words
        {
            get
            {
                var parts = new List<string>();
                if (BuildMs > 0) parts.Add($"{BuildMs:0} ms building the page");
                parts.Add($"{HandlerNetMs:0} ms handler");
                parts.Add(FrameMs >= 0 ? $"{FrameMs:0} ms to the frame" : "no frame seen");
                return $"{Page} {TotalMs:0} ms ({string.Join(", ", parts)})";
            }
        }
    }

    private readonly Switch[] _ring = new Switch[Window];
    private int _next;
    private int _count;
    private bool _open;
    private string _openPage = "";
    private long _openedAt;
    private double _handlerMs = -1;
    private double _buildMs;

    /// <summary>Switches recorded this session.</summary>
    public long Switches { get; private set; }

    /// <summary>Switches past <see cref="SlowMs"/> this session.</summary>
    public long SlowSwitches { get; private set; }

    public Switch? Last { get; private set; }

    /// <summary>The slowest switch this session.</summary>
    public Switch? WorstEver { get; private set; }

    /// <summary>A switch has begun and its frame has not been seen.</summary>
    public bool IsOpen => _open;

    /// <summary>The build counted into the open switch so far, ms; 0 with none open.</summary>
    public double OpenBuildMs => _open ? _buildMs : 0;

    /// <summary>The press: a switch begins. One still open — its frame never seen — is closed as it stands.</summary>
    public void Begin(string page, long timestamp)
    {
        if (_open) Close(-1);
        _open = true;
        _openPage = page;
        _openedAt = timestamp;
        _handlerMs = -1;
        _buildMs = 0;
    }

    /// <summary>The view model's handler done: its whole span, ms.</summary>
    public void Handler(double ms)
    {
        if (_open) _handlerMs = Math.Max(0, ms);
    }

    /// <summary>A page built, ms — counted into the open switch when it is that page's; the warm-up's builds are nobody's.</summary>
    public void Built(string page, double ms)
    {
        if (_open && page == _openPage) _buildMs += Math.Max(0, ms);
    }

    /// <summary>The first frame drawn after the switch: it closes on the budget.</summary>
    public void Framed(long timestamp)
    {
        if (!_open) return;
        var total = (timestamp - _openedAt) * 1000.0 / Stopwatch.Frequency;
        var handler = _handlerMs < 0 ? 0 : _handlerMs;
        Close(Math.Max(0, total - handler));
    }

    private void Close(double frameMs)
    {
        var s = new Switch(_openPage, _handlerMs < 0 ? 0 : _handlerMs, _buildMs, frameMs);
        _open = false;
        Switches++;
        if (s.TotalMs > SlowMs) SlowSwitches++;
        Last = s;
        if (WorstEver is null || s.TotalMs > WorstEver.Value.TotalMs) WorstEver = s;
        _ring[_next] = s;
        _next = (_next + 1) % Window;
        if (_count < Window) _count++;
    }

    /// <summary>The slowest switch in the window; null with none.</summary>
    public Switch? Worst
    {
        get
        {
            Switch? worst = null;
            for (var i = 0; i < _count; i++)
            {
                if (worst is null || _ring[i].TotalMs > worst.Value.TotalMs) worst = _ring[i];
            }
            return worst;
        }
    }

    /// <summary>The average switch in the window, ms; -1 with none.</summary>
    public double AverageMs
    {
        get
        {
            if (_count == 0) return -1;
            var sum = 0.0;
            for (var i = 0; i < _count; i++) sum += _ring[i].TotalMs;
            return sum / _count;
        }
    }

    public int InWindow => _count;

    /// <summary>The STABILITY line: "Page switch 5.0 ms · worst Machine 60 ms (30 ms building the page, 12 ms handler, 18 ms to the frame) in the last sixty · 1 past 16 ms this session".</summary>
    public string Describe()
    {
        if (Switches == 0) return "Page switch: none yet.";
        return $"Page switch {AverageMs:0.0} ms · worst {Worst!.Value.Words} in the last sixty · {SlowSwitches} past {SlowMs:0} ms this session";
    }

    public void Reset()
    {
        Array.Clear(_ring);
        _next = 0;
        _count = 0;
        _open = false;
        _openPage = "";
        _handlerMs = -1;
        _buildMs = 0;
        Switches = 0;
        SlowSwitches = 0;
        Last = null;
        WorstEver = null;
    }
}
