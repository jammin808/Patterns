using System.Globalization;
using System.Text;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>When one cue is expected, beside when the running order wanted it. Times of day.</summary>
public sealed record CueEstimate(RunCueConfig Cue, TimeSpan? PlannedAt, TimeSpan EstimatedAt, bool Uncertain, bool Past)
{
    /// <summary>Positive when the cue will be late against its plan.</summary>
    public TimeSpan? Delta => PlannedAt is { } p ? EstimatedAt - p : null;
}

/// <summary>The next break, lunch or the end: when it is expected, when it was planned, how far off.</summary>
public sealed record MarkEstimate(RunCueConfig Cue, CueMark Mark, TimeSpan EstimatedAt, TimeSpan? PlannedAt, bool Uncertain)
{
    public TimeSpan? Delta => PlannedAt is { } p ? EstimatedAt - p : null;

    /// <summary>"≈ 10:42 (planned 10:35, +7 min)" — "at least" when a running cue has overrun its plan.</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder(Uncertain ? "≥ " : "≈ ");
            sb.Append(CueTiming.FormatClock(EstimatedAt));
            if (PlannedAt is { } p)
            {
                sb.Append(" (planned ").Append(CueTiming.FormatClock(p));
                var d = Delta!.Value;
                if (Math.Abs(d.TotalSeconds) >= 30) sb.Append(", ").Append(CueTiming.FormatDelta(d));
                sb.Append(')');
            }
            return sb.ToString();
        }
    }
}

/// <summary>Where the day stands: how late or early, the running cue's remaining time, when the marks are expected.</summary>
public sealed class TimingReport
{
    public static readonly TimingReport Empty = new();

    /// <summary>Positive = behind the plan, negative = ahead; null when nothing has a planned time to compare.</summary>
    public TimeSpan? Offset { get; init; }

    /// <summary>"ON TIME", "3 MIN LATE", "2 MIN EARLY" — empty when nothing is planned.</summary>
    public string OffsetText { get; init; } = "";

    public bool IsLate => Offset is { } o && o > CueTiming.Tolerance;

    public bool IsEarly => Offset is { } o && o < -CueTiming.Tolerance;

    /// <summary>What is left of the running cue's plan; null without a plan; zero when it has overrun.</summary>
    public TimeSpan? RunningRemaining { get; init; }

    public bool RunningOverran { get; init; }

    public IReadOnlyList<CueEstimate> Cues { get; init; } = Array.Empty<CueEstimate>();

    public MarkEstimate? NextBreak { get; init; }

    public MarkEstimate? Lunch { get; init; }

    public MarkEstimate? End { get; init; }

    public CueEstimate? For(string cueId) => Cues.FirstOrDefault(e => e.Cue.Id == cueId);

    /// <summary>"Next break ≈ 10:42 (planned 10:35, +7 min) · Lunch ≈ 12:38 … · End ≈ 17:07 …" — empty with no marks and no end.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (NextBreak is { } b) parts.Add("Next break " + b.Text);
            if (Lunch is { } l) parts.Add("Lunch " + l.Text);
            if (End is { } e) parts.Add((e.Mark == CueMark.End ? "End " : "Last cue done ") + e.Text);
            return string.Join("  ·  ", parts);
        }
    }
}

/// <summary>
/// The caller's clock: planned starts and lengths against the real clock. Everything here is a
/// pure function of the list, the place and the time, so a late day reads the same on the Run
/// surface, on a remote and in the tests. Times of day; a show does not cross midnight here.
/// </summary>
public static class CueTiming
{
    /// <summary>Within this of the plan is "on time".</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(30);

    /// <summary>A cue squeezed by a catch-up never goes under this.</summary>
    public const int MinSeconds = 30;

    // ---- text ---------------------------------------------------------------------------------

