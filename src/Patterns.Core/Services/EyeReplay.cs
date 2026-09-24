using System.Globalization;

namespace Patterns.Core.Services;

/// <summary>
/// Round 84: what a replay reads — the journal's rows and the metrics file's samples, each sorted by time
/// once. The desk reads the two files and hands the lines in; the core never opens a file here, so the
/// replay is built and tested without a desk. A record is immutable: the desk builds one when the replay
/// opens and drops it when the replay closes.
/// </summary>
public sealed class ReplayRecord
{
    private readonly List<ShowLogEntry> _rows;
    private readonly List<MetricSample> _samples;

    private ReplayRecord(List<ShowLogEntry> rows, List<MetricSample> samples)
    {
        _rows = rows;
        _samples = samples;
        if (rows.Count == 0 && samples.Count == 0) return;
        var first = DateTime.MaxValue;
        var last = DateTime.MinValue;
        if (rows.Count > 0) { first = rows[0].AtUtc; last = rows[^1].AtUtc; }
        if (samples.Count > 0)
        {
            if (samples[0].Utc < first) first = samples[0].Utc;
            if (samples[^1].Utc > last) last = samples[^1].Utc;
        }
        FirstUtc = first;
        LastUtc = last;
    }

    public static ReplayRecord Empty { get; } = new(new List<ShowLogEntry>(), new List<MetricSample>());

    /// <summary>The record from what was read, in any order; rows and samples are sorted once here.</summary>
    public static ReplayRecord From(IEnumerable<ShowLogEntry> rows, IEnumerable<MetricSample> samples)
        => new(rows.OrderBy(r => r.AtUtc).ToList(), samples.OrderBy(s => s.Utc).ToList());

    /// <summary>The journal's rows, oldest first.</summary>
    public IReadOnlyList<ShowLogEntry> Rows => _rows;

    /// <summary>The metrics file's samples, oldest first.</summary>
    public IReadOnlyList<MetricSample> Samples => _samples;

    public bool IsEmpty => _rows.Count == 0 && _samples.Count == 0;

    /// <summary>The earliest stamp in the record (a row's or a sample's), null when the record is empty.</summary>
    public DateTime? FirstUtc { get; }

    /// <summary>The latest stamp in the record, null when the record is empty.</summary>
    public DateTime? LastUtc { get; }

    /// <summary>How long the record runs; zero for an empty record or one stamp.</summary>
    public TimeSpan Span => FirstUtc is { } f && LastUtc is { } l ? l - f : TimeSpan.Zero;

    /// <summary>The instant held inside the record; an empty record answers the instant itself.</summary>
    public DateTime Clamp(DateTime atUtc)
    {
        if (FirstUtc is not { } f || LastUtc is not { } l) return atUtc;
        if (atUtc < f) return f;
        return atUtc > l ? l : atUtc;
    }

    /// <summary>Where an instant sits in the record, 0 at the first stamp and 1 at the last — the scrub bar's position.</summary>
    public double Position(DateTime atUtc)
    {
        if (FirstUtc is not { } f || Span <= TimeSpan.Zero) return 1;
        return (Clamp(atUtc) - f).Ticks / (double)Span.Ticks;
    }

    /// <summary>The instant at a position of the record (0..1); an empty record answers the instant given as its floor.</summary>
    public DateTime AtPosition(double position, DateTime whenEmptyUtc)
    {
        if (FirstUtc is not { } f) return whenEmptyUtc;
        var p = double.IsNaN(position) ? 1 : Math.Clamp(position, 0, 1);
        return f + TimeSpan.FromTicks((long)Math.Round(Span.Ticks * p));
    }

    /// <summary>The last sample at or before the instant, and the one before it; null when there is none.</summary>
    public (MetricSample? Sample, MetricSample? Previous) SamplesAt(DateTime atUtc)
    {
        var i = UpperBound(_samples, s => s.Utc, atUtc) - 1;
        if (i < 0) return (null, null);
        return (_samples[i], i > 0 ? _samples[i - 1] : null);
    }

    /// <summary>The rows after one instant and up to another, oldest first, the newest kept when there are more than <paramref name="max"/>.</summary>
    public IReadOnlyList<ShowLogEntry> RowsBetween(DateTime afterUtc, DateTime untilUtc, int max)
    {
        var from = UpperBound(_rows, r => r.AtUtc, afterUtc);
        var to = UpperBound(_rows, r => r.AtUtc, untilUtc);
        if (to <= from) return Array.Empty<ShowLogEntry>();
        if (to - from > max) from = to - max;
        return _rows.GetRange(from, to - from);
    }

