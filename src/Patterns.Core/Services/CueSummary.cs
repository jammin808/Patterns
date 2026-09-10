using System.Text;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>The readable line under a cue: "Apply 'Awards holding' + Play audio + Part 'Main'".</summary>
public static class CueSummary
{
    public static string Describe(ShowState state, RunCueConfig cue, int max = 3)
    {
        if (cue.Actions.Count == 0) return "No actions — notes only.";
        var parts = new List<string>();
        foreach (var a in cue.Actions)
        {
            if (parts.Count == max)
            {
                parts.Add($"+{cue.Actions.Count - max} more");
                break;
            }
            parts.Add(DescribeAction(state, a));
        }
        var summary = string.Join(" + ", parts);
        // A cue with a shape in time says so: "… over 8 s" is the difference between a cue that
        // has happened and one that is still happening.
        var tail = CueSteps.TailSeconds(cue.Actions);
        return tail > 0 ? $"{summary} over {CueSteps.AtWords(tail).TrimStart('+')}" : summary;
    }

    /// <summary>One step in words. Every kind a cue may carry has its own words here (a test holds the door); the desk's own read as their label.</summary>
    public static string DescribeAction(ShowState state, CueActionConfig a)
        => a.DelaySeconds > 0
            ? $"after {CueSteps.AtWords(a.DelaySeconds).TrimStart('+')} {DescribeStep(state, a)}"
            : DescribeStep(state, a);

