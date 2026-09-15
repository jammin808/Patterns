using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.App.Views.Controls;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 62: the local App suite's host was killed three times at ~500 tests, 13.5 GB. A heap
/// dump named the root — the process-wide frame budget registry, holding one budget per closed
/// desk, each the RUN monitor tile's. The tile sits in the Run layout, whose content is laid out
/// — and so joins the visual tree — only when the layout is first shown; the tests' desks never
/// showed it, the viewport's binding arrived at boot, and the tile, off the tree with no detach
/// in its future, made a pipeline nothing would ever dispose. A pipeline's budget holds its bus
/// and the bus holds the desk, so every desk booted stayed alive. The rule that keeps it from
/// coming back: a tile off the surface has no pipeline, whatever its viewport does.
/// </summary>
public class MonitorTileLifetimeTests
{
    private static PipelineViewport Viewport(string label) =>
        PipelineViewport.Monitor("a", new SKSizeI(1920, 1080), label, previewSide: false);

    private static List<ScreenInfo> TwoScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
    };

    private static void Rig(TestApp.Booted b)
    {
        var fakes = TwoScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.RebuildSwitcherTiles(fakes);
        Dispatcher.UIThread.RunJobs();
    }

    private static bool IsMonitor(FrameBudget x, object bus) =>
        ReferenceEquals(x.Scope, bus) && x.Label.StartsWith("the main screen", StringComparison.Ordinal);

    [AvaloniaFact]
    public void ATileOffTheSurfaceHasNoPipelineWhateverItsViewportDoes()
    {
        var b = TestApp.Boot();
        try
        {
            var bus = b.Services.Bus;
            var tile = new MonitorTileControl { Viewport = Viewport("off the tree") };
            Dispatcher.UIThread.RunJobs();
            Assert.Null(tile.Pipeline);
            Assert.DoesNotContain(FrameBudgets.Attached, x => x.Label == "off the tree");

            // On a surface: the pipeline is made, and its budget is the registry's.
            var window = new Window { Content = tile, Width = 400, Height = 300 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(tile.Pipeline);
            Assert.Contains(FrameBudgets.Attached, x => x.Label == "off the tree" && ReferenceEquals(x.Scope, bus));

            // A new viewport while on the surface: the same pipeline follows it (its budget takes
            // the new words at the next frame it draws — none is drawn here).
            var before = tile.Pipeline;
            tile.Viewport = Viewport("retitled");
            Assert.Same(before, tile.Pipeline);
            Assert.Equal("retitled", tile.Pipeline!.Viewport.Label);
            Assert.Single(FrameBudgets.Attached, x => ReferenceEquals(x.Scope, bus) && x.Label == "off the tree");

            // Off the surface: disposed, detached — and a viewport that changes afterwards makes
            // nothing. A pipeline made here would have no detach in its future.
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(tile.Pipeline);
            tile.Viewport = Viewport("after the close");
            Dispatcher.UIThread.RunJobs();
            Assert.Null(tile.Pipeline);
            Assert.DoesNotContain(FrameBudgets.Attached, x => x.Label is "off the tree" or "retitled" or "after the close");

            // Back on a surface: made again, from the viewport it has.
            window.Content = tile;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(tile.Pipeline);
            Assert.Contains(FrameBudgets.Attached, x => x.Label == "after the close");
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(tile.Pipeline);
            Assert.DoesNotContain(FrameBudgets.Attached, x => x.Label == "after the close");
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheRunMonitorHasAPipelineOnlyWhileItsLayoutIsOpenAndAClosedDeskLeavesNothing()
    {
        var b = TestApp.Boot();
        var bus = b.Services.Bus;
        try
        {
            Rig(b);
            Assert.True(b.Vm.HasRunMonitor);
            // Up: the desk's own sinks — PROGRAM, PREVIEW and the wall's tiles — are the registry's.
            Assert.Contains(FrameBudgets.Attached, x => ReferenceEquals(x.Scope, bus));

            // The Run layout has not been opened: its content has never been laid out, so the
            // monitor's tile is on no surface — and has no pipeline. Before the fix the viewport's
            // arrival made one here, off the tree, and it lived for the process.
            Assert.DoesNotContain(FrameBudgets.Attached, x => IsMonitor(x, bus));

            // Open it: the surface lays out, the tile attaches, and the pipeline is made.
            b.Vm.IsRunLayout = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(FrameBudgets.Attached, x => IsMonitor(x, bus));
        }
        finally
        {
            b.Dispose();
        }

        // Closed: not one budget of this desk's bus stays. One that did was the whole desk kept
        // alive — the boot after boot the host could not survive.
        Assert.DoesNotContain(FrameBudgets.Attached, x => ReferenceEquals(x.Scope, bus));
    }
}
