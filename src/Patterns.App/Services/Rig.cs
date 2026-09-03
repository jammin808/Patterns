using Patterns.Core.Model;
using Patterns.Core.Rendering;
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

    /// <summary>Joined canvases (screens dragged flush) → member placements; letters A, B… follow this order.</summary>
    public static List<List<ScreenPlacement>> CanvasGroups(ShowState state, IReadOnlyList<ScreenInfo> known)
    {
        var live = OrderedLivePlacements(state, known);
        var arranged = live
            .Select(x =>
            {
                var size = OutputWindowManager.EffectiveSize(x.Placement, x.Info);
                return new ArrangedScreen(x.Placement.ScreenId,
                    SKRectI.Create(x.Placement.X, x.Placement.Y, size.Width, size.Height));
            })
            .ToList();
        var byId = live.ToDictionary(x => x.Placement.ScreenId, x => x.Placement);
        return ScreenLayout.Groups(arranged)
            .Where(g => g.Count > 1)
            .OrderBy(g => ScreenLayout.Union(g).Left).ThenBy(g => ScreenLayout.Union(g).Top)
            .Select(g => g.Select(m => byId[m.Id]).ToList())
            .ToList();
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
    {
        var result = new List<string>();
        var grouped = new HashSet<string>();
        foreach (var g in CanvasGroups(state, known))
        {
            foreach (var p in g) grouped.Add(p.ScreenId);
            result.Add(CanvasNameConfig.KeyFor(g.Select(p => p.ScreenId)));
        }
        foreach (var (placement, _) in OrderedLivePlacements(state, known))
        {
            if (!grouped.Contains(placement.ScreenId)) result.Add(placement.ScreenId);
        }
        return result;
    }

    /// <summary>A sensible shape when the rig has no screens at all.</summary>
    public static readonly SKSizeI DefaultTargetSize = new(1920, 1080);

    /// <summary>
    /// The pixel size of a content target: a canvas's union, a screen's effective (rotation-aware)
    /// size. The program (null) takes the first target's shape so the panes show something true.
    /// </summary>
    public static SKSizeI TargetSize(ShowState state, IReadOnlyList<ScreenInfo> known, string? targetId)
    {
        if (targetId is null)
        {
            var first = Targets(state, known).FirstOrDefault();
            return first is null ? DefaultTargetSize : TargetSize(state, known, first);
        }
        var live = OrderedLivePlacements(state, known);
        if (ContentTargets.IsCanvasKey(targetId))
        {
            var members = ContentTargets.Members(targetId);
            SKRectI? union = null;
            foreach (var (placement, info) in live)
            {
                if (!members.Contains(placement.ScreenId)) continue;
                var size = OutputWindowManager.EffectiveSize(placement, info);
                var rect = SKRectI.Create(placement.X, placement.Y, size.Width, size.Height);
                union = union is { } u ? SKRectI.Union(u, rect) : rect;
            }
            return union is { } r ? new SKSizeI(r.Width, r.Height) : DefaultTargetSize;
        }
        foreach (var (placement, info) in live)
        {
            if (placement.ScreenId == targetId) return OutputWindowManager.EffectiveSize(placement, info);
        }
        return DefaultTargetSize;
    }
}
