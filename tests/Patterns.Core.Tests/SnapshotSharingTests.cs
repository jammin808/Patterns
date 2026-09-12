using System.Runtime.CompilerServices;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// A publish copies only the sections of the show that moved and shares the rest with the
/// snapshot before it; every published section is immutable; the rig is rebuilt only when the
/// placements or the displays moved.
/// </summary>
public class SnapshotSharingTests
{
    private static (ShowState State, ChangeTracker Changes, SnapshotBus Bus) Rig()
    {
        var state = new ShowState();
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", Planned = true, PlannedWidth = 1920, PlannedHeight = 1080 });
        state.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in", Json = LookService.Capture(state) });
        var bus = new SnapshotBus(state, () => 10);
        var changes = new ChangeTracker(state, () => { }, trackSections: true);
        bus.Publish(state, changes);
        return (state, changes, bus);
    }

    [Fact]
    public void OnlyTheSectionThatMovedIsCopied()
    {
        var (state, changes, bus) = Rig();
        var before = bus.Current;

        state.Pattern.Grid.CellSize = 77;
        bus.Publish(state, changes);
        var after = bus.Current;

        Assert.Equal(77, after.State.Pattern.Grid.CellSize);
        Assert.NotSame(before.State.Pattern, after.State.Pattern);
        var shared = SnapshotClone.SharedSections(before.State, after.State);
        Assert.Contains(nameof(ShowState.Overlays), shared);
        Assert.Contains(nameof(ShowState.LooksAndCues), shared);
        Assert.Contains(nameof(ShowState.Output), shared);
        Assert.Contains(nameof(ShowState.Stingers), shared);
        Assert.DoesNotContain(nameof(ShowState.Pattern), shared);
        Assert.Same(before.Rig, after.Rig); // the placements did not move
    }

    [Fact]
    public void ScalarsAreAlwaysReadFresh()
    {
        var (state, changes, bus) = Rig();
        state.Blackout = true;
        state.Name = "Awards";
        bus.Publish(state, changes);
        Assert.True(bus.Current.State.Blackout);
        Assert.Equal("Awards", bus.Current.State.Name);
    }

    [Fact]
    public void ACollectionChangeCopiesItsSection()
    {
        var (state, changes, bus) = Rig();
        var before = bus.Current;
        state.LooksAndCues.Looks.Add(new LookConfig { Name = "Interval" });
        bus.Publish(state, changes);
        Assert.Equal(2, bus.Current.State.LooksAndCues.Looks.Count);
        Assert.Single(before.State.LooksAndCues.Looks); // the earlier snapshot is untouched
        Assert.Same(before.State.Pattern, bus.Current.State.Pattern);
    }

    [Fact]
    public void AnItemAddedLaterIsWatchedUnderItsSection()
    {
        var (state, changes, bus) = Rig();
        state.Independent.Add(new OutputAssignment { ScreenId = "a" });
        bus.Publish(state, changes);
        var before = bus.Current;

        state.Independent[0].Pattern.Kind = PatternKind.Focus;
        bus.Publish(state, changes);
        Assert.Equal(PatternKind.Focus, bus.Current.State.Independent[0].Pattern.Kind);
        Assert.NotSame(before.State.Independent, bus.Current.State.Independent);
        Assert.Same(before.State.Pattern, bus.Current.State.Pattern);
    }

    [Fact]
    public void PublishedSectionsAreImmutableAndSharedOnesStayImmutable()
    {
        var (state, changes, bus) = Rig();
        var snap = bus.Current;
        Assert.True(snap.State.IsPublished);
        Assert.True(snap.State.Pattern.IsPublished);
        Assert.True(snap.State.Output.Placements[0].IsPublished);
        Assert.True(snap.State.LooksAndCues.Looks[0].IsPublished);
        Assert.Throws<InvalidOperationException>(() => snap.State.Pattern.Grid.CellSize = 5);
        Assert.Throws<InvalidOperationException>(() => snap.State.Blackout = true);

        state.Overlays.Clock.Enabled = true;
        bus.Publish(state, changes);
        Assert.Throws<InvalidOperationException>(() => bus.Current.State.Pattern.Grid.CellSize = 5); // shared, still immutable
        Assert.Throws<InvalidOperationException>(() => bus.Current.State.Overlays.Clock.Enabled = false); // fresh, immutable

        // The live show is never marked, and a write to it never reaches a published copy.
        state.Pattern.Grid.CellSize = 9;
        Assert.NotEqual(9, bus.Current.State.Pattern.Grid.CellSize);
    }

    [Fact]
    public void WritingTheSameValueOnAPublishedObjectIsNotAWrite()
    {
        var (_, _, bus) = Rig();
        var cell = bus.Current.State.Pattern.Grid.CellSize;
        bus.Current.State.Pattern.Grid.CellSize = cell; // a no-op: nothing moved, nothing throws
    }