    /// <summary>The last row at or before the instant, whatever the window; null when there is none.</summary>
    public ShowLogEntry? LastRowAt(DateTime atUtc)
    {
        var i = UpperBound(_rows, r => r.AtUtc, atUtc) - 1;
        return i < 0 ? null : _rows[i];
    }

    /// <summary>The index of the first item whose stamp is after the instant (the count when none is).</summary>
    private static int UpperBound<T>(List<T> items, Func<T, DateTime> stamp, DateTime atUtc)
    {
        int lo = 0, hi = items.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (stamp(items[mid]) <= atUtc) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}

/// <summary>
/// One instant of the record: the sample in force then (the last one written at or before it) with the one
/// before it, the rows of the window that closes at the instant (oldest first), and the last row before the
/// instant whatever the window — what the picture and the strip read.
/// </summary>
public sealed record ReplayMoment(DateTime AtUtc, MetricSample? Sample, MetricSample? Previous, IReadOnlyList<ShowLogEntry> Recent, ShowLogEntry? Last)
{
    /// <summary>How long before the instant the sample in force was written; null without a sample.</summary>
    public TimeSpan? SampleAge => Sample is null ? null : AtUtc - Sample.Utc;
}

/// <summary>
/// Round 84: the Eye replayed. The structure is the Eye of now — the things and the links the desk has —
/// and the lights and the words come from the record alone: a screen shows the outcome of the rows that
/// named it in the thirty seconds before the instant, the desk shows the machine's sample then, and a thing
/// the record does not mention in that window is grey and says so. Attempts are not facts: what a row
/// recorded is what the replay shows, never what the structure of now would say.
/// </summary>
public static class EyeReplay
{
    /// <summary>The window a row lights its thing for: the thirty seconds before the instant.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    /// <summary>A sample older than this before the instant no longer speaks for the desk (three sample periods).</summary>
    public static readonly TimeSpan StaleSample = TimeSpan.FromSeconds(90);

    /// <summary>The most rows one moment carries — a bound, not a budget; a busy cue writes a few dozen.</summary>
    public const int MaxRecent = 200;

    public const string NoRecord = "no record in the last 30 s";

    /// <summary>The moment of the record at an instant.</summary>
    public static ReplayMoment At(ReplayRecord record, DateTime atUtc)
    {
        var (sample, previous) = record.SamplesAt(atUtc);
        return new ReplayMoment(atUtc, sample, previous, record.RowsBetween(atUtc - Window, atUtc, MaxRecent), record.LastRowAt(atUtc));
    }

    /// <summary>
    /// The structure with the moment's lights and words: every node and every link kept, in order, with
    /// its light and words replaced; the links are grey because the record holds things, not links.
    /// </summary>
    public static EyeGraph Apply(EyeGraph structure, ReplayMoment moment)
    {
        var byNode = new Dictionary<string, List<ShowLogEntry>>(StringComparer.Ordinal);
        foreach (var row in moment.Recent)
        {
            foreach (var id in NodesFor(structure, row))
            {
                if (!byNode.TryGetValue(id, out var list)) byNode[id] = list = new List<ShowLogEntry>();
                list.Add(row);
            }
        }
        var desk = Health(moment.Sample, moment.Previous, moment.SampleAge);
        return structure.Relit(n => Relight(n, byNode, desk), e => e with { Light = CheckLight.Grey });
    }

    /// <summary>
    /// The things a row speaks for: a canvas key names each of its member screens that is drawn; any other
    /// target is a screen by id or by its wire number, a device by id, a thing by its id, or a thing by its
    /// exact label; a target the picture does not have — a cue, a look, a lower third, nothing — is the
    /// desk's, because the desk did it. An empty list only when the structure has no desk.
    /// </summary>
    public static IReadOnlyList<string> NodesFor(EyeGraph structure, ShowLogEntry row)
    {
        var desk = structure.Find(EyeGraph.DeskId) is null ? Array.Empty<string>() : new[] { EyeGraph.DeskId };
        var target = row.Target.Trim();
        if (target.Length == 0) return desk;
        if (ContentTargets.IsCanvasKey(target))
        {
            var members = ContentTargets.Members(target).Select(m => "screen:" + m).Where(id => structure.Find(id) is not null).ToList();
            return members.Count > 0 ? members : desk;
        }
        var one = OneNodeFor(structure, target);
        return one is null ? desk : new[] { one };
    }

