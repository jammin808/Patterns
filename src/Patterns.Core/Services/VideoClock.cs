using System.Globalization;
using Patterns.Core.Media;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>Where the clip on air came from — what the caller's clock calls it.</summary>
public enum VideoRole
{
    /// <summary>The program's own media pattern plays a file.</summary>
    Program,
    /// <summary>The playlist on air is on a video item.</summary>
    Playlist,
    /// <summary>A stinger's clip owns the screens.</summary>
    Stinger,
    /// <summary>A layer over the pattern, or a screen of its own, plays a file.</summary>
    Layer,
}

/// <summary>
/// The clip on air as the caller reads it: which file, where it is, how long it is, what is left —
/// and whether it will end at all (a loop never does). Immutable; built on every poll.
/// </summary>
public sealed record VideoReading(
    string Key,
    string File,
    VideoRole Role,
    double PositionSeconds,
    double LengthSeconds,
    bool Playing,
    bool Ended,
    bool Loops,
    bool CanSeek)
{
    /// <summary>The decoder has said how long the clip is (it may not, in the first frames).</summary>
    public bool HasLength => LengthSeconds > 0;

    /// <summary>What is left of the clip, never negative; 0 when the length is unknown.</summary>
    public double RemainingSeconds => HasLength ? Math.Max(0, LengthSeconds - PositionSeconds) : 0;

    /// <summary>How far through the clip is, 0–1; 0 when the length is unknown.</summary>
    public double Fraction => HasLength ? Math.Clamp(PositionSeconds / LengthSeconds, 0, 1) : 0;

    /// <summary>The clip is in its last <paramref name="seconds"/> and will end — a loop never comes out, an ended clip already has.</summary>
    public bool InLast(double seconds) => HasLength && !Loops && !Ended && RemainingSeconds <= seconds;

    /// <summary>The file's name without its folders.</summary>
    public string Name => System.IO.Path.GetFileName(File);

    /// <summary>An audio-only file plays through the same decoder: the clock reads AUDIO for it, VT for a picture.</summary>
    public bool IsAudioOnly => PlaylistSequencer.IsAudioPath(File);
}

/// <summary>
/// The caller's VT clock, pure: which clip is on air from a snapshot and the mounted sources,
/// the words for the panel, the Run strip and the wire, and the ten-second call. The app layer
/// reads it every second; every surface — the desk, the phone, Companion, OSC — reads the same
/// reading, so nobody at the desk hears a different number.
/// </summary>
public static class VideoClock
{
    /// <summary>The seconds before the end where the caller wants the word: "ten seconds on VT".</summary>
    public const double OutWarningSeconds = 10;

    /// <summary>Where VIDEO END lands with no number: the last ten seconds, enough to see the end and hear the out.</summary>
    public const double DefaultToEndSeconds = 10;

    /// <summary>
    /// The clip on air, if any: the first file the program references in priority order (the
    /// program's own media, then its layers, then each screen of its own), read through
    /// <paramref name="resolve"/> — the input bus, or a fake in tests. Null when nothing on air
    /// is a file, or its decoder is not open yet.
    /// </summary>
    public static VideoReading? Read(ShowSnapshot snap, Func<string, IVideoFrameSource?> resolve, bool stingerClip = false)
    {
        var wanted = MediaLocator.FindWantedInputs(snap).FirstOrDefault(w => w.Kind == MediaLocator.WantedKind.VideoFile);
        if (wanted is null) return null;
        var source = resolve(wanted.Key);
        if (source is null) return null;

        var state = snap.State;
        var role = VideoRole.Layer;
        if (stingerClip) role = VideoRole.Stinger;
        else if (state.Pattern.Kind == PatternKind.Media && state.Pattern.Media.Source == MediaSource.Video &&
                 wanted.Key == InputKeys.Video(state.Pattern.Media.VideoPath)) role = VideoRole.Program;
        else if (snap.PlaylistNow is { IsVideo: true } now && wanted.Key == InputKeys.Video(now.Path)) role = VideoRole.Playlist;

        return new VideoReading(wanted.Key, wanted.Target, role, source.PositionSeconds, source.DurationSeconds,
            source.IsPlaying, source.IsEnded, wanted.Loop, source.CanSeek);
    }

    /// <summary>"0:07", "3:30", "1:02:15" — what a caller reads from a metre; never a millisecond.</summary>
    public static string Format(double seconds)
    {
        var s = (int)Math.Round(Math.Max(0, seconds));
        var h = s / 3600;
        var m = s / 60 % 60;
        var sec = s % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, sec)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, sec);
    }

    /// <summary>"VT", "AUDIO", "STINGER CLIP", "PLAYLIST" — the tag before the file's name.</summary>
    public static string Tag(VideoReading r) => r.Role switch
    {
        VideoRole.Stinger => "STINGER CLIP",
        VideoRole.Playlist => "PLAYLIST",
        _ => r.IsAudioOnly ? "AUDIO" : "VT",
    };

    /// <summary>"1:02 / 3:30 · 2:28 left", "1:02 · loop", "ended", "1:02" while the length is unknown.</summary>
    public static string Times(VideoReading r)
    {
        if (r.Ended && !r.Loops) return "ended";
        if (!r.HasLength) return Format(r.PositionSeconds);
        var head = $"{Format(r.PositionSeconds)} / {Format(r.LengthSeconds)}";
        return r.Loops ? head + " · loop" : $"{head} · {Format(r.RemainingSeconds)} left";
    }

    /// <summary>The one line every surface shows: "VT sponsor.mp4 · 1:02 / 3:30 · 2:28 left".</summary>
    public static string Describe(VideoReading? r) => r is null ? "" : $"{Tag(r)} {r.Name} · {Times(r)}";

    /// <summary>The Run strip's chip: "VT 2:28" — what is left, or the position of a loop, or VT ENDED.</summary>
    public static string Chip(VideoReading r)
    {
        var word = r.IsAudioOnly && r.Role is not (VideoRole.Stinger or VideoRole.Playlist) ? "AUDIO" : "VT";
        if (r.Ended && !r.Loops) return word + " ENDED";
        if (!r.HasLength) return $"{word} {Format(r.PositionSeconds)}";
        return r.Loops ? $"{word} LOOP {Format(r.PositionSeconds)}" : $"{word} {Format(r.RemainingSeconds)}";
    }

    /// <summary>The caller's call in the last seconds: "OUT IN 10", "OUT IN 3"; "" before that.</summary>
    public static string Call(VideoReading r)
        => r.InLast(OutWarningSeconds) ? $"OUT IN {Math.Max(1, (int)Math.Ceiling(r.RemainingSeconds))}" : "";

    /// <summary>
    /// "10", "5", "2.5", "" → the seconds before the end VIDEO END means; false for a word or a
    /// number outside a minute's reach either way.
    /// </summary>
    public static bool TryParseBeforeEnd(string? value, out double seconds)
    {
        seconds = DefaultToEndSeconds;
        var t = (value ?? "").Trim();
        if (t.Length == 0) return true;
        if (t.EndsWith("s", StringComparison.OrdinalIgnoreCase)) t = t[..^1].Trim();
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0 || v > 3600) return false;
        seconds = v;
        return true;
    }
}
