using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The wall's gaps from the desk: the Screens page sets them, the output viewports cut them, a joined canvas seams its members, the show file keeps them.</summary>
public class WallGapAppTests
{
    [AvaloniaFact]
    public void TheScreensPageSetsTheGapsAndTheOutputsAreCutByThem()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;

            // A planned LED wall: three pillars packed in a 600 × 200 raster, standing on its own (a new planned screen lands flush beside the last one).
            var wall = vm.AddPlannedScreen(600, 200, "Pillars");
            wall.X = 0;
            wall.Y = 4000;
            Dispatcher.UIThread.RunJobs();
            vm.SelectedPlacement = wall;
            Assert.True(vm.HasSelection);
            Assert.StartsWith("No gaps", vm.GapSummary);

            vm.GapGridColumns = 3;
            vm.GapGridRows = 1;
            vm.GapGridPx = 100;
            vm.SetGapsFromGridCommand.Execute(null);
            Assert.Equal(new[] { 200, 400 }, wall.Gaps.Select(g => g.At));
            Assert.All(wall.Gaps, g => Assert.Equal(GapAxis.Vertical, g.Axis));
            Assert.All(wall.Gaps, g => Assert.Equal(100, g.Size));
            Assert.Contains("2 vertical", vm.GapSummary);
            Assert.Contains("800×200", vm.GapSummary);
            Assert.Contains("3 runs", vm.GapSummary);
            Assert.Equal(wall.ScreenId, Rig.Geometry(vm.State, services.Screens.All).TargetOf(wall.ScreenId));

            // The output viewport of it: the surface with the strips put back, three runs of real pixels.
            var vps = OutputWindowManager.BuildViewports(vm.State.Output.Placements, services.Screens.All,
                includePlanned: true, canvases: vm.State.Output.CanvasNames);
            var vp = vps.Single(x => x.Screen.Id == wall.ScreenId).Viewport;
            Assert.Equal(new SKSizeI(800, 200), vp.ReferenceSize);
            Assert.Equal(new SKPointI(0, 0), vp.ViewportOrigin);
            Assert.Equal(SKRectI.Create(0, 0, 600, 200), vp.RasterRegion);
            Assert.Equal(3, vp.WallSlices);

            // The switcher tile of it takes the surface's shape.
            Assert.Contains(vm.SwitcherTiles, t => t.MemberIds.Contains(wall.ScreenId) && t.Size == new SKSizeI(800, 200));

            // The page: a row per gap with its remove button; + Gap adds one in the middle.
            var host = new Window { DataContext = vm, Width = 900, Height = 2800, Content = new ScrollViewer { Content = new OutputsSection() } };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            var removes = host.GetVisualDescendants().OfType<Button>().Where(x => x.DataContext is WallGap && x.Content as string == "✕").ToList();
            Assert.Equal(2, removes.Count);
            Assert.Contains(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.GapSummary);
            removes[0].Command!.Execute(removes[0].CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(wall.Gaps);
            Assert.Equal(400, wall.Gaps[0].At);
            vm.AddGapCommand.Execute(null);
            Assert.Equal(2, wall.Gaps.Count);
            Assert.Equal(300, wall.Gaps[1].At);
            host.Close();

            // Two planned displays joined into a canvas: the bezel between them seams it.
            var left = vm.AddPlannedScreen(1920, 1080, "Left");
            var right = vm.AddPlannedScreen(1920, 1080, "Right");
            left.X = 0; left.Y = 8000;
            right.X = 1920; right.Y = 8000;
            Dispatcher.UIThread.RunJobs();
            vm.SelectedPlacement = right;
            Assert.True(vm.SelectedIsInCanvas);
            Assert.Equal(0, vm.SelectedSeamGapX);
            vm.SelectedSeamGapX = 40;
            var key = CanvasNameConfig.KeyFor(new[] { left.ScreenId, right.ScreenId });
            Assert.Equal(40, vm.State.Output.CanvasNames.Single(c => c.MemberKey == key).SeamGapX);
            Assert.Equal(40, vm.SelectedSeamGapX);
            Assert.Contains("1 vertical", vm.GapSummary);
            vps = OutputWindowManager.BuildViewports(vm.State.Output.Placements, services.Screens.All,
                includePlanned: true, canvases: vm.State.Output.CanvasNames);
            var r = vps.Single(x => x.Screen.Id == right.ScreenId).Viewport;
            Assert.Equal(new SKSizeI(3880, 1080), r.ReferenceSize);
            Assert.Equal(new SKPointI(1960, 0), r.ViewportOrigin);
            Assert.Equal(1, r.WallSlices);
            var l = vps.Single(x => x.Screen.Id == left.ScreenId).Viewport;
            Assert.Equal(new SKPointI(0, 0), l.ViewportOrigin);
            Assert.Equal(new SKSizeI(3880, 1080), l.ReferenceSize);
            // Without the canvases list the viewports know nothing of the seam (the plain call other tests make).
            var plain = OutputWindowManager.BuildViewports(vm.State.Output.Placements, services.Screens.All, includePlanned: true);
            Assert.Equal(new SKSizeI(3840, 1080), plain.Single(x => x.Screen.Id == right.ScreenId).Viewport.ReferenceSize);

            // The program pane follows the surface of the first target.
            Assert.Equal(services.Bus.Current.Rig.SizeOf(null), Rig.TargetSize(vm.State, services.Screens.All, null));

            // The show file carries the gaps and the seams, and a reopened show has them.
            var json = JsonUtil.Serialize(vm.State);
            Assert.Contains("\"Gaps\"", json);
            Assert.Contains("\"SeamGapX\": 40", json);
            var clone = JsonUtil.Clone(vm.State);
            Assert.Equal(2, clone.Output.Placements.First(p => p.ScreenId == wall.ScreenId).Gaps.Count);
            Assert.Equal(40, clone.Output.CanvasNames.Single(c => c.MemberKey == key).SeamGapX);

            // Clear takes the strips and the seams away.
            vm.ClearGapsCommand.Execute(null);
            Assert.Equal(0, vm.SelectedSeamGapX);
            vm.SelectedPlacement = wall;
            vm.ClearGapsCommand.Execute(null);
            Assert.Empty(wall.Gaps);
            Assert.StartsWith("No gaps", vm.GapSummary);
        }
        finally
        {
            b.Dispose();
        }
    }
}
