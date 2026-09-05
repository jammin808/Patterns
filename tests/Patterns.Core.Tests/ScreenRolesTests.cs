using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>What a screen is for: locks that hold through looks and a clip's takeover, repeaters, and the words that drive them.</summary>
public class ScreenRolesTests
{
    private static ShowState Rig()
    {
        var s = new ShowState();
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "a" });
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "b" });
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "c" });
        s.Pattern.Kind = PatternKind.Grid;
        return s;
    }

    private static string LookOf(PatternKind kind)
    {
        var other = new ShowState();
        other.Pattern.Kind = kind;
        return LookService.Capture(other);
    }

    [Fact]
    public void ALockedScreenKeepsItsPictureThroughEveryLookUntilItIsUnlocked()
    {
        var s = Rig();
        Assert.True(s.Output.Placements[2].FollowsCues);
        ScreenRoles.SetLocked(s, "c", true);
        Assert.False(s.Output.Placements[2].FollowsCues);
        Assert.True(ScreenRoles.IsLocked(s, "c"));
        Assert.True(ContentTargets.UsesOwnPattern(s, "c"));                  // the lock gave it the program as its own
        Assert.Equal(PatternKind.Grid, s.Independent.Single(a => a.ScreenId == "c").Pattern.Kind);

        Assert.True(LookService.Apply(LookOf(PatternKind.ColorBars), s));
        Assert.Equal(PatternKind.ColorBars, s.Pattern.Kind);                 // the program moved
        Assert.False(ContentTargets.UsesOwnPattern(s, "a"));                  // a main screen follows
        Assert.True(ContentTargets.UsesOwnPattern(s, "c"));                   // the locked one kept its picture
        Assert.Equal(PatternKind.Grid, s.Independent.Single(a => a.ScreenId == "c").Pattern.Kind);

        // A look that carries a picture of its own for the locked screen is overruled too.
        var carrying = Rig();
        carrying.Pattern.Kind = PatternKind.Focus;
        ContentTargets.EnsureAssignment(carrying, "c").Pattern.Kind = PatternKind.Ramp;
        ContentTargets.SetOwnPattern(carrying, "c", true);
        Assert.True(LookService.Apply(LookService.Capture(carrying), s));
        Assert.Equal(PatternKind.Focus, s.Pattern.Kind);
        Assert.Equal(PatternKind.Grid, s.Independent.Single(a => a.ScreenId == "c").Pattern.Kind);

        ScreenRoles.SetLocked(s, "c", false);
        Assert.True(s.Output.Placements[2].FollowsCues);
        LookService.Apply(LookOf(PatternKind.Checkerboard), s);
        Assert.False(ContentTargets.UsesOwnPattern(s, "c"));                  // free again: the look sweeps it
    }

    [Fact]
    public void ACanvasLocksThroughItsMembersAndAClipTakeoverLeavesLockedTargetsAlone()
    {
        var s = Rig();
        var key = CanvasNameConfig.KeyFor(new[] { "a", "b" });
        Assert.False(ScreenRoles.IsLocked(s, key));
        ScreenRoles.SetLocked(s, key, true);
        Assert.True(ScreenRoles.IsLocked(s, key));
        Assert.False(ScreenRoles.IsLocked(s, CanvasNameConfig.KeyFor(new[] { "a", "c" }))); // c still follows
        Assert.True(ContentTargets.UsesOwnPattern(s, key));                  // the canvas got its row and the program
        Assert.Equal(new[] { key }, ScreenRoles.LockedTargets(s, new[] { key, "c" }));
        Assert.False(ScreenRoles.IsLocked(s, ""));
        Assert.False(ScreenRoles.IsLocked(s, "ghost"));

        // What a look (and a clip's takeover) must leave alone: the canvas and its members, never c.
        var held = ScreenRoles.Held(s);
        Assert.Contains(held, h => h.Id == key);
        Assert.Contains(held, h => h.Id == "a");
        Assert.DoesNotContain(held, h => h.Id == "c");
        Assert.All(held, h => Assert.Equal(PatternKind.Grid, h.Kept.Pattern.Kind));
    }

    [Fact]
    public void ARepeaterDrawsItsSourceAndAChainNeverHangs()
    {
        var s = Rig();
        s.Output.Placements[2].MirrorOf = "a";
        Assert.Equal("a", ScreenRoles.ResolveMirror(s, "c"));
        Assert.Equal("b", ScreenRoles.ResolveMirror(s, "b"));
        ContentTargets.EnsureAssignment(s, "a").Pattern.Kind = PatternKind.Focus;
        ContentTargets.SetOwnPattern(s, "a", true);
        var bus = new SnapshotBus(s);
        bus.Publish(s);
        Assert.Equal(PatternKind.Focus, bus.Current.PatternFor("c").Kind);   // the repeater shows its source's picture
        Assert.Equal(PatternKind.Grid, bus.Current.PatternFor("b").Kind);

        // A loop typed into a file, a ghost, a canvas: the chain stops at something real.
        s.Output.Placements[0].MirrorOf = "c";
        Assert.Contains(ScreenRoles.ResolveMirror(s, "c"), new[] { "a", "c" });
        s.Output.Placements[0].MirrorOf = "ghost";
        Assert.Equal("a", ScreenRoles.ResolveMirror(s, "a"));
        var key = CanvasNameConfig.KeyFor(new[] { "a", "b" });
        Assert.Equal(key, ScreenRoles.ResolveMirror(s, key));
        s.Output.Placements[2].MirrorOf = key;
        Assert.Equal(key, ScreenRoles.ResolveMirror(s, "c"));

        // A lock on a repeater never gives it a picture of its own; a rename follows the mirror.
        ScreenRoles.SetLocked(s, "c", true);
        Assert.False(ContentTargets.UsesOwnPattern(s, "c"));
        ContentTargets.RenameScreen(s, "b", "b2");
        Assert.Equal(CanvasNameConfig.KeyFor(new[] { "a", "b2" }), s.Output.Placements[2].MirrorOf);
    }

    [Fact]
    public void TheWordsForALockAndTheRolesDefaults()
    {
        Assert.Equal((RemoteCommandKind.ScreenLock, 2), (ControlProtocol.Parse("LOCK 2 ON").Kind, ControlProtocol.Parse("LOCK 2 ON").IntArg));
        Assert.Equal(RemoteCommandKind.ScreenUnlock, ControlProtocol.Parse("lock 3 off").Kind);
        Assert.Equal(RemoteCommandKind.ScreenLockToggle, ControlProtocol.Parse("LOCK 1").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("LOCK x ON").Kind);

        Assert.Equal((TargetKind.Screen, ValueKind.None), CueActionSpec.For(CueActionKind.ScreenLock));
        Assert.Contains(CueActionKind.ScreenUnlock, CueActionSpec.Editable);
        Assert.False(CueActionSpec.ChangesContent(CueActionKind.ScreenLock));
        var s = Rig();
        Assert.Contains("locked", CueSummary.DescribeAction(s, new CueActionConfig { Kind = CueActionKind.ScreenLock, Target = "c" }));
        Assert.Contains("follows", CueSummary.DescribeAction(s, new CueActionConfig { Kind = CueActionKind.ScreenUnlock, Target = "c" }));

        Assert.Equal("CONF", ScreenRoles.Badge(ScreenRole.Confidence));
        Assert.Equal("INFO", ScreenRoles.Badge(ScreenRole.Info));
        Assert.Equal("", ScreenRoles.Badge(ScreenRole.Main));
        Assert.False(ScreenRoles.DefaultFollows(ScreenRole.Info));
        Assert.False(ScreenRoles.DefaultFollows(ScreenRole.Confidence));
        Assert.True(ScreenRoles.DefaultFollows(ScreenRole.Repeater));

        // A new screen is a main screen that follows, with its own content; a role from a newer build reads as main.
        var fresh = new ScreenPlacement();
        Assert.Equal(ScreenRole.Main, fresh.Role);
        Assert.True(fresh.FollowsCues);
        Assert.Equal("", fresh.MirrorOf);
        s.Output.Placements[0].Role = ScreenRole.Info;
        var json = JsonUtil.Serialize(s);
        Assert.Contains("\"Info\"", json);
        var newer = JsonUtil.Deserialize<ShowState>(json.Replace("\"Info\"", "\"Hologram\""))!;
        Assert.Equal(ScreenRole.Main, newer.Output.Placements[0].Role);
        Assert.Equal(ScreenRole.Info, JsonUtil.Deserialize<ShowState>(json)!.Output.Placements[0].Role);
    }
}
