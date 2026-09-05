using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// A <em>content target</em> is the unit that can show something of its own: a single screen
/// (its placement id) or a joined canvas (its sorted member key, <c>a+b</c>). Screens inside a
/// joined canvas render through the canvas, so the canvas — not its members — is the target.
/// Everything that resolves "what does this sink show" goes through here.
/// </summary>
public static class ContentTargets
{
    /// <summary>Screen ids never contain '+'; canvas keys always do.</summary>
    public static bool IsCanvasKey(string targetId) => targetId.Contains('+');

    /// <summary>
    /// Moves every reference to a screen id onto a new one: the placement itself, per-screen
    /// patterns, joined-canvas keys and names, multiview tiles in any pattern, NDI senders, the
    /// stream source, the web target — and the saved looks, whose captured JSON carries
    /// per-screen assignments by id. Adopting a planned screen onto a display and a display
    /// changing mode (its id embeds its geometry) both need the whole surface, or something
    /// programmed against the screen is left orphaned.
    /// </summary>
    public static void RenameScreen(ShowState state, string oldId, string newId)
    {
        if (oldId.Length == 0 || newId.Length == 0 || oldId == newId) return;

        string Rekey(string key)
        {
            if (key == oldId) return newId;
            if (!IsCanvasKey(key)) return key;
            var members = Members(key);
            return members.Contains(oldId) ? CanvasNameConfig.KeyFor(members.Select(id => id == oldId ? newId : id)) : key;
        }

        foreach (var p in state.Output.Placements)
        {
            if (p.ScreenId == oldId) p.ScreenId = newId;
            if (p.MirrorOf.Length > 0) p.MirrorOf = Rekey(p.MirrorOf);
        }
        foreach (var a in state.Independent) a.ScreenId = Rekey(a.ScreenId);
        foreach (var canvas in state.Output.CanvasNames) canvas.MemberKey = Rekey(canvas.MemberKey);
        foreach (var pattern in AllPatterns(state))
        {
            foreach (var tile in pattern.Multiview.Tiles) tile.ScreenId = Rekey(tile.ScreenId);
        }
        foreach (var sender in state.Ndi.Senders) sender.SourceScreenId = Rekey(sender.SourceScreenId);
        state.Stream.SourceScreenId = Rekey(state.Stream.SourceScreenId);
        state.Web.TargetScreenId = Rekey(state.Web.TargetScreenId);

        // Saved looks are captured JSON — the id appears verbatim inside them.
        foreach (var look in state.LooksAndCues.Looks)
        {
            if (look.Json.Contains(oldId, StringComparison.Ordinal))
            {
                look.Json = look.Json.Replace(oldId, newId, StringComparison.Ordinal);
            }
        }
    }

    private static IEnumerable<PatternConfig> AllPatterns(ShowState state)
    {
        yield return state.Pattern;
        foreach (var a in state.Independent) yield return a.Pattern;
    }

    public static string[] Members(string canvasKey) => canvasKey.Split('+', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Does the target show its own pattern rather than the program?</summary>
    public static bool UsesOwnPattern(ShowState state, string targetId)
    {
        if (IsCanvasKey(targetId))
        {
            foreach (var c in state.Output.CanvasNames)
            {
                if (c.MemberKey == targetId) return c.UseCustomPattern;
            }
            return false;
        }
        foreach (var p in state.Output.Placements)
        {
            if (p.ScreenId == targetId) return p.UseCustomPattern;
        }
        return false;
    }

    /// <summary>Turns a target's own pattern on or off (a canvas gets its config row on demand).</summary>
    public static void SetOwnPattern(ShowState state, string targetId, bool on)
    {
        if (IsCanvasKey(targetId))
        {
            var canvas = state.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == targetId);
            if (canvas is null)
            {
                if (!on) return;
                canvas = new CanvasNameConfig { MemberKey = targetId };
                state.Output.CanvasNames.Add(canvas);
            }
            canvas.UseCustomPattern = on;
            return;
        }
        var placement = state.Output.Placements.FirstOrDefault(p => p.ScreenId == targetId);
        if (placement is not null) placement.UseCustomPattern = on;
    }

    /// <summary>The assignment holding a target's own pattern, created (as a copy of the program) when missing.</summary>
    public static OutputAssignment EnsureAssignment(ShowState state, string targetId)
    {
        var existing = state.Independent.FirstOrDefault(a => a.ScreenId == targetId);
        if (existing is not null) return existing;
        var assignment = new OutputAssignment { ScreenId = targetId };
        ModelCopier.Copy(state.Pattern, assignment.Pattern);
        state.Independent.Add(assignment);
        return assignment;
    }

    /// <summary>
    /// Does this id name something the show still has? A screen with a placement, or a canvas key
    /// with at least one member placement. Empty is never in the rig. Geometry-independent on
    /// purpose: a headless render with no display table must still resolve a real screen id.
    /// </summary>
    public static bool IsInRig(ShowState state, string targetId)
    {
        if (targetId.Length == 0) return false;
        if (IsCanvasKey(targetId))
        {
            foreach (var id in Members(targetId))
            {
                foreach (var p in state.Output.Placements)
                {
                    if (p.ScreenId == id) return true;
                }
            }
            return false;
        }
        foreach (var p in state.Output.Placements)
        {
            if (p.ScreenId == targetId) return true;
        }
        return false;
    }

    /// <summary>
    /// Is every screen behind this target switched on? A canvas needs all of its members; a single
    /// screen needs its own placement. An unknown or empty id, and a canvas key with no members,
    /// are never on.
    /// </summary>
    public static bool IsTargetEnabled(ShowState state, string targetId)
    {
        if (targetId.Length == 0) return false;
        if (IsCanvasKey(targetId))
        {
            var members = Members(targetId);
            if (members.Length == 0) return false;
            foreach (var id in members)
            {
                if (state.Output.Placements.FirstOrDefault(p => p.ScreenId == id)?.Enabled != true) return false;
            }
            return true;
        }
        return state.Output.Placements.FirstOrDefault(p => p.ScreenId == targetId)?.Enabled == true;
    }

    /// <summary>
    /// Targets whose own pattern is on air right now: enabled singles with the flag, and
    /// canvases with the flag whose members are all enabled. Drives media and input reconcile.
    /// </summary>
    public static IEnumerable<string> ActiveCustomTargets(ShowState state)
    {
        foreach (var p in state.Output.Placements)
        {
            if (p.UseCustomPattern && p.Enabled) yield return p.ScreenId;
        }
        foreach (var c in state.Output.CanvasNames)
        {
            if (c.UseCustomPattern && IsTargetEnabled(state, c.MemberKey)) yield return c.MemberKey;
        }
    }
}