    private static string DescribeStep(ShowState state, CueActionConfig a)
    {
        switch (a.Kind)
        {
            case ShowActionKind.Unknown: return "Unknown action (newer build)";
            case ShowActionKind.Note: return "Note";
            case ShowActionKind.ApplyLook:
            case ShowActionKind.ApplyLookToPreview:
            {
                var look = LookService.Find(state, a.Target);
                var sb = new StringBuilder(a.Kind == ShowActionKind.ApplyLookToPreview ? $"Preview '{look?.Name ?? a.Target}'" : $"Apply '{look?.Name ?? a.Target}'");
                if (a.Kind == ShowActionKind.ApplyLook) AppendTransition(sb, a.Value);
                return sb.ToString();
            }
            case ShowActionKind.LookBack:
            {
                var sb = new StringBuilder("The look before");
                AppendTransition(sb, a.Value);
                return sb.ToString();
            }
            case ShowActionKind.AudioPlay:
            {
                if (a.Target.Length == 0) return "Play audio";
                var track = AudioPlaylist.FindItem(state.AudioPlayer, a.Target);
                return track is not null ? $"Play audio: {track.DisplayName}" : int.TryParse(a.Target, out var n) ? $"Play audio track {n}" : $"Play audio: '{a.Target}' (not in the list)";
            }
            case ShowActionKind.AudioStop: return "Stop audio";
            case ShowActionKind.AudioNext: return "Audio: the next track";
            case ShowActionKind.AudioPrev: return "Audio: the previous track";
            case ShowActionKind.StingerFire:
            {
                var s = FindStinger(state, a.Target);
                if (s is null) return $"Sting '{a.Target}'";     // a dead target reads as it always did
                if (s.Source == StingerSource.EffectPulse) return $"Pulse '{s.DisplayName}'";
                return s.Kind == StingerKind.Vog
                    ? $"VOG '{s.DisplayName}'"
                    : $"Sting '{s.DisplayName}' ({StingerLibrary.AfterSummary(state, s)})";
            }
            case ShowActionKind.StingerStop: return "Stop VOG / stinger";
            case ShowActionKind.PlaylistPart: return $"Part '{a.Target}'";
            case ShowActionKind.StreamStart: return "Start stream";
            case ShowActionKind.StreamStop: return "Stop stream";
            case ShowActionKind.OutputsOn: return "Outputs on";
            case ShowActionKind.OutputsOff: return "Outputs off";
            case ShowActionKind.BlackoutOn: return "Blackout on";
            case ShowActionKind.BlackoutOff: return "Blackout off";
            case ShowActionKind.BlackoutToggle: return "Blackout toggle";
            case ShowActionKind.FadeToBlack:
            case ShowActionKind.FadeUp:
            {
                var scope = FadeScope.Parse(a.Target);
                var where = scope is { } sc ? (sc.Kind == FadeScopeKind.Target ? ScreenLabel(state, sc.Arg) : sc.Label) : $"'{a.Target}'?";
                var secs = a.Value.Trim().Length > 0 ? $" over {a.Value.Trim()} s" : "";
                return (a.Kind == ShowActionKind.FadeToBlack ? "Fade to black" : "Fade up") + $" — {where}{secs}";
            }
            case ShowActionKind.FreezeOn: return "Freeze every output";
            case ShowActionKind.FreezeOff: return "Release the freeze";
            case ShowActionKind.FreezeToggle: return "Freeze toggle";
            case ShowActionKind.ScreenOn: return $"Screen '{ScreenLabel(state, a.Target)}' on";
            case ShowActionKind.ScreenOff: return $"Screen '{ScreenLabel(state, a.Target)}' off";
            case ShowActionKind.ScreenToggle: return $"Screen '{ScreenLabel(state, a.Target)}' on / off";
            case ShowActionKind.ScreenLook: return $"Screen '{ScreenLabel(state, a.Target)}' → look '{LookService.Find(state, a.Value)?.Name ?? (a.Value.Length > 0 ? a.Value + " (not found)" : "?")}'";
            case ShowActionKind.ScreenProgram: return $"Screen '{ScreenLabel(state, a.Target)}' → the program";
            case ShowActionKind.ScreenLock: return $"Screen '{ScreenLabel(state, a.Target)}' locked — keeps its picture";
            case ShowActionKind.ScreenUnlock: return $"Screen '{ScreenLabel(state, a.Target)}' follows cues again";
            case ShowActionKind.ScreenLockToggle: return $"Screen '{ScreenLabel(state, a.Target)}' lock toggle";
            case ShowActionKind.CanvasOn: return $"Canvas '{CanvasLabel(state, a.Target)}' on";
            case ShowActionKind.CanvasOff: return $"Canvas '{CanvasLabel(state, a.Target)}' off";
            case ShowActionKind.PatternKind:
                return ActionSpec.ParsePatternKind(a.Value) is { } kind ? $"Pattern: {kind}" : $"Pattern: '{a.Value}' (not a kind)";
            case ShowActionKind.CountdownStart: return $"Countdown {a.Value} min";
            case ShowActionKind.CountdownTo: return $"Countdown to {a.Value.Trim()}";
            case ShowActionKind.CountdownStop: return "Stop countdown";
            case ShowActionKind.CountdownToggle: return "Countdown on / off";
            case ShowActionKind.CountdownLabel: return a.Value.Trim().Length > 0 ? $"Countdown label '{Shorten(a.Value.Trim())}'" : "Countdown label cleared";
            case ShowActionKind.AudioVolume: return $"Audio volume {a.Value}%";
            case ShowActionKind.SpotifyPlay:
            {
                if (a.Target.Length == 0) return "Break music play";
                var m = SpotifyLibrary.Find(state, a.Target);
                return $"Break music '{m?.DisplayName ?? a.Target}'";
            }
            case ShowActionKind.SpotifyPause: return "Break music pause";
            case ShowActionKind.SpotifyNext: return "Break music skip";
            case ShowActionKind.SpotifyVolume: return $"Break music {a.Value}%";
            case ShowActionKind.MessageOn: return $"Message '{Shorten(a.Value)}'";
            case ShowActionKind.MessageOff: return "Message off";
            case ShowActionKind.MessageToggle: return "Message toggle";
            case ShowActionKind.MessageScroll: return $"Message scroll {SwitchWords(a.Value)}";
            case ShowActionKind.ClockOn: return "Clock on";
            case ShowActionKind.ClockOff: return "Clock off";
            case ShowActionKind.ClockToggle: return "Clock toggle";
            case ShowActionKind.ClockFormat: return ActionSpec.TryParseHours(a.Value, out var hours) ? $"Clock {hours}-hour" : $"Clock hours '{a.Value}'?";
            case ShowActionKind.ClockSeconds: return $"Clock seconds {SwitchWords(a.Value)}";
            case ShowActionKind.ClockDate: return $"Clock date {SwitchWords(a.Value)}";
            case ShowActionKind.LogoOn: return "Logo on";
            case ShowActionKind.LogoOff: return "Logo off";
            case ShowActionKind.LogoToggle: return "Logo toggle";
            case ShowActionKind.PipOn: return "PiP on";
            case ShowActionKind.PipOff: return "PiP off";
            case ShowActionKind.PipToggle: return "PiP toggle";
            case ShowActionKind.OverlaysOff: return "Overlays off";
            case ShowActionKind.LowerThirdShow:
            case ShowActionKind.LowerThirdPreview:
            {
                var preview = a.Kind == ShowActionKind.LowerThirdPreview;
                var design = a.Target.Length == 0
                    ? (preview ? "(the default)" : "on air")
                    : $"'{state.LowerThirds.Find(a.Target)?.Name ?? a.Target}'";
                var head = preview ? $"Lower third to preview {design}" : $"Lower third {design}";
                if (a.Value.Length == 0) return head;
                var who = state.LowerThirds.FindEntry(a.Value);
                return who is null ? $"{head} — '{a.Value}' (not in the library)" : $"{head} — {who.Name}";
            }
            case ShowActionKind.LowerThirdHide: return "Lower third off";
            case ShowActionKind.LowerThirdPreviewOff: return "Lower third preview off";
            case ShowActionKind.LowerThirdTake: return "Lower third take (preview to air)";
            case ShowActionKind.LowerThirdUpdate: return "Lower third update (edits to air)";
            case ShowActionKind.WebKey: return $"Page: {WebPresets.LabelFor(a.Value)}{PageSuffix(a)}";
            case ShowActionKind.WebClick: return $"Page: click at {a.Value}{PageSuffix(a)}";
            case ShowActionKind.WebType: return $"Page: type '{Shorten(a.Value)}'{PageSuffix(a)}";
            case ShowActionKind.WebReload: return $"Page: reload{PageSuffix(a)}";
            case ShowActionKind.WebOpen: return $"Page: open {(a.Value.Contains("://") ? WebAddress.ShortName(a.Value) : Shorten(a.Value))}{PageSuffix(a)}";
            case ShowActionKind.DeckNext: return "Deck: the next page";
            case ShowActionKind.DeckPrev: return "Deck: the previous page";
            case ShowActionKind.DeckPage: return $"Deck: {Decks.DescribePage(a.Value)}";
            case ShowActionKind.VideoToEnd:
                return VideoClock.TryParseBeforeEnd(a.Value, out var before) ? $"Video: to its last {before:0.#} s" : $"Video: to its last '{a.Value}' (not seconds)";
            case ShowActionKind.VideoRestart: return "Video: restart from the top";
            case ShowActionKind.DeviceSend: return $"Device {(Interactive.Find(state.Interactive, a.Target)?.Name ?? (a.Target.Length > 0 ? a.Target : "?"))}: {a.Value}";
            case ShowActionKind.Announce:
            {
                var slot = a.Target.Length > 0 ? Schedule.Find(state.Install, a.Target) : Schedule.Find(state.Install, a.Value, SlotKind.Announcement);
                if (slot is not null) return $"Announcement '{slot.Name}'";
                return a.Target.Length > 0 ? $"Announcement '{a.Target}' (not on the Install page)" : a.Value.Length > 0 ? $"Announce: '{Shorten(a.Value)}'" : "Announce: (nothing)";
            }
            case ShowActionKind.AnnounceOff: return "Announcement off";
            case ShowActionKind.AdvertPlay: return $"Advert '{Schedule.Find(state.Install, a.Target, SlotKind.Advert)?.Name ?? (a.Target.Length > 0 ? a.Target + " (not found)" : "?")}' now";
            case ShowActionKind.AdvertOff: return "Advert ends";
            case ShowActionKind.ScheduleOn: return "Install schedule on";
            case ShowActionKind.ScheduleOff: return "Install schedule off";
            case ShowActionKind.WeatherOn: return "Weather on";
            case ShowActionKind.WeatherOff: return "Weather off";
            case ShowActionKind.WeatherToggle: return "Weather toggle";
            case ShowActionKind.WeatherView:
                return WeatherWords.ParseView(a.Value) is { } view ? $"Weather: {WeatherWords.ViewName(view).ToLowerInvariant()}" : $"Weather: '{a.Value}' (not a view)";
            case ShowActionKind.DuckOn: return "Duck for announcement";
            case ShowActionKind.DuckOff: return "Lift the duck";
            case ShowActionKind.DuckToggle: return "Duck toggle";
            case ShowActionKind.ToneOn: return "Line-up tone on";
            case ShowActionKind.ToneOff: return "Line-up tone off";
            case ShowActionKind.StopAll: return "Stop all";
            case ShowActionKind.ListArm: return $"Arm {StackName(state, a.Target)}";
            case ShowActionKind.ListDisarm: return $"Disarm {StackName(state, a.Target)}";
            case ShowActionKind.ListGo: return $"GO on {StackName(state, a.Target)}";
            case ShowActionKind.ListBack: return $"Back on {StackName(state, a.Target)}";
            case ShowActionKind.ListReset: return $"Reset {StackName(state, a.Target)}";
            default: return ActionSpec.Label(a.Kind);
        }
    }

