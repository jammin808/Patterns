using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Output hot-plug, pure: a display that re-indexed keeps its screen, one unplugged is lost, its
/// own display back is returned, a display never met is a stranger — and whether a stranger can
/// stand in for a lost screen.
/// </summary>
public class HotPlugWatchTests
{
    private static DisplayFact Display(int index, string label, int w, int h, int x, int y, int hz = 60)
        => new($"{index}:{w}x{h}@{x},{y}", label, w, h, x, y, hz);

    private static ScreenPlacement Live(DisplayFact d, string label = "")
        => new() { ScreenId = d.Id, CustomLabel = label, DisplayKey = d.Key, DisplayOrigin = d.Origin, DisplayHz = d.Hz, Enabled = true };

    private static ScreenPlacement Lost(string label, string key, string origin, int hz, int w, int h)
        => new() { ScreenId = HotPlugWatch.LostId(), CustomLabel = label, DisplayKey = key, DisplayOrigin = origin, DisplayHz = hz, Planned = true, PlannedWidth = w, PlannedHeight = h, LostAtUtc = DateTime.UtcNow, Enabled = false, UserPinned = true, WasEnabled = true };

    [Fact]
    public void TheKeysAndTheIdsReadBothWays()
    {
        Assert.Equal("EPSON PJ|1920x1080", HotPlugWatch.KeyOf(" EPSON PJ ", 1920, 1080));
        Assert.Equal("EPSON PJ", HotPlugWatch.LabelOf("EPSON PJ|1920x1080"));
        Assert.Equal((1920, 1080), HotPlugWatch.SizeOf("EPSON PJ|1920x1080"));
        Assert.Null(HotPlugWatch.SizeOf("EPSON PJ|wide"));
        Assert.Null(HotPlugWatch.SizeOf(""));
        Assert.Equal("1920,0", HotPlugWatch.OriginOf(1920, 0));
        var id = HotPlugWatch.LostId();
        Assert.StartsWith(ScreenPlacement.PlannedIdPrefix, id);
        Assert.StartsWith(HotPlugWatch.LostIdPrefix, id);
        Assert.NotEqual(id, HotPlugWatch.LostId());
        var d = Display(1, "EPSON PJ", 1920, 1080, 1920, 0);
        Assert.Equal("EPSON PJ 1920×1080 @ 60 Hz", d.Words);
        Assert.Equal("Display 1280×720", new DisplayFact("0:1280x720@0,0", "", 1280, 720, 0, 0).Words);
    }

    [Fact]
    public void NothingChangedIsAnEmptyPlanAndAScreenWithNoMemoryIsLostWhenItsIdGoes()
    {
        var a = Display(0, "Desk", 1920, 1080, 0, 0);
        var b = Display(1, "EPSON PJ", 1920, 1080, 1920, 0);
        var placements = new[] { Live(a), Live(b) };
        Assert.True(HotPlugWatch.Decide(placements, new[] { a, b }).IsEmpty);

        var forgetful = new ScreenPlacement { ScreenId = b.Id };   // a show file from before the watch: nothing remembered
        var plan = HotPlugWatch.Decide(new[] { Live(a), forgetful }, new[] { a });
        Assert.Same(forgetful, Assert.Single(plan.Lost));
        Assert.Empty(plan.Renamed);
        Assert.Empty(plan.Strangers);
    }

