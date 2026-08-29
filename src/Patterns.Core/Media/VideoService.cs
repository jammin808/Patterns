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
}
