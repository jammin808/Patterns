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

    /// <summary>The video (path, loop, mute) that should be decoding, playlist-aware.</summary>
    public static (string Path, bool Loop, bool Mute)? FindActiveVideo(ShowSnapshot snap)
    {
        var direct = FindActiveMedia(snap.State, MediaSource.Video);
        if (direct is not null && !string.IsNullOrWhiteSpace(direct.VideoPath))
        {
            return (direct.VideoPath, direct.Loop, direct.Mute);
        }

        var playlist = FindActivePlaylist(snap.State);
        if (playlist is not null && snap.PlaylistNow is { IsVideo: true } now)
        {
            // Playlist videos never loop themselves — their natural end advances the playlist.
            return (now.Path, false, playlist.Mute);
        }

        return null;
    }
}
