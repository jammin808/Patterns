using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The mesh on the desk: a point pulled, the density changed with the shape kept, the lattice shown on the output, the reset.</summary>
public class MeshWarpAppTests
{
    [AvaloniaFact]
    public void APointPulledOnTheScreensPageReachesTheShowFileAndTheOutputAndTheLatticeFollowsTheSelection()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var screens = vm.Screens;
            var pj = services.RigEditor.AddPlannedScreen(1920, 1080, "PJ");
            Dispatcher.UIThread.RunJobs();
            screens.SelectedPlacement = pj;
            Assert.StartsWith("5×5 lattice — click a point", screens.MeshDescription);
            Assert.Equal(5, screens.SelectedMeshDensity);
            Assert.Equal(new[] { 3, 5, 9, 17 }, screens.MeshDensityOptions);

            // A pull: the show file carries it, the words say it, the viewport draws it.
            screens.MeshSelectedIndex = 12;
            services.RigEditor.PullMeshPoint(pj, 12, 24, -8);
            screens.RaiseMesh();
            Assert.True(pj.HasMesh);
            Assert.True(WarpGrid.HasMesh(pj));
            Assert.Equal((24f, -8f), (WarpGrid.Parse(pj.WarpMesh, 5, 5)[24], WarpGrid.Parse(pj.WarpMesh, 5, 5)[25]));
            Assert.StartsWith("point 3,3 of 5×5 — pulled 24 px right, 8 px up", screens.MeshDescription);
            var json = Patterns.Core.Services.JsonUtil.SerializeCompact(vm.State);
            Assert.Equal(pj.WarpMesh, Patterns.Core.Services.JsonUtil.Deserialize<ShowState>(json)!.Output.Placements.Single(p => p.ScreenId == pj.ScreenId).WarpMesh);

            // The density: the shape kept — the centre's pull is the centre's still.
            screens.SelectedMeshDensity = 9;
            Assert.Equal(9, pj.WarpMeshColumns);
            Assert.Equal(9, pj.WarpMeshRows);
            var fine = WarpGrid.Parse(pj.WarpMesh, 9, 9);
            Assert.Equal(24, fine[(4 * 9 + 4) * 2]);
            Assert.Equal(-8, fine[(4 * 9 + 4) * 2 + 1]);
            Assert.Equal(-1, screens.MeshSelectedIndex);

            // The lattice on the output: the rig editor names the output and the picked point; a new pick follows; another selection moves it.
            screens.MeshSelectedIndex = 40;
            screens.ShowLatticeOnOutput = true;
            Assert.Equal(pj.ScreenId, services.RigEditor.LatticeOn);
            Assert.Equal(40, services.RigEditor.LatticePoint);
            screens.MeshSelectedIndex = 41;
            Assert.Equal(41, services.RigEditor.LatticePoint);
            var viewports = Patterns.App.Services.OutputWindowManager.BuildViewports(vm.State.Output.Placements, services.Screens.All, includePlanned: true,
                latticeOn: services.RigEditor.LatticeOn, latticePoint: services.RigEditor.LatticePoint);
            var vp = viewports.Single(v => v.Viewport.ScreenId == pj.ScreenId).Viewport;
            Assert.True(vp.ShowLattice);
            Assert.Equal(41, vp.LatticePoint);
            Assert.Equal(pj.WarpMesh, vp.WarpMesh);
            Assert.True(vp.HasMesh);
            var other = services.RigEditor.AddPlannedScreen(1280, 720, "Other");
            Dispatcher.UIThread.RunJobs();
            screens.SelectedPlacement = other;
            Assert.Equal(other.ScreenId, services.RigEditor.LatticeOn);
            Assert.Equal(-1, services.RigEditor.LatticePoint);
            screens.ShowLatticeOnOutput = false;
            Assert.Equal("", services.RigEditor.LatticeOn);

            // Reset: the mesh at rest, and Reset warp takes the mesh with it.
            screens.SelectedPlacement = pj;
            screens.ResetMeshCommand.Execute(null);
            Assert.Equal("", pj.WarpMesh);
            Assert.False(pj.HasMesh);
            services.RigEditor.PullMeshPoint(pj, 0, 5, 5);
            pj.WarpTopBow = 12;
            screens.ResetWarpCommand.Execute(null);
            Assert.Equal("", pj.WarpMesh);
            Assert.Equal(0, pj.WarpTopBow);
        }
        finally
        {
            b.Dispose();
        }
    }
}
