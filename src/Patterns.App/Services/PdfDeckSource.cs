using Patterns.Core.Media;
using Patterns.Core.Services;
using PDFtoImage;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// A PDF deck as an engine input: the page on show is rendered by PDFium (the renderer Chrome
/// uses) at the rig's raster — as large as the sharpest screen needs, at the page's own shape —
/// and published through a <see cref="FrameSlot"/> for every sink to draw. The pages either side
/// are rendered ahead in the background, so a turn is instant; a jump renders on demand.
/// PDFium is driven through one gate, never from two threads at once. The renderer runs on
/// Windows, Linux and macOS; <see cref="DeckEngine"/> opens a deck only there.
/// </summary>
#pragma warning disable CA1416 // the engine guards the platforms before opening a deck
public sealed class PdfDeckSource : IDeckSource, IDisposable
{
    /// <summary>Pages kept rendered around the page on show — the next two and the last two, so a turn either way is instant.</summary>
    public const int Window = 2;

    private static readonly object Gate = new();

    private readonly FrameSlot _slot = new();
    private readonly Dictionary<int, SKImage> _pages = new();
    private readonly object _cache = new();
    private readonly SKSizeI _raster;
    private byte[]? _bytes;
    private volatile int _page;
    private volatile string _status;
    private volatile bool _disposed;

    private PdfDeckSource(string path, int pageCount, SKSize shape, SKSizeI raster, byte[]? bytes, string status)
    {
        Path = path;
        PageCount = pageCount;
        PageShape = shape;
        _raster = raster;
        _bytes = bytes;
        _status = status;
    }

    /// <summary>
    /// Opens a deck at a page; a file that is missing or not a PDF gives a source with no pages
    /// whose status says why, so the placeholder card reads the reason and nothing throws.
    /// </summary>
    public static PdfDeckSource Open(string path, int startPage, SKSizeI ceiling)
    {
        var name = System.IO.Path.GetFileName(path);
        if (!File.Exists(path))
        {
            return new PdfDeckSource(path, 0, new SKSize(16, 9), ceiling, null, $"PDF not found: {name}");
        }
        try
        {
            var bytes = File.ReadAllBytes(path);
            int count;
            SKSize shape;
            lock (Gate)
            {
                count = Conversion.GetPageCount(bytes);
                if (count > 0)
                {
                    var points = Conversion.GetPageSize(bytes, new Index(0));
                    shape = new SKSize(points.Width, points.Height);
                }
                else
                {
                    shape = new SKSize(16, 9);
                }
            }
            if (count <= 0)
            {
                return new PdfDeckSource(path, 0, shape, ceiling, null, $"{name} has no pages.");
            }
            var source = new PdfDeckSource(path, count, shape, Decks.FitInto(shape, ceiling), bytes, "");
            source.GoTo(Math.Clamp(startPage, 1, count));
            return source;
        }
        catch (Exception ex)
        {
            Log.Warn($"Deck could not be opened: {path}", ex);
            return new PdfDeckSource(path, 0, new SKSize(16, 9), ceiling, null, $"Could not open {name}: {ex.Message}");
        }
    }

    public string Path { get; }

    public int PageCount { get; }

    public int Page => _page;

    public SKSize PageShape { get; }

    /// <summary>The raster every page is rendered at — the page's shape fitted into the rig's ceiling.</summary>
    public SKSizeI Raster => _raster;

    public bool GoTo(int page)
    {
        if (_disposed || PageCount == 0 || _bytes is null) return false;
        var target = Math.Clamp(page, 1, PageCount);
        if (target == _page) return false;
        var image = Rendered(target);
        if (image is null) return false;
        _page = target;
        _slot.Publish(image);
        _ = Task.Run(() => RenderAround(target));
        return true;
    }

    // ---- the frame source ----------------------------------------------------------------------

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint) => _slot.Draw(canvas, dest, paint, FrameCrop.None);

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop) => _slot.Draw(canvas, dest, paint, in crop);

    public SKSizeI? FrameSize => _slot.Size;

    public bool IsPlaying => _slot.HasFrame;

    public bool IsEnded => false;

    public double DurationSeconds => 0;

    public string StatusText => _status.Length > 0 ? _status : PageCount == 0 ? "Opening the deck…" : $"Page {_page} / {PageCount}";

    // ---- rendering -----------------------------------------------------------------------------

    /// <summary>The page's image, rendered now when it is not in the cache; the slot takes its own reference, so the cache may drop it later.</summary>
    private SKImage? Rendered(int page)
    {
        lock (_cache)
        {
            if (_pages.TryGetValue(page, out var cached)) return CopyForSlot(cached);
        }
        var fresh = Render(page);
        if (fresh is null) return null;
        lock (_cache)
        {
            if (_pages.TryGetValue(page, out var raced))
            {
                fresh.Dispose();
                return CopyForSlot(raced);
            }
            _pages[page] = fresh;
            return CopyForSlot(fresh);
        }
    }

    /// <summary>The slot disposes what it is given when the next page arrives, so it gets its own handle to the pixels.</summary>
    private static SKImage CopyForSlot(SKImage image)
    {
        using var pixmap = image.PeekPixels();
        if (pixmap is not null)
        {
            var copy = SKImage.FromPixelCopy(pixmap);
            if (copy is not null) return copy;
        }
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return SKImage.FromEncodedData(data);
    }

    private SKImage? Render(int page)
    {
        var bytes = _bytes;
        if (bytes is null || _disposed) return null;
        try
        {
            SKBitmap bitmap;
            lock (Gate)
            {
                bitmap = Conversion.ToImage(bytes, new Index(page - 1), null, new RenderOptions(
                    Width: _raster.Width,
                    Height: _raster.Height,
                    WithAnnotations: true,
                    WithFormFill: true,
                    WithAspectRatio: true,
                    BackgroundColor: SKColors.White));
            }
            using (bitmap)
            {
                bitmap.SetImmutable();
                return SKImage.FromBitmap(bitmap);
            }
        }
        catch (Exception ex)
        {
            _status = $"Page {page} could not be rendered: {ex.Message}";
            Log.Warn($"Deck page {page} render failed: {Path}", ex);
            return null;
        }
    }

    /// <summary>Renders the pages either side of the one on show and lets the rest go.</summary>
    private void RenderAround(int centre)
    {
        if (_disposed) return;
        for (var d = 1; d <= Window; d++)
        {
            foreach (var page in new[] { centre + d, centre - d })
            {
                if (page < 1 || page > PageCount || _disposed) continue;
                bool have;
                lock (_cache) have = _pages.ContainsKey(page);
                if (have) continue;
                var image = Render(page);
                if (image is null) continue;
                lock (_cache)
                {
                    if (!_pages.TryAdd(page, image)) image.Dispose();
                }
            }
        }
        lock (_cache)
        {
            foreach (var far in _pages.Keys.Where(p => Math.Abs(p - centre) > Window).ToList())
            {
                _pages[far].Dispose();
                _pages.Remove(far);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bytes = null;
        lock (_cache)
        {
            foreach (var image in _pages.Values) image.Dispose();
            _pages.Clear();
        }
        _slot.Dispose();
    }
}
#pragma warning restore CA1416
