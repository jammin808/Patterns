using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// Which media configuration is "live" right now. One video decoder and one playlist run at
/// a time; both the playlist service and the video engine use these rules so they agree.
/// Program pattern wins; then custom-pattern screens in placement order.
/// </summary>
public static class MediaLocator
{
    public static MediaOptions? FindActivePlaylist(ShowState state)
        => FindActiveMedia(state, MediaSource.Playlist);

    public static MediaOptions? FindActiveMedia(ShowState state, MediaSource source)
    {
        static bool Wants(PatternConfig p, MediaSource s) => p.Kind == PatternKind.Media && p.Media.Source == s;

        if (Wants(state.Pattern, source)) return state.Pattern.Media;
        foreach (var target in ContentTargets.ActiveCustomTargets(state))
        {
            var a = state.Independent.FirstOrDefault(x => x.ScreenId == target);
            if (a is not null && Wants(a.Pattern, source)) return a.Pattern.Media;
        }
        return null;
    }

    /// <summary>What the libVLC decoder should be playing (file, playlist item, or capture device).</summary>
    public readonly record struct ActivePlayback(string Target, bool Loop, bool IsCapture, bool Mute, double VolumePct);

    /// <summary>The media that should be decoding right now, playlist- and capture-aware.</summary>
    public static ActivePlayback? FindActiveVideo(ShowSnapshot snap)
    {
        var direct = FindActiveMedia(snap.State, MediaSource.Video);
        if (direct is not null && !string.IsNullOrWhiteSpace(direct.VideoPath))
        {
            return new ActivePlayback(direct.VideoPath, direct.Loop, false, direct.Mute, direct.VolumePct);
        }

        var capture = FindActiveMedia(snap.State, MediaSource.Capture);
        if (capture is not null && !string.IsNullOrWhiteSpace(capture.CaptureDevice))
        {
            return new ActivePlayback(capture.CaptureDevice, false, true, capture.Mute, capture.VolumePct);
        }

        var playlist = FindActivePlaylist(snap.State);
        if (playlist is not null && snap.PlaylistNow is { IsVideo: true } now)
        {
            // Playlist videos never loop themselves — their natural end advances the playlist.
            return new ActivePlayback(now.Path, false, false, playlist.Mute, playlist.VolumePct);
        }

        return null;
    }

    /// <summary>The first NDI source the show references (empty = none) — the fallback feed.</summary>
    public static string FindActiveNdiSource(ShowState state)
    {
        var m = FindActiveMedia(state, MediaSource.NdiFeed);
        return m is null ? "" : m.NdiSourceName;
    }

    public enum WantedKind
    {
        VideoFile,
        Capture,
        Ndi,
    }

    /// <summary>One input the show wants mounted right now, in priority order. <paramref name="Format"/> is a capture device's chosen mode ("1920x1080@60"; empty = the device's default).</summary>
    public sealed record WantedInput(string Key, WantedKind Kind, string Target, bool Loop, bool Mute, double VolumePct, string Format = "");

    /// <summary>
    /// Every input the snapshot references — the program pattern, each enabled custom-pattern
    /// screen, the PiP inset and multiview tiles — deduplicated by mount key, highest priority
    /// first (its audio settings win when two configs share a mount). This is the whole
    /// "distribute any input to any output" contract: engines mount this list, renderers
    /// resolve frames per key, and the same camera can sit on three screens at once.
    /// </summary>
    public static List<WantedInput> FindWantedInputs(ShowSnapshot snap)
    {
        var list = new List<WantedInput>();
        var seen = new HashSet<string>();
        var state = snap.State;

        void Add(WantedKind kind, string target, bool loop, bool mute, double volumePct)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            var key = kind switch
            {
                WantedKind.VideoFile => Media.InputKeys.Video(target),
                WantedKind.Capture => Media.InputKeys.Capture(target),
                _ => Media.InputKeys.Ndi(target),
            };
            if (!seen.Add(key)) return;
            var format = kind == WantedKind.Capture ? state.CaptureFormatFor(target) : "";
            list.Add(new WantedInput(key, kind, target, loop, mute, volumePct, format));
        }

        void FromPattern(PatternConfig p)
        {
            if (p.Kind == PatternKind.Media)
            {
                var m = p.Media;
                switch (m.Source)
                {
                    case MediaSource.Video:
                        // Audio-only files mount too — the decoder carries their sound.
                        Add(WantedKind.VideoFile, m.VideoPath, m.Loop, m.Mute, m.VolumePct);
                        break;
                    case MediaSource.Capture:
                        Add(WantedKind.Capture, m.CaptureDevice, false, m.Mute, m.VolumePct);
                        break;
                    case MediaSource.NdiFeed:
                        Add(WantedKind.Ndi, m.NdiSourceName, false, true, 0);
                        break;
                    case MediaSource.Playlist:
                        // Only the active playlist has a "now playing" item; videos never
                        // self-loop — their natural end advances the playlist.
                        if (snap.PlaylistNow is { IsVideo: true } now && ReferenceEquals(m, FindActivePlaylist(state)))
                        {
                            Add(WantedKind.VideoFile, now.Path, false, m.Mute, m.VolumePct);
                        }
                        break;
                }
            }
            else if (p.Kind == PatternKind.Multiview)
            {
                foreach (var tile in p.Multiview.Tiles)
                {
                    switch (tile.Source)
                    {
                        case MultiviewSource.NdiFeed:
                            Add(WantedKind.Ndi, tile.Input.Length > 0 ? tile.Input : FindActiveNdiSource(state), false, true, 0);
                            break;
                        case MultiviewSource.Capture:
                            Add(WantedKind.Capture, tile.Input, false, true, 0);
                            break;
                    }
                }
            }
        }

        FromPattern(state.Pattern);
        foreach (var target in ContentTargets.ActiveCustomTargets(state))
        {
            var a = state.Independent.FirstOrDefault(x => x.ScreenId == target);
            if (a is not null) FromPattern(a.Pattern);
        }

        var pip = state.Overlays.Pip;
        if (pip.Enabled)
        {
            if (pip.Source == PipSource.NdiFeed) Add(WantedKind.Ndi, pip.NdiSourceName, false, true, 0);
            else Add(WantedKind.Capture, pip.CaptureDevice, false, true, 0);
        }

        // The lower third on air: a media element's clip mounts too (silent b-roll unless told otherwise).
        var lower = state.LowerThirds;
        if (lower.ShownAtUtc is not null && lower.HiddenAtUtc is null && lower.Active is { } design)
        {
            foreach (var e in design.Elements)
            {
                if (e.Enabled && e.Kind == LowerThirds.LowerThirdElementKind.Media && PlaylistSequencer.IsVideoPath(e.Path))
                {
                    Add(WantedKind.VideoFile, e.Path, true, e.MediaMute, e.MediaVolumePct);
                }
            }
        }

        return list;
    }
}
