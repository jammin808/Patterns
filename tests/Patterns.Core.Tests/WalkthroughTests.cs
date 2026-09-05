using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The step-through scenarios as data, and a person's place in one of them.</summary>
public class WalkthroughTests
{
    [Fact]
    public void TheCatalogueIsWholeEveryRoleHasScenariosAndEveryCheckIsKnown()
    {
        var all = Walkthroughs.All;
        Assert.True(all.Count >= 10, $"{all.Count} scenarios");
        Assert.Equal(all.Count, all.Select(w => w.Id).Distinct().Count());
        foreach (var role in Enum.GetValues<DeskRole>())
        {
            Assert.True(Walkthroughs.For(role).Count() >= 2, $"{role} has {Walkthroughs.For(role).Count()} scenarios");
            Assert.NotEmpty(Walkthroughs.RoleLabel(role));
            Assert.NotEmpty(Walkthroughs.RoleBlurb(role));
        }
        foreach (var w in all)
        {
            Assert.NotEmpty(w.Title);
            Assert.NotEmpty(w.Goal);
            Assert.True(w.Steps.Count >= 4, $"{w.Id} has {w.Steps.Count} steps");
            foreach (var s in w.Steps)
            {
                Assert.NotEmpty(s.Page);
                Assert.NotEmpty(s.Title);
                Assert.NotEmpty(s.Detail);
                Assert.True(s.Check.Length == 0 || Walkthroughs.Checks.Contains(s.Check), $"{w.Id}: unknown check '{s.Check}'");
            }
            Assert.Contains(w.Steps, s => s.Check.Length > 0);   // every scenario has at least one step the app can tick
        }
        Assert.Equal(Walkthroughs.Checks.Count, Walkthroughs.Checks.Distinct().Count());
        Assert.Same(all.First(w => w.Id == "tech-venue"), Walkthroughs.Find("tech-venue"));
        Assert.Null(Walkthroughs.Find("nothing"));
    }

    [Fact]
    public void ProgressMovesTicksAndRestartsAndTheAppsFactsStand()
    {
        var w = Walkthroughs.Find("op-look")!;
        var p = new WalkthroughProgress(w);
        Assert.Equal(0, p.Current);
        Assert.Equal(0, p.DoneCount);
        Assert.False(p.Finished);
        Assert.Equal($"Step 1 of {w.Steps.Count} · 0 done", p.Words);
        Assert.Same(w.Steps[0], p.CurrentStep);

        // NEXT ticks the current step by hand and moves on; BACK moves without unticking.
        p.Next();
        Assert.Equal(1, p.Current);
        Assert.True(p.IsDone(0));
        Assert.False(p.IsDoneByApp(0));
        p.Back();
        Assert.Equal(0, p.Current);
        Assert.True(p.IsDone(0));
        p.Back();
        Assert.Equal(0, p.Current);

        // GO anywhere; a hand tick anywhere, and its removal.
        p.Go(3);
        Assert.Equal(3, p.Current);
        p.Go(99);
        Assert.Equal(3, p.Current);
        p.MarkDone(4);
        Assert.True(p.IsDone(4));
        p.Unmark(4);
        Assert.False(p.IsDone(4));

        // The app's answer ticks a step; a false answer takes only the app's tick away.
        p.Observe(2, true);
        Assert.True(p.IsDone(2));
        Assert.True(p.IsDoneByApp(2));
        p.MarkDone(2);
        p.Observe(2, false);
        Assert.True(p.IsDone(2));      // the hand tick stands
        Assert.False(p.IsDoneByApp(2));
        p.Unmark(2);
        Assert.False(p.IsDone(2));

        // Finished, then Restart: the hand ticks go, the app's facts stand.
        p.Observe(1, true);
        for (var i = 0; i < w.Steps.Count; i++) p.MarkDone(i);
        Assert.True(p.Finished);
        Assert.Equal(1.0, p.Fraction, 6);
        Assert.StartsWith("Finished", p.Words);
        p.Go(w.Steps.Count - 1);
        p.Next();                       // the last step: no further
        Assert.Equal(w.Steps.Count - 1, p.Current);
        p.Restart();
        Assert.Equal(0, p.Current);
        Assert.Equal(1, p.DoneCount);
        Assert.True(p.IsDone(1));
        Assert.False(p.IsDone(0));
        Assert.Equal(1.0 / w.Steps.Count, p.Fraction, 6);
    }
}
