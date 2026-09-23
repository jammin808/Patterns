using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 67: one rule for what the next CUT / TAKE changes — LOCKED never; ALL ARMED the armed alone;
/// FOCUSED the edited tile alone (the PGM tile is the programme); TICKED the ticked alone; TICKED GROUPS
/// the ticked canvases alone; a repeater never — and a plan that would move nothing is a refusal that
/// says why.
/// </summary>
public class TakePlanTests
{
    private static IReadOnlyList<TakeTarget> Rig() => new[]
    {
        new TakeTarget("s1", "1 · Main", Kind: "main"),
        new TakeTarget("s2", "2 · Comfort", Locked: true, Kind: "confidence"),
        new TakeTarget("a+b", "A · Wall", IsCanvas: true, Ticked: true, Kind: "main"),
        new TakeTarget("s3", "3 · Info", Armed: false, Ticked: true, Kind: "info"),
        new TakeTarget("s4", "4 · Repeater", IsMirror: true, Kind: "repeater"),
    };

    [Fact]
    public void AllArmedTakesTheArmedAloneAndNamesWhatItHolds()
    {
        var plan = TakePlan.Resolve(Rig(), FadeScope.Everything, focused: null);
        Assert.False(plan.IsRefused);
        Assert.Equal(new[] { "s1", "a+b" }, plan.Taken);
        Assert.Equal(new[] { ("s2", TakeHeld.LockedReason), ("s3", TakeHeld.NotArmedReason), ("s4", TakeHeld.RepeaterReason) }, plan.Held.Select(h => (h.Id, h.Reason)));
        Assert.Empty(plan.Outside);
        Assert.Equal(new[] { "s2", "s3", "s4" }, plan.Kept);
        Assert.Equal("on every armed screen", plan.Where);
        Assert.Equal("→ 1 · Main, A · Wall · held: 2 · Comfort (locked), 3 · Info (not armed), 4 · Repeater (a repeater)", plan.Words);
    }

    [Fact]
    public void FocusedIsTheEditedTileAloneAndThePgmTileIsTheProgramme()
    {
        var one = TakePlan.Resolve(Rig(), FadeScope.Focused, focused: "s1");
        Assert.Equal(new[] { "s1" }, one.Taken);
        Assert.Empty(one.Held);
        Assert.Equal(new[] { "s2", "a+b", "s3", "s4" }, one.Outside);
        Assert.Equal("on 1 · Main alone", one.Where);
        Assert.Equal("→ 1 · Main · the programme and 4 others untouched", one.Words);     // round 81: a scoped take lands on its target alone

        var pgm = TakePlan.Resolve(Rig(), FadeScope.Focused, focused: null);
        Assert.Equal(new[] { "s1", "a+b" }, pgm.Taken);
        Assert.Equal("on every armed screen", pgm.Where);

        var gone = TakePlan.Resolve(Rig(), FadeScope.Focused, focused: "not-here");
        Assert.True(gone.IsRefused);
        Assert.Contains("No wall tile is focused", gone.Refusal);
    }

    [Fact]
    public void LockedMeansLockedWhateverTheScopeSays()
    {
        var focused = TakePlan.Resolve(Rig(), FadeScope.Focused, focused: "s2");
        Assert.True(focused.IsRefused);
        Assert.Equal("2 · Comfort is locked — it keeps its picture. Unlock it (LOCK on its tile) to take to it.", focused.Refusal);
        Assert.Equal(focused.Refusal, focused.Words);

        var named = TakePlan.Resolve(Rig(), new FadeScope(FadeScopeKind.Screen, "2"), focused: null, named: new[] { "s2" });
        Assert.True(named.IsRefused);
        Assert.Contains("locked", named.Refusal);

        var all = TakePlan.Resolve(new[] { new TakeTarget("s1", "1 · Main", Locked: true), new TakeTarget("s2", "2 · Comfort", Locked: true) }, FadeScope.Everything, null);
        Assert.Equal("Every screen is locked — unlock one (LOCK on its tile) to take to it.", all.Refusal);
    }

    [Fact]
    public void TickedTakesTheTickedAloneAndArmStillCountsInsideIt()
    {
        var plan = TakePlan.Resolve(Rig(), FadeScope.Ticked, focused: "s1");
        Assert.Equal(new[] { "a+b" }, plan.Taken);
        Assert.Equal(new[] { ("s3", TakeHeld.NotArmedReason) }, plan.Held.Select(h => (h.Id, h.Reason)));
        Assert.Equal(new[] { "s1", "s2", "s4" }, plan.Outside);
        Assert.Equal("on the ticked tiles: A · Wall, 3 · Info", plan.Where);

        var none = TakePlan.Resolve(Rig().Select(t => t with { Ticked = false }).ToList(), FadeScope.Ticked, null);
        Assert.Equal("Tick the wall tiles first.", none.Refusal);

        var unarmedOnly = TakePlan.Resolve(Rig().Select(t => t with { Ticked = t.Id == "s3" }).ToList(), FadeScope.Ticked, null);
        Assert.Equal("3 · Info is not armed — ARM it, or use the tile's own TAKE.", unarmedOnly.Refusal);
    }

