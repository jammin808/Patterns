using Patterns.Core.Model;
using Patterns.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// The rig's edits that are logic rather than a binding: placements kept in step with the
/// displays, planned screens made, removed and adopted onto hardware, a display re-identified
/// after a mode change, a canvas's own entry, the dead strips of a wall, a feed's screen. The
/// Screens page's view model keeps the selection and the bindings and asks this for the edits;
/// a screen's role or its name goes through the action layer (<see cref="ShowActionKind.ScreenRole"/>,
/// <see cref="ShowActionKind.ScreenLabel"/>), so a cue, the wire and the journal see it too.
/// </summary>
public sealed class RigEditor
{
    private readonly AppServices _s;

    public RigEditor(AppServices services) => _s = services;

    private ShowState State => _s.State;

    /// <summary>
    /// Displays that physically exist among the given list. The "primary goes off when there
    /// are other screens" default must count these only — a planned screen has no hardware,
    /// so letting it tip the count would turn off the operator's one real output.
    /// </summary>
    public static int RealCount(IReadOnlyList<ScreenInfo> screens)
    {
        var n = 0;
        foreach (var s in screens)
        {
            if (!s.IsPlanned) n++;
        }
        return n;
    }

    /// <summary>
    /// Keeps placements in sync with detected screens: new screens appear to the right of the
    /// arrangement (disconnected), and — until the operator pins a choice — the primary screen
    /// defaults to disabled whenever other screens exist, so GO never covers the control UI.
    /// The default settles the arrangement at the desk; while the outputs are live it turns
    /// nothing on or off (round 76, see below).
    /// </summary>
    public void ReconcilePlacements(IReadOnlyList<ScreenInfo> screens)
    {
        var placements = State.Output.Placements;
        foreach (var screen in screens)
        {
            if (placements.All(p => p.ScreenId != screen.Id))
            {
                var maxRight = 0;
                foreach (var p in placements)
                {
                    var info = screens.FirstOrDefault(s => s.Id == p.ScreenId);
                    if (info is not null) maxRight = Math.Max(maxRight, p.X + info.Bounds.Width);
                }
                placements.Add(new ScreenPlacement
                {
                    ScreenId = screen.Id,
                    X = placements.Count == 0 ? 0 : maxRight + 120,
                    Y = 0,
                    Enabled = !(screen.IsPrimary && RealCount(screens) > 1),
                });
            }
        }

        // Re-evaluate the default for anything the user hasn't pinned — at the desk, never on the air.
        // Round 76: Windows always names one display primary, so unplugging the one it named (the desk's
        // own monitor, off by this default) promotes another — a projector — to primary, and this rule
        // then turned that live output off: black on a screen the unplugged display had nothing to do
        // with. The reverse held too: a desk monitor no longer primary was turned on under the operator.
        // While the outputs are live the arrangement is the show's; the default settles new placements
        // only, and a change of mind is the operator's, on the Screens page or the wire.
        if (_s.Outputs.IsLive) return;
        foreach (var p in placements)
        {
            if (p.UserPinned) continue;
            var info = screens.FirstOrDefault(s => s.Id == p.ScreenId);
            if (info is not null)
            {
                p.Enabled = !(info.IsPrimary && RealCount(screens) > 1);
            }
        }
    }

    /// <summary>
    /// A screen that does not exist yet, so the whole rig can be built at the desk — placed to the
    /// right of everything already arranged, a gap away: its own target until it is dragged flush,
    /// because flush is what joins screens into one canvas, and a rig built screen by screen used
    /// to come out as one wide wall.
    /// </summary>
    public ScreenPlacement AddPlannedScreen(int width, int height, string label)
    {
        var placement = new ScreenPlacement
        {
            ScreenId = ScreenPlacement.PlannedIdPrefix + Guid.NewGuid().ToString("N")[..8],
            Planned = true,
            PlannedWidth = width,
            PlannedHeight = height,
            CustomLabel = label,
            Enabled = true,
            UserPinned = true,
            X = NextFreeX(),
        };
        State.Output.Placements.Add(placement);
        _s.Screens.Refresh();
        return placement;
    }

    /// <summary>
    /// The live screens laid out as a grid of projectors that blend: columns × rows in arrangement
    /// order, each overlapping its neighbours by the zone width, every one on automatic blend so
    /// the zones follow the overlaps and the joins come out equal. Planned screens take part —
    /// the grid is built at the desk before the projectors are — feeds' own screens never.
    /// Returns the words for the status line.
    /// </summary>
    public string LayoutBlendGrid(int columns, int rows, int overlap)
    {
        var members = Rig.OrderedLivePlacements(State, _s.Screens.All)
            .Select(x => x.Placement)
            .Where(p => !p.IsVirtual && p.Enabled)
            .ToList();
        if (members.Count < 2) return "A blend grid needs at least two screens on — projectors, or planned screens standing in for them.";
        var sizes = members.Select(RasterOf).ToList();
        var positions = BlendGridLayout.Positions(sizes, columns, rows, overlap);
        _s.BulkEdit(() =>
        {
            for (var i = 0; i < positions.Count; i++)
            {
                members[i].X = positions[i].X;
                members[i].Y = positions[i].Y;
                members[i].BlendAuto = true;
            }
        });
        _s.Screens.Refresh();
        var placed = Math.Min(positions.Count, members.Count);
        return $"{placed} screen{(placed == 1 ? "" : "s")} arranged as a blend grid — {BlendGridLayout.Describe(sizes, positions, columns, rows, overlap)} Every one fades its overlaps; Edge blend on each screen reads the joins."
               + (members.Count > placed ? $" {members.Count - placed} screen(s) did not fit the grid and stayed where they were." : "");
    }

