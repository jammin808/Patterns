using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// Which sections of the show each side effect reads — the useful part of a game engine's
/// change mask: an edit names the sections it touched (the tracker already knows), and only the
/// systems that read those sections reconsider themselves. A lower-third's text does not make
/// the room's boxes, the wire, the beacon or the twin look at themselves; a device's address
/// does not make the decoders. A publish that could not name its sections (the first, a show
/// loaded whole, a forced republish) runs everything, as it always did. The lists err on the
/// generous side — a system that reads a section it is not listed under would miss a change,
/// and a system run once too often costs a diff that finds nothing.
/// </summary>
public static class SideEffectDomains
{
    /// <summary>The planned screens merged into the rig: the outputs' placements.</summary>
    public static readonly IReadOnlyList<string> Rig = new[] { nameof(ShowState.Output) };

    /// <summary>The NDI senders: their config, the outputs they send, the show's mode.</summary>
    public static readonly IReadOnlyList<string> NdiOut = new[] { nameof(ShowState.Ndi), nameof(ShowState.Output), nameof(ShowState.Mode) };

    /// <summary>
    /// The live-input pool — decoders, NDI receivers, web pages, decks, the arcade: everything a
    /// sink may draw, which the media locator walks (the pattern, the independent screens, the
    /// multiviews' tiles, the overlays' inset, the lower thirds' elements, the web config), the
    /// cues ahead the pre-roll opens for, the stingers that own screens, the audio routing the
    /// taps follow, the monitor rule that decides which bus each mount's sound belongs on, the
    /// capture formats, the switcher's staged tiles, the mode and blackout.
    /// </summary>
    public static readonly IReadOnlyList<string> Inputs = new[]
    {
        nameof(ShowState.Pattern), nameof(ShowState.Independent), nameof(ShowState.Multiviews), nameof(ShowState.Overlays),
        nameof(ShowState.LowerThirds), nameof(ShowState.Web), nameof(ShowState.Ndi), nameof(ShowState.Output),
        nameof(ShowState.LooksAndCues), nameof(ShowState.Stacks), nameof(ShowState.Stingers), nameof(ShowState.AudioRouting),
        nameof(ShowState.Monitor), nameof(ShowState.Presenter), nameof(ShowState.Switcher), nameof(ShowState.CaptureFormats),
        nameof(ShowState.MediaLibrary), nameof(ShowState.Mode), nameof(ShowState.Blackout),
    };

    /// <summary>The audio graph's plan: the routing matrix and the monitor rule (which bus each mount's sound belongs on).</summary>
    public static readonly IReadOnlyList<string> AudioGraph = new[] { nameof(ShowState.AudioRouting), nameof(ShowState.Monitor) };

    /// <summary>OSC in and out: the remote's config.</summary>
    public static readonly IReadOnlyList<string> Osc = new[] { nameof(ShowState.Control) };

    /// <summary>The room's boxes: the Interactive page.</summary>
    public static readonly IReadOnlyList<string> Devices = new[] { nameof(ShowState.Interactive) };

    /// <summary>The wire and the pages: the remote's config, and the install's admin the pages read.</summary>
    public static readonly IReadOnlyList<string> Wire = new[] { nameof(ShowState.Control), nameof(ShowState.Install) };

    /// <summary>The beacon: the watchdog's config, the twin's (callers accepted), the remote's ports, the stream's, the show's name.</summary>
    public static readonly IReadOnlyList<string> Beacon = new[] { nameof(ShowState.Watchdog), nameof(ShowState.Twin), nameof(ShowState.Control), nameof(ShowState.Stream), nameof(ShowState.Name) };

    /// <summary>DNS-SD: the remote's ports and the show's name in the advert.</summary>
    public static readonly IReadOnlyList<string> Mdns = new[] { nameof(ShowState.Control), nameof(ShowState.Name) };

    /// <summary>The twin link: its config and the show's name.</summary>
    public static readonly IReadOnlyList<string> Twin = new[] { nameof(ShowState.Twin), nameof(ShowState.Name) };

    /// <summary>Whether a change to the named sections reaches a system that reads these. Null is "everything moved": always.</summary>
    public static bool Touches(IReadOnlySet<string>? dirty, IReadOnlyList<string> reads)
    {
        if (dirty is null) return true;
        foreach (var section in reads)
        {
            if (dirty.Contains(section)) return true;
        }
        return false;
    }

    /// <summary>The sections as one word for the lines: "Pattern, Overlays", "everything", "nothing".</summary>
    public static string Words(IReadOnlySet<string>? dirty)
        => dirty is null ? "everything" : dirty.Count == 0 ? "nothing" : string.Join(", ", dirty.OrderBy(s => s, StringComparer.Ordinal));
}

/// <summary>
/// What the side effects cost, pass by pass and system by system — the desk-tick budget's twin
/// for the reconcile path: how many passes, the worst and what it followed, and for each system
/// how often it ran, how often the mask let it skip, its last and worst run. Read on the Machine
/// page and in the assistant's brief; a system that exceeds its budget names itself.
/// </summary>
public sealed class ReconcileBudget
{
    /// <summary>A pass past this is a hitch on the desk's thread: the amber line (the desk frame).</summary>
    public const double SlowMs = TickBudget.SlowMs;

