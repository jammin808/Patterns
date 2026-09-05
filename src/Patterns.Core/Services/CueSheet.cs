using System.Text.RegularExpressions;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>What an import made of a sheet: the cues, and the notes an operator should read.</summary>
public sealed class CueSheetImport
{
    public List<RunCueConfig> Cues { get; } = new();

    /// <summary>"Row 4: look 'Keynote' not found — the cue reads as broken until a look of that name exists."</summary>
    public List<string> Notes { get; } = new();

    public int Rows { get; set; }

    public string Summary
    {
        get
        {
            var cues = $"{Cues.Count} cue{(Cues.Count == 1 ? "" : "s")} from {Rows} row{(Rows == 1 ? "" : "s")}";
            return Notes.Count == 0 ? cues : $"{cues} — {Notes.Count} note{(Notes.Count == 1 ? "" : "s")}";
        }
    }
}

/// <summary>
/// A running order as a spreadsheet, both ways: a CSV or the first sheet of an .xlsx becomes
/// cues (looks and actions resolved by name against the show, times and lengths in any usual
/// spelling, marks named or guessed), a stack goes out as the same columns, and a template
/// shows the columns with a few rows to copy. Pure — the caller decides where the cues go.
/// </summary>
public static class CueSheet
{
    public static readonly string[] Headers =
    {
        "Number", "Name", "Track", "Start", "Duration", "Follow", "Mark", "Confirm", "Look", "Action", "Target", "Value", "Notes",
    };

    private static readonly string[] NumberHeaders = { "Number", "No", "No.", "#", "Cue number", "Cue #", "Cue no", "Q" };
    private static readonly string[] NameHeaders = { "Name", "Cue name", "Title", "Item", "Segment", "Cue", "Session" };
    private static readonly string[] TrackHeaders = { "Track", "Dept", "Department", "Who", "Owner", "Operator" };
    private static readonly string[] NotesHeaders = { "Notes", "Note", "Comment", "Comments", "Description", "Script", "Detail", "Details" };
    private static readonly string[] StartHeaders = { "Start", "Planned start", "Start time", "Time", "At", "Clock" };
    private static readonly string[] DurationHeaders = { "Duration", "Length", "Planned", "Dur", "Mins", "Minutes", "Running time" };
    private static readonly string[] FollowHeaders = { "Follow", "Auto", "Auto-follow", "Autofollow", "Continue" };
    private static readonly string[] MarkHeaders = { "Mark", "Type", "Kind", "Section type" };
    private static readonly string[] ConfirmHeaders = { "Confirm", "Confirmation", "Double press" };
    private static readonly string[] LookHeaders = { "Look", "Look name", "Preset" };
    private static readonly string[] ActionHeaders = { "Action", "Action kind", "Command" };
    private static readonly string[] TargetHeaders = { "Target", "Action target", "Which" };
    private static readonly string[] ValueHeaders = { "Value", "Action value", "Parameter" };

