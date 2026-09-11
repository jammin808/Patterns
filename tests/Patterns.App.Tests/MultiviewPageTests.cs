using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The Multiview page: where the monitor walls are, now that they are the show's rather than one
/// screen's pattern. The thing being pinned is that the operator does one thing — tick an output —
/// and everything a wall needs to reach it is done for them.
/// </summary>
public class MultiviewPageTests
{
    private static void Rig(MainViewModel vm)
    {
        vm.State.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", Planned = true, PlannedWidth = 1920, PlannedHeight = 1080, Enabled = true });
        vm.State.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", Planned = true, PlannedWidth = 1920, PlannedHeight = 1080, X = 1920, Enabled = true });
    }

    [AvaloniaFact]
    public void ThePageIsOnTheRailAndAWallArrivesReadyToUse()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            window.Width = 1500;
            window.Height = 950;
            Rig(vm);
            Dispatcher.UIThread.RunJobs();

            // Where it is: SETUP, beside Screens — the answer to "I cannot find the multiview".
            var page = Shell.Pages.Single(p => p.Header == "Multiview");
            Assert.Equal(ShellGroup.Setup, page.Group);
            vm.SelectPage(page.Index);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(page.Index, vm.SelectedPageIndex);

            Assert.Empty(vm.Walls);
            Assert.True(vm.HasNoWall);
            var add = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(x => x.Name == "AddWall");
            Assert.NotNull(add);

            // A wall arrives filled from the rig, with the two that decide anything at the top.
            vm.AddWallCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var wall = Assert.Single(vm.State.Multiviews);
            Assert.True(vm.HasWall);
            Assert.Same(wall, vm.SelectedWall);
            Assert.Equal(MultiviewSource.Program, wall.Tiles[0].Source);
            Assert.Equal(MultiviewSource.Preview, wall.Tiles[1].Source);
            Assert.Contains(wall.Tiles, t => t.ScreenId == "a");
            Assert.Equal(MultiviewLayout.ProgramAndPreview, wall.Layout);
            Assert.Single(vm.Walls);
            Assert.True(vm.Walls[0].IsSelected);

            // Two is the limit, and the button says so rather than failing quietly.
            vm.AddWallCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, vm.State.Multiviews.Count);
            Assert.False(vm.CanAddWall);
            vm.AddWallCommand.Execute(null);
            Assert.Equal(2, vm.State.Multiviews.Count);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TickingAnOutputIsTheWholeJob()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            Rig(vm);
            vm.State.Ndi.Senders.Add(new NdiSenderConfig { Name = "Gallery" });
            vm.ReconcilePlacements();
            vm.AddWallCommand.Execute(null);
            vm.SelectPage(Shell.IndexOf("Multiview"));
            Dispatcher.UIThread.RunJobs();
            var wall = vm.State.Multiviews[0];

            // Every output a wall can go to is offered: the screens, each NDI sender, the stream.
            Assert.Contains(vm.WallDestinations, d => d.TargetId == "a" && d.Kind == "SCREEN");
            Assert.Contains(vm.WallDestinations, d => d.Kind == "NDI");
            Assert.Contains(vm.WallDestinations, d => d.TargetId == StreamConfig.OwnScreenId && d.Kind == "STREAM");
            Assert.All(vm.WallDestinations, d => Assert.False(d.IsOn));

            // One tick: the screen's own pattern on, set to this wall, and the programme untouched.
            var screen = vm.WallDestinations.Single(d => d.TargetId == "b");
            var programmeWas = vm.State.Pattern.Kind;
            screen.IsOn = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Multiviews.IsShowing(vm.State, "b", wall.Id));
            Assert.Equal(programmeWas, vm.State.Pattern.Kind);   // the show is still the show
            Assert.Equal("showing this wall", screen.Note);
            Assert.Contains("on 1 output", vm.WallSummary);

            // The stream: pointed at its own picture too, so nothing is left to find on its page.
            var stream = vm.WallDestinations.Single(d => d.TargetId == StreamConfig.OwnScreenId);
            stream.IsOn = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Stream.UsesOwnScreen);
            Assert.True(Multiviews.IsShowing(vm.State, StreamConfig.OwnScreenId, wall.Id));

            // Unticked, straight back to the programme.
            screen.IsOn = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(ContentTargets.UsesOwnPattern(vm.State, "b"));
            Assert.Equal("showing the programme", vm.WallDestinations.Single(d => d.TargetId == "b").Note);

            // And removing the wall takes everything it was on back with it.
            vm.RemoveWallCommand.Execute(wall);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.State.Multiviews);
            Assert.False(vm.State.Stream.UsesOwnScreen);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void DraggingATileToTheTopIsHowYouChooseWhatTheWallWatches()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            window.Width = 1500;
            window.Height = 950;
            Rig(vm);
            vm.ReconcilePlacements();
            vm.AddWallCommand.Execute(null);
            vm.SelectPage(Shell.IndexOf("Multiview"));
            Dispatcher.UIThread.RunJobs();
            var wall = vm.State.Multiviews[0];

            var screen = wall.Tiles.First(t => t.ScreenId == "b");
            vm.MoveWallTileTo(screen, 0);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(screen, wall.Tiles[0]);
            Assert.Equal(MultiviewSource.Program, wall.Tiles[1].Source);   // the rest shuffled down

            // The grip is on every row for the pointer to take hold of.
            var grips = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "TileGrip").ToList();
            Assert.Equal(wall.Tiles.Count, grips.Count);
            Assert.All(grips, g => Assert.True(Views.Controls.DragReorder.GetGrip(g)));

            // The ↑ ↓ buttons run the same path, and neither can walk off the end of the list.
            vm.MoveWallTileDownCommand.Execute(screen);
            Assert.Same(screen, wall.Tiles[1]);
            vm.MoveWallTileUpCommand.Execute(screen);
            Assert.Same(screen, wall.Tiles[0]);
            vm.MoveWallTileUpCommand.Execute(screen);
            Assert.Same(screen, wall.Tiles[0]);
        }
        finally
        {
            b.Dispose();
        }
    }
}
