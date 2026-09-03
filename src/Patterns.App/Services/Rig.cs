using Patterns.Core.Model;
using Patterns.Core.Rendering;
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
}
