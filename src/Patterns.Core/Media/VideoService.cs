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

/// <summary>
/// Global mount point for the single active video source. Written by the app layer on the
/// UI thread when the media config changes; read (volatile) by render threads.
/// </summary>
public static class VideoService
{
    private static volatile IVideoFrameSource? _current;

    public static IVideoFrameSource? Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary>Availability text when no engine is present at all (e.g. libVLC missing).</summary>
    public static volatile string AvailabilityNote = "";

    /// <summary>
    /// The just-replaced source, kept alive briefly so crossfades can keep drawing the old
    /// content while it fades out (the app layer retires and disposes it after the fade).
    /// </summary>
    private static volatile IVideoFrameSource? _previous;

    public static IVideoFrameSource? Previous
    {
        get => _previous;
        set => _previous = value;
    }
}

/// <summary>
/// Mount point for the single active NDI® receive source (same contract as
/// <see cref="VideoService"/> — the app layer writes, render threads read).
/// </summary>
public static class NdiInput
{
    private static volatile IVideoFrameSource? _current;
    private static volatile IVideoFrameSource? _previous;

    public static IVideoFrameSource? Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary>See <see cref="VideoService.Previous"/> — the fade-out source.</summary>
    public static IVideoFrameSource? Previous
    {
        get => _previous;
        set => _previous = value;
    }

    /// <summary>Availability text when receive isn't possible (NDI runtime missing).</summary>
    public static volatile string AvailabilityNote = "";
}

/// <summary>Mount point for the picture-in-picture live input (independent of the main media).</summary>
public static class PipInput
{
    private static volatile IVideoFrameSource? _current;

    public static IVideoFrameSource? Current
    {
        get => _current;
        set => _current = value;
    }
}
