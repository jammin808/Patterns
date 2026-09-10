using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>One step of a cue and the second it runs at, measured from GO.</summary>
public readonly record struct CueStep(CueActionConfig Action, int Index, double AtSeconds)
{
    public bool IsImmediate => AtSeconds <= 0;
}

/// <summary>
/// A cue's shape in time: which of its steps run with the GO and which run after it, and what
/// happens to the timing when the operator moves a step.
///
/// Two rules, and both are here rather than in the desk so they can be read and tested without a
/// show:
///
/// 1. A step's delay is a wait after the step above it, so the list order and the running order
///    can never disagree. A cue whose steps are all 0 is one edit and one publish, exactly as it
///    was before delays existed — the cost of the feature is zero until somebody uses it.
///
/// 2. The delays belong to the cue's shape, not to the steps. Move a step and it swaps places
///    with its neighbour, taking the neighbour's wait and leaving its own behind: the timing an
///    operator built stays as they built it, and only the content moves. Carrying the wait with
///    the step instead would leave a cue whose steps run in an order the list does not show.
/// </summary>
public static class CueSteps
{
    /// <summary>Every step with the second it runs at, in list order — the running order.</summary>
    public static List<CueStep> Plan(IReadOnlyList<CueActionConfig> actions)
    {
        var list = new List<CueStep>(actions.Count);
        var at = 0.0;
        for (var i = 0; i < actions.Count; i++)
        {
            at += Math.Max(0, actions[i].DelaySeconds);
            list.Add(new CueStep(actions[i], i, at));
        }
        return list;
    }

    /// <summary>True when any step waits — the one test the fire path needs before it does anything different.</summary>
    public static bool HasDelays(IReadOnlyList<CueActionConfig> actions)
    {
        for (var i = 0; i < actions.Count; i++)
        {
            if (actions[i].DelaySeconds > 0) return true;
        }
        return false;
    }

    /// <summary>How long the cue takes to finish running itself; 0 when every step goes with the GO.</summary>
    public static double TailSeconds(IReadOnlyList<CueActionConfig> actions)
    {
        var at = 0.0;
        for (var i = 0; i < actions.Count; i++) at += Math.Max(0, actions[i].DelaySeconds);
        return at;
    }

    /// <summary>
    /// Moves the step at <paramref name="from"/> to <paramref name="to"/> and leaves the cue's
    /// timing where it was: the waits stay with the positions, so the shape an operator built is
    /// still the shape after they reorder it. False when there is nothing to move.
    /// </summary>
    public static bool Move(IList<CueActionConfig> actions, int from, int to)
    {
        if (from == to) return false;
        if (from < 0 || to < 0 || from >= actions.Count || to >= actions.Count) return false;
        var waits = new double[actions.Count];
        for (var i = 0; i < actions.Count; i++) waits[i] = actions[i].DelaySeconds;

        var moved = actions[from];
        actions.RemoveAt(from);
        actions.Insert(to, moved);

        // The waits never moved: position 0 still waits what position 0 waited.
        for (var i = 0; i < actions.Count; i++) actions[i].DelaySeconds = waits[i];
        return true;
    }

    /// <summary>"+5 s", "+1 m 30 s" — how far into the cue a step runs; "" for one that goes with the GO.</summary>
    public static string AtWords(double atSeconds)
    {
        if (atSeconds <= 0) return "";
        if (atSeconds < 60) return $"+{atSeconds:0.#} s";
        var t = TimeSpan.FromSeconds(atSeconds);
        return t.Seconds == 0 ? $"+{(int)t.TotalMinutes} m" : $"+{(int)t.TotalMinutes} m {t.Seconds} s";
    }
}
