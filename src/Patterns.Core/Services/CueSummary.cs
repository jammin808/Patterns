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
        return string.Join(" + ", parts);
    }

    public static string DescribeAction(ShowState state, CueActionConfig a)
    {
        switch (a.Kind)
        {
            case CueActionKind.Unknown: return "Unknown action (newer build)";
            case CueActionKind.Note: return "Note";
            case CueActionKind.ApplyLook:
            {
                var look = LookService.Find(state, a.Target);
                var sb = new StringBuilder($"Apply '{look?.Name ?? a.Target}'");
                if (CueActionSpec.TryParseTransition(a.Value, out var cut, out var ms))
                {
                    if (cut) sb.Append(" (cut)");
                    else if (ms >= 0) sb.Append($" ({ms} ms)");
                }
                return sb.ToString();
            }
            case CueActionKind.AudioPlay: return "Play audio";
            case CueActionKind.AudioStop: return "Stop audio";
            case CueActionKind.StingerFire:
            {
                var s = FindStinger(state, a.Target);
                if (s is null) return $"Sting '{a.Target}'";     // a dead target reads as it always did
                if (s.Source == StingerSource.EffectPulse) return $"Pulse '{s.DisplayName}'";
                return s.Kind == StingerKind.Vog
                    ? $"VOG '{s.DisplayName}'"
                    : $"Sting '{s.DisplayName}' ({StingerLibrary.AfterSummary(state, s)})";
            }
            case CueActionKind.StingerStop: return "Stop VOG / stinger";
            case CueActionKind.PlaylistPart: return $"Part '{a.Target}'";
            case CueActionKind.StreamStart: return "Start stream";
            case CueActionKind.StreamStop: return "Stop stream";
            case CueActionKind.BlackoutOn: return "Blackout on";
            case CueActionKind.BlackoutOff: return "Blackout off";
            case CueActionKind.ScreenOn: return $"Screen '{ScreenLabel(state, a.Target)}' on";
            case CueActionKind.ScreenOff: return $"Screen '{ScreenLabel(state, a.Target)}' off";
            case CueActionKind.ScreenLock: return $"Screen '{ScreenLabel(state, a.Target)}' locked — keeps its picture";
            case CueActionKind.ScreenUnlock: return $"Screen '{ScreenLabel(state, a.Target)}' follows cues again";
            case CueActionKind.CanvasOn: return $"Canvas '{CanvasLabel(state, a.Target)}' on";
            case CueActionKind.CanvasOff: return $"Canvas '{CanvasLabel(state, a.Target)}' off";
            case CueActionKind.CountdownStart: return $"Countdown {a.Value} min";
            case CueActionKind.CountdownStop: return "Stop countdown";
            case CueActionKind.AudioVolume: return $"Audio volume {a.Value}%";
            case CueActionKind.SpotifyPlay:
            {
                if (a.Target.Length == 0) return "Break music play";
                var m = SpotifyLibrary.Find(state, a.Target);
                return $"Break music '{m?.DisplayName ?? a.Target}'";
            }
            case CueActionKind.SpotifyPause: return "Break music pause";
            case CueActionKind.SpotifyNext: return "Break music skip";
            case CueActionKind.SpotifyVolume: return $"Break music {a.Value}%";
            case CueActionKind.MessageOn: return $"Message '{Shorten(a.Value)}'";
            case CueActionKind.MessageOff: return "Message off";
            case CueActionKind.LowerThirdShow:
            {
                var design = a.Target.Length == 0 ? "on air" : $"'{state.LowerThirds.Find(a.Target)?.Name ?? a.Target}'";
                if (a.Value.Length == 0) return $"Lower third {design}";
                var who = state.LowerThirds.FindEntry(a.Value);
                return who is null ? $"Lower third {design} — '{a.Value}' (not in the library)" : $"Lower third {design} — {who.Name}";
            }
            case CueActionKind.LowerThirdHide: return "Lower third off";
            case CueActionKind.ClockOn: return "Clock on";
            case CueActionKind.ClockOff: return "Clock off";
            case CueActionKind.DuckOn: return "Duck for announcement";
            case CueActionKind.DuckOff: return "Lift the duck";
            case CueActionKind.ListArm: return $"Arm {StackName(state, a.Target)}";
            case CueActionKind.ListDisarm: return $"Disarm {StackName(state, a.Target)}";
            case CueActionKind.ListGo: return $"GO on {StackName(state, a.Target)}";
            case CueActionKind.ListBack: return $"Back on {StackName(state, a.Target)}";
            case CueActionKind.ListReset: return $"Reset {StackName(state, a.Target)}";
            default: return a.Kind.ToString();
        }
    }

    /// <summary>A library item by number, id, then display name (case-insensitive) — either kind.</summary>
    public static StingerItemConfig? FindStinger(ShowState state, string idOrName) => StingerLibrary.Find(state, idOrName);

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
