using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 67.6: the one-shot for the next TAKE — the operator's words read exactly as a recall's are, a
/// video sting resolved through the library and never guessed, CLEAR and its kin the show's own, the
/// words and the wire line back, the desk-only verb, and the NEXT TRANSITION drawer on every TAKE menu.
/// </summary>
public class NextTransitionTests
{
    private static (string Id, string Name)? Stings(string name) => name.Equals("Whoosh", StringComparison.OrdinalIgnoreCase) ? ("s1", "Whoosh") : null;

    [Fact]
    public void TheWordsReadAsARecallsDoAndComeBackAsWordsAndAWireLine()
    {
        Assert.True(NextTransition.TryParse("wipe left 800", Stings, out var wipe, out _));
        Assert.Equal(TransitionKind.Wipe, wipe!.Kind);
        Assert.Equal(TransitionDirection.Left, wipe.Direction);
        Assert.Equal(800, wipe.FadeMs);
        Assert.Equal("WIPE LEFT 800 ms", wipe.Words);
        Assert.Equal("TAKE NEXT wipe left 800", wipe.Wire);

        Assert.True(NextTransition.TryParse("cut", Stings, out var cut, out _));
        Assert.True(cut!.Cut);
        Assert.Equal("CUT", cut.Words);
        Assert.Equal("TAKE NEXT cut", cut.Wire);

        Assert.True(NextTransition.TryParse("brand", Stings, out var brand, out _));
        Assert.Equal(TransitionKind.BrandStinger, brand!.Kind);
        Assert.Equal("BRAND STINGER", brand.Words);
        Assert.Equal("TAKE NEXT brand", brand.Wire);

        Assert.True(NextTransition.TryParse("1200", Stings, out var rate, out _));
        Assert.Null(rate!.Kind);
        Assert.Equal("1200 ms", rate.Words);
        Assert.Equal("TAKE NEXT 1200", rate.Wire);

        Assert.True(NextTransition.TryParse("reactive vortex 900", Stings, out var scene, out _));
        Assert.Equal(TransitionKind.Reactive, scene!.Kind);
        Assert.Equal(ReactiveScene.Vortex, scene.Scene);
        Assert.Equal("TAKE NEXT vortex 900", scene.Wire);
    }

    [Fact]
    public void AStingIsResolvedThroughTheLibraryAndClearIsTheShowsOwn()
    {
        Assert.True(NextTransition.TryParse("STING Whoosh", Stings, out var sting, out _));
        Assert.True(sting!.IsSting);
        Assert.Equal("s1", sting.StingId);
        Assert.Equal("STING Whoosh", sting.Words);
        Assert.Equal("TAKE NEXT STING Whoosh", sting.Wire);
        Assert.True(NextTransition.TryParse("stinger whoosh", Stings, out var lower, out _));
        Assert.Equal("s1", lower!.StingId);

        Assert.False(NextTransition.TryParse("STING Nothing", Stings, out _, out var missing));
        Assert.Contains("No video sting called 'Nothing'", missing);
        Assert.False(NextTransition.TryParse("STING", Stings, out _, out var bare));
        Assert.Contains("needs the sting's name", bare);
        Assert.False(NextTransition.TryParse("sideways", Stings, out _, out var nonsense));
        Assert.Contains("is not a transition", nonsense);

        foreach (var word in new[] { "", "CLEAR", "default", "none", "OFF" })
        {
            Assert.True(NextTransition.TryParse(word, Stings, out var none, out _), word);
            Assert.Null(none);
        }
    }

    [Fact]
    public void TheVerbIsTheDesksAloneAndCarriesATransitionValue()
    {
        Assert.Equal((TargetKind.None, ValueKind.Transition), ActionSpec.For(ShowActionKind.NextTransition));
        Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.NextTransition));
        Assert.Contains("one shot", ActionSpec.Label(ShowActionKind.NextTransition));
    }

    [Fact]
    public void EveryTakeMenuCarriesTheNextTransitionDrawerWithTheChoiceThatIsOnMarked()
    {
        var d = new DeskFacts
        {
            SandboxOpen = true,
            TransitionDefault = "dissolve · 400 ms",
            Stings = new[] { new MenuSting("s1", "Whoosh") },
            NextTake = "WIPE LEFT 800 ms",
            NextTakeWire = "wipe left 800",
        };
        foreach (var menu in new[] { DeskMenus.Program(d), DeskMenus.Preview(d) })
        {
            var drawer = menu.Find("take.next");
            Assert.NotNull(drawer);
            Assert.True(drawer!.HasChildren);
            Assert.Null(drawer.Action);
            Assert.Contains("WIPE LEFT 800 ms", drawer.Text);
            var choices = drawer.Children;
            var clear = Assert.Single(choices, c => c.Id == "take.next:default");
            Assert.Equal("TAKE NEXT CLEAR", clear.Wire);
            Assert.False(clear.IsOn);
            Assert.Contains("dissolve · 400 ms", clear.Detail);
            var wipe = Assert.Single(choices, c => c.Id == "take.next:wipe-left");
            Assert.True(wipe.IsOn);
            Assert.Equal(ShowActionKind.NextTransition, wipe.Action!.Value.Kind);
            Assert.Equal("wipe left", wipe.Action!.Value.Value);
            var rate = Assert.Single(choices, c => c.Id == "take.next:rate-500");
            Assert.Equal("TAKE NEXT wipe left 500", rate.Wire);                       // the rate keeps the kind that is on
            var sting = Assert.Single(choices, c => c.Id == "take.next:sting:s1");
            Assert.Equal("TAKE NEXT STING Whoosh", sting.Wire);
            Assert.Equal("STING Whoosh", sting.Action!.Value.Value);
        }
        var none = DeskMenus.Program(new DeskFacts { SandboxOpen = true, TransitionDefault = "dissolve · 400 ms" }).Find("take.next")!;
        Assert.Contains("the show's transition", none.Text);
        Assert.True(none.Children.Single(c => c.Id == "take.next:default").IsOn);
        Assert.Equal("TAKE NEXT 500", none.Children.Single(c => c.Id == "take.next:rate-500").Wire);
    }
}
