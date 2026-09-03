using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>What an action's Target names.</summary>
public enum TargetKind
{
    None,
    Look,
    Stinger,
    Part,
    Screen,
    Canvas,
    Stack,
}

/// <summary>What an action's Value holds (nothing else takes free text).</summary>
public enum ValueKind
{
    None,
    /// <summary>"cut", a fade in milliseconds, or empty for the show default.</summary>
    Transition,
    Minutes,
    Text,
}

/// <summary>
/// The one table the cue editor, the validator and the executor share: for each action kind,
/// what its target is, what its value is, and how it reads.
/// </summary>
public static class CueActionSpec
{
    public static (TargetKind Target, ValueKind Value) For(CueActionKind kind) => kind switch
    {
        CueActionKind.ApplyLook => (TargetKind.Look, ValueKind.Transition),
        CueActionKind.StingerFire => (TargetKind.Stinger, ValueKind.None),
        CueActionKind.StingerStop => (TargetKind.None, ValueKind.None),
        CueActionKind.PlaylistPart => (TargetKind.Part, ValueKind.None),
        CueActionKind.ScreenOn or CueActionKind.ScreenOff => (TargetKind.Screen, ValueKind.None),
        CueActionKind.CanvasOn or CueActionKind.CanvasOff => (TargetKind.Canvas, ValueKind.None),
        CueActionKind.CountdownStart => (TargetKind.None, ValueKind.Minutes),
        CueActionKind.MessageOn => (TargetKind.None, ValueKind.Text),
        CueActionKind.ListArm or CueActionKind.ListDisarm or CueActionKind.ListGo
            or CueActionKind.ListBack or CueActionKind.ListReset => (TargetKind.Stack, ValueKind.None),
        _ => (TargetKind.None, ValueKind.None),
    };

    /// <summary>What the operator reads in the kind picker.</summary>
    public static string Label(CueActionKind kind) => kind switch
    {
        CueActionKind.Unknown => "Unknown (newer build)",
        CueActionKind.Note => "Note only",
        CueActionKind.ApplyLook => "Apply look",
        CueActionKind.AudioPlay => "Play audio track",
        CueActionKind.AudioStop => "Stop audio track",
        CueActionKind.StingerFire => "Fire stinger",
        CueActionKind.StingerStop => "Stop stinger",
        CueActionKind.PlaylistPart => "Playlist part",
        CueActionKind.StreamStart => "Start stream",
        CueActionKind.StreamStop => "Stop stream",
        CueActionKind.BlackoutOn => "Blackout on",
        CueActionKind.BlackoutOff => "Blackout off",
        CueActionKind.ScreenOn => "Screen on",
        CueActionKind.ScreenOff => "Screen off",
        CueActionKind.CanvasOn => "Canvas on",
        CueActionKind.CanvasOff => "Canvas off",
        CueActionKind.CountdownStart => "Start countdown",
        CueActionKind.CountdownStop => "Stop countdown",
        CueActionKind.MessageOn => "Message on",
        CueActionKind.MessageOff => "Message off",
        CueActionKind.ClockOn => "Clock on",
        CueActionKind.ClockOff => "Clock off",
        CueActionKind.ListArm => "Arm a list",
        CueActionKind.ListDisarm => "Disarm a list",
        CueActionKind.ListGo => "GO on a list",
        CueActionKind.ListBack => "Back on a list",
        CueActionKind.ListReset => "Reset a list",
        _ => kind.ToString(),
    };

    /// <summary>The kinds an operator can pick, in picker order (Unknown is never offered).</summary>
    public static readonly IReadOnlyList<CueActionKind> Editable = new[]
    {
        CueActionKind.ApplyLook, CueActionKind.Note,
        CueActionKind.AudioPlay, CueActionKind.AudioStop,
        CueActionKind.StingerFire, CueActionKind.StingerStop,
        CueActionKind.PlaylistPart,
        CueActionKind.StreamStart, CueActionKind.StreamStop,
        CueActionKind.BlackoutOn, CueActionKind.BlackoutOff,
        CueActionKind.ScreenOn, CueActionKind.ScreenOff,
        CueActionKind.CanvasOn, CueActionKind.CanvasOff,
        CueActionKind.CountdownStart, CueActionKind.CountdownStop,
        CueActionKind.MessageOn, CueActionKind.MessageOff,
        CueActionKind.ClockOn, CueActionKind.ClockOff,
        CueActionKind.ListArm, CueActionKind.ListDisarm, CueActionKind.ListGo, CueActionKind.ListBack, CueActionKind.ListReset,
    };

    /// <summary>Content actions: a video stinger cannot share a cue with these (the clip owns every screen).</summary>
    public static bool ChangesContent(CueActionKind kind) => kind is
        CueActionKind.ApplyLook or CueActionKind.PlaylistPart or
        CueActionKind.ScreenOn or CueActionKind.ScreenOff or CueActionKind.CanvasOn or CueActionKind.CanvasOff;

    /// <summary>A transition value: empty, "cut", or a whole number of milliseconds.</summary>
    public static bool TryParseTransition(string? value, out bool cut, out int fadeMs)
    {
        cut = false;
        fadeMs = -1;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var v = value.Trim();
        if (string.Equals(v, "cut", StringComparison.OrdinalIgnoreCase))
        {
            cut = true;
            return true;
        }
        if (int.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ms) && ms >= 0)
        {
            fadeMs = ms;
            return true;
        }
        return false;
    }
}