    [Fact]
    public void GroupsTakesEveryScreenOfTheTickedTilesKindsAndACanvasIsNoGroup()
    {
        // Round 81: the canvas (main) and the info screen are ticked — every main and every info target is in the
        // scope: the main screen and the canvas take, the info screen is held (not armed), the confidence screen
        // and the repeater are outside. A joined canvas is one target of its members' kind, never a group.
        var plan = TakePlan.Resolve(Rig(), FadeScope.Groups, focused: null);
        Assert.Equal(new[] { "s1", "a+b" }, plan.Taken);
        Assert.Equal(new[] { "s3" }, plan.Held.Select(h => h.Id));
        Assert.Equal(new[] { "s2", "s4" }, plan.Outside);
        Assert.Equal("on the main and info screens: 1 · Main, A · Wall, 3 · Info", plan.Where);
        Assert.True(plan.IsScoped);

        // A ticked repeater names no group; nothing ticked names none: refused with what to do.
        var none = TakePlan.Resolve(Rig().Select(t => t with { Ticked = t.Id == "s4" }).ToList(), FadeScope.Groups, null);
        Assert.Equal("Tick a tile in a group first — GROUPS takes to every screen of the ticked tiles' groups (Main, Confidence, Info).", none.Refusal);

        // CANVASES is the old meaning under its own name: the ticked joined canvases alone.
        var canvases = TakePlan.Resolve(Rig(), FadeScope.Canvases, focused: null);
        Assert.Equal(new[] { "a+b" }, canvases.Taken);
        Assert.Equal("on the ticked canvases: A · Wall", canvases.Where);
        Assert.Equal("Tick a joined canvas on the wall first.", TakePlan.Resolve(Rig().Select(t => t with { Ticked = false }).ToList(), FadeScope.Canvases, null).Refusal);
    }

    [Fact]
    public void AGroupByKindNeedsNoTickAndTheRulesStillBindInsideIt()
    {
        // GROUP MAIN: the main screen and the main canvas; GROUP CONFIDENCE: the one confidence screen is locked, so
        // the plan is a refusal that says so; GROUP INFO: the info screen is not armed; a kind with no screen is none.
        var main = TakePlan.Resolve(Rig(), FadeScope.GroupOf(ScreenRole.Main), focused: null);
        Assert.Equal(new[] { "s1", "a+b" }, main.Taken);
        Assert.Equal("on the main screens: 1 · Main, A · Wall", main.Where);
        Assert.Equal("2 · Comfort is locked — it keeps its picture. Unlock it (LOCK on its tile) to take to it.", TakePlan.Resolve(Rig(), FadeScope.GroupOf(ScreenRole.Confidence), null).Refusal);
        Assert.Equal("3 · Info is not armed — ARM it, or use the tile's own TAKE.", TakePlan.Resolve(Rig(), FadeScope.GroupOf(ScreenRole.Info), null).Refusal);
        Assert.Equal("No info screen on the wall.", TakePlan.Resolve(Rig().Where(t => t.Kind != "info").ToList(), FadeScope.GroupOf(ScreenRole.Info), null).Refusal);
    }

    [Fact]
    public void NamedTargetsComeFromTheCallerAndARepeaterIsNeverTaken()
    {
        var canvas = TakePlan.Resolve(Rig(), new FadeScope(FadeScopeKind.Canvas, "A"), focused: null, named: new[] { "a+b" });
        Assert.Equal(new[] { "a+b" }, canvas.Taken);
        Assert.Equal("on A · Wall alone", canvas.Where);

        var missing = TakePlan.Resolve(Rig(), new FadeScope(FadeScopeKind.Canvas, "B"), focused: null, named: null);
        Assert.Equal("No canvas B on the wall.", missing.Refusal);

        var mirror = TakePlan.Resolve(Rig(), new FadeScope(FadeScopeKind.Screen, "4"), focused: null, named: new[] { "s4" });
        Assert.Equal("4 · Repeater is a repeater — it draws its source's picture; take to its source.", mirror.Refusal);
    }

    [Fact]
    public void NothingArmedIsARefusalNeverASilentTake()
    {
        var rig = Rig().Select(t => t with { Armed = false, Locked = t.Id == "s2" }).ToList();
        var plan = TakePlan.Resolve(rig, FadeScope.Everything, null);
        Assert.True(plan.IsRefused);
        Assert.Equal("Nothing is armed to take to — every screen is held (1 locked, 3 not armed, 1 repeater). ARM the screens to change, or ARM ALL.", plan.Refusal);
        Assert.Equal("No screens in the rig.", TakePlan.Resolve(Array.Empty<TakeTarget>(), FadeScope.Everything, null).Refusal);
    }
}
