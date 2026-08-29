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
        var dir = Path.Combine(Path.GetTempPath(), "patterns-ui-tests-" + Guid.NewGuid().ToString("N"));
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
    public void IndependentModeCreatesAssignmentsAndEditTargets()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Output.Mode = OutputMode.Independent;
            Dispatcher.UIThread.RunJobs();

            // Headless platforms report at least one screen through the service (possibly zero —
            // the VM must not blow up either way).
            Assert.True(vm.EditTargets.Count >= 1);
            Assert.Equal("Program", vm.EditTargets[0].Label);
            Assert.Equal(vm.State.Independent.Count, vm.EditTargets.Count - 1);

            if (vm.EditTargets.Count > 1)
            {
                vm.EditTarget = vm.EditTargets[1];
                vm.ActivePattern.Kind = PatternKind.Focus;
                Assert.Equal(PatternKind.Focus, services.State.Independent[0].Pattern.Kind);
                Assert.Equal(PatternKind.Grid, services.State.Pattern.Kind);
            }
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
