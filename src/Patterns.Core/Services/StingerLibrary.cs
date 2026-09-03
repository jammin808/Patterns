using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// One resolver for stingers wherever a name arrives — the desk, a cue action, the remote's
/// STINGER n: a 1-based index first, then the id, then the display name or the file name,
/// case-insensitive. Every caller reads the same rule, so "STINGER 3" and cue target "3" agree.
/// </summary>
public static class StingerLibrary
{
    public static StingerItemConfig? Find(ShowState state, string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var items = state.Stingers.Items;
        var t = target.Trim();
        if (int.TryParse(t, out var n)) return n >= 1 && n <= items.Count ? items[n - 1] : null;
        foreach (var s in items)
        {
            if (string.Equals(s.Id, t, StringComparison.Ordinal)) return s;
        }
        foreach (var s in items)
        {
            if (string.Equals(s.DisplayName, t, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Name, t, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>What still points at a stinger — the cues that fire it — so a delete can refuse and say why.</summary>
    public static IReadOnlyList<string> References(ShowState state, StingerItemConfig item)
    {
        var refs = new List<string>();
        foreach (var (stack, cue, action) in CueStacks.AllActions(state))
        {
            if (action.Kind != CueActionKind.StingerFire) continue;
            if (ReferenceEquals(Find(state, action.Target), item)) refs.Add($"{stack.Name} cue {cue.Number} {cue.Name}");
        }
        return refs;
    }
}
