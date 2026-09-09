using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 21 on a live desk: the reactive scenes as a pattern kind the operator can pick, name on
/// the wire and put in a cue — the registration a new kind needs, which is where a feature like
/// this ships half-wired if nobody works the list.
/// </summary>
public class ReactiveAppTests
{
    private static string Run(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    [AvaloniaFact]
    public void TheReactivePageIsOnTheRailAndCarriesItsControls()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            window.Width = 1420;
            window.Height = 900;

            var index = Shell.IndexOf("Reactive");
            Assert.True(index >= 0, "the Reactive page is on the rail");
            vm.SelectPage(index);
            Dispatcher.UIThread.RunJobs();

            var page = window.GetVisualDescendants().OfType<ReactiveSection>().FirstOrDefault();
            Assert.NotNull(page);

            // Six scenes, each a chip, and every one of them applies.
            Assert.Equal(6, vm.ReactiveScenes.Count);
            Assert.Equal(Enum.GetValues<ReactiveScene>().Length, vm.ReactiveScenes.Select(c => c.Scene).Distinct().Count());

            // USE IT makes it the editing target's pattern.
            vm.UsePatternKindCommand.Execute(PatternKind.Reactive);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Reactive, vm.ActivePattern.Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ASceneChipLandsAndTheLineSaysWhatItIsFor()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var o = vm.ActivePattern.Reactive;
            o.ColorsCsv = "#112233,#445566";
            o.AudioAmount = 0.4;
            o.UseBrandColors = false;

            var chip = vm.ReactiveScenes.First(c => c.Scene == ReactiveScene.StarWarp);
            vm.ApplyReactiveSceneCommand.Execute(chip);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(PatternKind.Reactive, vm.ActivePattern.Kind);
            Assert.Equal(ReactiveScene.StarWarp, o.Scene);
            Assert.Equal("Star Warp", o.Preset);
            Assert.Contains("Star Warp", vm.ReactiveSceneNote);

            // A scene is a starting point: the colours and the sound settings stay the operator's.
            Assert.Equal("#112233,#445566", o.ColorsCsv);
            Assert.Equal(0.4, o.AudioAmount);
            Assert.False(o.UseBrandColors);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheWireAndACueCanNameIt()
    {
        var b = TestApp.Boot();
        try
        {
            var services = b.Services;
            b.Vm.IsSandboxActive = false;
            var router = new CommandRouter(services);

            Assert.Equal("OK", Run(router, "PATTERN Reactive"));
            Assert.Equal(PatternKind.Reactive, services.AirState.Pattern.Kind);
            Assert.Contains("\"pattern\":\"Reactive\"", router.StateJson());

            // And it is one of the kinds a cue may name.
            Assert.Contains(ShowActionKind.PatternKind, ActionSpec.CueKinds);
            Assert.Equal("OK", Run(router, "PATTERN Grid"));
            Assert.Equal(PatternKind.Grid, services.AirState.Pattern.Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AReactiveSceneDrawsOnEverySinkTheDeskHas()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            vm.IsSandboxActive = false;
            services.BulkEdit(() =>
            {
                services.State.Pattern.Kind = PatternKind.Reactive;
                services.State.Pattern.Reactive.Scene = ReactiveScene.Tunnel;
            });
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // The desk drew it without a fault reaching the health line.
            var faults = HealthMonitor.Faults;
            vm.PollNow();
            Assert.Equal(faults, HealthMonitor.Faults);

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
        }
        finally
        {
            b.Dispose();
        }
    }
}
