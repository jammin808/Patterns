using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>Which side of the split a stinger's "after" target landed on.</summary>
public enum AfterTargetKind
{
    None,
    Look,
    Cue,
}

/// <summary>
/// A stinger's "after" target resolved once — a look first, then a cue — so the validator, the
/// service that runs it and the Audio page's picker cannot disagree.
/// </summary>
public readonly record struct StingerAfterTarget(AfterTargetKind Kind, string Id, string Label);

/// <summary>
/// One resolver for the library wherever a name arrives — the desk, a cue action, the remote's
/// STINGER n: a 1-based index first, then the id, then the display name or the file name,
/// case-insensitive. Every caller reads the same rule, so "STINGER 3" and cue target "3" agree.
/// Both kinds share the collection and the numbering: VOG 3, STING 3 and STINGER 3 all mean
/// library item 3, because two numbering schemes on a live desk is how the wrong button gets fired.
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

    /// <summary>The library filtered by kind, keeping library order (the numbering never changes).</summary>
    public static IEnumerable<StingerItemConfig> OfKind(ShowState state, StingerKind kind)
        => state.Stingers.Items.Where(i => i.Kind == kind);

    /// <summary>"VOG" / "stinger", for a sentence an operator reads.</summary>
    public static string KindWord(StingerKind kind) => kind == StingerKind.Vog ? "VOG" : "stinger";

    /// <summary>
    /// Does the item match what the caller asked for? <paramref name="assert"/> is "", "vog" or
    /// "sting". Blank accepts either kind; a word this build does not understand also accepts — a
    /// newer controller's vocabulary must never stop a press mid-show.
    /// </summary>
    public static bool KindMatches(StingerItemConfig item, string? assert, out string wanted)
    {
        wanted = "";
        if (string.IsNullOrWhiteSpace(assert)) return true;
        var a = assert.Trim();
        if (string.Equals(a, "vog", StringComparison.OrdinalIgnoreCase))
        {
            wanted = "VOG";
            return item.Kind == StingerKind.Vog;
        }
        if (string.Equals(a, "sting", StringComparison.OrdinalIgnoreCase))
        {
            wanted = "stinger";
            return item.Kind == StingerKind.Sting;
        }
        return true;
    }

    /// <summary>
    /// A stinger's "after" target: a look first, then a cue. The editor's picker writes ids and this
    /// is the one resolver used at build time and at run time, so they cannot diverge. A miss keeps
    /// the trimmed text in <see cref="StingerAfterTarget.Label"/> so a message can name it.
    /// </summary>
    public static StingerAfterTarget ResolveAfter(ShowState state, string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return new(AfterTargetKind.None, "", "");
        var t = target.Trim();
        if (LookService.Find(state, t) is { } look) return new(AfterTargetKind.Look, look.Id, look.Name);
        if (CueStacks.FindCue(state, t) is { } cue)
        {
            return new(AfterTargetKind.Cue, cue.Cue.Id, $"{cue.Cue.Number} {cue.Cue.Name}");
        }
        return new(AfterTargetKind.None, "", t);
    }

    /// <summary>
    /// Where the stinger leaves the show, in one phrase — the cue summary, the Show panel's chips
    /// and the Audio page's read-back all say the same words. A VOG has no after, so it reads "".
    /// Never used in a service status line: <c>CueStackService.LateFailure</c> scans those for
    /// failure words, and a look named "Missing person" would flip a good cue row to FailedLate.
    /// </summary>
    public static string AfterSummary(ShowState state, StingerItemConfig item)
    {
        if (item.Kind != StingerKind.Sting || item.Source == StingerSource.EffectPulse) return "";
        switch (item.After)
        {
            case StingerAfter.Manual:
                return "hold for a take";
            case StingerAfter.Next:
            {
                if (item.AfterTarget.Length == 0) return "the next cue on the caller's list";
                var stack = CueStacks.Find(state, item.AfterTarget);
                return stack is null ? "a cue list that is not there" : $"the next cue on '{stack.Name}'";
            }
            case StingerAfter.Custom:
            {
                if (item.AfterTarget.Length == 0) return "nothing chosen";
                var target = ResolveAfter(state, item.AfterTarget);
                return target.Kind switch
                {
                    AfterTargetKind.Look => $"look '{target.Label}'",
                    AfterTargetKind.Cue => $"cue {target.Label.Split(' ')[0]}",
                    _ => "a look or cue that is not there",
                };
            }
            default:
                return "content back";
        }
    }

    /// <summary>
    /// A stinger's after-policy that cannot possibly run — a Hard validator issue. Null for every
    /// VOG: a VOG never reads the field, so a stale target on one must never break a cue.
    /// </summary>
    public static string? AfterProblem(ShowState state, StingerItemConfig item)
    {
        if (item.Kind != StingerKind.Sting || item.Source == StingerSource.EffectPulse) return null;
        switch (item.After)
        {
            case StingerAfter.Next:
                if (item.AfterTarget.Length == 0) return null;   // blank = the caller's stack, which always exists
                return CueStacks.Find(state, item.AfterTarget) is null
                    ? $"'{item.DisplayName}' ends on a cue list that is not there — {item.AfterTarget}."
                    : null;
            case StingerAfter.Custom:
                if (item.AfterTarget.Length == 0)
                {
                    return $"'{item.DisplayName}' ends on a look or cue, but none is chosen.";
                }
                return ResolveAfter(state, item.AfterTarget).Kind == AfterTargetKind.None
                    ? $"'{item.DisplayName}' ends on a look or cue that is not there — {item.AfterTarget}."
                    : null;
            default:
                return null;
        }
    }

    /// <summary>Worth saying, not worth refusing — a Soft validator issue. First match wins.</summary>
    public static string? AfterNote(ShowState state, StingerItemConfig item)
    {
        if (item.Kind != StingerKind.Sting || item.Source == StingerSource.EffectPulse) return null;
        var name = item.DisplayName;
        if (item.After == StingerAfter.Manual && PlaylistSequencer.IsAudioPath(item.Path))
        {
            return $"'{name}' is a sound: there is nothing on the screens to hold, so it just ends.";
        }
        if (item.After == StingerAfter.Next)
        {
            if (item.AfterTarget.Length == 0)
            {
                if (CueStacks.Caller(state).Cues.Count == 0)
                {
                    return $"'{name}' moves on to the next cue, but the caller's list is empty.";
                }
                return null;
            }
            // One hop, pure: does the list it advances itself fire stingers that advance a list?
            if (CueStacks.Find(state, item.AfterTarget) is { } stack && RunsOn(state, stack))
            {
                return $"'{name}' advances a list whose cues fire stingers that advance a list — the show could run on by itself.";
            }
        }
        return null;
    }

    /// <summary>Does any cue on this list fire a stinger that itself moves the show on?</summary>
    private static bool RunsOn(ShowState state, CueStackConfig stack)
    {
        foreach (var cue in stack.Cues)
        {
            foreach (var action in cue.Actions)
            {
                if (action.Kind != CueActionKind.StingerFire) continue;
                if (Find(state, action.Target) is { Kind: StingerKind.Sting, After: StingerAfter.Next, Source: StingerSource.File }) return true;
            }
        }
        return false;
    }
}