    /// <summary>The output whose lattice is drawn over its picture while the Screens page pulls it ("" = none), and the point picked there.</summary>
    public string LatticeOn { get; private set; } = "";

    public int LatticePoint { get; private set; } = -1;

    /// <summary>The alignment game's targets on that lattice (the solver's node positions), or null.</summary>
    public IReadOnlyList<SKPoint>? LatticeTargets { get; private set; }

    /// <summary>The lattice shown on an output (or on none), with the picked point (and the game's targets): the output re-applies at once, no model edit.</summary>
    public void ShowLattice(string screenId, int point, IReadOnlyList<SKPoint>? targets = null)
    {
        if (LatticeOn == screenId && LatticePoint == point && ReferenceEquals(LatticeTargets, targets)) return;
        LatticeOn = screenId;
        LatticePoint = point;
        LatticeTargets = targets;
        _s.Outputs.OnScreensChanged();   // a live window takes its viewport again
        _s.RepublishNow();
    }

    /// <summary>One point of a placement's mesh pulled to an offset; the show file carries it.</summary>
    public void PullMeshPoint(ScreenPlacement placement, int index, float dx, float dy)
    {
        var line = WarpGrid.Moved(placement.WarpMesh, placement.WarpMeshColumns, placement.WarpMeshRows, index, dx, dy);
        if (line != placement.WarpMesh) _s.BulkEdit(() => placement.WarpMesh = line);
    }

    /// <summary>One point of a placement's mesh nudged by a step; the show file carries it.</summary>
    public void NudgeMeshPoint(ScreenPlacement placement, int index, float dx, float dy)
    {
        var line = WarpGrid.Nudged(placement.WarpMesh, placement.WarpMeshColumns, placement.WarpMeshRows, index, dx, dy);
        if (line != placement.WarpMesh) _s.BulkEdit(() => placement.WarpMesh = line);
    }

    /// <summary>The mesh over another density, its shape kept.</summary>
    public void SetMeshDensity(ScreenPlacement placement, int columns, int rows)
    {
        columns = WarpGrid.ClampSize(columns);
        rows = WarpGrid.ClampSize(rows);
        if (columns == placement.WarpMeshColumns && rows == placement.WarpMeshRows) return;
        var line = WarpGrid.Resampled(placement.WarpMesh, placement.WarpMeshColumns, placement.WarpMeshRows, columns, rows);
        _s.BulkEdit(() =>
        {
            placement.WarpMeshColumns = columns;
            placement.WarpMeshRows = rows;
            placement.WarpMesh = line;
        });
    }

    /// <summary>To the right of everything arranged, a gap away: where a screen that arrives on its own goes.</summary>
    public int NextFreeX()
    {
        var right = 0;
        foreach (var (placement, info) in Rig.OrderedLivePlacements(State, _s.Screens.All))
        {
            right = Math.Max(right, placement.X + OutputWindowManager.EffectiveSize(placement, info).Width + Patterns.Core.Geometry.ScreenLayout.ApartGap);
        }
        return right;
    }

    /// <summary>Takes a planned screen out of the rig with its own assignment; false for a feed's own screen, which goes with its feed.</summary>
    public bool RemovePlannedScreen(ScreenPlacement placement)
    {
        if (!placement.IsPlannedDisplay) return false;
        State.Output.Placements.Remove(placement);
        var assignment = State.Independent.FirstOrDefault(a => a.ScreenId == placement.ScreenId);
        if (assignment is not null) State.Independent.Remove(assignment);
        _s.Screens.Refresh();
        return true;
    }

