using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// One tile of the Library as a fact, with no desk in it: what it is, where the page files it,
/// and what it does — the picture it writes into a pattern (<see cref="Picture"/>), or, for a
/// brand kit, the colours it puts on the show (<see cref="Show"/>). Identified by <see cref="Id"/>
/// (never by name — two files of one name in two folders are two tiles); found by name for the
/// wire, which has no ids.
/// </summary>
public sealed record LibraryTile(string Id, string Section, string Category, string Name)
{
    /// <summary>The picture the tile makes, written into a pattern; null for a brand kit.</summary>
    public Action<PatternConfig>? Picture { get; init; }

    /// <summary>A brand kit: the colours onto the show; null for a picture tile.</summary>
    public Action<ShowState>? Show { get; init; }

    /// <summary>The pattern the thumbnail shows, built over the show's state; null for a swatch tile.</summary>
    public Func<ShowState, PatternConfig?>? Thumb { get; init; }

    /// <summary>A brand kit's colours: the thumbnail is bands of them.</summary>
    public IReadOnlyList<string>? Swatch { get; init; }

    /// <summary>Takes the tile out of the library (a media entry, a saved page); null for what cannot be removed here.</summary>
    public Action? Remove { get; init; }

    /// <summary>What the desk says after a tile that is not a picture applied ("Brand kit 'X' applied.").</summary>
    public string Words { get; init; } = "";

    public bool IsPicture => Picture is not null;
}

/// <summary>
/// The Library's catalogue as facts (round 73): every tile the page can show, built from the show
/// and the store — the factory table, the show's media, its saved web pages, the saved presets, the
/// brand kits. The page maps these to its tiles; the action layer finds one by name or id for the
/// wire's LIBRARY verb, so a deck's key and a click on the page are the same press.
/// </summary>
public static class LibraryItems
{
    /// <summary>"Section/name" or "Section:name" on the wire — the exact form when two sections share a name.</summary>
    private static readonly System.Buffers.SearchValues<char> SectionCuts = System.Buffers.SearchValues.Create("/:");

    /// <summary>The section chips, in the order they are shown; "All" first.</summary>
    public static readonly string[] SectionNames = { "All", "Patterns", "Images", "Videos", "Audio", "Decks", "Web", "Particles", "Fractals", "Presets", "Brand kits" };

