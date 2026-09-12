using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.Core.Rendering;

/// <summary>
/// What a multiview's tiles say, worked out once per snapshot rather than once per tile per
/// frame: the tile list (the show's, or the default wall), and for each tile its badges, its
/// caption and whether it is on air or the preview. Pure over the snapshot and the preview
/// snapshot, so the key is their two versions and the wall's own options object. A wall at
/// 60 Hz used to rebuild every string and every list of every tile on every frame.
/// Owned by one sink and touched on its render thread only.
/// </summary>
public sealed class MultiviewWords
{
    /// <summary>One tile's words: what it draws, what it says, and whether it is live or the preview.</summary>
    public sealed record TileWords(MultiviewTileConfig Tile, List<TileBadge> Badges, string Name, string Kind, bool OnAir, bool Preview);

    private long _version = -1;
    private long _previewVersion = -2;
    private MultiviewOptions? _opts;
    private List<TileWords> _tiles = new();

    /// <summary>How many times the words were worked out (tests).</summary>
    public int Builds { get; private set; }

    /// <summary>The wall's tiles and their words for this snapshot — the same list until the snapshot, the preview or the wall moves.</summary>
    public IReadOnlyList<TileWords> For(ShowSnapshot snap, MultiviewOptions opts)
    {
        var preview = snap.PreviewSource?.Invoke();
        var previewVersion = preview?.Version ?? -1;
        if (snap.Version == _version && previewVersion == _previewVersion && ReferenceEquals(opts, _opts)) return _tiles;

        var tiles = opts.Tiles.Count > 0 ? opts.Tiles.ToList() : Multiviews.DefaultTiles(snap.State);
        var words = new List<TileWords>(tiles.Count);
        foreach (var tile in tiles)
        {
            words.Add(new TileWords(
                tile,
                MultiviewTally.Badges(snap, tile),
                MultiviewTally.Name(snap, tile),
                MultiviewTally.Kind(snap, tile),
                MultiviewTally.IsOnAir(snap, tile),
                tile.Source == MultiviewSource.Preview && MultiviewTally.HasPreview(snap)));
        }
        _tiles = words;
        _version = snap.Version;
        _previewVersion = previewVersion;
        _opts = opts;
        Builds++;
        return _tiles;
    }
}