    [Fact]
    public void ARuntimeOnlyWriteDirtiesNothingAndIsToldApart()
    {
        var (state, changes, bus) = Rig();
        var before = bus.Current;
        var hits = 0;
        var chrome = 0;
        var watch = new ChangeTracker(state, () => hits++, trackSections: true, onRuntimeOnlyChanged: () => chrome++);
        watch.TakeDirty(); // the first take is "everything"
        state.Output.Placements[0].AdoptTargetId = "display-3"; // [JsonIgnore]: chrome the sinks never see
        Assert.Equal(0, hits);                                  // not an edit of the show…
        Assert.Equal(1, chrome);                                // …but the desk is told, apart
        Assert.Empty(watch.TakeDirty()!);

        state.Output.Placements[0].X = 40;                      // an edit of the show
        Assert.Equal(1, hits);
        Assert.Equal(1, chrome);

        // Without a chrome callback the desk hears everything, as it always did.
        var plain = 0;
        _ = new ChangeTracker(state, () => plain++, trackSections: true);
        state.Output.Placements[0].AdoptTargetId = "display-4";
        Assert.Equal(1, plain);

        bus.Publish(state, changes);
        Assert.NotSame(before.State.Output, bus.Current.State.Output); // X moved
    }

    [Fact]
    public void TheFirstTakeIsEverything()
    {
        var state = new ShowState();
        var watch = new ChangeTracker(state, () => { }, trackSections: true);
        Assert.Null(watch.TakeDirty());
        state.Brand.PrimaryColor = "#123456";
        Assert.Equal(new[] { nameof(ShowState.Brand) }, watch.TakeDirty()!.ToArray());
        Assert.Empty(watch.TakeDirty()!);
    }

    [Fact]
    public void ATrackerWithoutSectionsCopiesEverything()
    {
        var state = new ShowState();
        var bus = new SnapshotBus(state, () => 10);
        var plain = new ChangeTracker(state, () => { });
        bus.Publish(state, plain);
        var before = bus.Current;
        state.Pattern.Grid.CellSize = 3;
        bus.Publish(state, plain);
        Assert.Empty(SnapshotClone.SharedSections(before.State, bus.Current.State));
        Assert.Equal(3, bus.Current.State.Pattern.Grid.CellSize);
    }

    [Fact]
    public void ATrackerForAnotherRootIsIgnored()
    {
        var state = new ShowState();
        var other = new ShowState();
        var bus = new SnapshotBus(state, () => 10);
        var wrong = new ChangeTracker(other, () => { }, trackSections: true);
        bus.Publish(state, wrong);
        var before = bus.Current;
        state.Pattern.Grid.CellSize = 3;
        bus.Publish(state, wrong);
        Assert.Equal(3, bus.Current.State.Pattern.Grid.CellSize);
        Assert.Empty(SnapshotClone.SharedSections(before.State, bus.Current.State));
    }

    [Fact]
    public void TheRigFollowsThePlacementsAndTheDisplays()
    {
        var (state, changes, bus) = Rig();
        var before = bus.Current;

        state.Output.Placements[0].X = 100;
        bus.Publish(state, changes);
        Assert.NotSame(before.Rig, bus.Current.Rig);

        var again = bus.Current;
        bus.Displays = new Dictionary<string, ScreenGeometry>(StringComparer.Ordinal) { ["a"] = new(3840, 2160, "Wall") };
        state.Overlays.Clock.Enabled = true; // something unrelated moved, and the table with it
        bus.Publish(state, changes);
        Assert.NotSame(again.Rig, bus.Current.Rig);
        Assert.Equal("Wall", bus.Current.Rig.DisplayLabel("a"));
    }

    [Fact]
    public void TwoRootsKeepTheirOwnHistory()
    {
        // EDIT SAFE: the frozen program and the edited show are published side by side from two roots.
        var (state, changes, bus) = Rig();
        var program = JsonUtil.Clone(state);
        var programChanges = new ChangeTracker(program, () => { }, trackSections: true);
        bus.Publish(program, programChanges);
        bus.PublishSandbox(state, changes);
        var air = bus.Current;
        var preview = bus.Sandbox!;

        state.Pattern.Grid.CellSize = 42;                 // the operator edits the preview
        bus.Publish(program, programChanges);
        bus.PublishSandbox(state, changes);

        Assert.Same(air.State, bus.Current.State);        // nothing moved on air: the whole copy is shared
        Assert.NotSame(preview.State.Pattern, bus.Sandbox!.State.Pattern);
        Assert.Same(preview.State.Overlays, bus.Sandbox!.State.Overlays);
        Assert.Equal(42, bus.Sandbox!.State.Pattern.Grid.CellSize);
        Assert.NotEqual(42, bus.Current.State.Pattern.Grid.CellSize);
    }

    [Fact]
    public void TransitionKeysCarryOverWhenTheirInputsDidNotMove()
    {
        var (state, changes, bus) = Rig();
        var before = bus.Current;
        var key = before.TransitionKeyFor("a");

        state.Overlays.Clock.Enabled = true;              // an overlay: not a change of picture
        bus.Publish(state, changes);
        Assert.Equal(key, bus.Current.TransitionKeyFor("a"));

        state.Pattern.Kind = PatternKind.Focus;           // the picture itself
        bus.Publish(state, changes);
        Assert.NotEqual(key, bus.Current.TransitionKeyFor("a"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddAndRemoveALook(ShowState state)
    {
        var look = new LookConfig { Name = "Gone" };
        state.LooksAndCues.Looks.Add(look);
        look.Name = "Still gone";
        state.LooksAndCues.Looks.Remove(look);
        return new WeakReference(look);
    }

    [Fact]
    public void ARemovedItemIsNotHeldByTheTracker()
    {
        var state = new ShowState();
        _ = new ChangeTracker(state, () => { }, trackSections: true);
        var weak = AddAndRemoveALook(state);
        for (var i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "a look taken out of the show is still held by the change tracker");
    }
}
