using SkiaSharp;

namespace Patterns.Core.Media;

/// <summary>
/// A live video frame source. The app layer provides an implementation (libVLC callback
/// rendering); the engine just composites frames. Implementations must make
/// <see cref="DrawFrame"/> safe to call from any render thread.
/// </summary>
/// <summary>
/// What a draw drew: whether it did, the show clock the drawn frame arrived at (-1 when the
/// source does not time its frames), whether that frame is a live picture, and its generation
/// (0 for a source that does not count). The clock is the drawn pixels' own — taken with them,
/// never read from the source afterwards, when a newer frame may have arrived.
/// </summary>
public readonly record struct DrawnFrame(bool Drew, double FrameClock = -1, bool IsLive = false, long Generation = 0)
{
    public static readonly DrawnFrame Nothing = new(false);
}

public interface IVideoFrameSource
{
    /// <summary>Draws the newest decoded frame into dest. Returns false when no frame is available yet.</summary>
    bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint);

    /// <summary>
    /// Draws the newest frame and says which frame it was — the clock and the liveness of the
    /// pixels drawn. The default draws through <see cref="DrawFrame"/> and reports the source's
    /// words; a source with timed frames overrides it to report the frame it actually drew.
    /// </summary>
    DrawnFrame Draw(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop)
        => new(DrawFrame(canvas, dest, paint, in crop), FrameClock, IsLive);

    /// <summary>
    /// Draws the part of the newest frame that survives <paramref name="crop"/>, stretched into
    /// dest. Sources that own an image override this with a source-rect draw; the default draws
    /// the whole frame, so a source that knows nothing of crops keeps working.
    /// </summary>
    bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop)
        => DrawFrame(canvas, dest, paint);

    SKSizeI? FrameSize { get; }

    bool IsPlaying { get; }

    /// <summary>True when the media reached its natural end (drives playlist advance).</summary>
    bool IsEnded { get; }

    /// <summary>Decoder-reported length in seconds; 0 when unknown.</summary>
    double DurationSeconds { get; }

    /// <summary>Human-readable state for the placeholder card ("opening…", "libVLC not found", …).</summary>
    string StatusText { get; }

    /// <summary>Where the media is, in seconds from its start; 0 when unknown, or for a source with no timeline (a camera, a feed, a page).</summary>
    double PositionSeconds => 0;

    /// <summary>The media has a timeline that can be moved along — a file, never a camera, a feed or a page.</summary>
    bool CanSeek => false;

    /// <summary>
    /// Moves the media to a time from its start (the decoder clamps it to the file); a media that
    /// has ended plays again from there. False when this source cannot be moved.
    /// </summary>
    bool Seek(double seconds) => false;

    /// <summary>
    /// The show clock the newest frame arrived at, in seconds (-1: the source does not say). A
    /// sink that draws it can then say how old the picture on the glass is — the number IMAG is
    /// judged by.
    /// </summary>
    double FrameClock => -1;

    /// <summary>The frames are a camera's or a feed's now — a capture card, NDI — not a file's: the age of the frame on the glass is latency the room feels.</summary>
    bool IsLive => false;
}

/// <summary>
/// A web page the engine shows: a frame source the desk can also drive. The pointer, clicks, the
/// wheel, typed text and a few named keys go in; where the pointer is and when it last clicked
/// come out, so every sink can draw them for the room. The app layer provides the browser; the
/// engine and the desk only ever see this.
/// </summary>
public interface IWebSource : IVideoFrameSource
{
    /// <summary>The page's own size in CSS pixels (what it lays itself out for).</summary>
    SKSizeI PageSize { get; }

    /// <summary>Where the desk's pointer is on the page (0–1 of its width and height), or null when it is not over the page.</summary>
    SKPoint? PointerNorm { get; }

    /// <summary>When the page was last clicked — a ripple is drawn for a moment after — or null.</summary>
    DateTime? LastClickUtc { get; }

    /// <summary>The address the page is at now (after redirects) and its title, for the desk.</summary>
    string CurrentUrl { get; }

