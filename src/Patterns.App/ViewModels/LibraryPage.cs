using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// One tile in the Library: a factory pattern, a media file, a saved preset or a brand kit.
/// Identified by <see cref="Id"/> (never by name — two files of one name in two folders are
/// two tiles), filed under a <see cref="Section"/>, found by <see cref="SearchKey"/>, drawn from
/// <see cref="ThumbConfig"/> or a <see cref="Swatch"/>.
/// </summary>
public sealed class PresetItem : Observable
{
    private Bitmap? _thumbnail;
    private string? _searchKey;

    public required string Id { get; init; }
    public required string Section { get; init; }
    public required string Category { get; init; }
    public required string Name { get; init; }

    /// <summary>Puts the tile up. Refreshed on every rebuild, so a kept instance does what the tile does now.</summary>
    public required Action Apply { get; set; }

    /// <summary>The pattern the thumbnail shows, built over the show's state; null for a swatch tile.</summary>
    public Func<ShowState, PatternConfig?>? ThumbConfig { get; set; }

    /// <summary>A brand kit's colours: the thumbnail is bands of them.</summary>
    public IReadOnlyList<string>? Swatch { get; init; }

    /// <summary>Takes the tile out of the library (a media entry); null for what cannot be removed here.</summary>
    public Action? Remove { get; set; }

    public bool CanRemove => Remove is not null;

    /// <summary>Lower-case words the search box matches against: the name, the category, the section.</summary>
    public string SearchKey => _searchKey ??= $"{Name} {Category} {Section}".ToLowerInvariant();

    public Bitmap? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }

    private bool _isSelected;

    /// <summary>Lit on the page: the last tile put in the preview (round 67.4).</summary>
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>The same tile with the same face: the instance on the page stays, with its thumbnail.</summary>
    public bool SameFaceAs(PresetItem other)
        => Id == other.Id && Name == other.Name && Section == other.Section && Category == other.Category
           && (Swatch is null ? other.Swatch is null : other.Swatch is not null && Swatch.SequenceEqual(other.Swatch));
}

/// <summary>
/// The Library page's catalogue: every tile the page can show, built from the show and the store,
/// reconciled into the page's collections in place, and handed to the one thumbnail queue. The
/// page used to be cleared and refilled on every change — one file picked rebuilt every tile and
/// the ItemsControl re-mounted the lot — and every rebuild started a thumbnail pass over every tile
/// beside the passes already running. The view model keeps the collections and the bindings.
/// </summary>
public static class LibraryCatalogue
{
    /// <summary>What a tile needs from the desk: the pattern being edited, how to make an edit, where to say what happened.</summary>
    public sealed record Desk(Func<PatternConfig> ActivePattern, Action<Action> Edit, Action<string> Say);

    /// <summary>The section chips, in the order they are shown; "All" first.</summary>
    public static readonly string[] SectionNames = LibraryItems.SectionNames;

    /// <summary>
    /// Every tile as the show and the store have them now — the facts of <see cref="LibraryItems"/>
    /// (round 73: the same list the wire's LIBRARY verb finds a tile in) as the page's tiles: a
    /// picture tile applies its picture to the pattern being edited through the desk's edit; a
    /// brand kit puts its colours on the show and says so.
    /// </summary>
    public static List<PresetItem> Build(ShowState state, SettingsStore store, Desk desk)
    {
        var tiles = new List<PresetItem>();
        foreach (var tile in LibraryItems.Build(state, store))
        {
            var fact = tile;
            tiles.Add(new PresetItem
            {
                Id = fact.Id,
                Section = fact.Section,
                Category = fact.Category,
                Name = fact.Name,
                Apply = fact.Picture is { } picture
                    ? () => desk.Edit(() => picture(desk.ActivePattern()))
                    : () =>
                    {
                        desk.Edit(() => fact.Show?.Invoke(state));
                        if (fact.Words.Length > 0) desk.Say(fact.Words);
                    },
                ThumbConfig = fact.Thumb,
                Swatch = fact.Swatch,
                Remove = fact.Remove,
            });
        }
        return tiles;
    }

