using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>ARRANGE AS A BLEND GRID on the desk: four planned projectors laid out 2 × 2 with their overlaps, every one on automatic blend, the joins found and the black level read back.</summary>
public class BlendGridAppTests
{
    [AvaloniaFact]
    public void FourPlannedProjectorsBecomeATwoByTwoBlendWithEqualJoins()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var screens = vm.Screens;
            var projectors = new List<ScreenPlacement>();
            for (var i = 1; i <= 4; i++)
            {
                projectors.Add(services.RigEditor.AddPlannedScreen(1920, 1080, $"PJ {i}"));
            }
            Dispatcher.UIThread.RunJobs();
            // The headless window's own display is off (the primary goes off when there are other screens) — the grid takes the four projectors.
            foreach (var p in vm.State.Output.Placements.Where(p => !projectors.Contains(p)))
            {
                p.Enabled = false;
                p.UserPinned = true;
            }
            screens.BlendGridColumns = 2;
            screens.BlendGridRows = 2;
            screens.BlendGridOverlap = 200;
            screens.ArrangeBlendGridCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.StartsWith("4 screens arranged as a blend grid — 2 × 2, 200 px overlaps: the canvas is 3640 × 1960.", vm.StatusMessage);
            Assert.Equal((0, 0), (projectors[0].X, projectors[0].Y));
            Assert.Equal((1720, 0), (projectors[1].X, projectors[1].Y));
            Assert.Equal((0, 880), (projectors[2].X, projectors[2].Y));
            Assert.Equal((1720, 880), (projectors[3].X, projectors[3].Y));
            Assert.All(projectors, p => Assert.True(p.BlendAuto));

            // The top-left projector's zones follow the overlaps: a right side and a bottom, no corner of its own.
            screens.SelectedPlacement = projectors[0];
            var readback = screens.BlendReadback;
            Assert.Contains("left 0 · top 0 · right 200 · bottom 200 px", readback);
            Assert.Contains("corner where four projectors meet", readback);          // black level off: the words say what a black scene shows
            screens.SelectedBlendBlack = 4;
            Assert.Equal(4, projectors[0].BlendBlackPct);
            Assert.Contains("lifted 4% of white per missing projector", screens.BlendReadback);
            var arranged = vm.BuildArranged();
            var mine = arranged.First(a => a.Id == projectors[0].ScreenId);
            var widths = EdgeBlend.Resolve(projectors[0], EdgeBlend.Derive(mine.Rect, arranged.Where(a => a.Id != mine.Id).Select(a => a.Rect)));
            Assert.Equal(4, BlackLevel.MaxCoverage(widths));

            // Edge bends and the pedestal ride the placement into the show file's own words: a bend makes the picture bent, reset makes it straight.
            projectors[0].WarpTopBow = 30;
            Assert.True(projectors[0].HasBend);
            screens.ResetWarpCommand.Execute(null);
            Assert.False(projectors[0].HasBend);
            screens.ResetBlendCommand.Execute(null);
            Assert.Equal(0, projectors[0].BlendBlackPct);

            // Fewer than two screens on: the grid says so and moves nothing.
            foreach (var p in projectors.Skip(1)) p.Enabled = false;
            Dispatcher.UIThread.RunJobs();
            screens.ArrangeBlendGridCommand.Execute(null);
            Assert.StartsWith("A blend grid needs at least two screens on", vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }
}