    string Title { get; }

    /// <summary>The browser zoom, 25–400 %. Applied live.</summary>
    double ZoomPct { get; set; }

    /// <summary>The page's sound, off or on.</summary>
    bool IsMuted { get; set; }

    /// <summary>Frames the page delivered in the last second — a video's rate while it plays, 0 for a still page; what the status line reads.</summary>
    double FrameRate => 0;

    /// <summary>
    /// The Windows output the page's sound should leave by (its friendly name), "" for the
    /// machine's default. Steered through the page's own output picker where the browser allows;
    /// <see cref="AudioRouteNote"/> says what happened.
    /// </summary>
    string AudioDevice
    {
        get => "";
        set { }
    }

    /// <summary>"routed to HDMI 3", or why the page could not be — the Audio page's line; "" when nothing was asked.</summary>
    string AudioRouteNote => "";

    void PointerMove(float nx, float ny);
    void PointerDown(float nx, float ny);
    void PointerUp(float nx, float ny);
    void PointerLeave();

    /// <summary>A wheel step over the page: positive lines scroll up (Windows' sign), negative down; horizontal for a sideways wheel.</summary>
    void Wheel(float nx, float ny, float deltaLines, bool horizontal);

    /// <summary>Text for the field that has the page's focus.</summary>
    void TypeText(string text);

    /// <summary>A key chord as <see cref="Services.WebKeys"/> reads it: "Enter", "ArrowRight", "k", "Shift+N", "Ctrl+Shift+F5".</summary>
    void PressKey(string key);

    /// <summary>A line of script run in the page — a service's own player driven directly (YouTube's play, seek, mute).</summary>
    void RunScript(string script);

    /// <summary>The same, with the script's result as the browser's JSON text ("" when nothing came back) — how the page's player is read.</summary>
    Task<string> RunScriptAsync(string script)
    {
        RunScript(script);
        return Task.FromResult("");
    }

    /// <summary>
    /// The style the page wears: the service's furniture taken off (CLEAN), or "" for the page as
    /// the site drew it. Put into every document the page loads, before the page's own scripts
    /// run, so a player that rebuilds its controls never gets to draw them.
    /// </summary>
    string CleanCss { get; set; }

    void Navigate(string url);
    void GoBack();
    void GoForward();
    void Reload();
}

/// <summary>See <see cref="VideoService"/> — the web page side's availability note.</summary>
public static class WebInput
{
    /// <summary>Availability text when pages cannot be shown (no WebView2 runtime, not Windows); empty = fine.</summary>
    public static volatile string AvailabilityNote = "";
}

/// <summary>
/// A deck — a PDF presentation — as an engine input: the current page is the frame every sink
/// draws, and the click-through turns the pages. The app layer renders the pages; the engine,
/// the desk, cues and the remotes only ever see this.
/// </summary>
public interface IDeckSource : IVideoFrameSource
{
    /// <summary>The deck's file.</summary>
    string Path { get; }

    /// <summary>How many pages the deck has; 0 while it is opening or when it could not be opened.</summary>
    int PageCount { get; }

    /// <summary>The page on show, 1-based; 0 while nothing is loaded.</summary>
    int Page { get; }

    /// <summary>The page's own shape (its width and height in points) — what the fit keeps.</summary>
    SKSize PageShape { get; }

    bool AtStart => Page <= 1;

    bool AtEnd => PageCount > 0 && Page >= PageCount;

    /// <summary>Turns to a page (clamped to the deck); false when the page did not change.</summary>
    bool GoTo(int page);
}

/// <summary>See <see cref="VideoService"/> — the deck side's availability note.</summary>
public static class DeckInput
{
    /// <summary>Availability text when decks cannot be shown (the PDF renderer missing); empty = fine.</summary>
    public static volatile string AvailabilityNote = "";
}

