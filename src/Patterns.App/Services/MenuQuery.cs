using Patterns.Core.LowerThirds;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// MENU on the wire: the right-click menu the desk would show for a thing, answered as JSON with
/// each entry's wire line, so a tablet, a Companion page or a script can offer the desk's own
/// choices and send the desk's own words back. The words after MENU name the thing: SCREEN 2 (or
/// a screen id, or a canvas key), PGM, PREVIEW, CUE 03.020 (a number, a name, an id, or STANDBY),
/// LOOK Walk-in, LT Neon (a number or a name), PERSON Jane, LAYER 1, CLOCK / LOGO / MESSAGE /
/// PIP / WEATHER / BADGE / INFO, COUNTDOWN; bare MENU is the programme. Built from the same
/// facts the desk's menus read.
/// </summary>
public static class MenuQuery
{
    public static string Answer(AppServices s, string text)
    {
        var menu = Build(s, text, out var problem);
        return menu is null ? ControlProtocol.Err(problem) : ControlProtocol.Ok(MenuJson.Write(menu));
    }

    /// <summary>The menu for the words after MENU, or null with why not.</summary>
    public static DeskMenu? Build(AppServices s, string text, out string problem)
    {
        problem = "";
        var facts = DeskMenuFacts.Desk(s);
        var state = s.State;
        var words = (text ?? "").Trim().Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var what = words.Length > 0 ? words[0].ToUpperInvariant() : "";
        var rest = words.Length > 1 ? words[1].Trim() : "";
        switch (what)
        {
            case "":
            case "PGM":
            case "PROGRAM":
            case "PROGRAMME":
                return DeskMenus.Program(facts, DeskMenuFacts.Screen(s, ""));
            case "PVW":
            case "PREVIEW":
                return DeskMenus.Preview(facts);
            case "SCREEN":
            {
                var target = ResolveTarget(s, rest);
                if (target is null)
                {
                    problem = $"no screen '{rest}'";
                    return null;
                }
                return target.Length == 0 ? DeskMenus.Program(facts, DeskMenuFacts.Screen(s, "")) : DeskMenus.Screen(facts, DeskMenuFacts.Screen(s, target));
            }
            case "CUE":
            {
                var cue = FindCue(s, rest);
                if (cue is null)
                {
                    problem = $"no cue '{rest}'";
                    return null;
                }
                var standby = s.CueStack.StandbyCue?.Id == cue.Id;
                var report = CueValidator.ValidateOne(state, cue, s.ValidationContext);
                var broken = report.Broken.TryGetValue(cue.Id, out var why) ? why : "";
                return DeskMenus.Cue(facts, DeskMenuFacts.Cue(state, cue, standby, broken, CueSummary.Describe(state, cue)));
            }
            case "LOOK":
            {
                var look = rest.Length == 0 ? null : LookService.Find(state, rest);
                if (look is null)
                {
                    problem = $"no look '{rest}'";
                    return null;
                }
                var l = facts.Looks.FirstOrDefault(x => x.Id == look.Id) ?? new MenuLook(look.Id, look.Name, look.Hotkey, false, false);
                return DeskMenus.Look(facts, l);
            }
            case "LT":
            case "LOWERTHIRD":
            case "LOWER":
            {
                var design = FindDesign(state, rest);
                if (design is null)
                {
                    problem = $"no lower third '{rest}'";
                    return null;
                }
                var d = facts.Designs.FirstOrDefault(x => x.Id == design.Id) ?? new MenuDesign(design.Id, design.Name, design.IsDefault, design.IsOnAir, design.IsInPreview);
                return DeskMenus.LowerThird(facts, d);
            }
            case "PERSON":
            {
                var entry = rest.Length == 0 ? null : state.LowerThirds.FindEntry(rest);
                if (entry is null)
                {
                    problem = $"no person '{rest}'";
                    return null;
                }
                return DeskMenus.Person(facts, new MenuPerson(entry.Id, entry.Name, entry.Summary));
            }
            case "LAYER":
            {
                var index = rest == "2" ? 2 : rest is "" or "1" ? 1 : 0;
                if (index == 0)
                {
                    problem = "a layer is 1 or 2";
                    return null;
                }
                return DeskMenus.Layer(facts, DeskMenuFacts.Layer(state.Pattern, index));
            }
            case "COUNTDOWN":
            case "TIMER":
                return DeskMenus.Overlay(facts, DeskMenuFacts.Overlay(s, "countdown"));
            case "OVERLAY":
            case "CLOCK":
            case "LOGO":
            case "MESSAGE":
            case "PIP":
            case "WEATHER":
            case "BADGE":
            case "INFO":
            {
                var kind = (what == "OVERLAY" ? rest : what).ToLowerInvariant();
                if (PreviewEdits.OverlayLabel(kind).Length == 0)
                {
                    problem = $"no overlay '{kind}' — clock, logo, message, pip, weather, badge, info, countdown";
                    return null;
                }
                return DeskMenus.Overlay(facts, DeskMenuFacts.Overlay(s, kind));
            }
            default:
                problem = $"MENU what? SCREEN n · PGM · PREVIEW · CUE <number|name|standby> · LOOK <name> · LT <n|name> · PERSON <name> · LAYER 1|2 · CLOCK · LOGO · MESSAGE · PIP · WEATHER · COUNTDOWN — not '{text}'";
                return null;
        }
    }

    /// <summary>A screen number (overview order), a screen id, a canvas key, or PGM ("") — null for a stranger.</summary>
    private static string? ResolveTarget(AppServices s, string word)
    {
        if (ContentTargets.IsProgramTarget(word)) return "";
        if (int.TryParse(word, out var n))
        {
            var ordered = Rig.OrderedLivePlacements(s.State, s.Screens.All);
            return n >= 1 && n <= ordered.Count ? ordered[n - 1].Placement.ScreenId : null;
        }
        return ContentTargets.IsInRig(s.State, word) ? word : null;
    }

    private static RunCueConfig? FindCue(AppServices s, string word)
    {
        if (word.Length == 0 || word.Equals("standby", StringComparison.OrdinalIgnoreCase)) return s.CueStack.StandbyCue ?? CueStacks.Caller(s.State).Cues.FirstOrDefault();
        foreach (var stack in s.State.Stacks)
        {
            foreach (var cue in stack.Cues)
            {
                if (cue.Id == word || cue.Number == word || string.Equals(cue.Name, word, StringComparison.OrdinalIgnoreCase)) return cue;
            }
        }
        return null;
    }

    private static LowerThirdDesign? FindDesign(ShowState state, string word)
    {
        if (word.Length == 0) return state.LowerThirds.DefaultDesign;
        if (int.TryParse(word, out var n)) return n >= 1 && n <= state.LowerThirds.Designs.Count ? state.LowerThirds.Designs[n - 1] : null;
        return state.LowerThirds.Find(word);
    }
}
