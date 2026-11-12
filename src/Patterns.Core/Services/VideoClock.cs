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
    bool CanSeek,
    double Rate = 1,
    bool Paused = false)
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

    /// <summary>
    /// The clock this clip runs on beside its own is held with it: the playlist's for its item, the
    /// stinger's ending for its clip. The desk says it of the reading it hands out; false for a clip
    /// on no clock but its own, and for a paused clip whose playlist or stinger runs on.
    /// </summary>
    public bool ClockHeld { get; init; }

    /// <summary>
    /// VIDEO PAUSE and VIDEO PLAY hold and release this clip, with the clock it runs on — the
    /// programme's own, a layer's, a playlist item's with the playlist's clock, a stinger's with the
    /// stinger's ending. False for a live source or a decoder that is not open yet.
    /// </summary>
    public bool Pausable => CanSeek;

    /// <summary>
    /// VIDEO STOP puts this clip back to its top and holds it there. False for a stinger's clip —
    /// STINGER STOP is its stop, and puts the show back — and wherever the clip is not pausable.
    /// </summary>
    public bool Stoppable => CanSeek && Role != VideoRole.Stinger;
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
    /// is a file, or its decoder is not open yet. <paramref name="stingerClipKey"/> is the input key
    /// of the clip a stinger has on the screens, or null with none: the reading is a stinger's only
    /// when it is that clip — under a sting that covers some screens alone the programme's own clip
    /// is read first, and keeps its own role.
    /// </summary>
    public static VideoReading? Read(ShowSnapshot snap, Func<string, IPlayheadSource?> resolve, string? stingerClipKey = null)
    {
        var wanted = MediaLocator.FindWantedInputs(snap).FirstOrDefault(w => w.Kind == MediaLocator.WantedKind.VideoFile);
        if (wanted is null) return null;
        var source = resolve(wanted.Key);
        if (source is null) return null;
        return new VideoReading(wanted.Key, wanted.Target, RoleOf(snap, wanted.Key, stingerClipKey), source.PositionSeconds, source.DurationSeconds,
            source.IsPlaying, source.IsEnded, wanted.Loop, source.CanSeek, source.Rate, source.IsPaused);
    }

    /// <summary>
    /// Whose clip a key is in this picture: the stinger's when it is the clip a stinger has on the screens, the
    /// programme's own media, the playlist's item, else a layer's or a screen's own. One rule for the clip on air and
    /// for a clip named by its key, so the clock that holds with a clip is the same whichever way it was asked.
    /// </summary>
    public static VideoRole RoleOf(ShowSnapshot snap, string key, string? stingerClipKey = null)
    {
        var state = snap.State;
        if (!string.IsNullOrEmpty(stingerClipKey) && key == stingerClipKey) return VideoRole.Stinger;
        if (state.Pattern.Kind == PatternKind.Media && state.Pattern.Media.Source == MediaSource.Video &&
            key == InputKeys.Video(state.Pattern.Media.VideoPath)) return VideoRole.Program;
        if (snap.PlaylistNow is { IsVideo: true } now && key == InputKeys.Video(now.Path)) return VideoRole.Playlist;
        return VideoRole.Layer;
    }

    /// <summary>
    /// The clips the programme holds, each as the transport reads it, in the picture's own order (the clip on air
    /// first): every file the air's snapshot plays whose decoder is open. A clip mounted for the preview alone and
    /// the standby's pre-roll are not the air's and are not here.
    /// </summary>
    public static IReadOnlyList<VideoReading> ReadAll(ShowSnapshot snap, Func<string, IPlayheadSource?> resolve, string? stingerClipKey = null)
    {
        var list = new List<VideoReading>();
        foreach (var wanted in MediaLocator.FindWantedInputs(snap))
        {
            if (wanted.Kind != MediaLocator.WantedKind.VideoFile || resolve(wanted.Key) is not { } source) continue;
            list.Add(new VideoReading(wanted.Key, wanted.Target, RoleOf(snap, wanted.Key, stingerClipKey), source.PositionSeconds, source.DurationSeconds,
                source.IsPlaying, source.IsEnded, wanted.Loop, source.CanSeek, source.Rate, source.IsPaused));
        }
        return list;
    }

    /// <summary>The file a clip's key names without its folders — "b-roll.mp4" for "vid:C:\clips\b-roll.mp4"; the key itself when it is no file's.</summary>
    public static string NameOfKey(string key)
    {
        var path = key.StartsWith("vid:", StringComparison.Ordinal) ? key[4..] : key;
        var cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;
    }

    /// <summary>What a clip verb answers for a key no mounted clip has. The key is never read as the clip on air.</summary>
    public const string NotMountedWords = "No clip with that key is mounted — STATE videos[] lists them.";

    /// <summary>What a clip verb answers for a clip only the preview holds: the transport is the air's.</summary>
    public static string PreviewOnlyWords(string key) => $"'{NameOfKey(key)}' is the preview's clip, not the air's — a take puts it on the air.";

    /// <summary>What a clip verb answers for the standby's pre-rolled clip: it is held until its cue goes.</summary>
    public static string PreRolledWords(string key) => $"'{NameOfKey(key)}' is held for the standby — it plays when that goes.";

    /// <summary>What every transport verb answers with nothing to act on.</summary>
    public const string NoClipWords = "No video is on air — put a clip on the pattern of the look on air, or fire a video stinger, first.";

    /// <summary>
    /// Why VIDEO PLAY, PAUSE or STOP would change nothing on this clip now, in the words the wire
    /// answers and a menu shows; "" when the verb would act. One rule for the executor, the
    /// right-click entries and the desk's keys, so none offers what another refuses.
    /// </summary>
    public static string WhyNot(ShowActionKind verb, VideoReading? r)
    {
        if (r is null) return NoClipWords;
        if (!r.CanSeek) return $"'{r.Name}' cannot be moved — a live source, or its decoder is not open yet.";
        var ended = r.Ended && !r.Loops;
        switch (verb)
        {
            case ShowActionKind.VideoPause:
                if (ended) return $"'{r.Name}' has ended — VIDEO RESTART plays it from the top.";
                return r.Paused ? $"'{r.Name}' is already paused at {Format(r.PositionSeconds)}." : "";
            case ShowActionKind.VideoPlay:
                if (ended) return $"'{r.Name}' has ended — VIDEO RESTART plays it from the top.";
                return r.Paused ? "" : $"'{r.Name}' is already playing.";
            case ShowActionKind.VideoStop:
                if (r.Role == VideoRole.Stinger) return $"'{r.Name}' is a stinger's clip — STINGER STOP puts the show back.";
                return r.Paused && r.PositionSeconds < 0.05 ? $"'{r.Name}' is already stopped at its top." : "";
            default:
                return "";
        }
    }

    /// <summary>What holds with a paused clip beside its picture: "the playlist", "the stinger", or "" for a clip on no clock but its own.</summary>
    public static string HeldWith(VideoReading r) => r.Role switch
    {
        VideoRole.Playlist => "the playlist",
        VideoRole.Stinger => "the stinger",
        _ => "",
    };

    /// <summary>
    /// The clip on air in one line for a reader that wants words — the Run strip's tip: the clock's
    /// line, and for a paused clip what holds with it (<see cref="VideoReading.ClockHeld"/>, the
    /// fact, never the role alone) and what runs it on.
    /// </summary>
    public static string Words(VideoReading? r) => r is null ? "" : Describe(r) + HeldWords(r);

    /// <summary>
    /// The same for the assistant's facts, by the clip's role and never its file's name: "a playlist
    /// item · 1:02 / 3:30 · 2:28 left · paused — held, and the playlist holds with it; VIDEO PLAY
    /// runs both on".
    /// </summary>
    public static string FactWords(VideoReading? r)
    {
        if (r is null) return "";
        var what = r.Role switch
        {
            VideoRole.Playlist => "a playlist item",
            VideoRole.Stinger => "a stinger's clip",
            VideoRole.Layer => "a layer's or a screen's own clip",
            _ => r.IsAudioOnly ? "the programme's audio file" : "the programme's clip",
        };
        return $"{what} · {Times(r)}{HeldWords(r)}";
    }

    /// <summary>
    /// The clips the programme holds beside the clip on air, for the assistant's facts — each by its role and clock,
    /// never a file's name or a key — and the form that drives one: "a layer's or a screen's own clip · 0:12 / 1:00 ·
    /// 0:48 left — each is driven by its key: VIDEO PAUSE|PLAY|STOP|RESTART ON &lt;key&gt; …". "" with none.
    /// </summary>
    public static string OtherFactWords(IReadOnlyList<VideoReading> others)
        => others.Count == 0 ? "" : string.Join("; ", others.Select(FactWords))
            + " — each is driven by its key from STATE videos[]: VIDEO PAUSE|PLAY|STOP|RESTART ON <key>, VIDEO END <seconds> ON <key>, VIDEO SEEK <point> ON <key>; with no key a clip verb is the clip on air's";

    private static string HeldWords(VideoReading r)
    {
        if (!r.Paused) return "";
        var with = HeldWith(r);
        if (with.Length == 0) return " — held; VIDEO PLAY runs it on";
        return r.ClockHeld
            ? $" — held, and {with} holds with it; VIDEO PLAY runs both on"
            : $" — held, and {with} runs on without it; VIDEO PLAY runs it on";
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
        var paused = r.Paused ? " · paused" : "";
        if (!r.HasLength) return Format(r.PositionSeconds) + paused;
        var head = $"{Format(r.PositionSeconds)} / {Format(r.LengthSeconds)}";
        return (r.Loops ? head + " · loop" : $"{head} · {Format(r.RemainingSeconds)} left") + paused;
    }

    /// <summary>The one line every surface shows: "VT sponsor.mp4 · 1:02 / 3:30 · 2:28 left".</summary>
    public static string Describe(VideoReading? r) => r is null ? "" : $"{Tag(r)} {r.Name} · {Times(r)}";

    /// <summary>The Run strip's chip: "VT 2:28" — what is left, or the position of a loop, or VT ENDED.</summary>
    public static string Chip(VideoReading r)
    {
        var word = r.IsAudioOnly && r.Role is not (VideoRole.Stinger or VideoRole.Playlist) ? "AUDIO" : "VT";
        if (r.Ended && !r.Loops) return word + " ENDED";
        if (r.Paused) return $"{word} PAUSED {Format(r.HasLength && !r.Loops ? r.RemainingSeconds : r.PositionSeconds)}";
        if (!r.HasLength) return $"{word} {Format(r.PositionSeconds)}";
        return r.Loops ? $"{word} LOOP {Format(r.PositionSeconds)}" : $"{word} {Format(r.RemainingSeconds)}";
    }

    /// <summary>The caller's call in the last seconds: "OUT IN 10", "OUT IN 3"; "" before that.</summary>
    public static string Call(VideoReading r)
        => r.InLast(OutWarningSeconds) && !r.Paused ? $"OUT IN {Math.Max(1, (int)Math.Ceiling(r.RemainingSeconds))}" : "";

    /// <summary>The longest point VIDEO SEEK names: a day. Anything past a clip's end is clamped by the decoder to its end.</summary>
    public const double MaxSeekSeconds = 24 * 3600;

    /// <summary>
    /// VIDEO SEEK's value: a point from the top — "83", "83.5", "1:23", "1:02:03", "top" — or from
    /// the end — "END", "END 5" (blank = ten, as VIDEO END). False for anything else: a word, a
    /// negative, a point past a day. <paramref name="fromEnd"/> says which.
    /// </summary>
    public static bool TryParseSeek(string? value, out double seconds, out bool fromEnd)
    {
        seconds = 0;
        fromEnd = false;
        var t = (value ?? "").Trim();
        if (t.Length == 0) return false;
        var words = t.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (words[0].Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            fromEnd = true;
            return TryParseBeforeEnd(words.Length > 1 ? words[1] : "", out seconds);
        }
        if (t.EndsWith("s", StringComparison.OrdinalIgnoreCase) && !t.Contains(':')) t = t[..^1].Trim();
        if (!WebVt.TryParseTime(t, out var at) || double.IsNaN(at) || at < 0 || at > MaxSeekSeconds) return false;
        seconds = at;
        return true;
    }

    /// <summary>
    /// Where a seek lands on a clip of <paramref name="lengthSeconds"/> (0 = not known yet): from the
    /// top, clamped to the clip; from the end, never before the top — a clip shorter than the gap
    /// starts again. Null when the point is from the end and the length is not known: nothing to
    /// count back from, so the seek is refused rather than guessed.
    /// </summary>
    public static double? SeekTarget(double seconds, bool fromEnd, double lengthSeconds)
    {
        if (fromEnd) return lengthSeconds > 0 ? Math.Max(0, lengthSeconds - seconds) : null;
        return lengthSeconds > 0 ? Math.Clamp(seconds, 0, lengthSeconds) : Math.Max(0, seconds);
    }

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
