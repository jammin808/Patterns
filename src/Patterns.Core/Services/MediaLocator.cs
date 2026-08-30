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
        foreach (var placement in state.Output.Placements)
        {
            if (!placement.UseCustomPattern || !placement.Enabled) continue;
            var a = state.Independent.FirstOrDefault(x => x.ScreenId == placement.ScreenId);
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

    /// <summary>The NDI source name that should be received right now (empty = none).</summary>
    public static string FindActiveNdiSource(ShowState state)
    {
        var m = FindActiveMedia(state, MediaSource.NdiFeed);
        return m is null ? "" : m.NdiSourceName;
    }
}