    /// <summary>
    /// Every tile as the show and the store have them now. <paramref name="swatches"/> false skips
    /// reading each brand kit's file for its colours — the wire's lookup wants names, not pictures.
    /// </summary>
    public static List<LibraryTile> Build(ShowState state, SettingsStore store, bool swatches = true)
    {
        var tiles = new List<LibraryTile>();
        foreach (var b in BuiltInPresets.All)
        {
            var preset = b;
            tiles.Add(new LibraryTile($"builtin:{preset.Category}:{preset.Name}", preset.Section, preset.Category, preset.Name)
            {
                Picture = preset.Apply,
                Thumb = over =>
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
            tiles.Add(new LibraryTile("media:" + entry.Id, section, category, entry.DisplayName)
            {
                Picture = target => ApplyMedia(target, path, kind),
                Thumb = over =>
                {
                    var config = JsonUtil.ClonePattern(over.Pattern);
                    ApplyMedia(config, path, kind);
                    return config;
                },
                Remove = () => state.MediaLibrary.Remove(entry),
            });
        }

        // The show's saved web pages (round 62): a YouTube link, a Vimeo film, a deck of slides, a
        // schedule — every address the Media page remembered, as a tile with the service's colours,
        // grouped by what the address is. The picture is the page on the pattern being edited.
        foreach (var url in state.Web.SavedUrls.ToList())
        {
            var address = url;
            var service = WebPresets.Detect(address);
            tiles.Add(new LibraryTile("web:" + address, "Web", WebCategory(service), WebTitle(address))
            {
                Picture = target => ApplyWeb(target, address),
                Swatch = WebSwatch(service),
                Remove = () => state.Web.SavedUrls.Remove(address),
            });
        }

        foreach (var (name, path) in store.ListPresets())
        {
            var p = path;
            tiles.Add(new LibraryTile("preset:" + p, "Presets", "My presets", name)
            {
                Picture = target =>
                {
                    var cfg = store.LoadPreset(p);
                    if (cfg is not null) ModelCopier.Copy(cfg, target);
                },
                Thumb = _ => store.LoadPreset(p),
            });
        }

        foreach (var (name, path) in store.ListBrandKits())
        {
            var p = path;
            var kitName = name;
            IReadOnlyList<string>? swatch = null;
            if (swatches)
            {
                var kit = store.LoadBrandKit(p);
                if (kit is null) continue;
                swatch = new[] { kit.PrimaryColor, kit.SecondaryColor, kit.AccentColor, kit.BackgroundColor, kit.TextColor };
            }
            tiles.Add(new LibraryTile("brand:" + p, "Brand kits", "Brand kit", kitName)
            {
                Show = show =>
                {
                    var fresh = store.LoadBrandKit(p);
                    if (fresh is not null) ModelCopier.Copy(fresh, show.Brand);
                },
                Swatch = swatch,
                Words = $"Brand kit '{kitName}' applied.",
            });
        }
        return tiles;
    }

    /// <summary>
    /// A tile by its id, else by its name (case-blind, the first in the page's order), else by
    /// "section/name" or "section:name"; null when the library has no such tile. The wire names
    /// tiles as the page lists them; the desk's own press names them by id.
    /// </summary>
    public static LibraryTile? Find(IReadOnlyList<LibraryTile> tiles, string nameOrId)
    {
        var key = (nameOrId ?? "").Trim();
        if (key.Length == 0) return null;
        foreach (var t in tiles)
        {
            if (string.Equals(t.Id, key, StringComparison.Ordinal)) return t;
        }
        foreach (var t in tiles)
        {
            if (string.Equals(t.Name, key, StringComparison.OrdinalIgnoreCase)) return t;
        }
        var cut = key.AsSpan().IndexOfAny(SectionCuts);
        if (cut > 0)
        {
            var section = key[..cut].Trim();
            var name = key[(cut + 1)..].Trim();
            foreach (var t in tiles)
            {
                if (string.Equals(t.Section, section, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            }
        }
        return null;
    }

    /// <summary>A web tile on a pattern: the page, treated as its address says (Auto), on the picture being edited.</summary>
    public static void ApplyWeb(PatternConfig target, string url)
    {
        target.Kind = PatternKind.Media;
        target.Media.Source = MediaSource.Web;
        target.Media.WebUrl = WebAddress.Normalize(url);
        target.Media.WebService = PageServicePick.Auto;
    }

    /// <summary>The Library's group for a saved address: the service it is, or a plain saved page.</summary>
    public static string WebCategory(PageService service) => service switch
    {
        PageService.YouTube => "YouTube",
        PageService.Vimeo => "Vimeo",
        PageService.GoogleSlides => "Google Slides",
        PageService.PowerPoint => "PowerPoint",
        _ => "Saved pages",
    };

    /// <summary>The tile's bands: the service's own colours, so a YouTube link reads as one at a glance.</summary>
    public static IReadOnlyList<string> WebSwatch(PageService service) => service switch
    {
        PageService.YouTube => new[] { "#FF0000", "#282828", "#FFFFFF" },
        PageService.Vimeo => new[] { "#1AB7EA", "#0F1419", "#FFFFFF" },
        PageService.GoogleSlides => new[] { "#F4B400", "#FFFFFF", "#3C4043" },
        PageService.PowerPoint => new[] { "#D24726", "#FFFFFF", "#3C3C3C" },
        _ => new[] { "#3EC1F3", "#101319", "#E6EAF2" },
    };

    /// <summary>The tile's name: the host and the path's tail — "youtube.com · watch?v=…" — never the scheme, never the whole address.</summary>
    public static string WebTitle(string url)
    {
        var host = WebAddress.ShortName(url);
        if (Uri.TryCreate(WebAddress.Normalize(url), UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            var tail = Uri.UnescapeDataString(uri.PathAndQuery.Trim('/'));
            if (tail.Length > 0)
            {
                if (tail.Length > 28) tail = tail[..27] + "…";
                return $"{host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)} · {tail}";
            }
        }
        return host;
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
}
