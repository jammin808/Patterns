using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Pre-programming: planned screens with no hardware, and the GO guard.</summary>
public class PrepModeTests
{
    private static (AppServices Services, MainViewModel Vm, MainWindow Window) Boot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-prep-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var services = new AppServices(new SettingsStore(dir));
        AppServices.Instance = services;
        var vm = new MainViewModel(services);
        var window = new MainWindow { DataContext = vm };
        services.AttachMainWindow(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (services, vm, window);
    }

    [AvaloniaFact]
    public void ShowModeIsTheDefaultAndPrepHoldsGo()
    {
        var (services, vm, window) = Boot();
        try
        {
            Assert.False(vm.IsPrepMode);
            Assert.Equal(ShowMode.Show, services.State.Mode);

            vm.IsPrepMode = true;
            vm.GoCommand.Execute(null);

            Assert.False(services.Outputs.IsLive);
            Assert.Contains("PREP", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PREP", vm.ModeBanner, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void SwitchingIntoPrepClosesAnythingAlreadyLive()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.GoCommand.Execute(null); // may or may not find a headless screen
            vm.IsPrepMode = true;
            Assert.False(services.Outputs.IsLive);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void PlannedScreensJoinTheRigWithoutHardware()
    {
        var (services, vm, window) = Boot();
        try
        {
            var realCount = services.Screens.Real.Count;

            var planned = vm.AddPlannedScreen(3840, 1080, "Main wall");
            Dispatcher.UIThread.RunJobs();

            Assert.True(planned.Planned);
            Assert.StartsWith(ScreenPlacement.PlannedIdPrefix, planned.ScreenId);
            Assert.Equal(1, vm.PlannedScreenCount);

            // It is a first-class screen everywhere the operator works…
            var info = services.Screens.All.FirstOrDefault(s => s.Id == planned.ScreenId);
            Assert.NotNull(info);
            Assert.True(info!.IsPlanned);
            Assert.Equal(3840, info.Bounds.Width);
            Assert.Equal("Main wall", info.Label);
            Assert.Contains(vm.SwitcherTiles, t => t.MemberIds.Contains(planned.ScreenId));

            // Give it its own pattern (the operator's path) and it becomes an edit target.
            vm.SelectedPlacement = planned;
            vm.SelectedUseCustom = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(vm.EditTargets, t => t.ScreenId == planned.ScreenId);

            // …but never a real display, so no output window can open on it.
            Assert.Equal(realCount, services.Screens.Real.Count);
            Assert.DoesNotContain(
                OutputWindowManager.BuildViewports(services.State.Output.Placements, services.Screens.All),
                x => x.Screen.Id == planned.ScreenId);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void PlannedScreenSizeChangesFlowIntoTheArrangement()
    {
        var (services, vm, window) = Boot();
        try
        {
            var planned = vm.AddPlannedScreen(1920, 1080, "Side");
            planned.PlannedWidth = 2560;
            planned.PlannedHeight = 1440;
            Dispatcher.UIThread.RunJobs();

            var info = services.Screens.All.First(s => s.Id == planned.ScreenId);
            Assert.Equal(2560, info.Bounds.Width);
            Assert.Equal(1440, info.Bounds.Height);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void RemovingAPlannedScreenTakesItsPatternWithIt()
    {
        var (services, vm, window) = Boot();
        try
        {
            var planned = vm.AddPlannedScreen(1920, 1080, "Temp");
            services.State.Independent.Add(new OutputAssignment { ScreenId = planned.ScreenId });
            Dispatcher.UIThread.RunJobs();

            vm.RemovePlannedScreen(planned);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, vm.PlannedScreenCount);
            Assert.DoesNotContain(services.State.Output.Placements, p => p.ScreenId == planned.ScreenId);
            Assert.DoesNotContain(services.State.Independent, a => a.ScreenId == planned.ScreenId);
            Assert.DoesNotContain(services.Screens.All, s => s.Id == planned.ScreenId);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void AdoptingCarriesEveryProgrammedDetailOntoTheRealDisplay()
    {
        var (services, vm, window) = Boot();
        try
        {
            // The display the venue turned out to have (headless supplies one).
            var venueId = services.Screens.Real[0].Id;

            var planned = vm.AddPlannedScreen(3840, 2160, "Main wall");
            planned.Rotation = OutputRotation.Rot90;
            planned.BrightnessPct = 82;
            planned.UseCustomPattern = true;
            var assignment = new OutputAssignment { ScreenId = planned.ScreenId };
            assignment.Pattern.Kind = PatternKind.LedWall;
            services.State.Independent.Add(assignment);
            services.State.Output.CanvasNames.Add(new CanvasNameConfig
            {
                MemberKey = CanvasNameConfig.KeyFor(new[] { planned.ScreenId, "other" }),
                Name = "Wall",
            });
            services.State.Stream.SourceScreenId = planned.ScreenId;
            services.State.Pattern.Multiview.Tiles.Add(new MultiviewTileConfig
            {
                Source = MultiviewSource.Screen,
                ScreenId = planned.ScreenId,
            });
            var plannedId = planned.ScreenId;
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.AdoptPlannedScreen(planned, venueId));
            Dispatcher.UIThread.RunJobs();

            // The placement is now the real display, keeping everything programmed against it.
            Assert.False(planned.Planned);
            Assert.Equal(venueId, planned.ScreenId);
            Assert.Equal(OutputRotation.Rot90, planned.Rotation);
            Assert.Equal(82, planned.BrightnessPct);
            Assert.True(planned.UseCustomPattern);
            Assert.Equal("Main wall", planned.CustomLabel);
            Assert.Equal(0, vm.PlannedScreenCount);

            // …and every reference to the old id followed it.
            Assert.Equal(PatternKind.LedWall,
                services.State.Independent.First(a => a.ScreenId == venueId).Pattern.Kind);
            Assert.DoesNotContain(services.State.Independent, a => a.ScreenId == plannedId);
            Assert.Contains(services.State.Output.CanvasNames,
                c => c.MemberKey.Split('+').Contains(venueId));
            Assert.Equal(venueId, services.State.Stream.SourceScreenId);
            Assert.Equal(venueId, services.State.Pattern.Multiview.Tiles[0].ScreenId);

            // It is a real output now.
            Assert.Contains(
                OutputWindowManager.BuildViewports(services.State.Output.Placements, services.Screens.All),
                x => x.Screen.Id == venueId);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void AdoptingRefusesAnUnknownDisplay()
    {
        var (services, vm, window) = Boot();
        try
        {
            var planned = vm.AddPlannedScreen();
            Assert.False(vm.AdoptPlannedScreen(planned, "not-a-screen"));
            Assert.False(vm.AdoptPlannedScreen(planned, ""));
            Assert.True(planned.Planned);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void PlannedScreensAndModeSurviveASaveAndReload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-prep-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SettingsStore(dir);
        var state = store.Load();
        state.Mode = ShowMode.Prep;
        state.Output.Placements.Add(new ScreenPlacement
        {
            ScreenId = ScreenPlacement.PlannedIdPrefix + "abc123",
            Planned = true,
            PlannedWidth = 2560,
            PlannedHeight = 1600,
            CustomLabel = "Upstage",
        });
        store.Save(state);

        var back = new SettingsStore(dir).Load();
        Assert.Equal(ShowMode.Prep, back.Mode);
        var planned = back.Output.Placements.First(p => p.Planned);
        Assert.Equal(2560, planned.PlannedWidth);
        Assert.Equal(1600, planned.PlannedHeight);
        Assert.Equal("Upstage", planned.CustomLabel);
        Assert.Equal("", planned.AdoptTargetId); // runtime-only, never persisted
    }
}
