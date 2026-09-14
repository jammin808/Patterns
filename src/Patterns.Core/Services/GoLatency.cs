using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The GO on the clock: from the press, through the publish the cue's steps made, to the first
/// frame every output drew with it — the show's own press-to-glass, the number a caller feels
/// on every cue. The stack stamps the press and the publish it produced; the sinks' budgets say
/// when each first drew that publish; the slowest output names the GO's frame (the preview does
/// when no output is open). A GO nobody drew is closed after two seconds and says so; a GO that
/// changed nothing on the screens says that. The last sixty are kept with the worst and the
/// average, the GOs past fifty milliseconds are counted for the session. Pure; a ring.
/// </summary>
public sealed class GoLatency
{
    /// <summary>A GO past this from press to frame is one a caller feels: the amber line.</summary>
    public const double SlowMs = 50;

    /// <summary>Past this the room saw the cue land late: the red line.</summary>
    public const double StutterMs = 100;

    public const int Window = 60;

    /// <summary>A GO whose frame every counted sink has not shown after this long is closed with what was seen.</summary>
    public const double GiveUpSeconds = 2;

    /// <summary>One GO: the cue, the press to the publish, the publish to the slowest sink's first frame (-1 when none was seen), that sink, and a note.</summary>
    public readonly record struct Go(string Cue, double PublishMs, double FrameMs, string Sink, string Note = "")
    {
        public double TotalMs => PublishMs + Math.Max(0, FrameMs);

        /// <summary>"GO 07 34 ms (6 ms to the publish, 28 ms to OUT 2's frame)".</summary>
        public string Words => FrameMs >= 0
            ? $"GO {Cue} {TotalMs:0} ms ({PublishMs:0} ms to the publish, {FrameMs:0} ms to {Sink}'s frame{(Note.Length > 0 ? "; " + Note : "")})"
            : $"GO {Cue} {TotalMs:0} ms ({PublishMs:0} ms to the publish; {(Note.Length > 0 ? Note : "no frame seen")})";
    }

    private readonly Go[] _ring = new Go[Window];
    private int _next;
    private int _count;
    private bool _open;
    private string _cue = "";
    private long _version;
    private double _pressClock;
    private double _publishMs;

    public long Gos { get; private set; }

    public long SlowGos { get; private set; }

    public Go? Last { get; private set; }

    public Go? WorstEver { get; private set; }

    /// <summary>A GO pressed whose frame has not been seen.</summary>
    public bool IsOpen => _open;

    /// <summary>The publish the open GO waits on; -1 with none open.</summary>
    public long PendingVersion => _open ? _version : -1;

    /// <summary>The press: the bus's version before and after the cue ran, the show clock at the press, and how long the press took to become the publish.</summary>
    public void Pressed(string cue, long versionBefore, long versionAfter, double pressClock, double publishMs)
    {
        if (_open) Close(-1, "", "the next GO came first");
        _cue = cue;
        _version = versionAfter;
        _pressClock = pressClock;
        _publishMs = Math.Max(0, publishMs);
        _open = true;
        if (versionAfter == versionBefore) Close(-1, "", "nothing to draw");
    }

    /// <summary>
    /// The sinks' first frames for the pending publish: the outputs count when any output drew,
    /// the preview alone otherwise; the GO closes when every counted sink has shown it, on the
    /// slowest one's frame — or after the give-up with what was seen.
    /// </summary>
    public void Resolve(IReadOnlyList<(SinkKind Kind, int SinkIndex, double? Clock)> firstFrames, double clockSeconds)
    {
        if (!_open) return;
        var outputs = firstFrames.Where(f => f.Kind == SinkKind.Output).ToList();
        var counted = outputs.Count > 0 ? outputs : firstFrames.Where(f => f.Kind == SinkKind.Preview).ToList();
        var waited = clockSeconds - _pressClock;
        if (counted.Count == 0)
        {
            if (waited > GiveUpSeconds) Close(-1, "", "no sink drew");
            return;
        }
        var shown = counted.Where(f => f.Clock is not null).ToList();
        if (shown.Count < counted.Count && waited <= GiveUpSeconds) return;
        if (shown.Count == 0)
        {
            Close(-1, "", $"no frame seen in {GiveUpSeconds:0} s");
            return;
        }
        var slowest = shown.OrderByDescending(f => f.Clock).First();
        var frameMs = Math.Max(0, (slowest.Clock!.Value - _pressClock) * 1000.0 - _publishMs);
        var note = shown.Count < counted.Count ? $"{counted.Count - shown.Count} of {counted.Count} sinks never showed it" : "";
        Close(frameMs, Glance.SinkName(slowest.Kind, slowest.SinkIndex), note);
    }

    private void Close(double frameMs, string sink, string note)
    {
        var g = new Go(_cue, _publishMs, frameMs, sink, note);
        _open = false;
        Gos++;
        if (g.TotalMs > SlowMs) SlowGos++;
        Last = g;
        if (WorstEver is null || g.TotalMs > WorstEver.Value.TotalMs) WorstEver = g;
        _ring[_next] = g;
        _next = (_next + 1) % Window;
        if (_count < Window) _count++;
    }

    /// <summary>The slowest GO in the window; null with none.</summary>
    public Go? Worst
    {
        get
        {
            Go? worst = null;
            for (var i = 0; i < _count; i++)
            {
                if (worst is null || _ring[i].TotalMs > worst.Value.TotalMs) worst = _ring[i];
            }
            return worst;
        }
    }

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

    /// <summary>The glance line's word: "GO→frame 34 ms" for the last GO a sink showed; "" otherwise.</summary>
    public string GlanceWords => Last is { FrameMs: >= 0 } g ? $"GO→frame {g.TotalMs:0} ms" : "";

    /// <summary>The STABILITY line: "GO to first drawn frame 34.0 ms · worst GO 07 61 ms (8 ms to the publish, 53 ms to OUT 2's frame) in the last sixty · 0 past 50 ms this session" — the frame the sink drew, never the glass.</summary>
    public string Describe()
        => Gos == 0 ? "GO to first drawn frame: no GO yet." : $"GO to first drawn frame {AverageMs:0.0} ms · worst {Worst!.Value.Words} in the last sixty · {SlowGos} past {SlowMs:0} ms this session";

    public void Reset()
    {
        Array.Clear(_ring);
        _next = 0;
        _count = 0;
        _open = false;
        _cue = "";
        Gos = 0;
        SlowGos = 0;
        Last = null;
        WorstEver = null;
    }
}
