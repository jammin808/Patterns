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
        "Number", "Name", "Track", "Start", "Duration", "Follow", "Mark", "Confirm", "Look", "Action", "Target", "Value", "After", "Notes",
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
    private static readonly string[] AfterHeaders = { "After", "Delay", "Wait", "Offset", "After (s)", "Delay (s)" };

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
                // A number is a delay before a word is a yes: the export writes the seconds bare, and
                // "1" used to read back as "yes" — a one-second follow became a follow at once.
                if (CueTiming.ParseDuration(follow) is { } seconds) cue.FollowSeconds = seconds;
                else if (IsYes(follow)) cue.FollowSeconds = 0;
                else if (IsNo(follow)) cue.FollowSeconds = null;
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
                cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ApplyLook, Target = look?.Id ?? lookName });
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
                    var after = table.Get(r, AfterHeaders);
                    var wait = after.Length > 0 && double.TryParse(after.TrimEnd('s', 'S', ' '),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 0;
                    cue.Actions.Add(new CueActionConfig { Kind = kind, Target = resolved, Value = value, DelaySeconds = wait });
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
            new[] { "01.010", "Walk-in", "Video", "08:30", "30:00", "", "", "", "Walk-in", "", "", "", "", "Doors open — loops until the welcome" },
            new[] { "01.020", "Welcome", "Video", "09:00", "10:00", "", "", "yes", "Keynote", "Play audio track", "", "", "", "Confirm asked: the walk-in music stops here" },
            new[] { "01.030", "Coffee", "", "09:10", "20 min", "", "break", "", "Holding", "", "", "", "", "" },
            new[] { "02.010", "Session 2", "Video", "09:30", "45:00", "", "", "", "Session", "Lower third on", "Speaker one", "", "3", "After = 3: the name comes up three seconds after the picture" },
            new[] { "02.020", "Lunch", "", "12:15", "1h", "", "lunch", "", "Lunch", "Start countdown", "", "60", "", "A 60-minute countdown on the foyer screen" },
            new[] { "03.010", "Thanks", "", "17:00", "5:00", "0", "end", "", "Thanks", "", "", "", "", "Follow = 0: the next cue fires by itself at once" },
            new[] { "03.020", "Walk-out", "", "", "", "", "", "", "Walk-out", "Blackout off", "", "", "", "" },
        };
        return CsvTable.Write(rows);
    }

    /// <summary>A stack as the same columns, ready for Excel, a printout, or a round trip.</summary>
    public static string Export(ShowState state, CueStackConfig stack)
    {
        var rows = new List<IEnumerable<string>> { Headers };
        foreach (var cue in stack.Cues)
        {
            var look = cue.Actions.FirstOrDefault(a => a.Kind == ShowActionKind.ApplyLook);
            var other = cue.Actions.FirstOrDefault(a => a.Kind is not ShowActionKind.ApplyLook and not ShowActionKind.Note);
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
                other is null ? "" : ActionSpec.Label(other.Kind),
                other is null ? "" : TargetName(state, other),
                other is null ? "" : ValueName(state, other),
                other is null || other.DelaySeconds <= 0 ? "" : other.DelaySeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
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
    public static ShowActionKind? ParseKind(string text)
    {
        var key = Squash(text);
        if (key.Length == 0) return null;
        foreach (var kind in ActionSpec.CueKinds)
        {
            if (Squash(kind.ToString()) == key || Squash(ActionSpec.Label(kind)) == key) return kind;
        }
        // A few ways people write the common ones.
        return key switch
        {
            "look" or "applylook" or "recalllook" => ShowActionKind.ApplyLook,
            "audio" or "playaudio" or "music" or "track" or "playtrack" or "playaudiotrack" or "audiotrack" or "audioplay" => ShowActionKind.AudioPlay,
            "stopaudio" or "audiooff" => ShowActionKind.AudioStop,
            "audionext" or "nexttrack" or "tracknext" or "skiptrack" or "audioskip" => ShowActionKind.AudioNext,
            "audioprev" or "audioprevious" or "prevtrack" or "previoustrack" or "trackback" or "audioback" => ShowActionKind.AudioPrev,
            "sting" or "stinger" or "vog" or "fire" => ShowActionKind.StingerFire,
            "part" or "playlist" or "section" => ShowActionKind.PlaylistPart,
            "blackout" or "black" => ShowActionKind.BlackoutOn,
            "fade" or "fadetoblack" or "fadedown" or "fadeout" or "fadeblack" or "ftb" => ShowActionKind.FadeToBlack,
            "fadeup" or "fadein" or "fadefromblack" or "ftbup" => ShowActionKind.FadeUp,
            "countdown" or "timer" => ShowActionKind.CountdownStart,
            "message" or "ticker" => ShowActionKind.MessageOn,
            "weather" or "weatheron" or "forecast" or "forecaston" => ShowActionKind.WeatherOn,
            "weatheroff" or "forecastoff" or "noweather" => ShowActionKind.WeatherOff,
            "weatherview" or "weathernow" or "weatherday" or "weathertomorrow" or "forecastview" => ShowActionKind.WeatherView,
            "lowerthird" or "lt" or "name" or "person" or "speaker" => ShowActionKind.LowerThirdShow,
            "ltpreview" or "previewlt" or "lowerthirdpreview" or "namepreview" or "previewname" => ShowActionKind.LowerThirdPreview,
            "lttake" or "takelt" or "lowerthirdtake" or "nametake" or "takename" => ShowActionKind.LowerThirdTake,
            "stream" or "golive" => ShowActionKind.StreamStart,
            "web" or "page" or "webkey" or "pagekey" or "key" or "webaction" or "pageaction" or "slide" => ShowActionKind.WebKey,
            "webclick" or "pageclick" or "click" => ShowActionKind.WebClick,
            "webtype" or "pagetype" or "type" => ShowActionKind.WebType,
            "webreload" or "pagereload" or "reload" => ShowActionKind.WebReload,
            "decknext" or "nextpage" or "nextslide" or "deckforward" => ShowActionKind.DeckNext,
            "deckprev" or "deckprevious" or "prevpage" or "previouspage" or "prevslide" or "previousslide" or "deckback" => ShowActionKind.DeckPrev,
            "deck" or "deckpage" or "gotopage" or "deckgoto" or "pdf" => ShowActionKind.DeckPage,
            "device" or "devicesend" or "send" or "arduino" or "serial" or "relay" => ShowActionKind.DeviceSend,
            "announce" or "announcement" or "announcementon" or "announceon" or "pa" => ShowActionKind.Announce,
            "announceoff" or "announcementoff" or "announcestop" or "endannouncement" => ShowActionKind.AnnounceOff,
            "advert" or "ad" or "advertisement" or "commercial" or "advertplay" or "playadvert" or "spot" => ShowActionKind.AdvertPlay,
            "advertoff" or "adoff" or "advertend" or "endadvert" or "skipadvert" or "advertstop" => ShowActionKind.AdvertOff,
            "schedule" or "scheduleon" or "install" or "installon" or "installschedule" or "installscheduleon" => ShowActionKind.ScheduleOn,
            "scheduleoff" or "installoff" or "installscheduleoff" or "schedulestop" => ShowActionKind.ScheduleOff,
            "screenlook" or "lookonscreen" or "lookon" or "sendlook" or "screensend" or "own" or "ownlook" => ShowActionKind.ScreenLook,
            "preset" or "patternpreset" or "recallpreset" or "presetrecall" => ShowActionKind.PatternPreset,
            "screenpreset" or "presetonscreen" or "preseton" or "sendpreset" => ShowActionKind.ScreenPreset,
            "screenprogram" or "screenpgm" or "backtoprogram" or "toprogram" or "program" or "pgm" or "follow" => ShowActionKind.ScreenProgram,
            "videoend" or "videotoend" or "vtend" or "clipend" or "lastseconds" or "skiptoend" or "videolast" or "vtlast" => ShowActionKind.VideoToEnd,
            "videorestart" or "vtrestart" or "cliprestart" or "restartvideo" or "restartclip" or "videostart" or "vtstart" or "rewind" => ShowActionKind.VideoRestart,
            "logo" or "logoon" or "brand" or "brandlogo" => ShowActionKind.LogoOn,
            "logooff" or "nologo" => ShowActionKind.LogoOff,
            "pip" or "pipon" or "pictureinpicture" or "inset" => ShowActionKind.PipOn,
            "pipoff" or "nopip" => ShowActionKind.PipOff,
            "overlaysoff" or "overlayoff" or "nooverlays" or "cleanpicture" or "clean" or "clearoverlays" => ShowActionKind.OverlaysOff,
            "clockformat" or "clockhours" or "hours" or "12h" or "24h" or "12hour" or "24hour" => ShowActionKind.ClockFormat,
            "clockseconds" or "seconds" => ShowActionKind.ClockSeconds,
            "clockdate" or "dateline" or "date" => ShowActionKind.ClockDate,
            "messagescroll" or "scroll" or "tickerscroll" => ShowActionKind.MessageScroll,
            "countdowntoggle" or "timertoggle" => ShowActionKind.CountdownToggle,
            "countdownto" or "countdownat" or "countto" or "backat" => ShowActionKind.CountdownTo,
            "countdownlabel" or "timerlabel" or "label" => ShowActionKind.CountdownLabel,
            "pattern" or "patternkind" or "picture" or "kind" => ShowActionKind.PatternKind,
            "freeze" or "freezeon" or "hold" => ShowActionKind.FreezeOn,
            "freezeoff" or "unfreeze" or "release" => ShowActionKind.FreezeOff,
            "tone" or "toneon" or "lineup" or "lineuptone" => ShowActionKind.ToneOn,
            "toneoff" or "notone" => ShowActionKind.ToneOff,
            "stopall" or "allstop" or "stopeverything" or "panic" => ShowActionKind.StopAll,
            "outputson" or "outputs" => ShowActionKind.OutputsOn,
            "outputsoff" => ShowActionKind.OutputsOff,
            "lookback" or "back" or "previouslook" or "lastlook" or "undo" => ShowActionKind.LookBack,
            "preview" or "previewlook" or "looktopreview" or "preload" or "load" => ShowActionKind.ApplyLookToPreview,
            "webopen" or "open" or "openpage" or "address" or "url" or "goto" => ShowActionKind.WebOpen,
            "ltupdate" or "updatelt" or "lowerthirdupdate" or "nameupdate" => ShowActionKind.LowerThirdUpdate,
            "ltpreviewoff" or "previewoff" or "lowerthirdpreviewoff" => ShowActionKind.LowerThirdPreviewOff,
            "duck" or "duckon" => ShowActionKind.DuckOn,
            "duckoff" or "unduck" or "liftduck" => ShowActionKind.DuckOff,
            _ => null,
        };
    }

    private static (string Target, string? Note) ResolveTarget(ShowState state, ShowActionKind kind, string target)
    {
        var (targetKind, _) = ActionSpec.For(kind);
        if (targetKind == TargetKind.None) return ("", null);
        if (target.Length == 0)
        {
            // Break music resumes with no entry; the audio playlist plays with no track; a web action with no page reaches the page on air; an announcement with no slot says its value; a fade with no place is every screen.
            return targetKind is TargetKind.Music or TargetKind.Page or TargetKind.Track or TargetKind.Place || kind == ShowActionKind.Announce ? ("", null) : ("", $"{ActionSpec.Label(kind)} needs a Target.");
        }
        switch (targetKind)
        {
            case TargetKind.Place:
            {
                // The scope's own words, or a screen by its label — "Stage left" reads as ID <its id>.
                if (FadeScope.Parse(target) is { } scope) return (scope.Words, null);
                var labelled = state.Output.Placements.FirstOrDefault(p => string.Equals(p.CustomLabel, target, StringComparison.OrdinalIgnoreCase));
                return labelled is not null ? ($"ID {labelled.ScreenId}", null) : (target, $"'{target}' is not a place to fade — every screen (blank), SCREEN 2, GROUP A, FOCUSED, TICKED, GROUPS, or a screen's label.");
            }
            case TargetKind.Track:
                // A row by its name or file becomes its id (a rename or a re-order never breaks the cue); a number or a folder's file is used as written.
                return AudioPlaylist.FindItem(state.AudioPlayer, target) is { } row ? (row.Id, null) : (target, null);
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
            case TargetKind.Slot:
                // A slot is named by its name, so a sheet reads "Advert: Lunch offer" and survives a re-add.
                return Schedule.Find(state.Install, target) is { } slot ? (slot.Name, null) : (target, $"'{target}' is not on the Install page.");
            default:
                return (target, null); // a part name or a canvas key is used as written
        }
    }

    private static string TargetName(ShowState state, CueActionConfig a)
    {
        var (targetKind, _) = ActionSpec.For(a.Kind);
        return targetKind switch
        {
            TargetKind.Look => LookService.Find(state, a.Target)?.Name ?? a.Target,
            TargetKind.Stinger => StingerLibrary.Find(state, a.Target)?.DisplayName ?? a.Target,
            TargetKind.Screen => state.Output.Placements.FirstOrDefault(p => p.ScreenId == a.Target) is { CustomLabel.Length: > 0 } p ? p.CustomLabel : a.Target,
            TargetKind.Stack => CueStacks.Find(state, a.Target)?.Name ?? a.Target,
            TargetKind.Music => SpotifyLibrary.Find(state, a.Target)?.DisplayName ?? a.Target,
            TargetKind.LowerThird => state.LowerThirds.Find(a.Target)?.Name ?? a.Target,
            TargetKind.Place => FadeScope.Parse(a.Target) is { Kind: FadeScopeKind.Target } scope
                ? (state.Output.Placements.FirstOrDefault(p => p.ScreenId == scope.Arg) is { CustomLabel.Length: > 0 } p ? p.CustomLabel : a.Target)
                : a.Target,
            _ => a.Target,
        };
    }

    /// <summary>A value as a person reads it: a library entry's id goes out as its name, a look's id as the look's name.</summary>
    private static string ValueName(ShowState state, CueActionConfig a)
    {
        if (a.Value.Length == 0) return a.Value;
        return ActionSpec.For(a.Kind).Value switch
        {
            ValueKind.Person => state.LowerThirds.FindEntry(a.Value)?.Name ?? a.Value,
            ValueKind.Look => LookService.Find(state, a.Value)?.Name ?? a.Value,
            _ => a.Value,
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
