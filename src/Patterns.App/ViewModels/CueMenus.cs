using Patterns.App.Services;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// A cue's right-click menu, on the desk and on a caller node alike: built on the run host's
/// facts (the standby, the row's problem and summary), run through the run surface's own verbs
/// (standby, GO, a note, skip) and the cue-stack edits of the core (the look, the transition,
/// the overlays, the lower third, the follow, the mark). One code path, so a caller's menu and
/// the desk's never drift.
/// </summary>
public static class CueMenus
{
    public static DeskMenuVm Build(IRunHost host, RunViewModel run, CueEditor cues, RunCueConfig cue, RunRow? row, CueRow? cueRow, DeskFacts facts,
        Func<MenuRoute, string?> go, Func<string, string?> ask, Action<string> status)
    {
        var standby = host.CueStack.StandbyCue?.Id == cue.Id;
        var summary = row?.Summary ?? cueRow?.Summary ?? CueSummary.Describe(host.State, cue);
        var problem = row?.Problem ?? cueRow?.Problem ?? "";
        var c = DeskMenuFacts.Cue(host.State, cue, standby, problem, summary);
        var menu = DeskMenus.Cue(facts, c);
        return new DeskMenuVm(menu, entry => Run(host, run, cues, cue, row, entry, facts, go, ask, status));
    }

    private static string? Run(IRunHost host, RunViewModel run, CueEditor cues, RunCueConfig cue, RunRow? row, MenuEntry entry, DeskFacts facts,
        Func<MenuRoute, string?> go, Func<string, string?> ask, Action<string> status)
    {
        string? words = null;
        switch (entry.Scope)
        {
            case MenuScope.Stack when entry.Edit == "cue.skip":
                cue.Enabled = !cue.Enabled;
                words = cue.Enabled ? $"{cue.Number} {cue.Name} is back in the run." : $"{cue.Number} {cue.Name} is skipped — GO passes over it.";
                break;
            case MenuScope.Stack:
                words = CueMenuEdits.Apply(cue, entry.Edit, facts);
                break;
            case MenuScope.Live:
                switch (entry.Edit)
                {
                    case "cue.standby":
                        words = host.Actions.Execute(ShowActionKind.CueStandby, ActionOrigin.Desk, cue.Id).Message;
                        break;
                    case "cue.go":
                        words = host.Actions.FireCue(cue, ActionOrigin.Desk).Message;
                        break;
                    case "cue.note":
                        if (row is not null) run.EditNotesCommand.Execute(row);
                        else words = go(new MenuRoute("Cues", cue.Id));
                        break;
                }
                break;
            case MenuScope.Go:
                words = entry.Route is { } route ? go(route) : null;
                break;
            case MenuScope.Ask:
                words = ask(entry.Question);
                break;
        }
        // The stack changed under the rows: the surfaces re-read it, and the row that was chosen reads through.
        row?.RaiseCue();
        run.Refresh();
        cues.Refresh();
        if (words is { Length: > 0 }) status(words);
        return words;
    }
}
