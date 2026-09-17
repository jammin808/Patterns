using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 74: the desk's one table of rails and pages, and the words after NAV taken apart —
/// what the shell, the wire and a deck's navigator all read.
/// </summary>
public class DeskPagesTests
{
    [Fact]
    public void TheTableHasFiveRailsAndEveryPageSitsOnOne()
    {
        Assert.Equal(new[] { "SHOW", "PLAN", "BUILD", "SETUP", "ADMIN" }, DeskPages.Rails.Select(r => r.Label));
        Assert.Equal(29, DeskPages.All.Count);
        Assert.All(DeskPages.All, p => Assert.NotNull(DeskPages.FindRail(p.Rail)));
        Assert.Equal(DeskPages.All.Count, DeskPages.All.Select(p => p.Header).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(new[] { "Cues", "Looks", "Install" }, DeskPages.Of("Plan").Select(p => p.Header));
        Assert.Equal("Panel", DeskPages.All[0].Header);
        Assert.Equal("Run", DeskPages.All[1].Header);
        Assert.All(DeskPages.Rails, r => Assert.NotEmpty(DeskPages.Of(r.Id)));
    }

    [Fact]
    public void FindIsCaseBlindAndARailAnswersToItsLabelOrItsId()
    {
        Assert.Equal("Lower thirds", DeskPages.Find("lower THIRDS")!.Header);
        Assert.Null(DeskPages.Find("Lower"));
        Assert.Null(DeskPages.Find(""));
        Assert.Equal("Plan", DeskPages.FindRail("plan")!.Id);
        Assert.Equal("Plan", DeskPages.FindRail("PLAN")!.Id);
        Assert.Null(DeskPages.FindRail("Cues"));
    }

    [Fact]
    public void ResolveTakesTheLongestPageHeaderOffTheFrontAndLeavesTheItem()
    {
        Assert.Equal(("Lower thirds", "Neon"), DeskPages.Resolve("Lower thirds Neon")!.Value);
        Assert.Equal(("Lower thirds", ""), DeskPages.Resolve("lower thirds")!.Value);
        Assert.Equal(("Cues", "03.020"), DeskPages.Resolve("cues 03.020")!.Value);
        Assert.Equal(("Cues", "Walk in music"), DeskPages.Resolve("Cues Walk in music")!.Value);     // the rest is the item, spaces and all
        Assert.Equal(("PLAN", ""), DeskPages.Resolve("plan")!.Value);                                   // a rail by its label
        Assert.Equal(("Screens", "2"), DeskPages.Resolve("Screens 2")!.Value);
        Assert.Null(DeskPages.Resolve("Cuesheet"));                                                    // a word that merely starts with a header is a stranger
        Assert.Null(DeskPages.Resolve("Nowhere 3"));
        Assert.Null(DeskPages.Resolve(""));
    }

    [Fact]
    public void TheNavFactsSayWhereTheDeskIsInOneLine()
    {
        var f = new NavFacts("Cues", "PLAN", "#6E9BFF", false) { SettingsOpen = true, SettingsTitle = "SELECTED CUE · 03.020 Keynote", Back = "Looks", Selection = "cue 03.020 Keynote" };
        Assert.Equal("On the Cues page · cue 03.020 Keynote · settings column: SELECTED CUE · 03.020 Keynote · back: Looks", f.Words);
        Assert.Equal("On the Run surface", new NavFacts("Run", "SHOW", "#2EE68A", true).Words);
        Assert.Equal("On the Panel page", new NavFacts("Panel", "SHOW", "#2EE68A", false).Words);
    }
}
