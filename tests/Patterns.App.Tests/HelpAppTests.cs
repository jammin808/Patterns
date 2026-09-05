using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The Help page on a live desk: every topic's pages are real shell pages; the search filters the
/// cards and opens them with the words around the match; a section chip narrows the list; READ ALL
/// opens everything; a page's ? TIPS knows its topics and one press opens the Help page on that
/// card; GO on a card's page opens the page; the page renders.
/// </summary>
public class HelpAppTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static string CurrentPage(MainViewModel vm) => vm.PageStrip.First(c => c.IsCurrent).Header;

    [AvaloniaFact]
    public void TheCatalogueSearchesFiltersAndOpensTopicsAndEveryPageItNamesIsReal()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;

            // Every page a topic names is a shell page, so GO can always open it.
            var headers = Shell.Pages.Select(p => p.Header).ToHashSet(StringComparer.Ordinal);
            foreach (var topic in HelpTopics.All)
            {
                foreach (var page in topic.Pages) Assert.Contains(page, headers);
            }

            // The whole catalogue, closed, in order; ALL is the section.
            Assert.Equal(HelpTopics.All.Count, vm.HelpRows.Count);
            Assert.All(vm.HelpRows, r => Assert.False(r.IsOpen));
            Assert.True(vm.HelpGroups[0].IsCurrent);
            Assert.Equal("ALL", vm.HelpGroups[0].Label);
            Assert.Contains($"{HelpTopics.All.Count} topics", vm.HelpResultText);

            // A search: the hits only, strongest first, each open with the words around the match.
            vm.HelpQuery = "stinger";
            Assert.True(vm.IsHelpSearching);
            Assert.Equal("vog-stingers", vm.HelpRows[0].Id);
            Assert.All(vm.HelpRows, r => Assert.True(r.IsOpen && r.HasSnippet));
            Assert.Contains("match", vm.HelpResultText);
            vm.HelpQuery = "zzzqqq";
            Assert.Empty(vm.HelpRows);
            Assert.Contains("Nothing matches", vm.HelpResultText);
            vm.ClearHelpCommand.Execute(null);
            Assert.False(vm.IsHelpSearching);
            Assert.Equal(HelpTopics.All.Count, vm.HelpRows.Count);
            Assert.All(vm.HelpRows, r => Assert.False(r.HasSnippet));

            // A section chip narrows the list to its group.
            vm.HelpGroups.First(c => c.Group == HelpGroup.Control).PickCommand.Execute(null);
            Assert.Equal(HelpGroup.Control, vm.HelpGroupFilter);
            Assert.All(vm.HelpRows, r => Assert.Equal(HelpGroup.Control, r.Topic.Group));
            Assert.True(vm.HelpGroups.First(c => c.Group == HelpGroup.Control).IsCurrent);
            Assert.False(vm.HelpGroups[0].IsCurrent);
            Assert.Contains("CONTROL", vm.HelpResultText);

            // A card opens and closes on its own; READ ALL opens every card.
            var first = vm.HelpRows[0];
            first.ToggleCommand.Execute(null);
            Assert.True(first.IsOpen);
            Assert.Equal("▾", first.OpenText);
            first.ToggleCommand.Execute(null);
            Assert.False(first.IsOpen);
            vm.HelpReadAll = true;
            Assert.All(vm.HelpRows, r => Assert.True(r.IsOpen));
            vm.HelpReadAll = false;
            Assert.All(vm.HelpRows, r => Assert.False(r.IsOpen));

            // A page's ? TIPS knows the topics it belongs to; one press opens Help on that card, every section shown.
            var panelTopics = vm.HelpTopicsFor("Panel");
            Assert.Contains(panelTopics, t => t.Id == "show-panel");
            vm.SelectPage(Shell.PanelPage);
            Assert.Equal("Panel", CurrentPage(vm));
            vm.OpenHelpTopic("show-panel");
            Assert.Equal("Help", CurrentPage(vm));
            Assert.Null(vm.HelpGroupFilter);
            Assert.False(vm.IsHelpSearching);
            Assert.Equal(HelpTopics.All.Count, vm.HelpRows.Count);
            Assert.True(vm.HelpRows.Single(r => r.Id == "show-panel").IsOpen);
            Assert.Single(vm.HelpRows, r => r.IsOpen);
            Assert.Contains("Show panel", vm.HelpResultText);
            vm.OpenHelpTopic("no-such-topic");                                         // nothing happens
            Assert.Equal("Help", CurrentPage(vm));

            // GO on a card's page opens the page.
            var cues = vm.HelpRows.Single(r => r.Id == "cue-sheet");
            cues.Pages.First(p => p.Header == "Cues").GoCommand.Execute(null);
            Assert.Equal("Cues", CurrentPage(vm));

            // The page renders: the search, the sections, the cards, the open card's parts.
            vm.OpenHelpTopic("show-panel");
            Settle(window);
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("TOPICS", texts);
            Assert.Contains("HOW IT WORKS", texts);
            Assert.Contains("DO THIS", texts);
            Assert.Contains("ON THE WIRE", texts);
            Assert.Contains("The Show panel as the control surface", texts);
            Assert.Contains(window.GetVisualDescendants().OfType<ToggleButton>(), t => t.Content as string == "READ ALL");
            Assert.Contains(window.GetVisualDescendants().OfType<ToggleButton>(), t => t.Content as string == "START HERE");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "");
            Assert.NotEmpty(window.CurrentPageTips());
        }
        finally
        {
            b.Window.Close();
            b.Services.Shutdown();
        }
    }
}