    private static readonly Regex BreakWord = new(@"\b(break|coffee|tea|interval|recess)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LunchWord = new(@"\b(lunch|dinner|supper)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EndWord = new(@"\b(end of (the )?(day|show|event)|close|closing|wrap|goodbye|finish)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Every row becomes a cue. A Look column becomes an Apply look action (by name or id; an
    /// unknown name is kept so the cue reads as broken until the look exists), an Action column an
    /// action of that kind with its Target resolved by name where the kind names something. Numbers
    /// missing from the sheet continue from <paramref name="previousNumber"/>.
    /// </summary>
    public static CueSheetImport Import(TableData table, ShowState state, string? previousNumber = null)
    {
        var result = new CueSheetImport();
        if (table.Headers.Count == 0)
        {
            result.Notes.Add("The file has no header row — the first row must name the columns (download the template).");
            return result;
        }
        if (table.Column(NameHeaders) < 0 && table.Column(NumberHeaders) < 0)
        {
            result.Notes.Add("No Name or Number column was found — the columns are read by their header names (download the template).");
            return result;
        }

        var hasMarkColumn = table.Column(MarkHeaders) >= 0;
        var guessed = 0;
        var last = previousNumber;
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var rowNo = r + 2; // the header is row 1 in the sheet
            var number = table.Get(r, NumberHeaders);
            var name = table.Get(r, NameHeaders);
            var cue = new RunCueConfig
            {
                Number = number.Length > 0 ? number : CueNumber.Next(last),
                Name = name.Length > 0 ? name : "Cue",
                Track = table.Get(r, TrackHeaders),
                Notes = table.Get(r, NotesHeaders),
            };
            last = cue.Number;

            var start = table.Get(r, StartHeaders);
            if (start.Length > 0)
            {
                if (CueTiming.ParseClock(start) is { } at) cue.PlannedStart = CueTiming.FormatClock(at);
                else result.Notes.Add($"Row {rowNo}: start '{start}' is not a clock time (use HH:mm).");
            }
            var duration = table.Get(r, DurationHeaders);
            if (duration.Length > 0)
            {
                if (CueTiming.ParseDuration(duration) is { } seconds) cue.PlannedSeconds = seconds;
                else result.Notes.Add($"Row {rowNo}: duration '{duration}' is not a length (use mm:ss, 5 min or 90 s).");
            }
            var follow = table.Get(r, FollowHeaders);
            if (follow.Length > 0)
            {
                if (IsYes(follow) || follow == "0") cue.FollowSeconds = 0;
                else if (IsNo(follow)) cue.FollowSeconds = null;
                else if (CueTiming.ParseDuration(follow) is { } seconds) cue.FollowSeconds = seconds;
                else result.Notes.Add($"Row {rowNo}: follow '{follow}' is not yes, no, or a delay (use 0, 5 s or 1:30).");
            }
            if (hasMarkColumn)
            {
                cue.Mark = ParseMark(table.Get(r, MarkHeaders));
            }
            else
            {
                cue.Mark = GuessMark(cue.Name);
                if (cue.Mark != CueMark.None) guessed++;
            }
            cue.RequireConfirm = IsYes(table.Get(r, ConfirmHeaders));

            var lookName = table.Get(r, LookHeaders);
            if (lookName.Length > 0)
            {
                var look = LookService.Find(state, lookName);
                cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look?.Id ?? lookName });
                if (look is null) result.Notes.Add($"Row {rowNo}: look '{lookName}' not found — the cue reads as broken until a look of that name exists.");
            }
            var actionText = table.Get(r, ActionHeaders);
            if (actionText.Length > 0)
            {
                if (ParseKind(actionText) is { } kind)
                {
                    var target = table.Get(r, TargetHeaders);
                    var value = table.Get(r, ValueHeaders);
                    var (resolved, note) = ResolveTarget(state, kind, target);
                    cue.Actions.Add(new CueActionConfig { Kind = kind, Target = resolved, Value = value });
                    if (note is not null) result.Notes.Add($"Row {rowNo}: {note}");
                }
                else
                {
                    result.Notes.Add($"Row {rowNo}: action '{actionText}' is not one Patterns knows (the kinds are the names in the cue editor's picker).");
                }
            }

            result.Cues.Add(cue);
            result.Rows++;
        }
        if (guessed > 0) result.Notes.Add($"No Mark column: {guessed} cue{(guessed == 1 ? "" : "s")} marked as a break, lunch or the end from their names — check them.");
        return result;
    }

    /// <summary>The columns and a few rows that show every format — what the IMPORT reads.</summary>
    public static string Template()
    {
        var rows = new List<IEnumerable<string>>
        {
            Headers,
            new[] { "01.010", "Walk-in", "Video", "08:30", "30:00", "", "", "", "Walk-in", "", "", "", "Doors open — loops until the welcome" },
            new[] { "01.020", "Welcome", "Video", "09:00", "10:00", "", "", "yes", "Keynote", "Play audio track", "", "", "Confirm asked: the walk-in music stops here" },
            new[] { "01.030", "Coffee", "", "09:10", "20 min", "", "break", "", "Holding", "", "", "", "" },
            new[] { "02.010", "Session 2", "Video", "09:30", "45:00", "", "", "", "Session", "Lower third on", "Speaker one", "", "" },
            new[] { "02.020", "Lunch", "", "12:15", "1h", "", "lunch", "", "Lunch", "Start countdown", "", "60", "A 60-minute countdown on the foyer screen" },
            new[] { "03.010", "Thanks", "", "17:00", "5:00", "0", "end", "", "Thanks", "", "", "", "Follow = 0: the next cue fires by itself at once" },
            new[] { "03.020", "Walk-out", "", "", "", "", "", "", "Walk-out", "Blackout off", "", "", "" },
        };
        return CsvTable.Write(rows);
    }

    /// <summary>A stack as the same columns, ready for Excel, a printout, or a round trip.</summary>
    public static string Export(ShowState state, CueStackConfig stack)
    {
        var rows = new List<IEnumerable<string>> { Headers };
        foreach (var cue in stack.Cues)
        {
            var look = cue.Actions.FirstOrDefault(a => a.Kind == CueActionKind.ApplyLook);
            var other = cue.Actions.FirstOrDefault(a => a.Kind is not CueActionKind.ApplyLook and not CueActionKind.Note);
            rows.Add(new[]
            {
                cue.Number,
                cue.Name,
                cue.Track,
                cue.PlannedStart,
                cue.PlannedSeconds is { } p ? CueTiming.FormatDuration(p) : "",
                cue.FollowSeconds is { } f ? f.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                cue.Mark == CueMark.None ? "" : cue.Mark.ToString().ToLowerInvariant(),
                cue.RequireConfirm ? "yes" : "",
                look is null ? "" : LookService.Find(state, look.Target)?.Name ?? look.Target,
                other is null ? "" : CueActionSpec.Label(other.Kind),
                other is null ? "" : TargetName(state, other),
                other?.Value ?? "",
                cue.Notes,
            });
        }
        return CsvTable.Write(rows);
    }

    // ---- words ---------------------------------------------------------------------------------

    public static CueMark ParseMark(string text)
    {
        var s = text.Trim().ToLowerInvariant();
        if (s.Length == 0) return CueMark.None;
        if (s.Contains("lunch") || s.Contains("dinner")) return CueMark.Lunch;
        if (s.Contains("break") || s.Contains("coffee") || s.Contains("interval")) return CueMark.Break;
        if (s.Contains("end") || s.Contains("close") || s.Contains("wrap") || s.Contains("finish")) return CueMark.End;
        return CueMark.None;
    }

    /// <summary>A mark read off a cue's name, whole words only: "Coffee break" is a break, "Breakout session" is not.</summary>
    public static CueMark GuessMark(string name)
    {
        if (LunchWord.IsMatch(name)) return CueMark.Lunch;
        if (BreakWord.IsMatch(name)) return CueMark.Break;
        if (EndWord.IsMatch(name)) return CueMark.End;
        return CueMark.None;
    }

    /// <summary>An action kind by its enum name or its picker label, ignoring case, spaces and punctuation.</summary>
    public static CueActionKind? ParseKind(string text)
    {
        var key = Squash(text);
        if (key.Length == 0) return null;
        foreach (var kind in CueActionSpec.Editable)
        {
            if (Squash(kind.ToString()) == key || Squash(CueActionSpec.Label(kind)) == key) return kind;
        }
        // A few ways people write the common ones.
        return key switch
        {
            "look" or "applylook" or "recalllook" => CueActionKind.ApplyLook,
            "audio" or "playaudio" or "music" => CueActionKind.AudioPlay,
            "stopaudio" or "audiooff" => CueActionKind.AudioStop,
            "sting" or "stinger" or "vog" or "fire" => CueActionKind.StingerFire,
            "part" or "playlist" or "section" => CueActionKind.PlaylistPart,
            "blackout" or "black" => CueActionKind.BlackoutOn,
            "countdown" or "timer" => CueActionKind.CountdownStart,
            "message" or "ticker" => CueActionKind.MessageOn,
            "lowerthird" or "lt" or "name" => CueActionKind.LowerThirdShow,
            "stream" or "golive" => CueActionKind.StreamStart,
            _ => null,
        };
    }

    private static (string Target, string? Note) ResolveTarget(ShowState state, CueActionKind kind, string target)
    {
        var (targetKind, _) = CueActionSpec.For(kind);
        if (targetKind == TargetKind.None) return ("", null);
        if (target.Length == 0)
        {
            return targetKind == TargetKind.Music ? ("", null) : ("", $"{CueActionSpec.Label(kind)} needs a Target.");
        }
        switch (targetKind)
        {
            case TargetKind.Look:
                return LookService.Find(state, target) is { } look ? (look.Id, null) : (target, $"look '{target}' not found — the cue reads as broken until it exists.");
            case TargetKind.Stinger:
                return StingerLibrary.Find(state, target) is { } sting ? (sting.Id, null) : (target, $"VOG or stinger '{target}' not found.");
            case TargetKind.Screen:
            {
                var placement = state.Output.Placements.FirstOrDefault(p => p.ScreenId == target)
                                ?? state.Output.Placements.FirstOrDefault(p => string.Equals(p.CustomLabel, target, StringComparison.OrdinalIgnoreCase));
                return placement is not null ? (placement.ScreenId, null) : (target, $"screen '{target}' is not in the rig.");
            }
            case TargetKind.Stack:
                return CueStacks.Find(state, target) is { } stack ? (stack.Id, null) : (target, $"list '{target}' not found.");
            case TargetKind.Music:
                return SpotifyLibrary.Find(state, target) is { } music ? (music.Id, null) : (target, $"break music '{target}' not found.");
            case TargetKind.LowerThird:
                return state.LowerThirds.Find(target) is { } design ? (design.Id, null) : (target, $"lower third '{target}' not found.");
            default:
                return (target, null); // a part name or a canvas key is used as written
        }
    }

    private static string TargetName(ShowState state, CueActionConfig a)
    {
        var (targetKind, _) = CueActionSpec.For(a.Kind);
        return targetKind switch
        {
            TargetKind.Look => LookService.Find(state, a.Target)?.Name ?? a.Target,
            TargetKind.Stinger => StingerLibrary.Find(state, a.Target)?.DisplayName ?? a.Target,
            TargetKind.Screen => state.Output.Placements.FirstOrDefault(p => p.ScreenId == a.Target) is { CustomLabel.Length: > 0 } p ? p.CustomLabel : a.Target,
            TargetKind.Stack => CueStacks.Find(state, a.Target)?.Name ?? a.Target,
            TargetKind.Music => SpotifyLibrary.Find(state, a.Target)?.DisplayName ?? a.Target,
            TargetKind.LowerThird => state.LowerThirds.Find(a.Target)?.Name ?? a.Target,
            _ => a.Target,
        };
    }

    private static bool IsYes(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t is "yes" or "y" or "true" or "1" or "x" or "on" or "✓" or "auto";
    }

    private static bool IsNo(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t is "no" or "n" or "false" or "off" or "-" or "none" or "manual";
    }

    private static string Squash(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
