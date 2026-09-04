using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// Pure questions about the rig that the desk, the remote and the action layer all ask:
/// which placements are live and in what order, which of them form joined canvases, and
/// what a screen is called. One implementation so the strip, SCREEN n and the wall agree.
/// </summary>
public static class Rig
{
    /// <summary>Placements with a known display (real or planned), arrangement order: left to right, then top down.</summary>
    public static List<(ScreenPlacement Placement, ScreenInfo Info)> OrderedLivePlacements(
        ShowState state, IReadOnlyList<ScreenInfo> known)
        => state.Output.Placements
            .Select(p => (Placement: p, Info: known.FirstOrDefault(s => s.Id == p.ScreenId)))
            .Where(x => x.Info is not null)
            .Select(x => (x.Placement, Info: x.Info!))
            .OrderBy(x => x.Placement.X).ThenBy(x => x.Placement.Y)
            .ToList();

    /// <summary>The geometry table Core needs, built from the App's ScreenInfo list.</summary>
    public static IReadOnlyDictionary<string, ScreenGeometry> DisplaysOf(IReadOnlyList<ScreenInfo> known)
    {
        var map = new Dictionary<string, ScreenGeometry>(known.Count, StringComparer.Ordinal);
        foreach (var s in known) map[s.Id] = new ScreenGeometry(s.Bounds.Width, s.Bounds.Height, s.Label);
        return map;   // indexer, not ToDictionary: a duplicate id cannot throw
    }

    /// <summary>This rig's geometry, resolved from the placements and the known displays.</summary>
    public static RigGeometry Geometry(ShowState state, IReadOnlyList<ScreenInfo> known)
        => RigGeometry.Build(state, DisplaysOf(known));

    /// <summary>Joined canvases (screens dragged flush) → member placements; letters A, B… follow this order.</summary>
    public static List<List<ScreenPlacement>> CanvasGroups(ShowState state, IReadOnlyList<ScreenInfo> known)
    {
        var geo = Geometry(state, known);
        var byId = new Dictionary<string, ScreenPlacement>(StringComparer.Ordinal);
        foreach (var p in state.Output.Placements) byId[p.ScreenId] = p;
        var result = new List<List<ScreenPlacement>>();
        foreach (var key in geo.Targets)
        {
            if (!ContentTargets.IsCanvasKey(key)) continue;
            result.Add(geo.MembersOf(key).Select(id => byId[id]).ToList());
        }
        return result;
    }

    /// <summary>The canvas letter a placement belongs to, or null for a stand-alone screen.</summary>
    public static string? LetterOf(List<List<ScreenPlacement>> groups, ScreenPlacement placement)
    {
        for (var i = 0; i < groups.Count; i++)
        {
            if (groups[i].Contains(placement)) return ((char)('A' + i)).ToString();
        }
        return null;
    }

    /// <summary>The label a screen shows everywhere: the operator's name, or the OS one.</summary>
    public static string LabelFor(ScreenPlacement placement, ScreenInfo? info)
        => placement.CustomLabel.Length > 0 ? placement.CustomLabel : info?.Label ?? placement.ScreenId;

    /// <summary>
    /// Every content target in the rig, in wall order: joined canvases by member key, then the
    /// stand-alone screens. The strip, a scoped TAKE and the arming set all walk this list.
    /// </summary>
    public static List<string> Targets(ShowState state, IReadOnlyList<ScreenInfo> known)
        => Geometry(state, known).Targets.ToList();

    /// <summary>A sensible shape when the rig has no screens at all.</summary>
    public static readonly SKSizeI DefaultTargetSize = RigGeometry.FallbackTargetSize;

    /// <summary>
    /// The pixel size of a content target: a canvas's union, a screen's effective (rotation-aware)
    /// size. The program (null) takes the first target's shape so the panes show something true.
    /// </summary>
    public static SKSizeI TargetSize(ShowState state, IReadOnlyList<ScreenInfo> known, string? targetId)
        => Geometry(state, known).SizeOf(targetId);
}
