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
/// a dead image — the same discipline the NDI receiver keeps.
/// </summary>
public sealed class FrameSlot : IDisposable
{
    private static readonly object RetiredGate = new();
    private static readonly List<(SKImage Image, DateTime RetiredUtc)> Retired = new();
    public static readonly TimeSpan RetireHold = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private SKImage? _latest;
    private long _publishedUtcTicks;

    /// <summary>Takes ownership of <paramref name="image"/>; the previous frame retires.</summary>
    public void Publish(SKImage image)
    {
        lock (_gate)
        {
            Retire(_latest);
            _latest = image;
        }
        Interlocked.Exchange(ref _publishedUtcTicks, DateTime.UtcNow.Ticks);
    }

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

    public bool Draw(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop)
    {
        SKImage? image;
        lock (_gate)
        {
            image = _latest;
        }
        if (image is null) return false;
        if (crop.Any)
        {
            canvas.DrawImage(image, crop.SourceRect(new SKSizeI(image.Width, image.Height)), dest, Rendering.DrawUtil.Smooth, paint);
        }
        else
        {
            canvas.DrawImage(image, dest, Rendering.DrawUtil.Smooth, paint);
        }
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Retire(_latest);
            _latest = null;
        }
    }

    private static void Retire(SKImage? image)
    {
        lock (RetiredGate)
        {
            if (image is not null) Retired.Add((image, DateTime.UtcNow));
            var cutoff = DateTime.UtcNow - RetireHold;
            for (var i = Retired.Count - 1; i >= 0; i--)
            {
                if (Retired[i].RetiredUtc < cutoff)
                {
                    Retired[i].Image.Dispose();
                    Retired.RemoveAt(i);
                }
            }
        }
    }
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
