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
    public static readonly string[] SectionNames = { "All", "Patterns", "Images", "Videos", "Audio", "Particles", "Fractals", "Presets", "Brand kits" };

    /// <summary>Every tile as the show and the store have them now: the factory table, the show's media, the saved presets, the brand kits.</summary>
    public static List<PresetItem> Build(ShowState state, SettingsStore store, Desk desk)
    {
        var tiles = new List<PresetItem>();
        foreach (var b in BuiltInPresets.All)
        {
            var preset = b;
            tiles.Add(new PresetItem
            {
                Id = $"builtin:{preset.Category}:{preset.Name}",
                Section = preset.Section,
                Category = preset.Category,
                Name = preset.Name,
                Apply = () => desk.Edit(() => preset.Apply(desk.ActivePattern())),
                ThumbConfig = over =>
                {
                    var config = JsonUtil.ClonePattern(over.Pattern);
                    preset.Apply(config);
                    return config;
                },
            });
        }

        foreach (var media in state.MediaLibrary.ToList())
        {
            var entry = media;
            var path = entry.Path;
            var kind = entry.Kind == LibraryMediaKind.Unknown ? MediaLibraryEntry.KindOf(path, entry.IsVideo) : entry.Kind;
            var (section, category) = kind switch
            {
                LibraryMediaKind.Video => ("Videos", "My videos"),
                LibraryMediaKind.Audio => ("Audio", "My audio"),
                LibraryMediaKind.Deck => ("Decks", "My decks"),
                _ => ("Images", "My images"),
            };
            tiles.Add(new PresetItem
            {
                Id = "media:" + entry.Id,
                Section = section,
                Category = category,
                Name = entry.DisplayName,
                Apply = () => desk.Edit(() => ApplyMedia(desk.ActivePattern(), path, kind)),
                ThumbConfig = over =>
                {
                    var config = JsonUtil.ClonePattern(over.Pattern);
                    ApplyMedia(config, path, kind);
                    return config;
                },
                Remove = () => state.MediaLibrary.Remove(entry),
            });
        }

        foreach (var (name, path) in store.ListPresets())
        {
            var p = path;
            tiles.Add(new PresetItem
            {
                Id = "preset:" + p,
                Section = "Presets",
                Category = "My presets",
                Name = name,
                Apply = () =>
                {
                    var cfg = store.LoadPreset(p);
                    if (cfg is not null) desk.Edit(() => ModelCopier.Copy(cfg, desk.ActivePattern()));
                },
                ThumbConfig = _ => store.LoadPreset(p),
            });
        }

        foreach (var (name, path) in store.ListBrandKits())
        {
            var p = path;
            var kit = store.LoadBrandKit(p);
            if (kit is null) continue;
            var kitName = name;
            tiles.Add(new PresetItem
            {
                Id = "brand:" + p,
                Section = "Brand kits",
                Category = "Brand kit",
                Name = kitName,
                Apply = () =>
                {
                    var fresh = store.LoadBrandKit(p);
                    if (fresh is null) return;
                    desk.Edit(() => ModelCopier.Copy(fresh, state.Brand));
                    desk.Say($"Brand kit '{kitName}' applied.");
                },
                Swatch = new[] { kit.PrimaryColor, kit.SecondaryColor, kit.AccentColor, kit.BackgroundColor, kit.TextColor },
            });
        }
        return tiles;
    }

    /// <summary>A media tile on a pattern: an image shows; a deck opens at its first page; a video or an audio file plays through the decoder.</summary>
    public static void ApplyMedia(PatternConfig target, string path, LibraryMediaKind kind)
    {
        target.Kind = PatternKind.Media;
        if (kind == LibraryMediaKind.Image)
        {
            target.Media.Source = MediaSource.Image;
            target.Media.ImagePath = path;
        }
        else if (kind == LibraryMediaKind.Deck)
        {
            target.Media.Source = MediaSource.Deck;
            target.Media.DeckPath = path;
        }
        else
        {
            target.Media.Source = MediaSource.Video;
            target.Media.VideoPath = path;
        }
    }

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

    /// <summary>Brings a page's collection to <paramref name="wanted"/> in place: what stays keeps its container.</summary>
    public static void Sync<T>(ObservableCollection<T> page, IReadOnlyList<T> wanted) where T : class
    {
        var want = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);
        for (var i = page.Count - 1; i >= 0; i--)
        {
            if (!want.Contains(page[i])) page.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var item = wanted[i];
            var at = page.IndexOf(item);
            if (at == i) continue;
            if (at < 0) page.Insert(i, item);
            else page.Move(at, i);
        }
    }

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