    /// <summary>
    /// At the venue: a planned screen bound onto a real display. Everything programmed against
    /// it — position, label, per-screen pattern, trims, warp, rotation and any look that names
    /// it — follows onto the hardware, in the live show and the frozen program alike. Returns the
    /// display, or null when the screen is not a planned display or the display is not here.
    /// </summary>
    public ScreenInfo? AdoptPlannedScreen(ScreenPlacement planned, string realScreenId)
    {
        if (!planned.IsPlannedDisplay || realScreenId.Length == 0) return null;
        var info = _s.Screens.Real.FirstOrDefault(s => s.Id == realScreenId);
        if (info is null) return null;

        var oldId = planned.ScreenId;
        if (State.Output.Placements.FirstOrDefault(p => p.ScreenId == realScreenId) is { } existing)
        {
            // That display already has a placement — retire it and let the planned one take over.
            State.Output.Placements.Remove(existing);
            var stale = State.Independent.FirstOrDefault(a => a.ScreenId == realScreenId);
            if (stale is not null) State.Independent.Remove(stale);
        }
        _s.BulkEdit(() =>
        {
            planned.Planned = false;
            ContentTargets.RenameScreen(State, oldId, realScreenId);
        });
        // The rig lives in the frozen program too while EDIT SAFE is on — adopt there as well,
        // or the audience keeps the planned screen the operator just replaced.
        if (_s.Sandbox.ProgramState is { } air)
        {
            foreach (var p in air.Output.Placements.Where(p => p.ScreenId == oldId))
            {
                p.Planned = false;
            }
            ContentTargets.RenameScreen(air, oldId, realScreenId);
            _s.RepublishNow();
        }
        _s.Screens.Refresh();
        return info;
    }

    /// <summary>Everything programmed against one display id moved onto another — the live show and the frozen program alike.</summary>
    public void RenameScreen(string oldId, string newId)
    {
        _s.BulkEdit(() => ContentTargets.RenameScreen(State, oldId, newId));
        if (_s.Sandbox.ProgramState is { } air) ContentTargets.RenameScreen(air, oldId, newId);
        _s.RepublishNow();
    }

    /// <summary>The joined canvas a screen is in — its stored entry, made on demand — or null for a stand-alone screen.</summary>
    public CanvasNameConfig? CanvasConfigFor(ScreenPlacement placement, bool create)
    {
        var group = Rig.CanvasGroups(State, _s.Screens.All).FirstOrDefault(g => g.Any(m => m.ScreenId == placement.ScreenId));
        return group is null ? null : CanvasConfigFor(State, CanvasNameConfig.KeyFor(group.Select(m => m.ScreenId)), create);
    }

    /// <summary>A canvas's stored entry (its name, its seam gaps) by its key, made on demand.</summary>
    public static CanvasNameConfig? CanvasConfigFor(ShowState state, string key, bool create)
    {
        var entry = state.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == key);
        if (entry is null && create)
        {
            entry = new CanvasNameConfig { MemberKey = key };
            state.Output.CanvasNames.Add(entry);
        }
        return entry;
    }

    /// <summary>A screen's raster as the room sees it: the display's rotation-aware size, or the planned one.</summary>
    public SKSizeI RasterOf(ScreenPlacement placement)
    {
        var info = _s.Screens.All.FirstOrDefault(s => s.Id == placement.ScreenId);
        return info is null ? new SKSizeI(placement.PlannedWidth, placement.PlannedHeight) : OutputWindowManager.EffectiveSize(placement, info);
    }

    /// <summary>One vertical strip down the middle of the screen, to be dragged into place.</summary>
    public static void AddGap(ScreenPlacement placement, SKSizeI raster)
        => placement.Gaps.Add(new WallGap { Axis = GapAxis.Vertical, At = Math.Max(1, raster.Width / 2), Size = 100 });

    /// <summary>The strips of an even grid of panels packed in the screen's raster: columns − 1 vertical, rows − 1 horizontal, each the grid's gap wide.</summary>
    public void SetGapsFromGrid(ScreenPlacement placement, SKSizeI raster, int columns, int rows, int px)
    {
        _s.BulkEdit(() =>
        {
            placement.Gaps.Clear();
            for (var k = 1; k < columns; k++)
            {
                placement.Gaps.Add(new WallGap { Axis = GapAxis.Vertical, At = (int)Math.Round(raster.Width * (double)k / columns), Size = px });
            }
            for (var k = 1; k < rows; k++)
            {
                placement.Gaps.Add(new WallGap { Axis = GapAxis.Horizontal, At = (int)Math.Round(raster.Height * (double)k / rows), Size = px });
            }
        });
    }

    /// <summary>Every strip off the screen, and the seam gaps of the canvas it is in.</summary>
    public void ClearGaps(ScreenPlacement placement)
    {
        _s.BulkEdit(() =>
        {
            placement.Gaps.Clear();
            if (CanvasConfigFor(placement, create: false) is { } e)
            {
                e.SeamGapX = 0;
                e.SeamGapY = 0;
            }
        });
    }

    /// <summary>An NDI sender, disabled, named in sequence; it owns a screen on the rig from the moment it exists.</summary>
    public NdiSenderConfig AddNdiSender()
    {
        var n = State.Ndi.Senders.Count + 1;
        var sender = new NdiSenderConfig
        {
            Name = n == 1 ? "Patterns" : $"Patterns {n}",
            Enabled = false,
        };
        State.Ndi.Senders.Add(sender);
        return sender;
    }
}
