using Patterns.App.Services;
using Patterns.Core.LowerThirds;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// Round 74: the desk as a deck turns it. NAV &lt;page&gt; [item] goes through the same route the
/// menus' GO TO entries use, so a deck selects a cue, a screen, a design or a person exactly as a
/// right-click would; BACK walks the pages the operator (or the deck) came through; the settings
/// column opens and closes for the selection; and the facts — the page, the column, the page
/// before, the selection — are what STATE's nav row, the Eye's desk node and a following deck read.
/// </summary>
public sealed partial class MainViewModel : IDeskNavigator
{
    /// <summary>The pages behind this one, oldest first, bounded: what BACK walks.</summary>
    private readonly List<int> _pageHistory = new();
    private bool _navigatingBack;
    private const int PageHistoryDepth = 32;

    /// <summary>A page left for another: remembered for BACK, unless BACK itself is doing the leaving.</summary>
    private void RememberPage(int from, int to)
    {
        if (_navigatingBack || from == to) return;
        _pageHistory.Add(from);
        if (_pageHistory.Count > PageHistoryDepth) _pageHistory.RemoveAt(0);
    }

    public ActionResult Go(string pageOrRail, string item)
    {
        int index;
        if (DeskPages.Find(pageOrRail) is { } page)
        {
            index = Shell.IndexOf(page.Header);
        }
        else if (DeskPages.FindRail(pageOrRail) is { } rail)
        {
            var group = Enum.Parse<ShellGroup>(rail.Id);
            index = _lastPage.TryGetValue(group, out var last) ? last : Shell.FirstPage(group);
        }
        else
        {
            return ActionResult.Refused($"No page or rail called '{pageOrRail}' — a page: {string.Join(", ", DeskPages.All.Select(p => p.Header))}; a rail: {string.Join(", ", DeskPages.Rails.Select(r => r.Label))}.");
        }
        var header = Shell.Pages[index].Header;
        if (!Shell.IsVisible(_services.Profile, header)) return ActionResult.Refused($"The {header} page is not on this node.");
        var words = "";
        if (item.Trim().Length > 0)
        {
            var id = ResolveNavItem(header, item.Trim(), out var problem);
            if (id is null) return ActionResult.Refused(problem);
            words = GoTo(new MenuRoute(header, id)) ?? "";
        }
        else
        {
            SelectPage(index);
        }
        // The Run surface holds the desk while the stack is armed: SelectPage said so on the status line.
        if (_page != index) return ActionResult.Refused(StatusMessage);
        return ActionResult.Done(words.Length > 0 ? words : $"{header} page.");
    }

    /// <summary>The item after a page's name as the route's id: a cue by number, name or id; a look by name; a screen by number or id (PGM for the programme); a design by number or name, or a person.</summary>
    private string? ResolveNavItem(string header, string word, out string problem)
    {
        problem = "";
        switch (header)
        {
            case "Cues":
            {
                var found = CueStacks.FindCueByWord(State, word);
                if (found is null) problem = $"No cue '{word}' on the Cues page.";
                return found?.Cue.Id;
            }
            case "Looks":
            {
                var look = LookService.Find(State, word);
                if (look is null) problem = $"No look '{word}'.";
                return look?.Id;
            }
            case "Screens":
            case "Multiview":
            case "Pattern":
            case "Layers":
            {
                var target = MenuQuery.ResolveTarget(_services, word);
                if (target is null) problem = $"No screen '{word}' — a number in the overview's order, a screen id, or PGM.";
                return target;
            }
            case "Lower thirds":
            {
                var design = MenuQuery.FindDesign(State, word);
                if (design is not null) return design.Id;
                var person = State.LowerThirds.FindEntry(word);
                if (person is not null) return person.Id;
                problem = $"No lower third or person '{word}'.";
                return null;
            }
            default:
                problem = $"The {header} page has nothing to select by name — NAV {header} alone opens it.";
                return null;
        }
    }

    public ActionResult Back()
    {
        if (_pageHistory.Count == 0) return ActionResult.Refused("Nothing to go back to.");
        var index = _pageHistory[^1];
        _pageHistory.RemoveAt(_pageHistory.Count - 1);
        _navigatingBack = true;
        try
        {
            SelectPage(index);
        }
        finally
        {
            _navigatingBack = false;
        }
        if (_page != index) return ActionResult.Refused(StatusMessage);
        return ActionResult.Done($"Back to the {Shell.Pages[index].Header} page.");
    }

    public ActionResult Home()
    {
        var index = Shell.HomePage(_services.Profile);
        SelectPage(index);
        if (_page != index) return ActionResult.Refused(StatusMessage);
        return ActionResult.Done($"Home — the {Shell.Pages[index].Header} page.");
    }

    public ActionResult Settings(string mode)
    {
        var header = Shell.Pages[_page].Header;
        switch (mode.Trim().ToUpperInvariant())
        {
            case "ON":
                PopOut.ClearDismissal();
                RefreshPopOut();
                if (!PopOut.IsOpen)
                {
                    return ActionResult.Refused(header is "Cues" or "Screens" or "Lower thirds"
                        ? $"Nothing is selected on the {header} page — select a thing first (NAV {header} <name>)."
                        : $"The {header} page has no settings column.");
                }
                return ActionResult.Done($"Settings column: {PopOut.Title}.");
            case "OFF":
                if (!PopOut.IsOpen) return ActionResult.Done("The settings column is closed.");
                PopOut.Dismiss();
                return ActionResult.Done("Settings column closed.");
            case "":
            case "TOGGLE":
                return Settings(PopOut.IsOpen ? "OFF" : "ON");
            default:
                return ActionResult.Refused($"NAV SETTINGS ON, OFF or TOGGLE — not '{mode}'.");
        }
    }

    public NavFacts Facts()
    {
        var page = Shell.Pages[_page];
        return new NavFacts(page.Header, Shell.Info(page.Group).Label, page.Hue, _isRunLayout)
        {
            SettingsOpen = PopOut.IsOpen,
            SettingsKey = PopOut.Key,
            SettingsTitle = PopOut.Title,
            SettingsIdentity = PopOut.Identity,
            Back = _pageHistory.Count > 0 ? Shell.Pages[_pageHistory[^1]].Header : "",
            Selection = NavSelectionWords(page.Header),
        };
    }

    /// <summary>The thing selected on the page, in words, for STATE and the Eye.</summary>
    private string NavSelectionWords(string header) => header switch
    {
        "Cues" when Cues.SelectedCue is { } cue => $"cue {cue.Number} {cue.Name}".TrimEnd(),
        "Screens" when Screens.HasSelection => $"screen {Screens.SelectedScreenTitle}",
        "Lower thirds" when SelectedLowerThird is { } design => $"design {design.Name}",
        "Pattern" or "Layers" => $"editing {(_editTarget.ScreenId is { Length: > 0 } id ? TargetTitle(id) : "the programme")}",
        _ => "",
    };

    public PatternConfig EditingPicture => ActivePattern;

    public LowerThirdDesign NewLowerThird(string preset, string name)
    {
        var design = Designer.New(preset, name);
        SelectedLowerThird = design;
        SelectPage(Shell.IndexOf("Lower thirds"));
        StatusMessage = $"Lower third '{design.Name}' added.";
        return design;
    }
}