    /// <summary>A library item by number, id, then display name (case-insensitive) — either kind.</summary>
    public static StingerItemConfig? FindStinger(ShowState state, string idOrName) => StingerLibrary.Find(state, idOrName);

    private static void AppendTransition(StringBuilder sb, string value)
    {
        if (!ActionSpec.TryParseTransition(value, out var cut, out var ms)) return;
        if (cut) sb.Append(" (cut)");
        else if (ms >= 0) sb.Append($" ({ms} ms)");
    }

    private static string SwitchWords(string value) => value.Trim().ToLowerInvariant() switch
    {
        "on" or "1" or "true" or "show" or "yes" => "on",
        "off" or "0" or "false" or "hide" or "no" => "off",
        _ => "toggle",
    };

    /// <summary>" → the page" when a web action names one; "" for the page on air.</summary>
    private static string PageSuffix(CueActionConfig a)
        => a.Target.Length == 0 ? "" : $" → {(a.Target.Contains("://") ? WebAddress.ShortName(a.Target) : a.Target)}";

    private static string ScreenLabel(ShowState state, string id)
    {
        var p = state.Output.Placements.FirstOrDefault(x => x.ScreenId == id);
        return p is null ? id : p.CustomLabel.Length > 0 ? p.CustomLabel : id;
    }

    private static string CanvasLabel(ShowState state, string key)
    {
        var c = state.Output.CanvasNames.FirstOrDefault(x => x.MemberKey == key);
        return c is { Name.Length: > 0 } ? c.Name : key;
    }

    private static string StackName(ShowState state, string idOrName)
        => CueStacks.Find(state, idOrName)?.Name ?? (idOrName.Length == 0 ? "a list" : idOrName);

    private static string Shorten(string text) => text.Length <= 24 ? text : text[..22] + "…";
}
