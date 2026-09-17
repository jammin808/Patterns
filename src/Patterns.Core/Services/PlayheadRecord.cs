namespace Patterns.Core.Services;

/// <summary>Where one clip was: its mount key, its position from the start, and whether it loops.</summary>
public sealed record ClipPlace(string Key, double Seconds, bool Loops);

/// <summary>Where the music was: the track's place in the list and its position.</summary>
public sealed record MusicPlace(int Index, double Seconds);

/// <summary>
/// Round 76: the playheads a second ago — every clip with a timeline that is on air or in a
/// picture, and the playlist's track — written by the desk's poll into a sidecar of its own
/// (<c>patterns.playhead.json</c>), a few hundred bytes a second, apart from the recovery record,
/// which carries a whole show state and is written only when the air moves. A restart of any kind
/// reads it and resumes each clip where it would be by now, so the audience sees the clip carry on
/// rather than start again from its first frame, and the music comes back in the same track at the
/// same bar.
/// </summary>
public sealed record PlayheadRecord(DateTime UpdatedUtc, IReadOnlyList<ClipPlace> Clips, MusicPlace? Music = null)
{
    public bool IsEmpty => Clips.Count == 0 && Music is null;
}

/// <summary>The sidecar beside the settings that carries the playheads; atomic like the others.</summary>
public sealed class PlayheadStore
{
    public const string FileName = "patterns.playhead.json";

    private readonly string _path;

    public PlayheadStore(string directory) => _path = Path.Combine(directory, FileName);

    public string FilePath => _path;

    public PlayheadRecord? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonUtil.Deserialize<PlayheadRecord>(File.ReadAllText(_path));
        }
        catch (Exception ex)
        {
            Log.Warn("Playhead file unreadable.", ex);
            return null;
        }
    }

    public void Write(PlayheadRecord record)
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonUtil.SerializeCompact(record));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Playhead file write failed.", ex);
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch
        {
            // Nothing to clear — harmless.
        }
    }
}

/// <summary>The pure rule for where a clip resumes after a restart.</summary>
public static class PlayheadResume
{
    /// <summary>
    /// A record older than this is not resumed from: a desk hung for the watchdog's patience and
    /// killed, then booted, is well inside it; a record from an earlier day is not a position.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Where the clip would be by now: where it was plus the time that has passed since, because
    /// the clip kept playing in the desk that went (a hung desk decodes on; a crashed one left
    /// nothing, and the room's silence is the gap). A looping clip wraps around its length; a clip
    /// that would have ended by now is not resumed (null) — it starts again, as the show's own
    /// rule for an ended clip would have it. A length the decoder has not said yet (0) is taken on
    /// trust: the seek lands where it lands, and libVLC clamps it to the file.
    /// </summary>
    public static double? Target(double recordedSeconds, DateTime recordedUtc, DateTime nowUtc, double lengthSeconds, bool loops)
    {
        var elapsed = (nowUtc - recordedUtc).TotalSeconds;
        if (elapsed < 0 || elapsed > MaxAge.TotalSeconds) return null;
        var at = Math.Max(0, recordedSeconds) + elapsed;
        if (lengthSeconds > 0)
        {
            if (loops) return at % lengthSeconds;
            if (at >= lengthSeconds) return null;
        }
        return at;
    }

    /// <summary>The record is recent enough to act on at all.</summary>
    public static bool IsFresh(PlayheadRecord record, DateTime nowUtc)
    {
        var age = nowUtc - record.UpdatedUtc;
        return age >= TimeSpan.Zero && age <= MaxAge;
    }
}