    /// <summary>A web tile on a pattern: the page, treated as its address says (Auto), on the picture being edited.</summary>
    public static void ApplyWeb(PatternConfig target, string url) => LibraryItems.ApplyWeb(target, url);

    /// <summary>The Library's group for a saved address: the service it is, or a plain saved page.</summary>
    public static string WebCategory(PageService service) => LibraryItems.WebCategory(service);

    /// <summary>The tile's bands: the service's own colours, so a YouTube link reads as one at a glance.</summary>
    public static IReadOnlyList<string> WebSwatch(PageService service) => LibraryItems.WebSwatch(service);

    /// <summary>The tile's name: the host and the path's tail — "youtube.com · watch?v=…" — never the scheme, never the whole address.</summary>
    public static string WebTitle(string url) => LibraryItems.WebTitle(url);

    /// <summary>A media tile on a pattern: an image shows; a deck opens at its first page; a video or an audio file plays through the decoder.</summary>
    public static void ApplyMedia(PatternConfig target, string path, LibraryMediaKind kind) => LibraryItems.ApplyMedia(target, path, kind);

    /// <summary>
    /// Brings the page's tiles to <paramref name="fresh"/>: a tile that is still the same tile keeps
    /// its instance — its thumbnail and its container on the page with it — and takes the fresh
    /// tile's actions; a new or changed tile comes in as it is; a tile gone goes.
    /// </summary>
    public static void Reconcile(List<PresetItem> page, IReadOnlyList<PresetItem> fresh)
    {
        var kept = new Dictionary<string, PresetItem>(StringComparer.Ordinal);
        foreach (var tile in page) kept.TryAdd(tile.Id, tile);
        page.Clear();
        foreach (var tile in fresh)
        {
            if (kept.TryGetValue(tile.Id, out var old) && old.SameFaceAs(tile))
            {
                old.Apply = tile.Apply;
                old.ThumbConfig = tile.ThumbConfig;
                old.Remove = tile.Remove;
                page.Add(old);
            }
            else
            {
                page.Add(tile);
            }
        }
    }

    /// <summary>The chip and the search box together: every search word must appear in the tile's name, category or section.</summary>
    public static List<PresetItem> Filter(IReadOnlyList<PresetItem> all, string section, string search)
    {
        var words = search.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return all
            .Where(i => section == "All" || i.Section == section)
            .Where(i => words.All(w => i.SearchKey.Contains(w, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>"74 tiles", or "12 of 74 · Images · 'logo'".</summary>
    public static string Summary(int shown, int total, string section, string search)
    {
        var where = section == "All" ? "" : $" · {section}";
        var trimmed = search.Trim();
        var searched = trimmed.Length == 0 ? "" : $" · '{trimmed}'";
        return shown == total ? $"{total} tiles" : $"{shown} of {total}{where}{searched}";
    }

    /// <summary>Brings the page's collection to <paramref name="wanted"/> in place: what stays keeps its container.</summary>
    public static void Sync<T>(ObservableCollection<T> page, IReadOnlyList<T> wanted) where T : class => ObservableSync.Sync(page, wanted);

    /// <summary>What the queue draws for a tile: the key names the picture (the pattern on the show's brand), the render makes it.</summary>
    public static ThumbnailQueue.Job JobFor(PresetItem tile, ShowState over, string brandKey)
        => new(tile.Id, () =>
        {
            if (tile.Swatch is { } swatch)
            {
                var caption = tile.Name;
                return new ThumbnailQueue.Work(string.Join(",", swatch) + "|" + caption, () => ThumbnailRenderer.Swatch(swatch, caption));
            }
            if (tile.ThumbConfig?.Invoke(over) is not { } config) return null;
            return new ThumbnailQueue.Work(JsonUtil.SerializeCompact(config) + "|" + brandKey, () => ThumbnailRenderer.Render(over, config));
        }, bitmap => tile.Thumbnail = bitmap);
}
