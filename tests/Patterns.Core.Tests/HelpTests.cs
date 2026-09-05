using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The Help catalogue: every topic whole and filed, the sections in the order a show happens, a
/// topic found by its page; the search — words trimmed and lowered, every word required, the
/// title and the search words weighing most, the strongest hit first, the words around a match.
/// </summary>
public class HelpTests
{
    [Fact]
    public void EveryTopicIsWholeAndFiledAndTheSectionsRunInShowOrder()
    {
        Assert.True(HelpTopics.All.Count >= 36);
        Assert.Equal(HelpTopics.All.Count, HelpTopics.All.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var topic in HelpTopics.All)
        {
            Assert.Matches("^[a-z0-9-]+$", topic.Id);
            Assert.True(topic.Title.Length > 8, topic.Id);
            Assert.True(topic.Where.Length > 20, topic.Id);                          // where it sits in the workflow
            Assert.True(topic.Body.Length > 120, topic.Id);                          // how it works, in depth
            Assert.NotEmpty(topic.Pages);
            Assert.True(topic.Keywords.Count >= 3, topic.Id);
            Assert.All(topic.Keywords, k => Assert.Equal(k, k.ToLowerInvariant()));
            Assert.Equal(topic.HasSteps, topic.Steps.Count > 0);
            Assert.Equal(topic.HasWire, topic.Wire.Length > 0);
        }
        foreach (var group in HelpTopics.Groups)
        {
            Assert.True(HelpTopics.In(group).Count >= 2, group.ToString());
            Assert.NotEmpty(HelpTopics.GroupLabel(group));
            Assert.NotEmpty(HelpTopics.GroupBlurb(group));
        }
        // The catalogue reads in the order a show happens: START HERE first, the machine last, and the map first of all.
        Assert.Equal(HelpGroup.StartHere, HelpTopics.All[0].Group);
        Assert.Equal("how-a-show-flows", HelpTopics.All[0].Id);
        Assert.Equal(HelpGroup.TheMachine, HelpTopics.All[^1].Group);
        Assert.Equal(HelpTopics.All.Select(t => (int)t.Group).ToList(), HelpTopics.All.Select(t => (int)t.Group).OrderBy(g => g).ToList());

        Assert.NotNull(HelpTopics.Find("show-panel"));
        Assert.NotNull(HelpTopics.Find(" SHOW-PANEL "));
        Assert.Null(HelpTopics.Find("nothing-here"));
        Assert.Null(HelpTopics.Find(""));
        Assert.Contains(HelpTopics.ForPage("Panel"), t => t.Id == "show-panel");
        Assert.Contains(HelpTopics.ForPage("Cues"), t => t.Id == "cue-sheet");
        Assert.Empty(HelpTopics.ForPage("Nowhere"));
        Assert.Empty(HelpTopics.ForPage(""));
    }

    [Fact]
    public void TheSearchWantsEveryWordAndPutsTheStrongestTopicFirst()
    {
        Assert.Equal(new[] { "stinger", "hold" }, HelpSearch.Tokens("  Stinger,  hold! "));
        Assert.Equal(new[] { "cd" }, HelpSearch.Tokens("a b cd"));
        Assert.Equal(new[] { "look" }, HelpSearch.Tokens("look LOOK \"look\""));
        Assert.Empty(HelpSearch.Tokens(""));

        var stinger = HelpSearch.Find("stinger");
        Assert.Equal("vog-stingers", stinger[0].Topic.Id);
        Assert.Contains(stinger, h => h.Topic.Id == "show-panel");                 // the panel fires them
        Assert.True(stinger[0].Score > stinger[^1].Score);
        Assert.All(stinger, h => Assert.Contains("stinger", h.Snippet, StringComparison.OrdinalIgnoreCase));

        Assert.Equal("break-music", HelpSearch.Find("spotify")[0].Topic.Id);
        Assert.Equal("keys", HelpSearch.Find("F5")[0].Topic.Id);
        Assert.Equal("interactive", HelpSearch.Find("arduino")[0].Topic.Id);
        Assert.Equal("edge-blend", HelpSearch.Find("projector overlap")[0].Topic.Id);

        // Every word must be found: "screen look" is the panel and the switcher, never the Spotify topic.
        var both = HelpSearch.Find("SCREEN LOOK");
        Assert.Contains(both, h => h.Topic.Id == "show-panel");
        Assert.Contains(both, h => h.Topic.Id == "switcher");
        Assert.DoesNotContain(both, h => h.Topic.Id == "break-music");
        Assert.Empty(HelpSearch.Find("zzzqqq"));
        Assert.Empty(HelpSearch.Find("stinger zzzqqq"));
        Assert.Empty(HelpSearch.Find(""));
        Assert.Empty(HelpSearch.Find("   "));

        // The order is stable: equal scores keep the catalogue's order.
        var flow = HelpSearch.Find("show");
        Assert.True(flow.Count > 5);
        for (var i = 1; i < flow.Count; i++) Assert.True(flow[i - 1].Score >= flow[i].Score);
    }

    [Fact]
    public void TheSnippetShowsTheWordsAroundTheFirstMatch()
    {
        var vog = HelpTopics.Find("vog-stingers")!;
        var snippet = HelpSearch.Snippet(vog, new[] { "duck" });
        Assert.Contains("duck", snippet, StringComparison.OrdinalIgnoreCase);
        Assert.True(snippet.Length <= 2 * 110 + 40, snippet);

        // A word deep in the explanation is cut on both sides; the workflow line is whole when the word is there.
        var deep = HelpSearch.Snippet(HelpTopics.Find("how-a-show-flows")!, new[] { "recovery" });
        Assert.StartsWith("…", deep);
        Assert.EndsWith("…", deep);
        Assert.Contains("recovery", deep);
        var where = HelpSearch.Snippet(HelpTopics.Find("show-panel")!, new[] { "operator" });
        Assert.Equal(HelpTopics.Find("show-panel")!.Where, where);

        // A word found only in the title or the search words: the workflow line stands in.
        var title = HelpSearch.Snippet(HelpTopics.Find("keys")!, new[] { "hotkey" });
        Assert.Equal(HelpTopics.Find("keys")!.Where, title);
        Assert.Equal("", HelpSearch.Snippet(vog, Array.Empty<string>()));
    }
}
