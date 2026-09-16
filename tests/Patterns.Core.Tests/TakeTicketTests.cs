using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 72: a press is a transaction. The ticket a TAKE under a sting freezes at the press lands what the
/// press promised — less only what a lock since or the rig took away — never more, and a landing with
/// nothing left is a refusal that says why.
/// </summary>
public class TakeTicketTests
{
    private static TakePlan Plan() => TakePlan.Resolve(
        new[] { new TakeTarget("a", "1 · Left"), new TakeTarget("b", "2 · Right"), new TakeTarget("c", "3 · Foyer") },
        FadeScope.Everything, focused: null);

    [Fact]
    public void ATicketIsThePressFrozenAndLandsNoMoreThanItPromised()
    {
        var plan = Plan();
        var pressed = new DateTime(2026, 9, 16, 20, 0, 0, DateTimeKind.Utc);
        var ticket = TakeTicket.From(plan, "", "Whoosh", pressed);
        Assert.Equal(new[] { "a", "b", "c" }, ticket.Taken);
        Assert.Equal(new[] { "1 · Left", "2 · Right", "3 · Foyer" }, ticket.Labels);
        Assert.Equal("on every armed screen", ticket.Where);
        Assert.Equal("", ticket.Scope);
        Assert.Equal("Whoosh", ticket.Cover);
        Assert.Equal(pressed, ticket.PressedUtc);
        Assert.False(ticket.Tile);
        Assert.Equal("→ 1 · Left, 2 · Right, 3 · Foyer when 'Whoosh' ends", ticket.Words);

        // Nothing changed since: everything promised lands, nothing is kept.
        var same = ticket.Land(new[] { "a", "b", "c" }, _ => false);
        Assert.False(same.IsRefused);
        Assert.Equal(new[] { "a", "b", "c" }, same.Landed);
        Assert.Empty(same.HeldSince);
        Assert.Empty(same.Kept);
        Assert.Equal("", same.HeldWords);

        // A screen arrived during the clip: it is the next press's, and keeps its picture — never more than promised.
        var more = ticket.Land(new[] { "a", "b", "c", "d" }, _ => false);
        Assert.Equal(new[] { "a", "b", "c" }, more.Landed);
        Assert.Equal(new[] { "d" }, more.Kept);

        // A lock since holds its screen, with the reason; a screen gone from the rig cannot be sent to; both are kept.
        var less = ticket.Land(new[] { "a", "b", "d" }, id => id == "b");
        Assert.Equal(new[] { "a" }, less.Landed);
        Assert.Equal(new[] { "b", "d" }, less.Kept);
        Assert.Collection(less.HeldSince,
            h => { Assert.Equal("b", h.Id); Assert.Equal("2 · Right", h.Label); Assert.Equal(TakeHeld.LockedSinceReason, h.Reason); },
            h => { Assert.Equal("c", h.Id); Assert.Equal("3 · Foyer", h.Label); Assert.Equal(TakeHeld.GoneReason, h.Reason); });
        Assert.Equal(" · held since the press: 2 · Right (locked since the press), 3 · Foyer (gone from the rig)", less.HeldWords);

        // Everything held since: a refusal that says why, never a silent success — and everything keeps its picture.
        var none = ticket.Land(new[] { "a", "b", "c" }, _ => true);
        Assert.True(none.IsRefused);
        Assert.Equal("Nothing lands — 1 · Left (locked since the press), 2 · Right (locked since the press), 3 · Foyer (locked since the press).", none.Refusal);
        Assert.Empty(none.Landed);
        Assert.Equal(new[] { "a", "b", "c" }, none.Kept);
    }

    [Fact]
    public void ATilesTicketNamesItsOneTargetAndAnEmptyPromiseIsRefused()
    {
        var tile = new TakeTicket { Scope = "TILE b", Taken = new[] { "b" }, Labels = new[] { "2 · Right" }, Where = "on 2 · Right alone", Tile = true, Cover = "Whoosh" };
        Assert.True(tile.Tile);
        Assert.Equal("→ 2 · Right when 'Whoosh' ends", tile.Words);
        var landing = tile.Land(new[] { "a", "b" }, _ => false);
        Assert.Equal(new[] { "b" }, landing.Landed);
        Assert.Equal(new[] { "a" }, landing.Kept);

        var empty = new TakeTicket { Scope = "", Taken = Array.Empty<string>(), Where = "on every armed screen" };
        Assert.Equal("→ ", empty.Words);
        Assert.Equal("The press promised nothing.", empty.Land(new[] { "a" }, _ => false).Refusal);

        // Labels the press did not keep: the ids stand in.
        var bare = new TakeTicket { Scope = "TICKED", Taken = new[] { "a", "b" }, Where = "on the ticked tiles" };
        Assert.Equal("→ a, b", bare.Words);
        var held = Assert.Single(bare.Land(new[] { "a", "b" }, id => id == "b").HeldSince);
        Assert.Equal(("b", "b", TakeHeld.LockedSinceReason), (held.Id, held.Label, held.Reason));
    }
}
