using Patterns.Core.Media;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// Hosts one deck per file the show references, published on the <see cref="InputBus"/> like
/// the web pages: a deck mounts when a pattern or the sandboxed preview wants it — opening at
/// its start page — stays while anything wants it, and retires briefly for a crossfade when
/// nothing does. The same deck on two screens is one deck, on one page. A PowerPoint, Keynote
/// or Impress file mounts as a pending deck while LibreOffice converts it (once; the PDF is
/// kept) and becomes the PDF the moment the conversion lands.
/// </summary>
public sealed class DeckEngine : IDisposable
{
    private sealed record Mounted(IDeckSource Source, Task<DeckConverter.Result>? Conversion, int StartPage);

    private readonly Dictionary<string, Mounted> _decks = new();
    private readonly List<(string Key, IDeckSource Source, DateTime RetiredUtc)> _retired = new();
    private bool _disposed;

    public DeckEngine(string baseDirectory)
    {
        Converter = new DeckConverter(baseDirectory);
    }

    /// <summary>LibreOffice, for the decks that are not PDFs yet.</summary>
    public DeckConverter Converter { get; }

    /// <summary>Tests stand in for the PDF renderer here: a wanted deck → a source, or null to skip it.</summary>
    public Func<MediaLocator.WantedInput, SKSizeI, IDeckSource?>? SourceFactory { get; set; }

    /// <summary>Raised from a background thread when a conversion lands or fails: the app reconciles on the UI thread so the PDF takes the pending deck's place.</summary>
    public Action? Changed { get; set; }

    public int DeckCount => _decks.Count;

    public bool IsDisposed => _disposed;

    /// <summary>The mounted deck for a key, or null.</summary>
    public IDeckSource? For(string key) => _decks.TryGetValue(key, out var deck) ? deck.Source : null;

    /// <summary>Mounted keys with a short status each — the Media page's active-inputs line.</summary>
    public IReadOnlyList<(string Key, string Status)> MountStatuses
        => _decks.Select(kv => (kv.Key, kv.Value.Source.StatusText)).ToList();

    /// <summary>True while a mounted deck is still being converted.</summary>
    public bool Converting => _decks.Values.Any(m => m.Conversion is { IsCompleted: false });

    /// <summary>Completes when every conversion running now has landed or failed (tests wait on it; the swap itself happens on the next reconcile).</summary>
    public Task WhenConversionsSettled()
        => Task.WhenAll(_decks.Values.Where(m => m.Conversion is { IsCompleted: false }).Select(m => (Task)m.Conversion!));

    /// <summary>Also called from the app's 1 s poll so a retired deck never lingers.</summary>
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

