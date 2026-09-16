using System.Text.Json;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>What a caller's plan would change on the desk: per stack, the cues added, changed, and the desk's own the plan does not have.</summary>
public sealed record PlanDiff(int Added, int Changed, int Removed, int Unchanged, IReadOnlyList<string> Stacks)
{
    public bool IsEmpty => Added == 0 && Changed == 0 && Removed == 0;

    /// <summary>"12 cues to add, 3 changed, 1 the desk has that the plan does not — in Main".</summary>
    public string Words
    {
        get
        {
            if (Stacks.Count == 0) return "an empty plan";
            if (IsEmpty) return $"the same {Unchanged} cue{(Unchanged == 1 ? "" : "s")} the desk has";
            var parts = new List<string>();
            if (Added > 0) parts.Add($"{Added} cue{(Added == 1 ? "" : "s")} to add");
            if (Changed > 0) parts.Add($"{Changed} changed");
            if (Removed > 0) parts.Add($"{Removed} the desk has that the plan does not");
            return string.Join(", ", parts) + " — in " + string.Join(", ", Stacks);
        }
    }
}

/// <summary>
/// The cues a caller planned at home, against the desk's: the diff both sides read before APPLY,
/// and the merge APPLY makes — the plan's stacks replace the desk's of the same id (or name, or
/// role for the caller's and the clicker's own), stacks the plan does not name are kept. Pure, so
/// the words are the same on the desk, on the caller and in the tests.
/// </summary>
public static class CuePlan
{
    /// <summary>The plan as it travels: the stacks that have cues — an empty list is nothing to offer.</summary>
    public static string Json(ShowState state) => JsonSerializer.Serialize(state.Stacks.Where(s => s.Cues.Count > 0).ToList(), JsonUtil.CloneOptions);

    public static IReadOnlyList<CueStackConfig>? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<CueStackConfig>>(json, JsonUtil.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>How many cues the plan has, in how many stacks: "12 cues in 1 stack".</summary>
    public static string Count(IEnumerable<CueStackConfig> stacks)
    {
        var list = stacks.ToList();
        var cues = list.Sum(s => s.Cues.Count);
        return $"{cues} cue{(cues == 1 ? "" : "s")} in {list.Count} stack{(list.Count == 1 ? "" : "s")}";
    }

    /// <summary>
    /// The desk's stack a plan stack lands on: the same id, else the same name and role, else — when
    /// the plan carries exactly one list of that role — the desk's list of that role (the caller's
    /// running order is the desk's running order, whatever either side named it). A plan with two
    /// lists of one role matches them by id and name only, so its second one is added, not folded.
    /// </summary>
    private static CueStackConfig? Match(IEnumerable<CueStackConfig> among, CueStackConfig wanted, IReadOnlyList<CueStackConfig> plan)
    {
        var lists = among as IReadOnlyList<CueStackConfig> ?? among.ToList();                    // enumerated once, matched three ways
        return lists.FirstOrDefault(s => s.Id == wanted.Id)
               ?? lists.FirstOrDefault(s => string.Equals(s.Name, wanted.Name, StringComparison.OrdinalIgnoreCase) && s.Role == wanted.Role)
               ?? (plan.Count(s => s.Role == wanted.Role) == 1 ? lists.FirstOrDefault(s => s.Role == wanted.Role) : null);
    }

    private static string Fingerprint(RunCueConfig cue) => JsonSerializer.Serialize(cue, JsonUtil.CloneOptions);

    public static PlanDiff Diff(IReadOnlyList<CueStackConfig> desk, IReadOnlyList<CueStackConfig> plan)
    {
        int added = 0, changed = 0, removed = 0, same = 0;
        var names = new List<string>();
        foreach (var stack in plan)
        {
            names.Add(stack.Name.Length > 0 ? stack.Name : stack.Role.ToString());
            var mine = Match(desk, stack, plan);
            if (mine is null)
            {
                added += stack.Cues.Count;
                continue;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cue in stack.Cues)
            {
                var theirs = mine.Cues.FirstOrDefault(c => c.Id == cue.Id);
                if (theirs is null) { added++; continue; }
                seen.Add(cue.Id);
                if (Fingerprint(theirs) == Fingerprint(cue)) same++; else changed++;
            }
            removed += mine.Cues.Count(c => !seen.Contains(c.Id));
        }
        return new PlanDiff(added, changed, removed, same, names);
    }

    /// <summary>
    /// Whether a Stacks section as it arrives keeps the desk's shape — the same stacks in the same
    /// order, each with the same cues in the same order — so that landing it moves no cue under an
    /// armed stack's runtime. Notes, the pad and a cue's own words keep the shape; a cue added,
    /// removed or moved does not, and neither does a section this build cannot read.
    /// </summary>
    public static bool SameShape(IReadOnlyList<CueStackConfig> mine, string sectionJson)
    {
        List<CueStackConfig>? theirs;
        try
        {
            theirs = JsonSerializer.Deserialize<List<CueStackConfig>>(sectionJson, JsonUtil.CloneOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        if (theirs is null || theirs.Count != mine.Count) return false;
        for (var i = 0; i < mine.Count; i++)
        {
            if (mine[i].Id != theirs[i].Id || mine[i].Cues.Count != theirs[i].Cues.Count) return false;
            for (var j = 0; j < mine[i].Cues.Count; j++)
            {
                if (mine[i].Cues[j].Id != theirs[i].Cues[j].Id) return false;
            }
        }
        return true;
    }

    /// <summary>The plan onto the show: each of its stacks replaces the desk's match, cues and pad; a stack with no match is added; the rest are kept.</summary>
    public static int Merge(ShowState target, IReadOnlyList<CueStackConfig> plan)
    {
        var stacks = 0;
        foreach (var stack in plan)
        {
            var mine = Match(target.Stacks, stack, plan);
            var copy = JsonSerializer.Deserialize<CueStackConfig>(JsonSerializer.Serialize(stack, JsonUtil.CloneOptions), JsonUtil.Options);
            if (copy is null) continue;
            if (mine is null)
            {
                target.Stacks.Add(copy);
            }
            else
            {
                mine.Name = copy.Name.Length > 0 ? copy.Name : mine.Name;
                mine.Scratchpad = copy.Scratchpad;
                mine.LoopAtEnd = copy.LoopAtEnd;
                mine.SuspendAutomationWhileArmed = copy.SuspendAutomationWhileArmed;
                mine.Cues.Clear();
                foreach (var cue in copy.Cues) mine.Cues.Add(cue);
            }
            stacks++;
        }
        return stacks;
    }
}
