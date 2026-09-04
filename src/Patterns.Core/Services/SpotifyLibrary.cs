using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// One resolver for break music wherever a name arrives — the desk, a cue action, MUSIC PLAY 3:
/// a 1-based index first, then the id, then the display name or the name, case-insensitive.
/// Every caller reads the same rule, so "MUSIC PLAY 3" and cue target "3" agree.
/// </summary>
public static class SpotifyLibrary
{
    public static SpotifyItemConfig? Find(ShowState state, string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var items = state.Spotify.Items;
        var t = target.Trim();
        if (int.TryParse(t, out var n)) return n >= 1 && n <= items.Count ? items[n - 1] : null;
        foreach (var m in items)
        {
            if (string.Equals(m.Id, t, StringComparison.Ordinal)) return m;
        }
        foreach (var m in items)
        {
            if (string.Equals(m.DisplayName, t, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Name, t, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }
        return null;
    }

    /// <summary>What still points at an entry — the cues that play it — so a delete can refuse and say why.</summary>
    public static IReadOnlyList<string> References(ShowState state, SpotifyItemConfig item)
    {
        var refs = new List<string>();
        foreach (var (stack, cue, action) in CueStacks.AllActions(state))
        {
            if (action.Kind != CueActionKind.SpotifyPlay) continue;
            if (ReferenceEquals(Find(state, action.Target), item)) refs.Add($"{stack.Name} cue {cue.Number} {cue.Name}");
        }
        return refs;
    }
}