    /// <summary>Reconciles the decks with the program (and sandbox) snapshot (UI thread).</summary>
    public void Reconcile(ShowSnapshot snap, ShowSnapshot? sandbox = null)
    {
        if (_disposed) return;
        SweepRetired();

        var wanted = new List<MediaLocator.WantedInput>();
        var seen = new HashSet<string>();
        foreach (var w in MediaLocator.FindWantedInputs(snap))
        {
            if (w.Kind == MediaLocator.WantedKind.Deck && seen.Add(w.Key)) wanted.Add(w);
        }
        if (sandbox is not null)
        {
            foreach (var w in MediaLocator.FindWantedInputs(sandbox))
            {
                if (w.Kind == MediaLocator.WantedKind.Deck && seen.Add(w.Key)) wanted.Add(w);
            }
        }

        foreach (var key in _decks.Keys.ToList())
        {
            if (wanted.All(w => w.Key != key)) Retire(key);
        }
        if (wanted.Count == 0) return;

        var ceiling = Decks.RasterCeiling(snap.Rig);
        foreach (var w in wanted)
        {
            if (_decks.TryGetValue(w.Key, out var have))
            {
                if (have.Conversion is { IsCompleted: true } done) Land(w, have, done.Result, ceiling);
                continue;
            }
            try
            {
                var start = StartPage(w);
                Task<DeckConverter.Result>? conversion = null;
                var source = SourceFactory is { } open ? open(w, ceiling) : Open(w, start, ceiling, out conversion);
                if (source is null) continue;
                _decks[w.Key] = new Mounted(source, conversion, start);
                InputBus.Mount(w.Key, source);
                DeckInput.AvailabilityNote = "";
            }
            catch (Exception ex)
            {
                Log.Error($"Deck open failed for '{w.Target}'.", ex);
                DeckInput.AvailabilityNote = $"Could not open the deck: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// RELOAD on the desk: the file is read again — after an edit, or to convert it again — and
    /// the cached PDF for a converted deck is dropped. The next reconcile mounts it afresh.
    /// </summary>
    public void Reload(string path)
    {
        if (path.Length == 0) return;
        if (DeckConversion.NeedsConversion(path)) Converter.Forget(path);
        var key = InputKeys.Deck(path);
        if (_decks.ContainsKey(key)) Retire(key);
    }

    private void Retire(string key)
    {
        var deck = _decks[key];
        _decks.Remove(key);
        InputBus.Unmount(key);
        InputBus.SetPrevious(key, deck.Source);
        _retired.Add((key, deck.Source, DateTime.UtcNow));
    }

    private static int StartPage(MediaLocator.WantedInput w)
        => int.TryParse(w.Format, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var page) ? page : 1;

    private IDeckSource? Open(MediaLocator.WantedInput w, int start, SKSizeI ceiling, out Task<DeckConverter.Result>? conversion)
    {
        conversion = null;
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            DeckInput.AvailabilityNote = "Decks need Windows, Linux or macOS (the PDF renderer).";
            return null;
        }
        if (!DeckConversion.NeedsConversion(w.Target))
        {
            return PdfDeckSource.Open(w.Target, start, ceiling);
        }
        // A PowerPoint, Keynote or Impress file: the PDF LibreOffice made of it, or a pending deck until it has.
        if (Converter.Cached(w.Target) is { } cached)
        {
            return PdfDeckSource.Open(w.Target, cached, start, ceiling);
        }
        var name = System.IO.Path.GetFileName(w.Target);
        if (!File.Exists(w.Target))
        {
            var missing = new PendingDeckSource(w.Target, $"Deck not found: {name}");
            missing.Fail(missing.StatusText);
            return missing;
        }
        var pending = new PendingDeckSource(w.Target, $"Converting {name} with LibreOffice Impress…");
        conversion = Converter.ConvertAsync(w.Target);
        conversion.ContinueWith(_ => Changed?.Invoke(), TaskScheduler.Default);
        return pending;
    }

    /// <summary>A finished conversion: the PDF takes the pending deck's place in the same slot — the card becomes the page — or the card reads why not.</summary>
    private void Land(MediaLocator.WantedInput w, Mounted have, DeckConverter.Result result, SKSizeI ceiling)
    {
        if (have.Source is not PendingDeckSource pending)
        {
            _decks[w.Key] = have with { Conversion = null };
            return;
        }
        if (!result.Ok)
        {
            if (!pending.Failed) pending.Fail(result.Message);
            _decks[w.Key] = have with { Conversion = null };
            return;
        }
        var pdf = PdfDeckSource.Open(w.Target, result.PdfPath, have.StartPage, ceiling);
        _decks[w.Key] = new Mounted(pdf, null, have.StartPage);
        InputBus.Mount(w.Key, pdf);
    }

    private static void Dispose(IDeckSource source)
    {
        try
        {
            (source as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Deck close issue.", ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var (key, deck) in _decks)
        {
            InputBus.Unmount(key);
            Dispose(deck.Source);
        }
        _decks.Clear();
        foreach (var (key, source, _) in _retired)
        {
            InputBus.SetPrevious(key, null);
            Dispose(source);
        }
        _retired.Clear();
    }
}
