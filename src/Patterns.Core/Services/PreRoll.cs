using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The standby cue's clips, opened before GO. A GO onto a look with a clip used to open its
/// decoder at the press: the file, the codec, the card's decoder and the first frame all happened
/// after the cut, so the room saw the previous picture — or black — for as long as the open took,
/// and on a small laptop that is the "tries to fade in" moment. Now the cue on standby has its
/// look's file clips mounted while the caller waits, held on their first frame and silent, so GO
/// lands on a picture. Pure: what the standby cue would put up, and the words the strip shows.
/// </summary>
public static class PreRoll
{
    /// <summary>The look the standby cue would put on air — its first "apply look" action, resolved by id or name; null with none.</summary>
    public static LookConfig? LookOf(ShowState state, RunCueConfig? standby)
    {
        if (standby is null) return null;
        foreach (var action in standby.Actions)
        {
            if (action.Kind != ShowActionKind.ApplyLook) continue;
            return LookService.Find(state, action.Target);
        }
        return null;
    }

    /// <summary>The file clips the standby cue's look references, as the pool will want them on GO.</summary>
    public static IReadOnlyList<MediaLocator.WantedInput> WantedFor(ShowState state, RunCueConfig? standby)
        => WantedFor(LookOf(state, standby)?.Json);

    /// <summary>
    /// The file clips a look's pattern and its two layers reference — the same key, loop, mute
    /// and volume the pool will want when the look is on air, so the mount carries over. Capture
    /// devices, NDI feeds and web pages are live and never pre-rolled; a playlist plays what it
    /// is at; a blackout look has no picture to open.
    /// </summary>
    public static IReadOnlyList<MediaLocator.WantedInput> WantedFor(string? lookJson)
    {
        if (string.IsNullOrWhiteSpace(lookJson)) return Array.Empty<MediaLocator.WantedInput>();
        LookData? data;
        try
        {
            data = JsonUtil.Deserialize<LookData>(lookJson);
        }
        catch
        {
            return Array.Empty<MediaLocator.WantedInput>();
        }
        if (data is null || data.Blackout) return Array.Empty<MediaLocator.WantedInput>();

        var list = new List<MediaLocator.WantedInput>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? path, bool loop, bool mute, double volumePct)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var key = Media.InputKeys.Video(path);
            if (!seen.Add(key)) return;
            list.Add(new MediaLocator.WantedInput(key, MediaLocator.WantedKind.VideoFile, path, loop, mute, volumePct));
        }

        var p = data.Pattern;
        if (p.Kind == PatternKind.Media && p.Media.Source == MediaSource.Video) Add(p.Media.VideoPath, p.Media.Loop, p.Media.Mute, p.Media.VolumePct);
        foreach (var layer in new[] { p.Layer1, p.Layer2 })
        {
            if (layer.Enabled && layer.Source == LayerSource.Video) Add(layer.VideoPath, layer.Loop, layer.Mute, layer.VolumePct);
        }
        return list;
    }

    /// <summary>Where one wanted clip stands in the pool.</summary>
    public enum State
    {
        /// <summary>Not mounted: the decoder limit, a file that would not open, or no decoder on this machine.</summary>
        Missing,
        /// <summary>Mounted and opening — no first frame yet.</summary>
        Opening,
        /// <summary>Held on its first frame, silent, ready for GO.</summary>
        Ready,
        /// <summary>Already on the screens (the program or the preview shows it): nothing to pre-roll.</summary>
        OnAir,
    }

    /// <summary>
    /// The strip's chip over the standby cue: "" with no clip; PRE-ROLLED when every clip is held
    /// or already up; CLIP ON AIR when they all are; PRE-ROLLING… while one opens; CLIP NOT OPEN
    /// when one could not be mounted (the ready and the warning are two chips — a green one and an
    /// amber one — so the caller reads the colour before the word).
    /// </summary>
    public static (string Good, string Wait) Words(IReadOnlyList<State> states)
    {
        if (states.Count == 0) return ("", "");
        var missing = states.Count(s => s == State.Missing);
        if (missing > 0) return ("", missing == states.Count && states.Count > 1 ? "CLIPS NOT OPEN" : "CLIP NOT OPEN");
        if (states.Any(s => s == State.Opening)) return ("", "PRE-ROLLING…");
        return (states.All(s => s == State.OnAir) ? "CLIP ON AIR" : "PRE-ROLLED", "");
    }
}
