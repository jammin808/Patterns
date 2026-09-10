using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The show's monitor walls, and what points at them.
///
/// A wall used to be a pattern kind, which put it in the one place it could not usefully be: to
/// configure a multiview you had to make it the picture of the target you were editing, so a
/// screen showing a wall could not also show the show, and two screens showing "the same" wall
/// were two configurations that drifted. Now the walls are the show's, up to two of them, and a
/// pattern with <see cref="PatternKind.Multiview"/> names which one it draws.
///
/// Nothing about the rig changed to make that work: a spare display, an NDI sender and the stream
/// each already own a content target that can carry a pattern of its own, so pointing any of them
/// at a wall is the assignment that already existed, filled in for them.
/// </summary>
public static class Multiviews
{
    /// <summary>How many walls one show may have. Two monitors is what a rack has room for.</summary>
    public const int Max = 2;

    /// <summary>What to call a wall that has not been named: its place in the list.</summary>
    public static string NameOf(ShowState state, MultiviewOptions wall)
    {
        if (wall.Name.Length > 0) return wall.Name;
        var at = state.Multiviews.IndexOf(wall);
        return at >= 0 ? $"Multiview {at + 1}" : "Multiview";
    }

    /// <summary>The wall by id, or null.</summary>
    public static MultiviewOptions? Find(ShowState state, string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var wall in state.Multiviews)
        {
            if (wall.Id == id) return wall;
        }
        return null;
    }

    /// <summary>
    /// The options a multiview pattern actually draws: the wall it names, or — for a show made
    /// before the walls existed, or one whose wall has been deleted — the tiles held on the
    /// pattern itself, exactly as that show has always drawn them.
    /// </summary>
    public static MultiviewOptions For(ShowState state, PatternConfig cfg)
        => Find(state, cfg.MultiviewId) ?? cfg.Multiview;

    /// <summary>
    /// The wall a viewer who did not say which one gets — the remote's /multiview page, and any
    /// other reader with nowhere to ask. The show's first, or, for a show made before the walls
    /// existed, the tiles the programme's own pattern carries.
    /// </summary>
    public static MultiviewOptions Primary(ShowState state, int number = 1)
    {
        var at = Math.Clamp(number, 1, Max) - 1;
        if (at < state.Multiviews.Count) return state.Multiviews[at];
        return state.Multiviews.Count > 0 ? state.Multiviews[0] : state.Pattern.Multiview;
    }

    /// <summary>
    /// The wall a show that has configured nothing gets: the programme and the preview first —
    /// the two the default layout draws large, because they are the two that decide anything —
    /// then every arranged screen, then a clock.
    /// </summary>
    public static List<MultiviewTileConfig> DefaultTiles(ShowState state)
    {
        var tiles = new List<MultiviewTileConfig>
        {
            new() { Source = MultiviewSource.Program },
            new() { Source = MultiviewSource.Preview },
        };
        foreach (var p in state.Output.Placements.OrderBy(p => p.X).ThenBy(p => p.Y))
        {
            tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = p.ScreenId });
        }
        tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Clock });
        return tiles;
    }

    /// <summary>
    /// Adds a wall (up to <see cref="Max"/>) and returns it; the existing one when the show is
    /// full. It arrives filled from the rig as it stands, because a wall with no tiles is a page
    /// of work before it shows anything, and the arrangement it would end up at is the obvious one.
    /// </summary>
    public static MultiviewOptions Add(ShowState state)
    {
        if (state.Multiviews.Count >= Max) return state.Multiviews[^1];
        var wall = new MultiviewOptions { Id = Guid.NewGuid().ToString("N"), Name = $"Multiview {state.Multiviews.Count + 1}" };
        foreach (var tile in DefaultTiles(state)) wall.Tiles.Add(tile);
        state.Multiviews.Add(wall);
        return wall;
    }

    /// <summary>
    /// Takes a wall out of the show and hands every target pointing at it back to the programme,
    /// so removing a wall can never leave a screen drawing a monitor picture nobody can configure.
    /// </summary>
    public static void Remove(ShowState state, MultiviewOptions wall)
    {
        foreach (var target in TargetsShowing(state, wall.Id).ToList()) Clear(state, target);
        state.Multiviews.Remove(wall);
    }

    /// <summary>Every content target whose own pattern is this wall.</summary>
    public static IEnumerable<string> TargetsShowing(ShowState state, string wallId)
    {
        if (wallId.Length == 0) yield break;
        foreach (var a in state.Independent)
        {
            if (a.Pattern.Kind == PatternKind.Multiview && a.Pattern.MultiviewId == wallId
                && ContentTargets.UsesOwnPattern(state, a.ScreenId))
            {
                yield return a.ScreenId;
            }
        }
    }

    /// <summary>True when that target is drawing this wall right now.</summary>
    public static bool IsShowing(ShowState state, string targetId, string wallId)
    {
        if (targetId.Length == 0 || wallId.Length == 0) return false;
        if (!ContentTargets.UsesOwnPattern(state, targetId)) return false;
        foreach (var a in state.Independent)
        {
            if (a.ScreenId == targetId) return a.Pattern.Kind == PatternKind.Multiview && a.Pattern.MultiviewId == wallId;
        }
        return false;
    }

    /// <summary>
    /// Puts a wall on a target: its own pattern on, that pattern set to this wall. For an NDI
    /// sender or the stream, that means its own screen rather than a mirror of something else —
    /// done here rather than left to the operator to find on another page.
    /// </summary>
    public static void Show(ShowState state, string targetId, MultiviewOptions wall)
    {
        if (targetId.Length == 0) return;
        PointFeedAtItsOwnScreen(state, targetId);
        ContentTargets.SetOwnPattern(state, targetId, true);
        var assignment = ContentTargets.EnsureAssignment(state, targetId);
        assignment.Pattern.Kind = PatternKind.Multiview;
        assignment.Pattern.MultiviewId = wall.Id;
        assignment.PinnedByTake = false;
    }

    /// <summary>Hands a target back to the programme — the wall itself is untouched.</summary>
    public static void Clear(ShowState state, string targetId)
    {
        if (targetId.Length == 0) return;
        foreach (var a in state.Independent)
        {
            if (a.ScreenId != targetId) continue;
            if (a.Pattern.Kind == PatternKind.Multiview) a.Pattern.MultiviewId = "";
            break;
        }
        ContentTargets.SetOwnPattern(state, targetId, false);
        if (targetId == StreamConfig.OwnScreenId) state.Stream.SourceScreenId = "";
        else if (targetId.StartsWith("ndi:", StringComparison.Ordinal))
        {
            foreach (var sender in state.Ndi.Senders)
            {
                if (sender.OwnScreenId == targetId) sender.SourceScreenId = "";
            }
        }
    }

    /// <summary>Every pattern the show itself holds: the programme's, and every target's own.</summary>
    public static IEnumerable<PatternConfig> PatternsOf(ShowState state)
    {
        yield return state.Pattern;
        foreach (var a in state.Independent) yield return a.Pattern;
    }

    /// <summary>
    /// v9: a wall that belonged to one pattern becomes one the show holds, and the pattern points
    /// at it. Two targets that carried identical tiles were two configurations an operator had to
    /// keep level by hand; they come out of this pointing at one wall, which is what they meant.
    ///
    /// The layout a migrated wall gets is the grid the old build drew, not the new default: a show
    /// that already exists must open looking the way it was left. Idempotent, and a pattern whose
    /// tiles are empty is left alone — it was drawing the automatic wall and still will.
    /// </summary>
    public static void Migrate(ShowState state)
    {
        // A hand-edited file can carry a wall with no id; nothing can point at one of those.
        foreach (var wall in state.Multiviews)
        {
            if (wall.Id.Length == 0) wall.Id = Guid.NewGuid().ToString("N");
        }
        foreach (var cfg in PatternsOf(state))
        {
            if (cfg.Kind != PatternKind.Multiview || cfg.MultiviewId.Length > 0) continue;
            if (cfg.Multiview.Tiles.Count == 0) continue;

            var key = ShapeOf(cfg.Multiview);
            var wall = state.Multiviews.FirstOrDefault(w => ShapeOf(w) == key);
            if (wall is null)
            {
                if (state.Multiviews.Count >= Max)
                {
                    // A show with more walls than this build allows keeps its first two and the
                    // rest fall back to the tiles they already carry — nothing is thrown away.
                    continue;
                }
                wall = new MultiviewOptions
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = $"Multiview {state.Multiviews.Count + 1}",
                    Layout = MultiviewLayout.Grid,
                    Columns = cfg.Multiview.Columns,
                    ShowLabels = cfg.Multiview.ShowLabels,
                    ShowTally = cfg.Multiview.ShowTally,
                };
                foreach (var tile in cfg.Multiview.Tiles)
                {
                    wall.Tiles.Add(new MultiviewTileConfig
                    {
                        Source = tile.Source,
                        ScreenId = tile.ScreenId,
                        Input = tile.Input,
                        Label = tile.Label,
                    });
                }
                state.Multiviews.Add(wall);
            }
            cfg.MultiviewId = wall.Id;
        }
    }

    /// <summary>What makes two walls the same wall: the tiles, in order, and how they are shown.</summary>
    private static string ShapeOf(MultiviewOptions wall)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(wall.Columns).Append('|').Append(wall.ShowLabels).Append('|').Append(wall.ShowTally);
        foreach (var t in wall.Tiles)
        {
            sb.Append('|').Append(t.Source).Append(':').Append(t.ScreenId).Append(':').Append(t.Input).Append(':').Append(t.Label);
        }
        return sb.ToString();
    }

    /// <summary>An NDI sender or the stream has to be on its own screen before that screen has a picture.</summary>
    private static void PointFeedAtItsOwnScreen(ShowState state, string targetId)
    {
        if (targetId == StreamConfig.OwnScreenId)
        {
            state.Stream.SourceScreenId = StreamConfig.OwnScreenId;
            VirtualScreens.Sync(state);
            return;
        }
        if (!targetId.StartsWith("ndi:", StringComparison.Ordinal)) return;
        foreach (var sender in state.Ndi.Senders)
        {
            if (sender.OwnScreenId == targetId) sender.SourceScreenId = targetId;
        }
        VirtualScreens.Sync(state);
    }
}
