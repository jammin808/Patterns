using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 72: a press is a transaction. The ticket a TAKE under a sting freezes at the press lands what the
/// press promised — less only what a lock since or the rig took away — never more, and a landing with
/// nothing left is a refusal that says why. Round 75: less also a target that is not what the press saw — a
/// screen made a repeater, a canvas whose members moved, a screen that joined a canvas — held with what it is
/// now, so a transaction never lands on a destination it did not promise.
/// </summary>
public class TakeTicketTests
{
    private const string Own = "a screen of its own";
    private static readonly DateTime Pressed = new(2026, 9, 16, 20, 0, 0, DateTimeKind.Utc);

    private static TakeTarget Screen(string id, string label, bool locked = false, string shape = Own) => new(id, label, Locked: locked, Shape: shape);

    private static TakePlan Plan() => TakePlan.Resolve(
        new[] { Screen("a", "1 · Left"), Screen("b", "2 · Right"), Screen("c", "3 · Foyer") },
        FadeScope.Everything, focused: null);

    [Fact]
    public void ATicketIsThePressFrozenAndLandsNoMoreThanItPromised()
    {
        var plan = Plan();
        var ticket = TakeTicket.From(plan, "", "Whoosh", Pressed);
        Assert.Equal(new[] { "a", "b", "c" }, ticket.Taken);
        Assert.Equal(new[] { "1 · Left", "2 · Right", "3 · Foyer" }, ticket.Labels);
        Assert.Equal(new[] { Own, Own, Own }, ticket.Shapes);
        Assert.Equal("on every armed screen", ticket.Where);
        Assert.Equal("", ticket.Scope);
        Assert.Equal("Whoosh", ticket.Cover);
        Assert.Equal(Pressed, ticket.PressedUtc);
        Assert.False(ticket.Tile);
        Assert.Equal("→ 1 · Left, 2 · Right, 3 · Foyer when 'Whoosh' ends", ticket.Words);

        // Nothing changed since: everything promised lands, nothing is kept.
        var same = ticket.Land(new[] { Screen("a", "1 · Left"), Screen("b", "2 · Right"), Screen("c", "3 · Foyer") });
        Assert.False(same.IsRefused);
        Assert.Equal(new[] { "a", "b", "c" }, same.Landed);
        Assert.Empty(same.HeldSince);
        Assert.Empty(same.Kept);
        Assert.Equal("", same.HeldWords);

        // A screen arrived during the clip: it is the next press's, and keeps its picture — never more than promised.
        var more = ticket.Land(new[] { Screen("a", "1 · Left"), Screen("b", "2 · Right"), Screen("c", "3 · Foyer"), Screen("d", "4 · Stage") });
        Assert.Equal(new[] { "a", "b", "c" }, more.Landed);
        Assert.Equal(new[] { "d" }, more.Kept);

        // A lock since holds its screen, with the reason; a screen gone from the rig cannot be sent to; both are kept.
        var less = ticket.Land(new[] { Screen("a", "1 · Left"), Screen("b", "2 · Right", locked: true), Screen("d", "4 · Stage") });
        Assert.Equal(new[] { "a" }, less.Landed);
        Assert.Equal(new[] { "b", "d" }, less.Kept);
        Assert.Collection(less.HeldSince,
            h => { Assert.Equal("b", h.Id); Assert.Equal("2 · Right", h.Label); Assert.Equal(TakeHeld.LockedSinceReason, h.Reason); },
            h => { Assert.Equal("c", h.Id); Assert.Equal("3 · Foyer", h.Label); Assert.Equal(TakeHeld.GoneReason, h.Reason); });
        Assert.Equal(" · held since the press: 2 · Right (locked since the press), 3 · Foyer (gone from the rig)", less.HeldWords);

        // Everything held since: a refusal that says why, never a silent success — and everything keeps its picture.
        var none = ticket.Land(new[] { Screen("a", "1 · Left", locked: true), Screen("b", "2 · Right", locked: true), Screen("c", "3 · Foyer", locked: true) });
        Assert.True(none.IsRefused);
        Assert.Equal("Nothing lands — 1 · Left (locked since the press), 2 · Right (locked since the press), 3 · Foyer (locked since the press).", none.Refusal);
        Assert.Empty(none.Landed);
        Assert.Equal(new[] { "a", "b", "c" }, none.Kept);
    }

