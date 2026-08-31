using SkiaSharp;

namespace Patterns.Core.Media;

/// <summary>
/// A live video frame source. The app layer provides an implementation (libVLC callback
/// rendering); the engine just composites frames. Implementations must make
/// <see cref="DrawFrame"/> safe to call from any render thread.
/// </summary>
public interface IVideoFrameSource
{
    /// <summary>Draws the newest decoded frame into dest. Returns false when no frame is available yet.</summary>
    bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint);

    SKSizeI? FrameSize { get; }

    bool IsPlaying { get; }

    /// <summary>True when the media reached its natural end (drives playlist advance).</summary>
    bool IsEnded { get; }

    /// <summary>Decoder-reported length in seconds; 0 when unknown.</summary>
    double DurationSeconds { get; }

    /// <summary>Human-readable state for the placeholder card ("opening…", "libVLC not found", …).</summary>
    string StatusText { get; }
}

/// <summary>Canonical mount keys — the same "ndi:"/"cap:" scheme the operator input labels use.</summary>
public static class InputKeys
{
    public static string Video(string path) => path.Length == 0 ? "" : "vid:" + path;
    public static string Capture(string device) => device.Length == 0 ? "" : "cap:" + device;
    public static string Ndi(string source) => source.Length == 0 ? "" : "ndi:" + source;
}

/// <summary>
/// The live-input pool: every mounted source (video decoders, capture devices, NDI®
/// receivers) keyed by identity, so any number of consumers — the program, per-screen
/// patterns, PiP, multiview tiles, the sandboxed preview — draw the same frames from the
/// same mount. The app layer's engines write on the UI thread by swapping copy-on-write
/// maps; render threads only ever read the volatile references.
/// </summary>
public static class InputBus
{
    private static readonly Dictionary<string, IVideoFrameSource> Empty = new();
    private static volatile Dictionary<string, IVideoFrameSource> _current = Empty;
    private static volatile Dictionary<string, IVideoFrameSource> _previous = Empty;

    /// <summary>The mounted source for a key, or null (not mounted / empty key).</summary>
    public static IVideoFrameSource? For(string key)
        => key.Length > 0 && _current.TryGetValue(key, out var s) ? s : null;

    /// <summary>The just-unmounted source for a key, kept briefly so crossfades fade real frames.</summary>
    public static IVideoFrameSource? PreviousFor(string key)
        => key.Length > 0 && _previous.TryGetValue(key, out var s) ? s : null;

    /// <summary>What a renderer should draw: the fade-out side prefers the retired source.</summary>
    public static IVideoFrameSource? Resolve(string key, bool isFadeSource)
        => isFadeSource ? PreviousFor(key) ?? For(key) : For(key);

    public static IReadOnlyCollection<string> Keys => _current.Keys;

    public static void Mount(string key, IVideoFrameSource source)
    {
        var next = new Dictionary<string, IVideoFrameSource>(_current) { [key] = source };
        _current = next;
    }

    public static void Unmount(string key)
    {
        if (!_current.ContainsKey(key)) return;
        var next = new Dictionary<string, IVideoFrameSource>(_current);
        next.Remove(key);
        _current = next;
    }

    /// <summary>Sets (source) or clears (null) the fade-out entry for a key.</summary>
    public static void SetPrevious(string key, IVideoFrameSource? source)
    {
        var next = new Dictionary<string, IVideoFrameSource>(_previous);
        if (source is null) next.Remove(key);
        else next[key] = source;
        _previous = next;
    }

    /// <summary>
    /// Clears the fade-out entry only if it is still <paramref name="expected"/>. A key that
    /// was remounted and retired again has a newer fade source; sweeping the older retirement
    /// must not take the newer one down with it.
    /// </summary>
    public static void ClearPreviousIf(string key, IVideoFrameSource expected)
    {
        if (!_previous.TryGetValue(key, out var current) || !ReferenceEquals(current, expected)) return;
        SetPrevious(key, null);
    }

    public static void Clear()
    {
        _current = Empty;
        _previous = Empty;
    }
}

/// <summary>Availability notes for the video/NDI stacks (empty = fine). Shown on placeholder cards.</summary>
public static class VideoService
{
    /// <summary>Availability text when no decode engine is present at all (e.g. libVLC missing).</summary>
    public static volatile string AvailabilityNote = "";
}

/// <summary>See <see cref="VideoService"/> — the NDI® receive side's availability note.</summary>
public static class NdiInput
{
    /// <summary>Availability text when receive isn't possible (NDI runtime missing).</summary>
    public static volatile string AvailabilityNote = "";
}
