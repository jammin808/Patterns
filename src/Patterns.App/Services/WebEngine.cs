using Patterns.Core.Media;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Hosts one browser per web page the show references, published on the <see cref="InputBus"/>
/// like the NDI receivers: a page mounts when a pattern, a layer or the sandboxed preview wants
/// it, retires briefly for a crossfade when nothing does, and reopens when its viewport changes.
/// The same address on three screens costs one browser. Windows with the WebView2 runtime only;
/// elsewhere the placeholder card says so.
/// </summary>
public sealed class WebEngine : IDisposable
{
    /// <summary>Simultaneous pages — each is a browser process family.</summary>
    public const int MaxPages = 4;

    private sealed record Page(IWebSource Source, string Format);

    private readonly string _userDataFolder;
    private readonly Dictionary<string, Page> _pages = new();
    private readonly List<(string Key, IWebSource Source, DateTime RetiredUtc)> _retired = new();

    public WebEngine(string baseDirectory)
    {
        _userDataFolder = Path.Combine(baseDirectory, "webview2");
    }

    /// <summary>Tests (and any other browser) stand in for WebView2 here: a wanted page → a source, or null to skip it.</summary>
    public Func<MediaLocator.WantedInput, IWebSource?>? SourceFactory { get; set; }

    /// <summary>Non-empty when more pages are wanted than the cap allows.</summary>
    public string LimitNote { get; private set; } = "";

    /// <summary>Mounted keys with a short status each — the Media page's active-inputs line.</summary>
    public IReadOnlyList<(string Key, string Status)> MountStatuses
        => _pages.Select(kv => (kv.Key, kv.Value.Source.StatusText)).ToList();

    public int PageCount => _pages.Count;

    /// <summary>The mounted page for a key, or null.</summary>
    public IWebSource? For(string key) => _pages.TryGetValue(key, out var page) ? page.Source : null;

    /// <summary>A page's viewport from its wanted Format: "1280x720" → (1280, 720); anything else is 1080p.</summary>
    public static (int Width, int Height) ParseSize(string format)
    {
        var parts = (format ?? "").Split('x', '×');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w >= 320 && h >= 240)
        {
            return (Math.Min(w, 7680), Math.Min(h, 4320));
        }
        return (1920, 1080);
    }

    /// <summary>Also called from the app's 1 s poll so a retired page never lingers.</summary>
    public void SweepRetired()
    {
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            if (DateTime.UtcNow - _retired[i].RetiredUtc <= TimeSpan.FromSeconds(4)) continue;
            InputBus.ClearPreviousIf(_retired[i].Key, _retired[i].Source);
            Dispose(_retired[i].Source);
            _retired.RemoveAt(i);
        }
    }

    /// <summary>Reconciles the page pool with the program (and sandbox) snapshot (UI thread).</summary>
    public void Reconcile(ShowSnapshot snap, ShowSnapshot? sandbox = null)
    {
        SweepRetired();

        var wanted = new List<MediaLocator.WantedInput>();
        var seen = new HashSet<string>();
        foreach (var w in MediaLocator.FindWantedInputs(snap))
        {
            if (w.Kind == MediaLocator.WantedKind.Web && seen.Add(w.Key)) wanted.Add(w);
        }
        if (sandbox is not null)
        {
            foreach (var w in MediaLocator.FindWantedInputs(sandbox))
            {
                if (w.Kind == MediaLocator.WantedKind.Web && seen.Add(w.Key)) wanted.Add(w);
            }
        }

        // A page nobody wants any more, or one whose viewport changed, retires — kept briefly so a
        // crossfade fades out real frames — and a new viewport reopens below.
        foreach (var key in _pages.Keys.ToList())
        {
            var want = wanted.FirstOrDefault(w => w.Key == key);
            if (want is null || want.Format != _pages[key].Format) Retire(key);
        }

        if (wanted.Count == 0)
        {
            LimitNote = "";
            return;
        }

        if (SourceFactory is null && !Supported(out var note))
        {
            WebInput.AvailabilityNote = note;
            return;
        }
        WebInput.AvailabilityNote = "";

        var over = 0;
        foreach (var w in wanted)
        {
            if (_pages.TryGetValue(w.Key, out var page))
            {
                // Zoom and sound apply live — the page never reloads for them.
                page.Source.ZoomPct = w.Zoom;
                page.Source.IsMuted = w.Mute;
                continue;
            }
            if (_pages.Count >= MaxPages)
            {
                over++;
                continue;
            }
            try
            {
                var source = SourceFactory is { } open ? open(w) : Open(w);
                if (source is null) continue;
                source.ZoomPct = w.Zoom;
                source.IsMuted = w.Mute;
                _pages[w.Key] = new Page(source, w.Format);
                InputBus.Mount(w.Key, source);
            }
            catch (Exception ex)
            {
                Log.Error($"Web page open failed for '{w.Target}'.", ex);
                WebInput.AvailabilityNote = $"Could not open the page: {ex.Message}";
            }
        }
        LimitNote = over > 0
            ? $"Page limit: {MaxPages} web pages at once — {over} page{(over == 1 ? "" : "s")} waiting."
            : "";
    }

    private void Retire(string key)
    {
        var page = _pages[key];
        _pages.Remove(key);
        InputBus.Unmount(key);
        InputBus.SetPrevious(key, page.Source);
        _retired.Add((key, page.Source, DateTime.UtcNow));
    }

    private static bool Supported(out string note)
    {
        if (!OperatingSystem.IsWindows())
        {
            note = "Web pages inside the engine need Windows (WebView2).";
            return false;
        }
        return WebFrameSource.Probe(out note);
    }

    private IWebSource? Open(MediaLocator.WantedInput w)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return WebFrameSource.Create(w, _userDataFolder);
    }

    private static void Dispose(IWebSource source)
    {
        try
        {
            (source as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Web page close issue.", ex);
        }
    }

    public void Dispose()
    {
        foreach (var (key, page) in _pages)
        {
            InputBus.Unmount(key);
            Dispose(page.Source);
        }
        _pages.Clear();
        foreach (var (key, source, _) in _retired)
        {
            InputBus.SetPrevious(key, null);
            Dispose(source);
        }
        _retired.Clear();
    }
}
