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

    /// <summary>What still points at an entry — the cues that play it, the looks that start it — so a delete can refuse and say why.</summary>
    public static IReadOnlyList<string> References(ShowState state, SpotifyItemConfig item)
    {
        var refs = new List<string>();
        foreach (var (stack, cue, action) in CueStacks.AllActions(state))
        {
            if (action.Kind != ShowActionKind.SpotifyPlay) continue;
            if (ReferenceEquals(Find(state, action.Target), item)) refs.Add($"{stack.Name} cue {cue.Number} {cue.Name}");
        }
        foreach (var look in state.LooksAndCues.Looks)
        {
            if (StartsMusic(look) && ReferenceEquals(Find(state, look.MusicItemId), item)) refs.Add($"look '{look.Name}'");
        }
        return refs;
    }

    /// <summary>True when a look names an entry to play (not "leave it", not "pause").</summary>
    public static bool StartsMusic(LookConfig look)
        => look.MusicItemId.Length > 0 && look.MusicItemId != LookConfig.PauseMusic;

    /// <summary>The Music picker's choices on every look: leave it, pause it, each entry, and any entry a look still names that is no longer in the list (marked, so the look keeps its choice until it is pointed elsewhere).</summary>
    public static List<(string Id, string Label)> LookChoices(ShowState state)
    {
        var wanted = new List<(string Id, string Label)> { ("", "Leave the music alone"), (LookConfig.PauseMusic, "Pause break music") };
        foreach (var m in state.Spotify.Items) wanted.Add((m.Id, "▶ " + m.DisplayName));
        foreach (var look in state.LooksAndCues.Looks)
        {
            var id = look.MusicItemId;
            if (id.Length > 0 && wanted.All(w => w.Id != id)) wanted.Add((id, "▶ (no longer in break music)"));
        }
        return wanted;
    }

    /// <summary>A Spotify link pasted on the desk becomes an entry (named by its kind until the operator names it); false with the reason when it is not a Spotify link.</summary>
    public static bool TryAddLink(ShowState state, string? link, out SpotifyItemConfig? entry, out string problem)
    {
        entry = null;
        problem = "";
        if (!SpotifyUri.TryParse(link ?? "", out var r))
        {
            problem = "That is not a Spotify link — copy one from Spotify with Share → Copy link.";
            return false;
        }
        entry = new SpotifyItemConfig { Uri = r.Uri };
        state.Spotify.Items.Add(entry);
        return true;
    }

    /// <summary>A browsed song, a playlist or a search hit becomes a one-press entry; the same link twice stays one entry (the existing one comes back, <paramref name="added"/> false).</summary>
    public static SpotifyItemConfig? AddEntry(ShowState state, string uri, string name, out bool added)
    {
        added = false;
        if (!SpotifyUri.TryParse(uri, out var r)) return null;
        if (state.Spotify.Items.FirstOrDefault(i => i.Uri == r.Uri) is { } existing) return existing;
        var entry = new SpotifyItemConfig { Uri = r.Uri, Name = name };
        state.Spotify.Items.Add(entry);
        added = true;
        return entry;
    }

    /// <summary>Takes an entry out of the list — refused, with what still points at it, while a cue plays it or a look starts it (a deleted entry fails at show time).</summary>
    public static bool TryRemove(ShowState state, SpotifyItemConfig item, out string problem)
    {
        problem = "";
        var refs = References(state, item);
        if (refs.Count > 0)
        {
            problem = $"'{item.DisplayName}' is still used by {string.Join(", ", refs)} — remove those first.";
            return false;
        }
        state.Spotify.Items.Remove(item);
        return true;
    }
}
