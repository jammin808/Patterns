using Patterns.Core.Media;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// Hosts one deck per PDF the show references, published on the <see cref="InputBus"/> like
/// the web pages: a deck mounts when a pattern or the sandboxed preview wants it — opening at
/// its start page — stays while anything wants it, and retires briefly for a crossfade when
/// nothing does. The same deck on two screens is one deck, on one page.
/// </summary>
public sealed class DeckEngine : IDisposable
{
    private sealed record Mounted(IDeckSource Source);

    private readonly Dictionary<string, Mounted> _decks = new();
    private readonly List<(string Key, IDeckSource Source, DateTime RetiredUtc)> _retired = new();

    /// <summary>Tests stand in for the PDF renderer here: a wanted deck → a source, or null to skip it.</summary>
    public Func<MediaLocator.WantedInput, SKSizeI, IDeckSource?>? SourceFactory { get; set; }

    public int DeckCount => _decks.Count;

    /// <summary>The mounted deck for a key, or null.</summary>
    public IDeckSource? For(string key) => _decks.TryGetValue(key, out var deck) ? deck.Source : null;

    /// <summary>Mounted keys with a short status each — the Media page's active-inputs line.</summary>
    public IReadOnlyList<(string Key, string Status)> MountStatuses
        => _decks.Select(kv => (kv.Key, kv.Value.Source.StatusText)).ToList();

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
            if (_decks.ContainsKey(w.Key)) continue;
            try
            {
                var source = SourceFactory is { } open ? open(w, ceiling) : Open(w, ceiling);
                if (source is null) continue;
                _decks[w.Key] = new Mounted(source);
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

    private void Retire(string key)
    {
        var deck = _decks[key];
        _decks.Remove(key);
        InputBus.Unmount(key);
        InputBus.SetPrevious(key, deck.Source);
        _retired.Add((key, deck.Source, DateTime.UtcNow));
    }

    private static IDeckSource? Open(MediaLocator.WantedInput w, SKSizeI ceiling)
    {
        var start = int.TryParse(w.Format, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var page) ? page : 1;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return PdfDeckSource.Open(w.Target, start, ceiling);
        }
        DeckInput.AvailabilityNote = "Decks need Windows, Linux or macOS (the PDF renderer).";
        return null;
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