    [Fact]
    public void ADisplayUnpluggedOnTheLeftReIndexesTheRestAndOnlyTheOneThatWentIsLost()
    {
        var desk = Display(0, "Desk", 1920, 1080, 0, 0);
        var pjL = Display(1, "EPSON PJ", 1920, 1080, 1920, 0);
        var pjR = Display(2, "EPSON PJ", 1920, 1080, 3840, 0);
        var left = Live(pjL, "PJ left");
        var right = Live(pjR, "PJ right");
        var placements = new[] { Live(desk, "Desk"), left, right };

        // The desk monitor goes; Windows re-anchors: the projectors come back as 0 and 1, shifted left.
        var pjL2 = Display(0, "EPSON PJ", 1920, 1080, 0, 0);
        var pjR2 = Display(1, "EPSON PJ", 1920, 1080, 1920, 0);
        var plan = HotPlugWatch.Decide(placements, new[] { pjL2, pjR2 });
        Assert.Equal("Desk", Assert.Single(plan.Lost).CustomLabel);                       // its id now carries another make: not its display
        // Two of one make and size: the one whose id and place still hold keeps them, the other takes the free display — neither is lost.
        var (renamedPlacement, renamedDisplay) = Assert.Single(plan.Renamed);
        Assert.Same(right, renamedPlacement);
        Assert.Equal(pjL2.Id, renamedDisplay.Id);
        Assert.Equal(pjR2.Id, left.ScreenId);
        Assert.Empty(plan.Strangers);
        Assert.Empty(plan.Returned);

        // Two of another make, told apart by place: each keeps its own.
        var tvL = Display(1, "LG", 1920, 1080, 1920, 0);
        var tvR = Display(2, "LG", 1920, 1080, 3840, 0);
        var tvs = new[] { Live(tvL, "TV left"), Live(tvR, "TV right") };
        var tvL2 = Display(0, "LG", 1920, 1080, 0, 0);
        var tvR2 = Display(1, "LG", 1920, 1080, 1920, 0);
        var shifted = HotPlugWatch.Decide(tvs, new[] { tvL2, tvR2 });
        Assert.Empty(shifted.Lost);
        var (tvPlacement, tvDisplay) = Assert.Single(shifted.Renamed);
        Assert.Equal("TV right", tvPlacement.CustomLabel);
        Assert.Equal(tvL2.Id, tvDisplay.Id);
    }

    [Fact]
    public void AModeChangeOrALikeForLikeSwapKeepsTheScreenByNameAndPlace()
    {
        var pj = Display(1, "EPSON PJ", 1920, 1080, 1920, 0);
        var placement = Live(pj, "Stage");
        var smaller = Display(1, "EPSON PJ", 1280, 720, 1920, 0);   // the same projector in another mode
        var plan = HotPlugWatch.Decide(new[] { placement }, new[] { smaller });
        var (p, d) = Assert.Single(plan.Renamed);
        Assert.Same(placement, p);
        Assert.Equal(smaller.Id, d.Id);
        Assert.Empty(plan.Lost);

        var elsewhere = Display(1, "EPSON PJ", 1280, 720, 4000, 0);  // another size somewhere else: not the same screen
        var plan2 = HotPlugWatch.Decide(new[] { placement }, new[] { elsewhere });
        Assert.Same(placement, Assert.Single(plan2.Lost));
        Assert.Equal(elsewhere.Id, Assert.Single(plan2.Strangers).Id);
    }

    [Fact]
    public void ALostScreensOwnDisplayBackIsReturnedAndAnyOtherIsAStranger()
    {
        var desk = Display(0, "Desk", 1920, 1080, 0, 0);
        var lost = Lost("Stage", "EPSON PJ|1920x1080", "1920,0", 60, 1920, 1080);
        var placements = new[] { Live(desk), lost };
        Assert.True(HotPlugWatch.IsLost(lost));
        Assert.Contains(lost, HotPlugWatch.LostScreens(new ShowState { Output = { Placements = { lost } } }));

        var back = Display(1, "EPSON PJ", 1920, 1080, 1920, 0);
        var plan = HotPlugWatch.Decide(placements, new[] { desk, back });
        var (p, d) = Assert.Single(plan.Returned);
        Assert.Same(lost, p);
        Assert.Equal(back.Id, d.Id);
        Assert.Empty(plan.Strangers);

        var other = Display(1, "BENQ", 1280, 720, 1920, 0);
        var plan2 = HotPlugWatch.Decide(placements, new[] { desk, other });
        Assert.Empty(plan2.Returned);
        Assert.Equal(other.Id, Assert.Single(plan2.Strangers).Id);

        // A lost screen is matched by name and size only: the same projector in another mode is not "back" by itself.
        var otherMode = Display(1, "EPSON PJ", 1280, 720, 1920, 0);
        var plan3 = HotPlugWatch.Decide(placements, new[] { desk, otherMode });
        Assert.Empty(plan3.Returned);
        Assert.Single(plan3.Strangers);
    }

