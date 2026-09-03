using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Patterns.App.Tests.TestAppBuilder))]

namespace Patterns.App.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false, // real Skia rendering, headless framebuffer
        })
        .UseSkia();
}

public class HeadlessUiTests
{
    private static (AppServices Services, MainViewModel Vm, MainWindow Window) Boot()
    {
        var b = TestApp.Boot();
        return (b.Services, b.Vm, b.Window);
    }

    [AvaloniaFact]
    public void MainWindowBootsAndRendersPreview()
    {
        var (services, _, window) = Boot();
        try
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            // The default grid preview must have drawn something non-background.
            var hasContent = false;
            using (var fb = frame!.Lock())
            {
                unsafe
                {
                    var ptr = (byte*)fb.Address;
                    var count = fb.Size.Width * fb.Size.Height;
                    for (var i = 0; i < count; i += 97)
                    {
                        var px = ptr + i * 4;
                        if (px[0] > 0xE0 && px[1] > 0xE0 && px[2] > 0xE0)
                        {
                            hasContent = true;
                            break;
                        }
                    }
                }
            }
            Assert.True(hasContent, "expected bright grid pixels in the rendered window");
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void EveryPatternKindSurvivesTheRealUi()
    {
        var (services, vm, window) = Boot();
        try
        {
            foreach (var kind in Enum.GetValues<PatternKind>())
            {
                vm.ActivePattern.Kind = kind;
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void StateChangesFlowToSnapshotsAndBack()
    {
        var (services, vm, window) = Boot();
        try
        {
            var before = services.Bus.Current.Version;
            vm.State.Pattern.Grid.CellSize = 123;
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Bus.Current.Version > before);
            Assert.Equal(123, services.Bus.Current.State.Pattern.Grid.CellSize);

            vm.BlackoutCommand.Execute(null);
            Assert.True(services.Bus.Current.State.Blackout);
            vm.BlackoutCommand.Execute(null);
            Assert.False(services.Bus.Current.State.Blackout);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void PlacementsReconcileAndCustomPatternsFlow()
    {
        var (services, vm, window) = Boot();
        try
        {
            // Every detected screen gets a placement.
            var screens = services.Screens.All;
            Assert.Equal(screens.Count, vm.State.Output.Placements.Count(p => screens.Any(s => s.Id == p.ScreenId)));

            if (screens.Count > 0)
            {
                var placement = vm.State.Output.Placements.First(p => p.ScreenId == screens[0].Id);
                vm.SelectedPlacement = placement;
                vm.SelectedUseCustom = true;
                Dispatcher.UIThread.RunJobs();

                // Assignment created, edit target appeared and was auto-selected.
                Assert.Contains(vm.State.Independent, a => a.ScreenId == placement.ScreenId);
                Assert.True(vm.ShowEditTargets);
                Assert.Equal(placement.ScreenId, vm.EditTarget.ScreenId);

                vm.ActivePattern.Kind = PatternKind.Focus;
                Assert.Equal(PatternKind.Focus,
                    services.State.Independent.First(a => a.ScreenId == placement.ScreenId).Pattern.Kind);
                Assert.Equal(PatternKind.Grid, services.State.Pattern.Kind);

                // Enable toggle pins the user's choice.
                vm.SelectedEnabled = false;
                Assert.True(placement.UserPinned);
                Assert.False(placement.Enabled);
            }
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void PrimaryScreenDefaultsOffWhenOthersExist()
    {
        var (services, vm, window) = Boot();
        try
        {
            var fakes = new List<ScreenInfo>
            {
                new("p", "Primary", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
                new("b", "Wall feed", new Avalonia.PixelRect(1920, 0, 1920, 1080), 1.0, false, 1),
            };
            vm.State.Output.Placements.Clear();
            vm.ReconcilePlacements(fakes);

            Assert.False(vm.State.Output.Placements.First(p => p.ScreenId == "p").Enabled);
            Assert.True(vm.State.Output.Placements.First(p => p.ScreenId == "b").Enabled);

            // Alone, the primary must stay enabled or GO would do nothing.
            vm.State.Output.Placements.Clear();
            vm.ReconcilePlacements(new List<ScreenInfo> { fakes[0] });
            Assert.True(vm.State.Output.Placements.First(p => p.ScreenId == "p").Enabled);

            // A pinned user choice survives re-detection.
            vm.State.Output.Placements.Clear();
            vm.ReconcilePlacements(fakes);
            var primary = vm.State.Output.Placements.First(p => p.ScreenId == "p");
            primary.Enabled = true;
            primary.UserPinned = true;
            vm.ReconcilePlacements(fakes);
            Assert.True(primary.Enabled);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [Fact]
    public void StableFolderKeyIsDeterministicAndCaseInsensitive()
    {
        // string.GetHashCode is randomized per process; the single-instance mutex needs a
        // stable name or the second-instance autosave guard never engages.
        var a = AppServices.StableFolderKey(@"C:\Shows\Patterns");
        var b = AppServices.StableFolderKey(@"c:\shows\patterns");
        var c = AppServices.StableFolderKey(@"D:\Other");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(24, a.Length);
        Assert.Matches("^[0-9A-F]+$", a);
    }

    [AvaloniaFact]
    public void LibraryPresetsApplyToState()
    {
        var (services, vm, window) = Boot();
        try
        {
            var smpte = vm.Library.First(p => p.Name == "SMPTE bars");
            smpte.Apply();
            Assert.Equal(PatternKind.ColorBars, services.State.Pattern.Kind);

            var blend = vm.Library.First(p => p.Name.StartsWith("Blend 3×"));
            blend.Apply();
            Assert.Equal(PatternKind.ProjectionBlend, services.State.Pattern.Kind);
            Assert.Equal(3, services.State.Pattern.Blend.Projectors);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }
}
