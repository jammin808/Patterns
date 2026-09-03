using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The two lists a show holds — the caller's cue stack and the speaker's clicker list — found
/// by role and created when missing, so nothing else ever asks "is there a stack".
/// </summary>
public static class CueStacks
{
    public const string CallerName = "Cue stack";
    public const string ClickerName = "Clicker list";

    public static CueStackConfig Caller(ShowState state) => Ensure(state, StackRole.Caller);

    public static CueStackConfig Clicker(ShowState state) => Ensure(state, StackRole.Clicker);

    public static CueStackConfig Ensure(ShowState state, StackRole role)
    {
        foreach (var s in state.Stacks)
        {
            if (s.Role == role) return s;
        }
        var stack = new CueStackConfig
        {
            Role = role,
            Name = role == StackRole.Caller ? CallerName : ClickerName,
        };
        state.Stacks.Add(stack);
        return stack;
    }

    public static CueStackConfig? Find(ShowState state, string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        foreach (var s in state.Stacks)
        {
            if (string.Equals(s.Id, idOrName, StringComparison.Ordinal)) return s;
        }
        foreach (var s in state.Stacks)
        {
            if (string.Equals(s.Name, idOrName, StringComparison.OrdinalIgnoreCase)) return s;
        }
        return null;
    }

    public static (CueStackConfig Stack, RunCueConfig Cue)? FindCue(ShowState state, string cueId)
    {
        foreach (var s in state.Stacks)
        {
            foreach (var c in s.Cues)
            {
                if (c.Id == cueId) return (s, c);
            }
        }
        return null;
    }

    /// <summary>
    /// Presenter steps become the clicker list (one Apply Look each, the label as the cue's
    /// name and notes, loop carried over) so the page keys, NEXT / PREV and Companion keep
    /// driving the same looks. Runs once, on the schema-5 upgrade; the steps are cleared.
    /// </summary>
    public static int MigratePresenter(ShowState state)
    {
        var clicker = Clicker(state);
        var presenter = state.Presenter;
        var moved = 0;
        foreach (var step in presenter.Steps)
        {
            var look = LookService.Find(state, step.LookName);
            var cue = new RunCueConfig
            {
                Number = CueNumber.Next(clicker.Cues.Count > 0 ? clicker.Cues[^1].Number : null),
                Name = step.Label.Length > 0 ? step.Label : look?.Name ?? step.LookName,
                Notes = step.Label,
            };
            cue.Actions.Add(new CueActionConfig
            {
                Kind = CueActionKind.ApplyLook,
                Target = look?.Id ?? step.LookName, // a name still resolves later, or reads as broken
            });
            clicker.Cues.Add(cue);
            moved++;
        }
        clicker.LoopAtEnd = presenter.Loop || clicker.LoopAtEnd;
        presenter.Steps.Clear();
        Caller(state); // both lists always exist
        return moved;
    }

    /// <summary>Every cue action that references a look, stinger or stack, for delete-refusal and rename checks.</summary>
    public static IEnumerable<(CueStackConfig Stack, RunCueConfig Cue, CueActionConfig Action)> AllActions(ShowState state)
    {
        foreach (var s in state.Stacks)
        {
            foreach (var c in s.Cues)
            {
                foreach (var a in c.Actions) yield return (s, c, a);
            }
        }
    }
}