    /// <summary>A pass past this is a stutter the operator feels: the red line.</summary>
    public const double StutterMs = TickBudget.StutterMs;

    /// <summary>One system's tally.</summary>
    public sealed record Line(string Name, long Runs, long Skipped, double LastMs, double WorstMs, double TotalMs)
    {
        /// <summary>"inputs 8 runs, 33 skipped, worst 3.9 ms".</summary>
        public string Words => Runs == 0
            ? $"{Name} never ran{(Skipped > 0 ? $", {Skipped} skipped" : "")}"
            : $"{Name} {Runs} run{(Runs == 1 ? "" : "s")}{(Skipped > 0 ? $", {Skipped} skipped" : "")}, worst {WorstMs:0.0} ms";
    }

    private sealed class Entry
    {
        public long Runs;
        public long Skipped;
        public double LastMs = -1;
        public double WorstMs = -1;
        public double TotalMs;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    /// <summary>Passes so far: one per publish that ran the side effects.</summary>
    public long Passes { get; private set; }

    /// <summary>Passes that ran everything because the publish could not name its sections.</summary>
    public long FullPasses { get; private set; }

    /// <summary>Passes past the slow line.</summary>
    public long SlowPasses { get; private set; }

    public double LastPassMs { get; private set; } = -1;
    public double WorstPassMs { get; private set; } = -1;

    /// <summary>The sections the worst pass followed.</summary>
    public string WorstPassSections { get; private set; } = "";

    public int LastRan { get; private set; }
    public int LastSkipped { get; private set; }
    public string LastSections { get; private set; } = "";

    private Entry For(string name)
    {
        if (!_entries.TryGetValue(name, out var e))
        {
            e = new Entry();
            _entries[name] = e;
            _order.Add(name);
        }
        return e;
    }

    /// <summary>A system ran, and took this long.</summary>
    public void Ran(string name, double ms)
    {
        var e = For(name);
        e.Runs++;
        e.LastMs = ms;
        e.TotalMs += ms;
        if (ms > e.WorstMs) e.WorstMs = ms;
    }

    /// <summary>The mask let a system skip: nothing it reads moved.</summary>
    public void Skipped(string name) => For(name).Skipped++;

    /// <summary>A pass is over: how long, how many ran and skipped, and the sections it followed.</summary>
    public void Pass(double ms, int ran, int skipped, string sections, bool full)
    {
        Passes++;
        if (full) FullPasses++;
        if (ms > SlowMs) SlowPasses++;
        LastPassMs = ms;
        LastRan = ran;
        LastSkipped = skipped;
        LastSections = sections;
        if (ms > WorstPassMs)
        {
            WorstPassMs = ms;
            WorstPassSections = sections;
        }
    }

    /// <summary>Every system in the order it was first seen.</summary>
    public IReadOnlyList<Line> Lines()
    {
        var lines = new List<Line>(_order.Count);
        foreach (var name in _order)
        {
            var e = _entries[name];
            lines.Add(new Line(name, e.Runs, e.Skipped, e.LastMs, e.WorstMs, e.TotalMs));
        }
        return lines;
    }

    /// <summary>One system's line, or null before it was seen.</summary>
    public Line? Of(string name) => _entries.TryGetValue(name, out var e) ? new Line(name, e.Runs, e.Skipped, e.LastMs, e.WorstMs, e.TotalMs) : null;

    /// <summary>
    /// The STABILITY line: "Side effects: 41 passes · worst 4.2 ms after Pattern · last 0.3 ms,
    /// 1 of 9 ran after Countdown · inputs 8 runs, 33 skipped, worst 3.9 ms · devices 2 runs, 39
    /// skipped, worst 0.2 ms" — the three costliest systems named.
    /// </summary>
    public string Describe()
    {
        if (Passes == 0) return "Side effects: no pass yet — the first edit runs them.";
        var parts = new List<string>
        {
            $"Side effects: {Passes} pass{(Passes == 1 ? "" : "es")}{(FullPasses > 0 ? $" ({FullPasses} ran everything)" : "")}",
            $"worst {WorstPassMs:0.0} ms after {WorstPassSections}",
            $"last {LastPassMs:0.0} ms, {LastRan} of {LastRan + LastSkipped} ran after {LastSections}",
        };
        foreach (var line in Lines().Where(l => l.Runs > 0).OrderByDescending(l => l.WorstMs).Take(3)) parts.Add(line.Words);
        if (SlowPasses > 0) parts.Add($"{SlowPasses} past {SlowMs:0} ms");
        return string.Join(" · ", parts);
    }

    public void Reset()
    {
        _entries.Clear();
        _order.Clear();
        Passes = 0;
        FullPasses = 0;
        SlowPasses = 0;
        LastPassMs = -1;
        WorstPassMs = -1;
        WorstPassSections = "";
        LastRan = 0;
        LastSkipped = 0;
        LastSections = "";
    }
}