    [Fact]
    public void AStrangerStandsInWhenItMatchesOrCanBeForcedAndTheWordsSayWhyNot()
    {
        var lost = Lost("Stage", "EPSON PJ|1920x1080", "1920,0", 60, 1920, 1080);
        var none = Array.Empty<(int, int, int)>();

        var same = HotPlugWatch.Assess(lost, Display(1, "BENQ", 1920, 1080, 1920, 0, 60), none);
        Assert.True(same.Fits);
        Assert.True(same.Offered);
        Assert.StartsWith("BENQ 1920×1080 @ 60 Hz matches Stage (1920×1080 @ 60 Hz) — USE AS SUBSTITUTE", same.Words);

        var unknownRate = HotPlugWatch.Assess(lost, Display(1, "BENQ", 1920, 1080, 1920, 0, 0), none);
        Assert.True(unknownRate.Fits);                                                       // a rate nobody knows is not a mismatch

        var slower = HotPlugWatch.Assess(lost, Display(1, "BENQ", 1920, 1080, 1920, 0, 50), new[] { (1920, 1080, 60), (1920, 1080, 50) });
        Assert.False(slower.Fits);
        Assert.Equal((1920, 1080, 60), slower.Mode);
        Assert.Contains("can be forced to 1920×1080 @ 60 Hz", slower.Words);

        var smaller = HotPlugWatch.Assess(lost, Display(1, "BENQ", 1280, 720, 1920, 0, 60), new[] { (1280, 720, 60), (1920, 1080, 60) });
        Assert.True(smaller.Offered);
        Assert.Equal((1920, 1080, 60), smaller.Mode);

        var noMode = HotPlugWatch.Assess(lost, Display(1, "LG TV", 3840, 2160, 1920, 0, 60), new[] { (3840, 2160, 60) });
        Assert.False(noMode.Offered);
        Assert.Contains("another resolution (3840×2160) and no 1920×1080 mode offered", noMode.Words);
        Assert.EndsWith("It can be ITS OWN SCREEN.", noMode.Words);

        var aspect = HotPlugWatch.Assess(lost, Display(1, "Portrait", 1080, 1920, 1920, 0, 60), none);
        Assert.False(aspect.Offered);
        Assert.Contains("another aspect (1080×1920 against 1920×1080)", aspect.Words);

        var rate = HotPlugWatch.Assess(lost, Display(1, "BENQ", 1920, 1080, 1920, 0, 50), none);
        Assert.False(rate.Offered);
        Assert.Contains("another rate (50 Hz against 60 Hz) and no 1920×1080 @ 60 Hz mode offered", rate.Words);
    }

    [Fact]
    public void TheWordsNameTheScreenItsDisplayAndWhenItWent()
    {
        var at = new DateTime(2026, 9, 12, 19, 41, 58, DateTimeKind.Utc);
        var lost = Lost("Stage", "EPSON PJ|1920x1080", "1920,0", 60, 1920, 1080);
        lost.LostAtUtc = at;
        var words = HotPlugWatch.LostWords(lost);
        Assert.StartsWith("'Stage' (EPSON PJ 1920×1080 @ 60 Hz) unplugged at ", words);
        Assert.EndsWith("plug it back in and it comes back on; another display connected can stand in for it.", words);
        Assert.Equal("Stage", HotPlugWatch.LostName(lost));
        Assert.Equal("EPSON PJ", HotPlugWatch.LostName(new ScreenPlacement { DisplayKey = "EPSON PJ|1920x1080" }));
        Assert.Equal("the screen", HotPlugWatch.LostName(new ScreenPlacement()));
        Assert.Equal("", HotPlugWatch.HealthWords(Array.Empty<ScreenPlacement>()));
        Assert.StartsWith("SCREEN MISSING: 'Stage' unplugged at", HotPlugWatch.HealthWords(new[] { lost }));
        Assert.StartsWith("2 SCREENS MISSING: 'Stage' unplugged at", HotPlugWatch.HealthWords(new[] { lost, Lost("Wall", "LG|3840x2160", "0,0", 0, 3840, 2160) }));
    }

    /// <summary>Round 76: a pass's renames followed through — a step aside and the rename on it read as one move; a loop is cut.</summary>
    [Fact]
    public void RenamesAreResolvedThroughTheirChains()
    {
        var resolved = HotPlugWatch.Resolved(new Dictionary<string, string>
        {
            ["8:a"] = "7:a",                     // re-indexed
            ["7:a"] = "planned:move-1",          // stepped aside first
            ["planned:move-1"] = "6:a",          // then renamed on
            ["x"] = "y",
            ["y"] = "x",                         // a loop: cut where it repeats
        });
        Assert.Equal("6:a", resolved["8:a"]);
        Assert.Equal("6:a", resolved["7:a"]);
        Assert.Equal("6:a", resolved["planned:move-1"]);
        Assert.Equal("y", resolved["x"]);
        Assert.Equal("x", resolved["y"]);
        Assert.Empty(HotPlugWatch.Resolved(new Dictionary<string, string>()));
    }
}