    [Fact]
    public void ATargetThatIsNotWhatThePressSawIsHeldWithWhatItIsNow()
    {
        var ticket = TakeTicket.From(Plan(), "", "Whoosh", Pressed);

        // The right screen was a screen of its own and is a repeater now; the foyer screen is no longer a target of
        // its own — it joined a canvas (whereNow says which) — and the canvas that arrived is the next press's.
        var rigNow = new[]
        {
            Screen("a", "1 · Left"),
            Screen("b", "2 · Right", shape: "a repeater of 1 · Left"),
            new TakeTarget("c+d", "Canvas A", IsCanvas: true, Shape: "Canvas A of 3 · Foyer, 4 · Stage"),
        };
        var landing = ticket.Land(rigNow, id => id == "c" ? "in Canvas A" : "");
        Assert.Equal(new[] { "a" }, landing.Landed);
        Assert.Equal(new[] { "b", "c+d" }, landing.Kept);
        Assert.Collection(landing.HeldSince,
            h => { Assert.Equal("b", h.Id); Assert.Equal("changed since the press — now a repeater of 1 · Left", h.Reason); },
            h => { Assert.Equal("c", h.Id); Assert.Equal("changed since the press — now in Canvas A", h.Reason); });
        Assert.All(landing.HeldSince, h => Assert.StartsWith(TakeHeld.ChangedSinceReason, h.Reason, StringComparison.Ordinal));
        Assert.Equal(" · held since the press: 2 · Right (changed since the press — now a repeater of 1 · Left), 3 · Foyer (changed since the press — now in Canvas A)", landing.HeldWords);

        // A canvas the press promised whose members moved is not the canvas the press saw: its key is gone and
        // whereNow names the canvas it became; with nothing else promised the landing is a refusal that says so.
        var wall = TakeTicket.From(
            TakePlan.Resolve(new[] { new TakeTarget("b+c", "Canvas A", IsCanvas: true, Shape: "Canvas A of 2 · Right, 3 · Foyer") }, FadeScope.Everything, focused: null),
            "", "Whoosh", Pressed);
        var grown = wall.Land(
            new[] { new TakeTarget("b+c+d", "Canvas A", IsCanvas: true, Shape: "Canvas A of 2 · Right, 3 · Foyer, 4 · Stage") },
            id => id == "b+c" ? "Canvas A of 2 · Right, 3 · Foyer, 4 · Stage" : "");
        var held = Assert.Single(grown.HeldSince);
        Assert.Equal("changed since the press — now Canvas A of 2 · Right, 3 · Foyer, 4 · Stage", held.Reason);
        Assert.True(grown.IsRefused);
        Assert.Equal("Nothing lands — Canvas A (changed since the press — now Canvas A of 2 · Right, 3 · Foyer, 4 · Stage).", grown.Refusal);
        Assert.Equal(new[] { "b+c+d" }, grown.Kept);

        // A change on a target locked since reads as the change — the more telling fact; a rig whose caller keeps no
        // shapes compares none; and a plain "gone" stays "gone from the rig" when whereNow has nothing to say.
        var lockedToo = ticket.Land(new[] { Screen("a", "1 · Left", locked: true, shape: "a repeater of 2 · Right"), Screen("b", "2 · Right"), Screen("c", "3 · Foyer") });
        Assert.Equal("changed since the press — now a repeater of 2 · Right", Assert.Single(lockedToo.HeldSince).Reason);
        var shapeless = ticket.Land(new[] { new TakeTarget("a", "1 · Left"), new TakeTarget("b", "2 · Right"), new TakeTarget("c", "3 · Foyer") });
        Assert.Equal(new[] { "a", "b", "c" }, shapeless.Landed);
        var gone = ticket.Land(new[] { Screen("a", "1 · Left") }, _ => "");
        Assert.All(gone.HeldSince, h => Assert.Equal(TakeHeld.GoneReason, h.Reason));
        Assert.Equal(TakeHeld.ChangedSinceReason, TakeHeld.ChangedSince(""));
    }

    [Fact]
    public void ATilesTicketNamesItsOneTargetAndAnEmptyPromiseIsRefused()
    {
        var tile = new TakeTicket { Scope = "TILE b", Taken = new[] { "b" }, Labels = new[] { "2 · Right" }, Where = "on 2 · Right alone", Tile = true, Cover = "Whoosh" };
        Assert.True(tile.Tile);
        Assert.Equal("→ 2 · Right when 'Whoosh' ends", tile.Words);
        var landing = tile.Land(new[] { Screen("a", "1 · Left"), Screen("b", "2 · Right") });
        Assert.Equal(new[] { "b" }, landing.Landed);
        Assert.Equal(new[] { "a" }, landing.Kept);

        var empty = new TakeTicket { Scope = "", Taken = Array.Empty<string>(), Where = "on every armed screen" };
        Assert.Equal("→ ", empty.Words);
        Assert.Equal("The press promised nothing.", empty.Land(new[] { Screen("a", "1 · Left") }).Refusal);

        // Labels the press did not keep: the ids stand in.
        var bare = new TakeTicket { Scope = "TICKED", Taken = new[] { "a", "b" }, Where = "on the ticked tiles" };
        Assert.Equal("→ a, b", bare.Words);
        var held = Assert.Single(bare.Land(new[] { new TakeTarget("a", "a"), new TakeTarget("b", "b", Locked: true) }).HeldSince);
        Assert.Equal(("b", "b", TakeHeld.LockedSinceReason), (held.Id, held.Label, held.Reason));
    }
}
