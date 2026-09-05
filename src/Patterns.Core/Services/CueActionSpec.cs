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
    /// <summary>A break-music library entry (blank = resume what is loaded).</summary>
    Music,
    /// <summary>A lower third design.</summary>
    LowerThird,
}

/// <summary>What an action's Value holds (nothing else takes free text).</summary>
public enum ValueKind
{
    None,
    /// <summary>"cut", a fade in milliseconds, or empty for the show default.</summary>
    Transition,
    Minutes,
    Text,
    /// <summary>A whole number of percent, 0–125.</summary>
    Percent,
    /// <summary>A whole number of percent, 0–100 (a Spotify device's own range).</summary>
    Level,
    /// <summary>A lower-thirds library entry (id, name or number) to fill the design with first; empty = as designed.</summary>
    Person,
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
        CueActionKind.ScreenLock or CueActionKind.ScreenUnlock => (TargetKind.Screen, ValueKind.None),
        CueActionKind.CanvasOn or CueActionKind.CanvasOff => (TargetKind.Canvas, ValueKind.None),
        CueActionKind.CountdownStart => (TargetKind.None, ValueKind.Minutes),
        CueActionKind.MessageOn => (TargetKind.None, ValueKind.Text),
        CueActionKind.AudioVolume => (TargetKind.None, ValueKind.Percent),
        CueActionKind.SpotifyPlay => (TargetKind.Music, ValueKind.None),
        CueActionKind.SpotifyVolume => (TargetKind.None, ValueKind.Level),
        CueActionKind.LowerThirdShow => (TargetKind.LowerThird, ValueKind.Person),
        CueActionKind.LowerThirdPreview => (TargetKind.LowerThird, ValueKind.Person),
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
        CueActionKind.AudioVolume => "Audio volume",
        CueActionKind.SpotifyPlay => "Break music — play",
        CueActionKind.SpotifyPause => "Break music — pause",
        CueActionKind.SpotifyNext => "Break music — skip track",
        CueActionKind.SpotifyVolume => "Break music level",
        CueActionKind.StingerFire => "Fire VOG / stinger",
        CueActionKind.StingerStop => "Stop VOG / stinger",
        CueActionKind.PlaylistPart => "Playlist part",
        CueActionKind.StreamStart => "Start stream",
        CueActionKind.StreamStop => "Stop stream",
        CueActionKind.BlackoutOn => "Blackout on",
        CueActionKind.BlackoutOff => "Blackout off",
        CueActionKind.ScreenOn => "Screen on",
        CueActionKind.ScreenOff => "Screen off",
        CueActionKind.ScreenLock => "Screen lock (keeps its picture)",
        CueActionKind.ScreenUnlock => "Screen unlock (follows cues)",
        CueActionKind.CanvasOn => "Canvas on",
        CueActionKind.CanvasOff => "Canvas off",
        CueActionKind.CountdownStart => "Start countdown",
        CueActionKind.CountdownStop => "Stop countdown",
        CueActionKind.MessageOn => "Message on",
        CueActionKind.MessageOff => "Message off",
        CueActionKind.ClockOn => "Clock on",
        CueActionKind.ClockOff => "Clock off",
        CueActionKind.DuckOn => "Duck for an announcement",
        CueActionKind.DuckOff => "Lift the duck",
        CueActionKind.LowerThirdShow => "Lower third on",
        CueActionKind.LowerThirdHide => "Lower third off",
        CueActionKind.LowerThirdPreview => "Lower third to preview",
        CueActionKind.LowerThirdTake => "Lower third take (preview to air)",
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
        CueActionKind.AudioPlay, CueActionKind.AudioStop, CueActionKind.AudioVolume,
        CueActionKind.SpotifyPlay, CueActionKind.SpotifyPause, CueActionKind.SpotifyNext, CueActionKind.SpotifyVolume,
        CueActionKind.StingerFire, CueActionKind.StingerStop,
        CueActionKind.PlaylistPart,
        CueActionKind.StreamStart, CueActionKind.StreamStop,
        CueActionKind.BlackoutOn, CueActionKind.BlackoutOff,
        CueActionKind.ScreenOn, CueActionKind.ScreenOff,
        CueActionKind.ScreenLock, CueActionKind.ScreenUnlock,
        CueActionKind.CanvasOn, CueActionKind.CanvasOff,
        CueActionKind.CountdownStart, CueActionKind.CountdownStop,
        CueActionKind.MessageOn, CueActionKind.MessageOff,
        CueActionKind.ClockOn, CueActionKind.ClockOff,
        CueActionKind.LowerThirdShow, CueActionKind.LowerThirdHide, CueActionKind.LowerThirdPreview, CueActionKind.LowerThirdTake,
        CueActionKind.DuckOn, CueActionKind.DuckOff,
        CueActionKind.ListArm, CueActionKind.ListDisarm, CueActionKind.ListGo, CueActionKind.ListBack, CueActionKind.ListReset,
    };

    /// <summary>
    /// Content actions: a video stinger cannot share a cue with these (the clip owns every screen).
    /// Break music is sound only and is deliberately not here.
    /// </summary>
    public static bool ChangesContent(CueActionKind kind) => kind is
        CueActionKind.ApplyLook or CueActionKind.PlaylistPart or
        CueActionKind.ScreenOn or CueActionKind.ScreenOff or CueActionKind.CanvasOn or CueActionKind.CanvasOff;

    /// <summary>A percent value: a number from 0 to 125 (the player's own ceiling, ≈ +2 dB).</summary>
    public static bool TryParsePercent(string? value, out double percent)
    {
        percent = 0;
        if (!double.TryParse(value?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        if (double.IsNaN(v) || v < 0 || v > 125) return false;
        percent = v;
        return true;
    }

    /// <summary>A level value: a number from 0 to 100 (a Spotify device's own volume range).</summary>
    public static bool TryParseLevel(string? value, out double level)
    {
        level = 0;
        if (!double.TryParse(value?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        if (double.IsNaN(v) || v < 0 || v > 100) return false;
        level = v;
        return true;
    }

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