    /// <summary>"9:30", "09:30", "09:30:15", "9.30", "0930", "9:30 am", "2:15 pm" → a time of day, or null.</summary>
    public static TimeSpan? ParseClock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().ToLowerInvariant();
        var pm = s.EndsWith("pm") || s.EndsWith("p.m.");
        var am = s.EndsWith("am") || s.EndsWith("a.m.");
        if (pm || am) s = s[..s.LastIndexOf(pm ? 'p' : 'a')].Trim();
        s = s.Replace('.', ':');
        int hours, minutes, seconds = 0;
        var parts = s.Split(':');
        if (parts.Length == 1)
        {
            if (parts[0].Length is 3 or 4 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var packed))
            {
                hours = packed / 100;
                minutes = packed % 100;
            }
            else
            {
                return null;
            }
        }
        else if (parts.Length is 2 or 3)
        {
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)) return null;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)) return null;
            if (parts.Length == 3 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out seconds)) return null;
        }
        else
        {
            return null;
        }
        if (pm && hours < 12) hours += 12;
        if (am && hours == 12) hours = 0;
        if (hours is < 0 or > 23 || minutes is < 0 or > 59 || seconds is < 0 or > 59) return null;
        return new TimeSpan(hours, minutes, seconds);
    }

    public static string FormatClock(TimeSpan t)
    {
        var total = ((int)t.TotalMinutes % (24 * 60) + 24 * 60) % (24 * 60);
        return $"{total / 60:00}:{total % 60:00}";
    }

    /// <summary>"mm:ss", "h:mm:ss", "5m", "5 min", "1h", "1h30", "90s", "300" (seconds), "2.5 min" → seconds, or null.</summary>
    public static int? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().ToLowerInvariant().Replace(" ", "");
        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length is < 2 or > 3) return null;
            var nums = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return null;
            }
            return parts.Length == 2 ? nums[0] * 60 + nums[1] : nums[0] * 3600 + nums[1] * 60 + nums[2];
        }
        // Units: h, m/min/mins/minute(s), s/sec/secs; "1h30" = 1 h 30 min.
        double total = 0;
        var number = new StringBuilder();
        var any = false;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsDigit(ch) || ch == '.' || ch == ',')
            {
                number.Append(ch == ',' ? '.' : ch);
                continue;
            }
            if (number.Length == 0) return null;
            if (!double.TryParse(number.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
            number.Clear();
            var unit = new StringBuilder();
            while (i < s.Length && char.IsLetter(s[i])) unit.Append(s[i++]);
            i--;
            var u = unit.ToString();
            if (u.StartsWith('h')) total += value * 3600;
            else if (u.StartsWith('m')) total += value * 60;
            else if (u.StartsWith('s')) total += value;
            else return null;
            any = true;
        }
        if (number.Length > 0)
        {
            if (!double.TryParse(number.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rest)) return null;
            // A bare number is seconds; after an hour it is minutes ("1h30").
            total += any ? rest * 60 : rest;
        }
        else if (!any)
        {
            return null;
        }
        return total < 0 ? null : (int)Math.Round(total);
    }

    public static string FormatDuration(int seconds)
    {
        seconds = Math.Max(0, seconds);
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    /// <summary>"+7 min", "−2 min", "+40 s".</summary>
    public static string FormatDelta(TimeSpan d)
    {
        var sign = d < TimeSpan.Zero ? "−" : "+";
        var abs = d.Duration();
        return abs.TotalMinutes >= 1 ? $"{sign}{Math.Round(abs.TotalMinutes):0} min" : $"{sign}{abs.Seconds} s";
    }

    /// <summary>"ON TIME", "3 MIN LATE", "40 S EARLY".</summary>
    public static string OffsetText(TimeSpan offset)
    {
        var abs = offset.Duration();
        if (abs <= Tolerance) return "ON TIME";
        var amount = abs.TotalMinutes >= 1 ? $"{Math.Round(abs.TotalMinutes):0} MIN" : $"{abs.Seconds} S";
        return $"{amount} {(offset > TimeSpan.Zero ? "LATE" : "EARLY")}";
    }

    // ---- the plan --------------------------------------------------------------------------------

    /// <summary>What to plan for a cue's length: its own, else the gap to the next planned start, else null (unknown).</summary>
    public static int? DurationOf(IList<RunCueConfig> cues, int index)
    {
        if (index < 0 || index >= cues.Count) return null;
        var cue = cues[index];
        if (cue.PlannedSeconds is { } own) return own;
        if (ParseClock(cue.PlannedStart) is not { } start) return null;
        for (var i = index + 1; i < cues.Count; i++)
        {
            if (!cues[i].Enabled) continue;
            if (ParseClock(cues[i].PlannedStart) is { } next)
            {
                var gap = next - start;
                return gap >= TimeSpan.Zero ? (int)gap.TotalSeconds : null;
            }
            return null; // the next cue's length is unknown, so the gap is not this cue's
        }
        return null;
    }

    /// <summary>
    /// Where the day stands. The running cue (last GO at <paramref name="runningStartLocal"/>) is
    /// given what its plan has left; every later cue is placed after it by the planned lengths;
    /// the offset is the running cue's real start against its planned one, else the standby's
    /// expected start against its plan. A cue without a length makes everything after it
    /// "at least"; so does a running cue that has overrun.
    /// </summary>
    public static TimingReport Estimate(IList<RunCueConfig> cues, string? runningId, DateTime? runningStartLocal, string? standbyId, DateTime nowLocal)
    {
        if (cues.Count == 0) return TimingReport.Empty;
        var now = nowLocal.TimeOfDay;
        var runningIndex = runningId is null ? -1 : IndexOf(cues, runningId);
        var standbyIndex = standbyId is null ? -1 : IndexOf(cues, standbyId);

        var cursor = now;
        var uncertain = false;
        TimeSpan? remaining = null;
        var overran = false;
        int startIndex;
        if (runningIndex >= 0 && runningStartLocal is { } started)
        {
            var planned = DurationOf(cues, runningIndex);
            var elapsed = nowLocal - started;
            if (planned is { } p)
            {
                var left = TimeSpan.FromSeconds(p) - elapsed;
                if (left > TimeSpan.Zero)
                {
                    remaining = left;
                    cursor = now + left;
                }
                else
                {
                    remaining = TimeSpan.Zero;
                    overran = true;
                    uncertain = true;
                }
            }
            else
            {
                uncertain = true;
            }
            startIndex = standbyIndex > runningIndex ? standbyIndex : runningIndex + 1;
        }
        else
        {
            startIndex = standbyIndex >= 0 ? standbyIndex : 0;
        }

        TimeSpan? offset = null;
        if (runningIndex >= 0 && runningStartLocal is { } s && ParseClock(cues[runningIndex].PlannedStart) is { } plannedStart)
        {
            offset = s.TimeOfDay - plannedStart;
        }
        else if (startIndex < cues.Count && ParseClock(cues[startIndex].PlannedStart) is { } standbyPlan)
        {
            offset = cursor - standbyPlan;
        }

        var list = new List<CueEstimate>(cues.Count);
        MarkEstimate? nextBreak = null, lunch = null, end = null;
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var plannedAt = ParseClock(cue.PlannedStart);
            if (i < startIndex)
            {
                list.Add(new CueEstimate(cue, plannedAt, plannedAt ?? TimeSpan.Zero, false, Past: true));
                continue;
            }
            list.Add(new CueEstimate(cue, plannedAt, cursor, uncertain, Past: false));
            if (!cue.Enabled) continue;
            switch (cue.Mark)
            {
                case CueMark.Break when nextBreak is null:
                    nextBreak = new MarkEstimate(cue, CueMark.Break, cursor, plannedAt, uncertain);
                    break;
                case CueMark.Lunch when lunch is null:
                    lunch = new MarkEstimate(cue, CueMark.Lunch, cursor, plannedAt, uncertain);
                    break;
                case CueMark.End when end is null:
                    end = new MarkEstimate(cue, CueMark.End, cursor, plannedAt, uncertain);
                    break;
            }
            var length = DurationOf(cues, i);
            if (length is { } len) cursor += TimeSpan.FromSeconds(len);
            else uncertain = true;
        }
        if (end is null)
        {
            // No cue marks the end: the day ends when the last enabled cue's plan runs out.
            var last = cues.LastOrDefault(c => c.Enabled);
            if (last is not null && cues.IndexOf(last) >= startIndex) end = new MarkEstimate(last, CueMark.None, cursor, null, uncertain);
        }

        return new TimingReport
        {
            Offset = offset,
            OffsetText = offset is { } o ? OffsetText(o) : "",
            RunningRemaining = remaining,
            RunningOverran = overran,
            Cues = list,
            NextBreak = nextBreak,
            Lunch = lunch,
            End = end,
        };
    }

    // ---- the caller's edits ---------------------------------------------------------------------

    /// <summary>Moves every planned start from a cue onward by a delta — the caller pushing or pulling the rest of the day. Returns how many moved.</summary>
    public static int Shift(IList<RunCueConfig> cues, int fromIndex, TimeSpan delta)
    {
        var moved = 0;
        for (var i = Math.Max(0, fromIndex); i < cues.Count; i++)
        {
            if (ParseClock(cues[i].PlannedStart) is not { } at) continue;
            cues[i].PlannedStart = FormatClock(at + delta);
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// "We resume now": the cue's planned start becomes <paramref name="nowOfDay"/> and every later
    /// planned start moves by the same amount, so the plan is honest again and the marks re-estimate
    /// from it. A cue without a planned start just gets one. Returns how many cues changed.
    /// </summary>
    public static int Rebase(IList<RunCueConfig> cues, int index, TimeSpan nowOfDay)
    {
        if (index < 0 || index >= cues.Count) return 0;
        var now = TimeSpan.FromMinutes(Math.Round(nowOfDay.TotalMinutes));
        if (ParseClock(cues[index].PlannedStart) is { } planned)
        {
            return Shift(cues, index, now - planned);
        }
        cues[index].PlannedStart = FormatClock(now);
        return 1;
    }

    /// <summary>
    /// Makes up time before the next mark: the planned lengths from <paramref name="fromIndex"/> up to
    /// (not including) the next break, lunch or end shrink together, in proportion, until
    /// <paramref name="behind"/> is recovered or every cue is at its floor. Only cues with a length
    /// of their own take part. Returns the seconds recovered.
    /// </summary>
    public static int CatchUp(IList<RunCueConfig> cues, int fromIndex, TimeSpan behind)
    {
        var want = (int)Math.Round(behind.TotalSeconds);
        if (want <= 0) return 0;
        var stretch = new List<RunCueConfig>();
        for (var i = Math.Max(0, fromIndex); i < cues.Count; i++)
        {
            var cue = cues[i];
            if (cue.Mark != CueMark.None && i > fromIndex) break;
            if (cue.Enabled && cue.PlannedSeconds is { } p && p > MinSeconds) stretch.Add(cue);
        }
        var shrinkable = stretch.Sum(c => c.PlannedSeconds!.Value - MinSeconds);
        if (shrinkable <= 0) return 0;
        var take = Math.Min(want, shrinkable);
        var factor = take / (double)shrinkable;
        var recovered = 0;
        foreach (var cue in stretch)
        {
            var room = cue.PlannedSeconds!.Value - MinSeconds;
            var cut = (int)Math.Round(room * factor);
            cut = Math.Min(cut, take - recovered);
            cue.PlannedSeconds -= cut;
            recovered += cut;
        }
        // Rounding can leave a few seconds: the last cue with room gives them up.
        for (var i = stretch.Count - 1; i >= 0 && recovered < take; i--)
        {
            var room = stretch[i].PlannedSeconds!.Value - MinSeconds;
            var cut = Math.Min(room, take - recovered);
            stretch[i].PlannedSeconds -= cut;
            recovered += cut;
        }
        return recovered;
    }

    private static int IndexOf(IList<RunCueConfig> cues, string id)
    {
        for (var i = 0; i < cues.Count; i++)
        {
            if (cues[i].Id == id) return i;
        }
        return -1;
    }
}