/// <summary>
/// The newest frame of a live source: published from any thread, drawn from any render thread.
/// A replaced frame is kept for a moment before it is disposed, so a draw in flight never touches
/// a dead image — the one discipline every source keeps (<see cref="RetiredFrames"/>).
/// </summary>
public sealed class FrameSlot : IDisposable
{
    private readonly object _gate = new();
    private SKImage? _latest;
    private double _latestClock = -1;
    private long _publishedUtcTicks;
    private long _publishedClockBits = BitConverter.DoubleToInt64Bits(-1);

    /// <summary>Takes ownership of <paramref name="image"/>, stamped with the show clock now; the previous frame retires.</summary>
    public void Publish(SKImage image)
    {
        var clock = Services.ShowClock.Seconds;
        lock (_gate)
        {
            Retire(_latest);
            _latest = image;
            _latestClock = clock;
        }
        Interlocked.Exchange(ref _publishedUtcTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _publishedClockBits, BitConverter.DoubleToInt64Bits(clock));
    }

    /// <summary>The show clock the newest frame arrived at (seconds; -1 before one): a source's <see cref="IVideoFrameSource.FrameClock"/>.</summary>
    public double PublishedClock => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _publishedClockBits));

    public bool HasFrame
    {
        get
        {
            lock (_gate)
            {
                return _latest is not null;
            }
        }
    }

    public SKSizeI? Size
    {
        get
        {
            lock (_gate)
            {
                return _latest is { } img ? new SKSizeI(img.Width, img.Height) : null;
            }
        }
    }

    /// <summary>When the newest frame arrived (UTC ticks; 0 = never).</summary>
    public long PublishedUtcTicks => Interlocked.Read(ref _publishedUtcTicks);

    public bool Draw(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop) => DrawTimed(canvas, dest, paint, in crop).Drew;

    /// <summary>Draws the newest frame and reports the clock it arrived at — the drawn frame's own, taken with it.</summary>
    public DrawnFrame DrawTimed(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop)
    {
        SKImage? image;
        double clock;
        lock (_gate)
        {
            image = _latest;
            clock = _latestClock;
        }
        if (image is null) return DrawnFrame.Nothing;
        if (crop.Any)
        {
            canvas.DrawImage(image, crop.SourceRect(new SKSizeI(image.Width, image.Height)), dest, Rendering.DrawUtil.Smooth, paint);
        }
        else
        {
            canvas.DrawImage(image, dest, Rendering.DrawUtil.Smooth, paint);
        }
        return new DrawnFrame(true, clock);
    }

    /// <summary>Lets the newest frame go — a source nobody draws holds no picture; the next publish fills it again.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Retire(_latest);
            _latest = null;
            _latestClock = -1;
        }
    }

    public void Dispose() => Clear();

    private static void Retire(SKImage? image) => RetiredFrames.Retire(image);
}

/// <summary>Canonical mount keys — the same "ndi:"/"cap:"/"web:" scheme the operator input labels use.</summary>
public static class InputKeys
{
    public static string Video(string path) => path.Length == 0 ? "" : "vid:" + path;
    public static string Capture(string device) => device.Length == 0 ? "" : "cap:" + device;
    public static string Ndi(string source) => source.Length == 0 ? "" : "ndi:" + source;

    /// <summary>A page's key is its normalised address, so "example.com" and "https://example.com" share one browser.</summary>
    public static string Web(string url)
    {
        var normalized = Services.WebAddress.Normalize(url);
        return normalized.Length == 0 ? "" : "web:" + normalized;
    }

    /// <summary>A deck's key is its file: the same deck on two screens is one deck, on one page.</summary>
    public static string Deck(string path) => string.IsNullOrWhiteSpace(path) ? "" : "deck:" + path.Trim();

    /// <summary>
    /// This machine's own arcade — the game its loop draws — as an input: one picture whatever
    /// asks for it, so a pattern, a layer, the PiP and a wall tile share the frames the loop
    /// already drew, with no NDI, no network and no decode between the game and the wall.
    /// </summary>
    public const string ArcadeKey = "arcade:local";

    public static string Arcade() => ArcadeKey;
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
