using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// What a screen is for, and what that means for the content that reaches it: a locked target
/// (a screen that does not follow cues, or a canvas whose members all don't) keeps its picture
/// through looks, cues, TAKE ALL and a stinger's takeover; a repeater draws another target's
/// picture. Pure — every rule the engine, the actions and the wall share lives here.
/// </summary>
public static class ScreenRoles
{
    /// <summary>The follow default a role picks when it is chosen: main and repeater screens follow, monitors do not.</summary>
    public static bool DefaultFollows(ScreenRole role) => role is ScreenRole.Main or ScreenRole.Repeater;

    /// <summary>The wall tile's badge; empty for a main screen.</summary>
    public static string Badge(ScreenRole role) => role switch
    {
        ScreenRole.Confidence => "CONF",
        ScreenRole.Info => "INFO",
        ScreenRole.Repeater => "REP",
        _ => "",
    };

    public static string Label(ScreenRole role) => role switch
    {
        ScreenRole.Confidence => "Confidence — a stage monitor",
        ScreenRole.Info => "Info — a foyer or info screen",
        ScreenRole.Repeater => "Repeater — a copy of another target",
        _ => "Main — the audience's picture",
    };

    /// <summary>A single screen locked, or a canvas whose every member is; a ghost or an empty key never is.</summary>
    public static bool IsLocked(ShowState state, string targetId)
    {
        if (targetId.Length == 0) return false;
        if (ContentTargets.IsCanvasKey(targetId))
        {
            var members = ContentTargets.Members(targetId);
            if (members.Length == 0) return false;
            foreach (var id in members)
            {
                if (Find(state, id) is not { FollowsCues: false }) return false;
            }
            return true;
        }
        return Find(state, targetId) is { FollowsCues: false };
    }

    /// <summary>The locked targets among the rig's targets — what TAKE ALL leaves alone beside the un-armed tiles.</summary>
    public static List<string> LockedTargets(ShowState state, IEnumerable<string> targets)
    {
        var list = new List<string>();
        foreach (var target in targets)
        {
            if (IsLocked(state, target)) list.Add(target);
        }
        return list;
    }

    /// <summary>
    /// Locks or unlocks a target (every member of a canvas). A lock means "keep what you show":
    /// a target still following the program gets that picture as its own, so the next look, cue
    /// or TAKE cannot reach it. <paramref name="pictureNow"/> is what the target shows on air —
    /// the caller resolves it, since the state being edited may be a preview. A repeater keeps
    /// following its source either way.
    /// </summary>
    public static void SetLocked(ShowState state, string targetId, bool locked, PatternConfig? pictureNow = null)
    {
        var members = ContentTargets.IsCanvasKey(targetId) ? ContentTargets.Members(targetId) : new[] { targetId };
        var any = false;
        foreach (var id in members)
        {
            var p = Find(state, id);
            if (p is null) continue;
            p.FollowsCues = !locked;
            any = true;
        }
        if (!any || !locked) return;
        if (!ContentTargets.IsCanvasKey(targetId) && (Find(state, targetId)?.MirrorOf.Length ?? 0) > 0) return;
        if (ContentTargets.UsesOwnPattern(state, targetId)) return;
        var assignment = ContentTargets.EnsureAssignment(state, targetId);
        if (pictureNow is not null) ModelCopier.Copy(pictureNow, assignment.Pattern);
        ContentTargets.SetOwnPattern(state, targetId, true);
    }

    /// <summary>
    /// The target whose picture this one draws: itself, or the end of its mirror chain. A
    /// canvas never mirrors; a chain stops at a ghost, at itself, or after a few hops, so a
    /// loop typed into a show file cannot hang a sink. No allocation on the hot path.
    /// </summary>
    public static string ResolveMirror(ShowState state, string targetId)
    {
        var current = targetId;
        for (var hop = 0; hop < 4; hop++)
        {
            if (ContentTargets.IsCanvasKey(current)) return current;
            var p = Find(state, current);
            if (p is null || p.MirrorOf.Length == 0 || p.MirrorOf == current) return current;
            if (!ContentTargets.IsInRig(state, p.MirrorOf)) return current;
            current = p.MirrorOf;
        }
        return current;
    }

    /// <summary>
    /// The targets a look must leave alone, with the picture each keeps: every locked single
    /// screen that is not a repeater, and every named canvas whose members are all locked. A
    /// locked target with no picture of its own gets the program it is showing now, so the look
    /// lands everywhere else and nowhere here.
    /// </summary>
    public static List<(string Id, OutputAssignment Kept)> Held(ShowState state)
    {
        var list = new List<(string, OutputAssignment)>();
        foreach (var p in state.Output.Placements)
        {
            if (p.FollowsCues || p.MirrorOf.Length > 0) continue;
            list.Add((p.ScreenId, Keep(state, p.ScreenId)));
        }
        foreach (var c in state.Output.CanvasNames)
        {
            if (IsLocked(state, c.MemberKey)) list.Add((c.MemberKey, Keep(state, c.MemberKey)));
        }
        return list;
    }

    private static OutputAssignment Keep(ShowState state, string targetId)
    {
        var kept = new OutputAssignment { ScreenId = targetId };
        var own = ContentTargets.UsesOwnPattern(state, targetId)
            ? state.Independent.FirstOrDefault(a => a.ScreenId == targetId)?.Pattern
            : null;
        ModelCopier.Copy(own ?? state.Pattern, kept.Pattern);
        return kept;
    }

    private static ScreenPlacement? Find(ShowState state, string id)
    {
        foreach (var p in state.Output.Placements)
        {
            if (p.ScreenId == id) return p;
        }
        return null;
    }
}
