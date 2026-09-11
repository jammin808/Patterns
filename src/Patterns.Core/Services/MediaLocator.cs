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
        /// <summary>A web page rendered by the app's browser engine.</summary>
        Web,
        /// <summary>A PDF deck rendered a page at a time by the app's PDF renderer.</summary>
        Deck,
    }

    /// <summary>
    /// One input the show wants mounted right now, in priority order. <paramref name="Format"/> is a
    /// capture device's chosen mode ("1920x1080@60"; empty = the device's default), a web page's
    /// viewport ("1920x1080") or a deck's start page ("1"); <paramref name="Zoom"/> is a web page's zoom in per cent.
    /// </summary>
    /// <summary><paramref name="Clean"/> is the style a web page wears while CLEAN is on ("" = the page as the site drew it).</summary>
    public sealed record WantedInput(string Key, WantedKind Kind, string Target, bool Loop, bool Mute, double VolumePct, string Format = "", double Zoom = 100, string Clean = "")
    {
        /// <summary>
        /// Every picture that wants this mount. One clip on the programme and in the preview is one
        /// decoder on two buses, so which of them the desk is listening to is a question about the
        /// list rather than about a single owner.
        /// </summary>
        public IReadOnlyList<MediaBus> Buses { get; init; } = Array.Empty<MediaBus>();

        /// <summary>Which output this mount's sound belongs on; see <see cref="AudioMonitorRule"/>.</summary>
        public AudioDestination Destination { get; init; } = AudioDestination.Program;
    }

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
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        var buses = new List<List<MediaBus>>();
        var state = snap.State;
        var bus = MediaBus.Program;

        void Add(WantedKind kind, string target, bool loop, bool mute, double volumePct, string format = "", double zoom = 100, string clean = "")
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            if (kind == WantedKind.Web) target = WebAddress.Normalize(target);
            var key = kind switch
            {
                WantedKind.VideoFile => Media.InputKeys.Video(target),
                WantedKind.Capture => Media.InputKeys.Capture(target),
                WantedKind.Web => Media.InputKeys.Web(target),
                WantedKind.Deck => Media.InputKeys.Deck(target),
                _ => Media.InputKeys.Ndi(target),
            };
            if (at.TryGetValue(key, out var already))
            {
                // One mount, however many pictures want it: the first one's settings stand, and
                // the rest are recorded so the monitor knows every bus this sound is on.
                if (!buses[already].Contains(bus)) buses[already].Add(bus);
                return;
            }
            if (kind == WantedKind.Capture) format = state.CaptureFormatFor(target);
            at[key] = list.Count;
            buses.Add(new List<MediaBus> { bus });
            list.Add(new WantedInput(key, kind, target, loop, mute, volumePct, format, zoom, clean));
        }

        void FromPattern(PatternConfig p)
        {
            // The pattern's own source first, then the two layers that ride over it: when a layer
            // shows the same clip or page as the pattern, the pattern's settings are the ones kept.
            FromMedia(p);
            foreach (var l in new[] { p.Layer1, p.Layer2 })
            {
                if (!l.Enabled) continue;
                switch (l.Source)
                {
                    case LayerSource.Video:
                        Add(WantedKind.VideoFile, l.VideoPath, l.Loop, l.Mute, l.VolumePct);
                        break;
                    case LayerSource.Capture:
                        Add(WantedKind.Capture, l.CaptureDevice, false, true, 0);
                        break;
                    case LayerSource.NdiFeed:
                        Add(WantedKind.Ndi, l.NdiSourceName, false, true, 0);
                        break;
                    case LayerSource.Web:
                        Add(WantedKind.Web, l.WebUrl, false, l.Mute, 0, $"{l.WebWidth}x{l.WebHeight}", l.WebZoomPct,
                            WebPresets.CleanCss(l.WebUrl, l.WebService, l.WebClean));
                        break;
                }
            }
        }

        void FromMedia(PatternConfig p)
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
                    case MediaSource.Web:
                        Add(WantedKind.Web, m.WebUrl, false, m.Mute, 0, $"{m.WebWidth}x{m.WebHeight}", m.WebZoomPct,
                            WebPresets.CleanCss(m.WebUrl, m.WebService, m.WebClean));
                        break;
                    case MediaSource.Deck:
                        Add(WantedKind.Deck, m.DeckPath, false, true, 0, m.DeckStartPage.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                // The wall this pattern draws — the show's, or the tiles it carries itself. The
                // inputs a monitor wall shows have to be opened like any other picture, so a wall
                // on a spare screen keeps its NDI feeds and capture boxes alive.
                foreach (var tile in Multiviews.For(state, p).Tiles)
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
            if (a is null) continue;
            // A screen on its own picture is its own bus: its clip's sound is heard only while
            // the desk is listening to that screen.
            bus = MediaBus.Output(target);
            FromPattern(a.Pattern);
        }
        bus = MediaBus.Program;   // the inset, the lower third: part of the programme's picture

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
                    // Resolved, so a show opened on another machine mounts its own copy of the
                    // clip — and so the mount's key is the one the renderer will look up.
                    Add(WantedKind.VideoFile, ShowFiles.Resolve(e.Path), true, e.MediaMute, e.MediaVolumePct);
                }
            }
        }

        for (var i = 0; i < list.Count; i++) list[i] = list[i] with { Buses = buses[i] };
        return list;
    }
}