    private static string? OneNodeFor(EyeGraph structure, string target)
    {
        if (structure.Find("screen:" + target) is not null) return "screen:" + target;
        if (structure.Find("device:" + target) is not null) return "device:" + target;
        if (structure.Find(target) is not null) return target;
        foreach (var (id, number) in structure.ScreenNumbers)
        {
            if (number.Length > 0 && number == target) return id;                        // the wire's SCREEN n
        }
        return structure.Nodes.FirstOrDefault(n => n.Label.Equals(target, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>The light a journal outcome earns; a word the replay does not know is grey, never green.</summary>
    public static CheckLight LightOf(string outcome) => outcome switch
    {
        "Failed" or "FailedLate" => CheckLight.Red,
        "Refused" or "DoneWithWarnings" or "Skipped" or "Partial" => CheckLight.Amber,
        "Done" or "Requested" or "Solved" or "Settled" => CheckLight.Green,
        _ => CheckLight.Grey,
    };

    /// <summary>
    /// The desk's light from the machine's sample in force at the instant: grey without a sample or with a
    /// stale one; red for a render fault or a missed slot in the sample's own minute (the budget counts both
    /// over the last minute, so the sample's number is the count and is never read against the sample
    /// before), for a pool starved since the sample before (that counter counts since the desk started), or
    /// for a p95 frame past 50 ms; amber for a slow frame, a p95 past the hitch line or a desk on battery;
    /// else green. The words carry the sample's numbers and when it was written.
    /// </summary>
    public static (CheckLight Light, string Sub, IReadOnlyList<string> Words) Health(MetricSample? sample, MetricSample? previous, TimeSpan? age)
    {
        if (sample is null || age is not { } ago)
        {
            return (CheckLight.Grey, "no sample at this time", new[] { "The metrics file has no sample at or before this moment." });
        }
        var words = new List<string> { $"Sample {Clock(sample.Utc)} · {Ago(ago)} before this moment" };
        if (ago > StaleSample)
        {
            words.Add("Too old to speak for the desk at this moment — samples come every 30 s while the desk runs.");
            return (CheckLight.Grey, $"the last sample is {Ago(ago)} before this moment", words);
        }
        var light = CheckLight.Green;
        var reasons = new List<string>();
        void Mark(CheckLight l, string reason)
        {
            if (l > light) light = l;
            reasons.Add(reason);
        }
        var renderFaults = sample.RenderFaults;                                           // the last minute's count, as written
        var missed = sample.MissedSlots;                                                  // the last minute's count, as written
        var starved = Rose(sample.PoolStarved, previous?.PoolStarved);                    // since the desk started: its rise
        if (renderFaults > 0) Mark(CheckLight.Red, Count(renderFaults, "render fault"));
        if (starved > 0) Mark(CheckLight.Red, $"pool starved {N(starved)}×");
        if (missed > 0) Mark(CheckLight.Red, Count(missed, "missed slot"));
        if (sample.P95FrameMs > 50) Mark(CheckLight.Red, $"p95 frame {N(sample.P95FrameMs)} ms");
        else if (sample.P95FrameMs > RenderStats.SlowFrameMs) Mark(CheckLight.Amber, $"p95 frame {N(sample.P95FrameMs)} ms");
        if (sample.SlowFrames > 0) Mark(CheckLight.Amber, Count(sample.SlowFrames, "slow frame"));
        if (sample.OnBattery) Mark(CheckLight.Amber, "on battery");
        var sub = reasons.Count == 0 ? "steady" : string.Join(" · ", reasons);
        words.Add($"Output {N(sample.OutputFps)} fps · worst frame {N(sample.WorstFrameMs)} ms" + (sample.P95FrameMs >= 0 ? $" · p95 {N(sample.P95FrameMs)} ms" : ""));
        words.Add($"CPU {Pct(sample.CpuAppPct)} (machine {Pct(sample.CpuSystemPct)}) · RAM {Mb(sample.RamAppMB)} (machine {Pct(sample.RamSystemPct)})" + (sample.GpuBusyPct >= 0 ? $" · GPU {Pct(sample.GpuBusyPct)}" : ""));
        if (sample.Faults > 0) words.Add($"{N(sample.Faults)} faults since the desk started");
        return (light, sub, words);
    }

    /// <summary>One line for the strip: the instant, the rows in its window, the machine's light from the sample then (the desk node adds its rows to that), and the last row before it when the window is empty.</summary>
    public static string Words(ReplayMoment moment)
    {
        var desk = Health(moment.Sample, moment.Previous, moment.SampleAge);
        var rows = moment.Recent.Count switch { 0 => "no rows", 1 => "1 row", var n => $"{N(n)} rows" };
        var line = $"{Clock(moment.AtUtc)} · {rows} in the 30 s before · machine {EyeGraph.Light(desk.Light)}: {desk.Sub}";
        if (moment.Recent.Count == 0 && moment.Last is { } last) line += $" · last row {Clock(last.AtUtc)} ({Ago(moment.AtUtc - last.AtUtc)} before): {last.Kind} {last.Target} {last.Outcome}".TrimEnd();
        return line;
    }

    /// <summary>A row as one line of a thing's words: the clock, the verb, the target, the outcome, the message and the round-79 stamps.</summary>
    public static string Line(ShowLogEntry row)
    {
        var line = $"{Clock(row.AtUtc)} {row.Kind}" + (row.Target.Length > 0 ? " " + row.Target : "") + $" — {row.Outcome}";
        if (row.Message.Length > 0) line += ": " + row.Message;
        if (row.Visibility is { Length: > 0 } v) line += " · " + v;
        if (row.Effect is { Length: > 0 } e) line += " · " + e;
        return line;
    }

    private static EyeNode Relight(EyeNode n, Dictionary<string, List<ShowLogEntry>> byNode, (CheckLight Light, string Sub, IReadOnlyList<string> Words) desk)
    {
        var isDesk = n.Id == EyeGraph.DeskId;
        var light = isDesk ? desk.Light : CheckLight.Grey;
        var sub = isDesk ? desk.Sub : NoRecord;
        var words = isDesk ? new List<string>(desk.Words) : new List<string>();
        if (byNode.TryGetValue(n.Id, out var rows) && rows.Count > 0)
        {
            var worst = rows.Max(r => LightOf(r.Outcome));
            if (worst > light) light = worst;
            var last = rows[^1];
            var target = isDesk && last.Target.Length > 0 ? " " + last.Target : "";        // a thing's own rows need no target; the desk's say what they did it to
            var lastWords = $"{last.Kind}{target} {last.Outcome}";
            sub = isDesk ? desk.Sub + " · " + lastWords : lastWords + (last.Message.Length > 0 ? " · " + last.Message : "");
            words.AddRange(rows.Select(Line));
        }
        return n with { Light = light, Sub = sub, Words = words };
    }

    private static long Rose(long now, long? before) => before is { } b ? Math.Max(0, now - b) : now;

    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span < TimeSpan.FromMinutes(1)) return $"{N(Math.Round(span.TotalSeconds))} s";
        if (span < TimeSpan.FromHours(1)) return $"{N(Math.Floor(span.TotalMinutes))} min {N(span.Seconds)} s";
        return $"{N(Math.Floor(span.TotalHours))} h {N(span.Minutes)} min";
    }

    private static string Count(long n, string noun) => n == 1 ? $"1 {noun}" : $"{N(n)} {noun}s";
    private static string Pct(double v) => v < 0 ? "—" : N(Math.Round(v)) + "%";
    private static string Mb(double v) => v < 0 ? "—" : N(Math.Round(v)) + " MB";
    private static string N(double v) => v.ToString(CultureInfo.InvariantCulture);
    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Round 84: the time a replay verb names. A clock time (20:14 or 20:14:03) is today's in the desk's zone,
/// or yesterday's when it lies ahead of now; an ISO 8601 stamp is read as given (a Z or an offset honoured,
/// none read as the desk's zone). Anything else is refused: a time is a value, and a word that is not one
/// is an unknown word.
/// </summary>
public static class ReplayTime
{
    private static readonly string[] ClockShapes = { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" };
    private static readonly string[] StampShapes =
    {
        "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "yyyy-MM-ddTHH:mmK",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm",
    };

    public static bool TryParse(string? words, DateTime nowUtc, out DateTime atUtc)
    {
        atUtc = default;
        var w = (words ?? "").Trim();
        if (w.Length == 0) return false;
        if (TimeSpan.TryParseExact(w, ClockShapes, CultureInfo.InvariantCulture, out var clock))
        {
            var nowLocal = nowUtc.ToLocalTime();
            var candidate = nowLocal.Date + clock;
            if (candidate > nowLocal.AddMinutes(1)) candidate = candidate.AddDays(-1);
            atUtc = DateTime.SpecifyKind(candidate, DateTimeKind.Local).ToUniversalTime();
            return true;
        }
        if (DateTime.TryParseExact(w, StampShapes, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var stamp))
        {
            atUtc = DateTime.SpecifyKind(stamp, DateTimeKind.Utc);
            return true;
        }
        return false;
    }

    /// <summary>A stamp the wire can give back and read again: ISO 8601 in UTC.</summary>
    public static string Stamp(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
