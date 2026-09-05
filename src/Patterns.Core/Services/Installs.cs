using System.Globalization;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The clock side of a permanent install — a shop window, a hotel lobby, a museum wall: which
/// programme is on at any moment, when an advert or an announcement fires, what the next change
/// is, what the day looks like. Pure: a config and a local time in, decisions out, so every rule
/// is unit tested against a fixed clock.
/// </summary>
public static class Schedule
{
    public const int Mon = 1, Tue = 2, Wed = 4, Thu = 8, Fri = 16, Sat = 32, Sun = 64;
    public const int Weekdays = Mon | Tue | Wed | Thu | Fri;
    public const int Weekend = Sat | Sun;
    public const int EveryDay = Weekdays | Weekend;

    private static readonly string[] DayNames = { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };
    private static readonly string[] DayLabels = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
    private static readonly string[] DateFormats = { "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy", "d MMM yyyy", "d MMMM yyyy", "MMM d yyyy" };

    public static int DayBit(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Mon,
        DayOfWeek.Tuesday => Tue,
        DayOfWeek.Wednesday => Wed,
        DayOfWeek.Thursday => Thu,
        DayOfWeek.Friday => Fri,
        DayOfWeek.Saturday => Sat,
        _ => Sun,
    };

    /// <summary>
    /// "" or "every day" → all seven; "Mon–Fri", "weekdays", "Sat Sun", "Mon, Wed, Fri", "Fri-Sun",
    /// "weekends"; day names whole or by their first letters. False for a word nobody knows.
    /// </summary>
    public static bool TryParseDays(string? text, out int mask)
    {
        mask = 0;
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0)
        {
            mask = EveryDay;
            return true;
        }
        t = t.Replace('–', '-').Replace('—', '-').Replace("..", "-").Replace(" to ", "-").Replace(" - ", "-").Replace(" -", "-").Replace("- ", "-");
        foreach (var raw in t.Split(new[] { ' ', ',', '/', '&', '+', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim('.');
            switch (word)
            {
                case "daily": case "every": case "everyday": case "all": case "always":
                    mask |= EveryDay;
                    continue;
                case "day": case "days": case "and": case "on":
                    continue;
                case "weekdays": case "weekday": case "workdays":
                    mask |= Weekdays;
                    continue;
                case "weekend": case "weekends":
                    mask |= Weekend;
                    continue;
            }
            var dash = word.IndexOf('-');
            if (dash > 0 && dash < word.Length - 1)
            {
                var a = DayIndex(word[..dash]);
                var b = DayIndex(word[(dash + 1)..]);
                if (a < 0 || b < 0) return false;
                for (var i = a; ; i = (i + 1) % 7)
                {
                    mask |= 1 << i;
                    if (i == b) break;
                }
                continue;
            }
            var index = DayIndex(word);
            if (index < 0) return false;
            mask |= 1 << index;
        }
        return mask != 0;
    }

    private static int DayIndex(string word)
    {
        if (word.Length < 2) return -1;
        var head = word.Length >= 3 ? word[..3] : word;
        for (var i = 0; i < 7; i++)
        {
            if (DayNames[i].StartsWith(head, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>"every day", "Mon–Fri", "Sat, Sun", "Mon, Wed, Fri", "Thu–Sun"; "?" when the text does not read.</summary>
    public static string DescribeDays(string? text) => TryParseDays(text, out var mask) ? DescribeMask(mask) : "?";

    public static string DescribeMask(int mask)
    {
        if (mask == EveryDay) return "every day";
        if (mask == Weekdays) return "Mon–Fri";
        if (mask == Weekend) return "Sat, Sun";
        var parts = new List<string>();
        var i = 0;
        while (i < 7)
        {
            if ((mask & (1 << i)) == 0)
            {
                i++;
                continue;
            }
            var j = i;
            while (j + 1 < 7 && (mask & (1 << (j + 1))) != 0) j++;
            if (j - i >= 2) parts.Add($"{DayLabels[i]}–{DayLabels[j]}");
            else if (j == i) parts.Add(DayLabels[i]);
            else parts.Add($"{DayLabels[i]}, {DayLabels[j]}");
            i = j + 1;
        }
        return parts.Count == 0 ? "never" : string.Join(", ", parts);
    }

    /// <summary>"2026-12-24" (the safe form), "24/12/2026", "24.12.2026", "24 Dec 2026".</summary>
    public static bool TryParseDate(string? text, out DateOnly date)
    {
        date = default;
        var t = (text ?? "").Trim();
        return t.Length > 0 && DateOnly.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    public static bool TryParseTime(string? text, out TimeSpan time) => CountdownService.TryParseTime(text, out time);

    /// <summary>The slot is on this calendar day: its days of the week and its dates (both ends inclusive; blank = open).</summary>
    public static bool OnDay(ScheduleSlotConfig slot, DateOnly day)
    {
        if (!TryParseDays(slot.Days, out var mask) || (mask & DayBit(day.DayOfWeek)) == 0) return false;
        if (slot.From.Length > 0 && (!TryParseDate(slot.From, out var from) || day < from)) return false;
        if (slot.Until.Length > 0 && (!TryParseDate(slot.Until, out var until) || day > until)) return false;
        return true;
    }

    /// <summary>A window ending at or before it starts runs into the next day (22:00–02:00).</summary>
    public static bool CrossesMidnight(ScheduleSlotConfig slot)
        => TryParseTime(slot.Start, out var s) && TryParseTime(slot.End, out var e) && e <= s;

    /// <summary>The window that starts on <paramref name="day"/> — it may end the next day — or null when the slot is not on that day or its times do not read.</summary>
    public static (DateTime Start, DateTime End)? WindowOn(ScheduleSlotConfig slot, DateOnly day)
    {
        if (!OnDay(slot, day) || !TryParseTime(slot.Start, out var s) || !TryParseTime(slot.End, out var e)) return null;
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        var start = midnight + s;
        var end = midnight + e;
        if (end <= start) end = end.AddDays(1);
        return (start, end);
    }

    /// <summary>The window containing <paramref name="now"/>: today's, or yesterday's still running past midnight.</summary>
    public static (DateTime Start, DateTime End)? WindowAt(ScheduleSlotConfig slot, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        foreach (var day in new[] { today, today.AddDays(-1) })
        {
            if (WindowOn(slot, day) is { } w && now >= w.Start && now < w.End) return w;
        }
        return null;
    }

    /// <summary>The programme on at <paramref name="now"/>: a dated row beats an undated one (a season over the daily rota), then the later start, then the first in the list.</summary>
    public static ScheduleSlotConfig? ProgrammeAt(InstallConfig cfg, DateTime now)
    {
        ScheduleSlotConfig? best = null;
        DateTime bestStart = default;
        var bestDated = false;
        foreach (var slot in cfg.Slots)
        {
            if (!slot.Enabled || slot.Kind != SlotKind.Programme) continue;
            if (WindowAt(slot, now) is not { } w) continue;
            var dated = slot.From.Length > 0 || slot.Until.Length > 0;
            if (best is null || (dated && !bestDated) || (dated == bestDated && w.Start > bestStart))
            {
                best = slot;
                bestStart = w.Start;
                bestDated = dated;
            }
        }
        return best;
    }

    /// <summary>When an advert or announcement fires in the window that starts on <paramref name="day"/>: the start, then every so many minutes before the end. Empty for a programme.</summary>
    public static List<DateTime> FiringsOn(ScheduleSlotConfig slot, DateOnly day)
    {
        var list = new List<DateTime>();
        if (slot.Kind == SlotKind.Programme || WindowOn(slot, day) is not { } w) return list;
        list.Add(w.Start);
        if (slot.EveryMinutes <= 0) return list;
        for (var t = w.Start.AddMinutes(slot.EveryMinutes); t < w.End && list.Count < 2000; t = t.AddMinutes(slot.EveryMinutes)) list.Add(t);
        return list;
    }

    /// <summary>The next firing strictly after <paramref name="after"/>, within the week; null when there is none.</summary>
    public static DateTime? NextFiring(ScheduleSlotConfig slot, DateTime after)
    {
        if (!slot.Enabled) return null;
        var day = DateOnly.FromDateTime(after).AddDays(-1);
        for (var i = 0; i < 9; i++, day = day.AddDays(1))
        {
            foreach (var t in FiringsOn(slot, day))
            {
                if (t > after) return t;
            }
        }
        return null;
    }

    /// <summary>The next moment the clock changes something: a programme starting or ending, an advert or announcement firing.</summary>
    public static (string What, DateTime At)? NextChange(InstallConfig cfg, DateTime now)
    {
        (string What, DateTime At)? best = null;
        void Consider(string what, DateTime at)
        {
            if (at > now && (best is null || at < best.Value.At)) best = (what, at);
        }
        foreach (var slot in cfg.Slots)
        {
            if (!slot.Enabled) continue;
            if (slot.Kind == SlotKind.Programme)
            {
                var day = DateOnly.FromDateTime(now).AddDays(-1);
                for (var i = 0; i < 9; i++, day = day.AddDays(1))
                {
                    if (WindowOn(slot, day) is not { } w) continue;
                    Consider($"{slot.Name} starts", w.Start);
                    Consider($"{slot.Name} ends", w.End);
                }
            }
            else if (NextFiring(slot, now) is { } at)
            {
                Consider($"{KindWord(slot.Kind)} {slot.Name}", at);
            }
        }
        return best;
    }

    public static string KindWord(SlotKind kind) => kind switch
    {
        SlotKind.Programme => "programme",
        SlotKind.Advert => "advert",
        _ => "announcement",
    };

    /// <summary>The day as the page shows it: every programme window and every firing, in time order.</summary>
    public static List<TimelineRow> Timeline(InstallConfig cfg, DateOnly day)
    {
        var rows = new List<TimelineRow>();
        foreach (var slot in cfg.Slots)
        {
            if (!slot.Enabled) continue;
            if (slot.Kind == SlotKind.Programme)
            {
                if (WindowOn(slot, day) is { } w) rows.Add(new TimelineRow(w.Start, w.End, slot.Kind, slot.Name, slot.Look.Length > 0 ? $"look {slot.Look}" : "no look"));
                continue;
            }
            foreach (var t in FiringsOn(slot, day)) rows.Add(new TimelineRow(t, null, slot.Kind, slot.Name, DetailOf(slot)));
        }
        return rows.OrderBy(r => r.At).ThenBy(r => (int)r.Kind).ToList();
    }

    /// <summary>"Mon–Fri 09:00–17:00 · look Daytime", "every day every 30 min from 10:00 to 18:00, 20 s · look Lunch offer · on screens 1, 3".</summary>
    public static string Describe(ScheduleSlotConfig slot)
    {
        var when = DescribeDays(slot.Days);
        if (slot.From.Length > 0) when += $" from {slot.From}";
        if (slot.Until.Length > 0) when += $" until {slot.Until}";
        when += slot.Kind == SlotKind.Programme
            ? $" {slot.Start}–{slot.End}"
            : slot.EveryMinutes > 0
                ? $" every {slot.EveryMinutes} min from {slot.Start} to {slot.End}, {slot.DurationSeconds} s"
                : $" at {slot.Start}, {slot.DurationSeconds} s";
        return $"{when} · {DetailOf(slot)}";
    }

    /// <summary>What a slot puts up: its look, its words, its VOG, its screens.</summary>
    public static string DetailOf(ScheduleSlotConfig slot)
    {
        var parts = new List<string>();
        if (slot.Look.Length > 0) parts.Add($"look {slot.Look}");
        if (slot.Text.Length > 0) parts.Add($"“{slot.Text}”");
        if (slot.Sound.Length > 0) parts.Add($"VOG {slot.Sound}");
        if (slot.Screens.Trim().Length > 0) parts.Add($"on screens {slot.Screens.Trim()}");
        return parts.Count == 0 ? "nothing to show" : string.Join(" · ", parts);
    }

    /// <summary>Every row that cannot do what it says, in words the page shows: a day that does not read, a look the show lacks, an advert with no picture.</summary>
    public static List<string> Problems(InstallConfig cfg, ShowState state)
    {
        var list = new List<string>();
        foreach (var slot in cfg.Slots)
        {
            if (!slot.Enabled) continue;
            var name = slot.Name.Length > 0 ? slot.Name : "(unnamed)";
            if (!TryParseDays(slot.Days, out _)) list.Add($"{name}: days '{slot.Days}' do not read — Mon–Fri, weekends, Sat Sun, every day.");
            if (slot.From.Length > 0 && !TryParseDate(slot.From, out _)) list.Add($"{name}: from '{slot.From}' is not a date (2026-12-24).");
            if (slot.Until.Length > 0 && !TryParseDate(slot.Until, out _)) list.Add($"{name}: until '{slot.Until}' is not a date (2026-12-31).");
            if (!TryParseTime(slot.Start, out _)) list.Add($"{name}: start '{slot.Start}' is not a time (09:00).");
            if (!TryParseTime(slot.End, out _)) list.Add($"{name}: end '{slot.End}' is not a time (17:00).");
            if (slot.Look.Length > 0 && LookService.Find(state, slot.Look) is null) list.Add($"{name}: look '{slot.Look}' is not in the show.");
            if (slot.Sound.Length > 0 && StingerLibrary.Find(state, slot.Sound) is null) list.Add($"{name}: VOG '{slot.Sound}' is not in the library (Audio page).");
            switch (slot.Kind)
            {
                case SlotKind.Programme when slot.Look.Length == 0:
                    list.Add($"{name}: a programme needs a look.");
                    break;
                case SlotKind.Advert when slot.Look.Length == 0:
                    list.Add($"{name}: an advert needs a look.");
                    break;
                case SlotKind.Announcement when slot.Look.Length == 0 && slot.Text.Length == 0 && slot.Sound.Length == 0:
                    list.Add($"{name}: an announcement needs words, a VOG or a look.");
                    break;
            }
        }
        if (cfg.IdleLook.Length > 0 && LookService.Find(state, cfg.IdleLook) is null) list.Add($"The idle look '{cfg.IdleLook}' is not in the show.");
        return list;
    }

    /// <summary>A slot by name (case-blind), by its place (1-based) or by id; a kind narrows it. Programmes are found too — the caller decides what to do with one.</summary>
    public static ScheduleSlotConfig? Find(InstallConfig cfg, string nameOrNumber, SlotKind? kind = null)
    {
        var key = (nameOrNumber ?? "").Trim();
        if (key.Length == 0) return null;
        var candidates = kind is { } k ? cfg.Slots.Where(s => s.Kind == k).ToList() : cfg.Slots.ToList();
        var byName = candidates.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))
                     ?? candidates.FirstOrDefault(s => s.Id == key);
        if (byName is not null) return byName;
        return int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= candidates.Count ? candidates[n - 1] : null;
    }

    /// <summary>"1, 3" or "Window, Till": the screen numbers and the words of a placement.</summary>
    public static (List<int> Numbers, List<string> Words) ParseScreens(string? text)
    {
        var numbers = new List<int>();
        var words = new List<string>();
        foreach (var raw in (text ?? "").Split(new[] { ',', ';', '/', '&', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) numbers.Add(n);
            else if (raw.Length > 0) words.Add(raw);
        }
        return (numbers, words);
    }
}

/// <summary>One line of the day's timeline: a programme window, or a firing.</summary>
public sealed record TimelineRow(DateTime At, DateTime? Until, SlotKind Kind, string Name, string Detail)
{
    public string TimeText => Until is { } u ? $"{At:HH:mm}–{u:HH:mm}" : At.ToString("HH:mm", CultureInfo.InvariantCulture);

    public string KindText => Kind switch
    {
        SlotKind.Programme => "PROGRAMME",
        SlotKind.Advert => "ADVERT",
        _ => "ANNOUNCEMENT",
    };

    /// <summary>"NOW" while a window holds the moment, "done" once it has passed, "" ahead.</summary>
    public string StateAt(DateTime now)
    {
        if (Until is { } u) return now >= At && now < u ? "NOW" : now >= u ? "done" : "";
        return now >= At ? "done" : "";
    }
}

public enum InstallStepKind
{
    /// <summary>The programme's look to air (the slot names it).</summary>
    Programme,
    /// <summary>No programme is on: the idle look, or black.</summary>
    Idle,
    /// <summary>An advert or announcement starts (the slot): its look, its words, its VOG, its placement.</summary>
    OverrideStart,
    /// <summary>The override ends: the words down, the placement freed, the programme back (a Programme or Idle step follows when the clock runs).</summary>
    OverrideEnd,
    /// <summary>Nothing to do, something to say: a firing skipped because the desk owned the screens.</summary>
    Note,
}

public sealed record InstallStep(InstallStepKind Kind, ScheduleSlotConfig? Slot, string Note = "");

/// <summary>
/// The install's state machine, driven by a tick: which programme is applied, which override is
/// running and until when, which firings have been handled and which wait. Announcements beat
/// adverts (an advert due during an announcement waits and fires when the way is clear, if its
/// time is still near; an announcement due during an advert cuts it short); a firing that lands
/// while the desk owns the screens — the caller armed, a stinger holding — is skipped and said.
/// Pure: the service performs the steps through the action layer.
/// </summary>
public sealed class InstallRuntime
{
    /// <summary>A firing that had to wait fires late by at most this long; later than that it is missed, not fired into the wrong moment.</summary>
    public const int CatchUpMinutes = 5;

    private readonly Dictionary<string, DateTime> _handled = new(StringComparer.Ordinal);
    private readonly List<(ScheduleSlotConfig Slot, DateTime Due)> _deferred = new();
    private DateTime? _started;

    /// <summary>The id of the programme slot applied ("" = none yet, or the picture moved).</summary>
    public string ProgrammeId { get; private set; } = "";

    /// <summary>The idle content is up (no programme on).</summary>
    public bool Idle { get; private set; }

    public ScheduleSlotConfig? Override { get; private set; }

    public DateTime OverrideEndsAt { get; private set; }

    /// <summary>Firings deferred behind an override, waiting for the way to clear.</summary>
    public int Waiting => _deferred.Count;

    public IReadOnlyList<InstallStep> Tick(InstallConfig cfg, DateTime now, bool busy = false)
    {
        var steps = new List<InstallStep>();
        // 1. An override that has run its time ends; so does one whose slot left the show.
        if (Override is { } over && (now >= OverrideEndsAt || (!over.IsAdHoc && !cfg.Slots.Contains(over))))
        {
            End(steps, "ran its time");
        }
        if (!cfg.Enabled)
        {
            // The clock is off: by-hand overrides still run and end; nothing fires, nothing is remembered.
            ProgrammeId = "";
            Idle = false;
            _handled.Clear();
            _deferred.Clear();
            _started = null;
            return steps;
        }
        _started ??= now;

        // 2. What is due: announcements before adverts, then by time.
        var due = new List<(ScheduleSlotConfig Slot, DateTime At)>();
        foreach (var slot in cfg.Slots)
        {
            if (!slot.Enabled || slot.Kind == SlotKind.Programme) continue;
            if (DueFiring(slot, now) is { } at) due.Add((slot, at));
        }
        foreach (var (slot, at) in due.OrderBy(d => d.Slot.Kind == SlotKind.Announcement ? 0 : 1).ThenBy(d => d.At))
        {
            _handled[slot.Id] = at;
            if (busy)
            {
                steps.Add(new InstallStep(InstallStepKind.Note, slot, $"{slot.Name} at {at:HH:mm} skipped — the desk owns the screens"));
                continue;
            }
            if (Override is { } current)
            {
                if (current.Kind == SlotKind.Advert && slot.Kind == SlotKind.Announcement)
                {
                    End(steps, $"cut short by announcement {slot.Name}");
                }
                else if (slot.Kind == SlotKind.Advert)
                {
                    _deferred.Add((slot, at));      // waits behind what is on
                    continue;
                }
                else
                {
                    End(steps, $"replaced by announcement {slot.Name}");
                }
            }
            Start(steps, slot, now, slot.DurationSeconds);
        }

        // 3. A deferred advert once the way is clear, if its moment is still near.
        if (Override is null && !busy)
        {
            while (_deferred.Count > 0)
            {
                var (slot, at) = _deferred[0];
                _deferred.RemoveAt(0);
                if (!slot.Enabled || !cfg.Slots.Contains(slot) || (now - at).TotalMinutes > CatchUpMinutes) continue;
                Start(steps, slot, now, slot.DurationSeconds);
                break;
            }
        }

        // 4. The programme underneath — applied when nothing is over it.
        if (Override is null && !busy)
        {
            var programme = Schedule.ProgrammeAt(cfg, now);
            if (programme is not null)
            {
                if (programme.Id != ProgrammeId || Idle)
                {
                    ProgrammeId = programme.Id;
                    Idle = false;
                    steps.Add(new InstallStep(InstallStepKind.Programme, programme));
                }
            }
            else if (!Idle)
            {
                Idle = true;
                ProgrammeId = "";
                steps.Add(new InstallStep(InstallStepKind.Idle, null));
            }
        }
        return steps;
    }

    /// <summary>A slot fired by hand — ANNOUNCE, ADVERT, the page's PLAY NOW: starts now for its seconds (or the seconds given), replacing whatever override is on.</summary>
    public IReadOnlyList<InstallStep> Fire(ScheduleSlotConfig slot, DateTime now, int? seconds = null)
    {
        var steps = new List<InstallStep>();
        if (Override is { } current) End(steps, $"replaced by {slot.Name}");
        Start(steps, slot, now, seconds ?? slot.DurationSeconds);
        return steps;
    }

    /// <summary>The override on ends now (ANNOUNCE OFF, ADVERT OFF, the page's END NOW).</summary>
    public IReadOnlyList<InstallStep> EndOverride()
    {
        var steps = new List<InstallStep>();
        End(steps, "ended by hand");
        return steps;
    }

    /// <summary>Forget everything (the schedule switched off, a show loaded): an override in progress ends, the clock starts afresh next tick.</summary>
    public IReadOnlyList<InstallStep> Reset()
    {
        var steps = new List<InstallStep>();
        End(steps, "the schedule reset");
        ProgrammeId = "";
        Idle = false;
        _handled.Clear();
        _deferred.Clear();
        _started = null;
        return steps;
    }

    /// <summary>A free-text announcement: a transient slot that lives only while it runs.</summary>
    public static ScheduleSlotConfig AdHoc(string text, int seconds)
        => new() { Name = "Announcement", Kind = SlotKind.Announcement, Text = text, DurationSeconds = seconds, IsAdHoc = true };

    /// <summary>The latest firing at or before now that is still within the catch-up window, after the last one handled and not before the clock started.</summary>
    private DateTime? DueFiring(ScheduleSlotConfig slot, DateTime now)
    {
        var start = _started ?? now;
        _handled.TryGetValue(slot.Id, out var last);
        DateTime? best = null;
        var day = DateOnly.FromDateTime(now).AddDays(-1);
        for (var i = 0; i < 2; i++, day = day.AddDays(1))
        {
            foreach (var t in Schedule.FiringsOn(slot, day))
            {
                if (t <= now && t >= start && t > last && (now - t).TotalMinutes <= CatchUpMinutes) best = t;
            }
        }
        return best;
    }

    private void Start(List<InstallStep> steps, ScheduleSlotConfig slot, DateTime now, int seconds)
    {
        Override = slot;
        OverrideEndsAt = now.AddSeconds(Math.Max(1, seconds));
        steps.Add(new InstallStep(InstallStepKind.OverrideStart, slot));
    }

    private void End(List<InstallStep> steps, string why)
    {
        if (Override is not { } slot) return;
        Override = null;
        steps.Add(new InstallStep(InstallStepKind.OverrideEnd, slot, why));
        if (slot.Look.Length > 0)
        {
            // The picture moved: the programme (or the idle content) comes back underneath on this tick.
            ProgrammeId = "";
            Idle = false;
        }
    }
}
