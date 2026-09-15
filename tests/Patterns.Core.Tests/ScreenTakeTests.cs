using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 63: "the screen tiles in the center bar need a small Cut/Take button that only works on
/// that individual screen. If OWN is not set, it should auto set to OWN." Two verbs of the one
/// vocabulary — SCREEN n TAKE and SCREEN n CUT — that the tile's keys, the wire, OSC, the tile's
/// menu and a Companion key all run; the desk's alone, like TAKE and CUT, because they send the
/// operator's preview to air.
/// </summary>
public class ScreenTakeTests
{
    [Fact]
    public void TheWireAndOscSayThemAndTheTableClassifiesThem()
    {
        var take = ControlProtocol.Parse("SCREEN 2 TAKE");
        Assert.True(take.IsAction);
        Assert.Equal(ShowActionKind.ScreenTake, take.Action.Kind);
        Assert.Equal("2", take.Action.Target);
        var cut = ControlProtocol.Parse("screen 3 cut");
        Assert.Equal(ShowActionKind.ScreenCut, cut.Action.Kind);
        Assert.Equal("3", cut.Action.Target);

        Assert.Equal("SCREEN 2 TAKE", OscMap.ToLine(OscMessage.Of("/patterns/screen/2/take")));
        Assert.Equal("SCREEN 4 CUT", OscMap.ToLine(OscMessage.Of("/patterns/screen/4/cut")));
        Assert.Contains(OscMap.Reference, r => r.Address.Contains("/screen/<n>/take"));
        Assert.Contains(OscMap.Reference, r => r.Address.Contains("/screen/<n>/cut"));

        // A screen's verb, no value; the desk's own — a running order never takes a half-built preview.
        Assert.Equal((TargetKind.Screen, ValueKind.None), ActionSpec.For(ShowActionKind.ScreenTake));
        Assert.Equal((TargetKind.Screen, ValueKind.None), ActionSpec.For(ShowActionKind.ScreenCut));
        Assert.Contains("desk key", ActionSpec.DeskOnly(ShowActionKind.ScreenTake));
        Assert.Contains("desk key", ActionSpec.DeskOnly(ShowActionKind.ScreenCut));
        Assert.DoesNotContain(ShowActionKind.ScreenTake, ActionSpec.CueKinds);
        Assert.Contains("TAKE", ActionSpec.Label(ShowActionKind.ScreenTake));
        Assert.Contains("CUT", ActionSpec.Label(ShowActionKind.ScreenCut));
        Assert.Contains(HelpTopics.All, t => t.Wire.Contains("SCREEN <n> TAKE / CUT"));
    }

    [Fact]
    public void TheTilesMenuOffersThemToAirAndSaysWhyNotWhenItCannot()
    {
        var facts = new DeskFacts { SandboxOpen = true };
        var screen = new ScreenFacts { TargetId = "b", Title = "2 · Right", Number = "2" };
        var menu = DeskMenus.Screen(facts, screen);
        var toAir = Assert.Single(menu.Groups, g => g.Heading == "TO AIR");
        var take = Assert.Single(toAir.Entries, e => e.Id == "screen.take");
        Assert.Equal(MenuScope.Live, take.Scope);
        Assert.Equal("SCREEN 2 TAKE", take.Wire);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenTake, "b"), take.Action);
        Assert.True(take.IsEnabled);
        var cut = Assert.Single(toAir.Entries, e => e.Id == "screen.cut");
        Assert.Equal("SCREEN 2 CUT", cut.Wire);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenCut, "b"), cut.Action);

        // EDIT SAFE off: the preview is the air, so there is nothing to take — the entry says so and stays put.
        var closed = DeskMenus.Screen(facts with { SandboxOpen = false }, screen);
        var held = closed.Groups.Single(g => g.Heading == "TO AIR").Entries.Single(e => e.Id == "screen.take");
        Assert.False(held.IsEnabled);
        Assert.Contains("EDIT SAFE is off", held.Because);

        // A repeater has no picture of its own to take to.
        var mirror = DeskMenus.Screen(facts, screen with { IsMirror = true });
        Assert.Contains("repeater", mirror.Groups.Single(g => g.Heading == "TO AIR").Entries.Single(e => e.Id == "screen.cut").Because);
    }
}
