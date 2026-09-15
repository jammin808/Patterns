using Patterns.Core.RigDay;
using Patterns.Core.Geometry;
using Patterns.Rendering;
using SkiaSharp;

namespace Patterns.Rendering.RigDay;


/// <summary>
/// Blend Quest: a rig's joins as levels; a level clears when its members' audits are green (the grey
/// check is the operator's eye — the audit reads the widths, the curves, the sides); where four
/// projectors share a corner, the 2×2's middle is the boss. Pure over the arrangement and the audit.
/// </summary>
public static class BlendQuest
{
    public static IReadOnlyList<QuestLevel> Levels(IReadOnlyList<ArrangedScreen> arranged, Func<string, IReadOnlyList<BlendNote>> notesOf, Func<string, string> nameOf)
    {
        var levels = new List<QuestLevel>();
        var warnings = new Dictionary<string, List<string>>();
        foreach (var s in arranged) warnings[s.Id] = notesOf(s.Id).Where(n => n.Warning).Select(n => n.Text).ToList();
        for (var i = 0; i < arranged.Count; i++)
        {
            for (var j = i + 1; j < arranged.Count; j++)
            {
                var a = arranged[i];
                var b = arranged[j];
                if (!a.Rect.IntersectsWith(b.Rect)) continue;
                var overlap = RasterRect.Intersect(a.Rect, b.Rect);
                if (overlap.Width <= 0 || overlap.Height <= 0) continue;
                // A join shares an edge; two that touch only at a corner (a 2×2's diagonal) are the boss's, not a level.
                if (overlap.Width < 0.5 * Math.Min(a.Rect.Width, b.Rect.Width) && overlap.Height < 0.5 * Math.Min(a.Rect.Height, b.Rect.Height)) continue;
                var bad = warnings[a.Id].Concat(warnings[b.Id]).ToList();
                var name = $"{nameOf(a.Id)} ⟷ {nameOf(b.Id)}";
                levels.Add(new QuestLevel(name, new[] { a.Id, b.Id }, bad.Count == 0, false,
                    bad.Count == 0 ? $"{name}: clear ({Math.Min(overlap.Width, overlap.Height)} px)" : $"{name}: {bad[0]}"));
            }
        }
        // The boss: four screens sharing one patch.
        for (var i = 0; i < arranged.Count; i++)
            for (var j = i + 1; j < arranged.Count; j++)
                for (var k = j + 1; k < arranged.Count; k++)
                    for (var l = k + 1; l < arranged.Count; l++)
                    {
                        var common = RasterRect.Intersect(RasterRect.Intersect(arranged[i].Rect, arranged[j].Rect), RasterRect.Intersect(arranged[k].Rect, arranged[l].Rect));
                        if (common.Width <= 0 || common.Height <= 0) continue;
                        var members = new[] { arranged[i].Id, arranged[j].Id, arranged[k].Id, arranged[l].Id };
                        var bad = members.SelectMany(m => warnings[m]).ToList();
                        levels.Add(new QuestLevel("The 2×2's middle", members, bad.Count == 0, true,
                            bad.Count == 0 ? $"the boss is down — the {common.Width}×{common.Height} px middle reads flat by the audit; the black level is your eye" : $"the boss: {bad[0]}"));
                    }
        return levels;
    }

    /// <summary>"Blend Quest: 2 of 3 joins clear · the boss waits".</summary>
    public static string Words(IReadOnlyList<QuestLevel> levels)
    {
        if (levels.Count == 0) return "Blend Quest: no joins on this rig — overlap two projectors on the Screens page and the levels appear.";
        var joins = levels.Where(l => !l.Boss).ToList();
        var boss = levels.FirstOrDefault(l => l.Boss);
        var words = $"Blend Quest: {joins.Count(l => l.Cleared)} of {joins.Count} join{(joins.Count == 1 ? "" : "s")} clear";
        if (boss is not null) words += boss.Cleared ? " · the boss is down" : " · the boss waits";
        if (levels.All(l => l.Cleared)) words += " — every level clear";
        return words;
    }
}
