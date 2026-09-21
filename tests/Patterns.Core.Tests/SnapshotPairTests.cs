using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 79.6: the programme and the sandbox are one pair on the bus, replaced in one assignment, so a frame's
/// capture reads one generation of both; SectionsPublished fires once the new snapshot is readable, not from inside
/// the build; the change tracker counts the changes it sees, so a reader can key a cache on the count.
/// </summary>
public class SnapshotPairTests
{
    private static (ShowState Program, ChangeTracker ProgramChanges, ShowState Sandbox, ChangeTracker SandboxChanges, SnapshotBus Bus) Rig()
    {
        var program = new ShowState();
        program.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", Planned = true, PlannedWidth = 1920, PlannedHeight = 1080 });
        var sandbox = new ShowState();
        sandbox.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", Planned = true, PlannedWidth = 1920, PlannedHeight = 1080 });
        var bus = new SnapshotBus(program, () => 10);
        var programChanges = new ChangeTracker(program, () => { }, trackSections: true);
        var sandboxChanges = new ChangeTracker(sandbox, () => { }, trackSections: true);
        bus.Publish(program, programChanges);
        return (program, programChanges, sandbox, sandboxChanges, bus);
    }

    [Fact]
    public void TheProgrammeAndTheSandboxArePublishedAsOnePair()
    {
        var (program, programChanges, sandbox, sandboxChanges, bus) = Rig();
        Assert.Null(bus.Sandbox);
        Assert.Same(bus.Current, bus.Pair.Current);

        sandbox.Pattern.Grid.CellSize = 77;
        program.Pattern.Grid.CellSize = 33;
        bus.PublishBoth(program, programChanges, sandbox, sandboxChanges);
        var pair = bus.Pair;
        Assert.Same(pair.Current, bus.Current);
        Assert.Same(pair.Sandbox, bus.Sandbox);
        Assert.Equal(33, pair.Current.State.Pattern.Grid.CellSize);
        Assert.Equal(77, pair.Sandbox!.State.Pattern.Grid.CellSize);
        Assert.Equal(pair.Current.Version + 1, pair.Sandbox.Version);           // built in one publish: consecutive generations

        // A programme publish alone keeps the sandbox; a sandbox publish alone keeps the programme; clearing keeps the programme.
        program.Pattern.Grid.CellSize = 34;
        bus.Publish(program, programChanges);
        Assert.Same(pair.Sandbox, bus.Sandbox);
        Assert.Equal(34, bus.Current.State.Pattern.Grid.CellSize);
        var current = bus.Current;
        sandbox.Pattern.Grid.CellSize = 78;
        bus.PublishSandbox(sandbox, sandboxChanges);
        Assert.Same(current, bus.Current);
        Assert.Equal(78, bus.Sandbox!.State.Pattern.Grid.CellSize);
        bus.ClearSandbox();
        Assert.Null(bus.Sandbox);
        Assert.Same(current, bus.Current);
        Assert.Same(current, bus.Pair.Current);
    }

    [Fact]
    public void SectionsPublishedFiresOnceTheNewSnapshotIsReadable()
    {
        var (program, programChanges, sandbox, sandboxChanges, bus) = Rig();
        var seen = new List<(ShowState Root, HashSet<string>? Dirty, long CurrentVersion, long? SandboxVersion, int CellSeen)>();
        bus.SectionsPublished += (root, dirty) => seen.Add((
            root, dirty, bus.Current.Version, bus.Sandbox?.Version,
            ReferenceEquals(root, program) ? bus.Current.State.Pattern.Grid.CellSize : bus.Sandbox!.State.Pattern.Grid.CellSize));

        program.Pattern.Grid.CellSize = 41;
        bus.Publish(program, programChanges);
        var one = Assert.Single(seen);
        Assert.Same(program, one.Root);
        Assert.Equal(new[] { nameof(ShowState.Pattern) }, one.Dirty!.OrderBy(s => s, StringComparer.Ordinal));
        Assert.Equal(bus.Current.Version, one.CurrentVersion);
        Assert.Equal(41, one.CellSeen);                                            // the handler read the snapshot this publish made, not the one before

        seen.Clear();
        program.Pattern.Grid.CellSize = 42;
        sandbox.Pattern.Grid.CellSize = 99;
        bus.PublishBoth(program, programChanges, sandbox, sandboxChanges);
        Assert.Equal(2, seen.Count);
        Assert.Same(program, seen[0].Root);
        Assert.Same(sandbox, seen[1].Root);
        Assert.Equal(new[] { nameof(ShowState.Pattern) }, seen[0].Dirty!.OrderBy(s => s, StringComparer.Ordinal));
        Assert.Null(seen[1].Dirty);                                                // the sandbox's first publish from its root: everything
        Assert.Equal(42, seen[0].CellSeen);
        Assert.Equal(99, seen[1].CellSeen);
        Assert.Equal(bus.Pair.Current.Version, seen[0].CurrentVersion);
        Assert.Equal(bus.Pair.Sandbox!.Version, seen[0].SandboxVersion);           // the pair was whole when the first fired
    }

    [Fact]
    public void TheTrackerCountsTheChangesItSeesAndNotRuntimeOnlyWrites()
    {
        var state = new ShowState();
        var placement = new ScreenPlacement { ScreenId = "a" };
        state.Output.Placements.Add(placement);
        var runtimeOnly = 0;
        var changes = new ChangeTracker(state, () => { }, trackSections: true, onRuntimeOnlyChanged: () => runtimeOnly++);
        var start = changes.Version;

        state.Pattern.Grid.CellSize = 5;
        Assert.Equal(start + 1, changes.Version);
        placement.Enabled = !placement.Enabled;
        Assert.Equal(start + 2, changes.Version);
        placement.AdoptTargetId = "b";                                             // [JsonIgnore]: reaches no snapshot, counts for nothing
        Assert.Equal(start + 2, changes.Version);
        Assert.Equal(1, runtimeOnly);
        changes.TakeDirty();
        Assert.Equal(start + 2, changes.Version);                                  // reading the sections is not a change
        state.Pattern.Grid.CellSize = 5;                                           // the same value again: no change, no count
        Assert.Equal(start + 2, changes.Version);
    }
}
