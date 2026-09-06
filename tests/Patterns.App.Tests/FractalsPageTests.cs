using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 18: "Fractals needs its own menu area like Particles." The Fractals page on the BUILD rail —
/// scenes in families, a chip applying a scene, the operator's own scenes beside them, the brand
/// kit as a palette, USE IT, and the Pattern page's pointer to it.
/// </summary>
public class FractalsPageTests
{
    [AvaloniaFact]
    public void ScenesComeInFamiliesAndAChipAppliesOneLeavingTheSoundAlone()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            Assert.Equal(FractalPresets.Categories, vm.FractalSceneGroups.Select(g => g.Category).ToArray());
            Assert.Equal(FractalPresets.Scenes.Count, vm.FractalSceneGroups.Sum(g => g.Chips.Count));
            var julia = vm.FractalSceneGroups.Single(g => g.Category == "Julia");
            Assert.Contains(julia.Chips, c => c.Name == "Douady's rabbit");

            // USE IT makes the Fractal the pattern; a chip applies a scene and never touches the sound settings.
            Assert.NotEqual(PatternKind.Fractal, vm.ActivePattern.Kind);
            vm.UsePatternKindCommand.Execute(PatternKind.Fractal);
            Assert.Equal(PatternKind.Fractal, vm.ActivePattern.Kind);
            Assert.Contains("studio shows live", vm.StatusMessage);
            vm.ActivePattern.Fractal.AudioSource = AudioSourceKind.External;
            vm.ActivePattern.Fractal.AudioDevice = "Desk mic";
            vm.ActivePattern.Fractal.AudioAmount = 0.8;
            var triad = vm.FractalSceneGroups.Single(g => g.Category == "Newton").Chips.Single(c => c.Name == "Newton triad");
            vm.ApplyFractalChipCommand.Execute(triad);
            Assert.Equal(FractalKind.Newton, vm.ActivePattern.Fractal.Kind);
            Assert.Equal("Newton triad", vm.ActivePattern.Fractal.Preset);
            Assert.Equal(("Desk mic", 0.8, AudioSourceKind.External), (vm.ActivePattern.Fractal.AudioDevice, vm.ActivePattern.Fractal.AudioAmount, vm.ActivePattern.Fractal.AudioSource));

            // The brand kit's five colours as the palette.
            vm.State.Brand.BackgroundColor = "#101010";
            vm.State.Brand.PrimaryColor = "#3EC1F3";
            vm.State.Brand.SecondaryColor = "#F03EAE";
            vm.State.Brand.AccentColor = "#FFB020";
            vm.State.Brand.TextColor = "#FFFFFF";
            vm.ApplyBrandPaletteToFractalCommand.Execute(null);
            Assert.Equal("#101010,#3EC1F3,#F03EAE,#FFB020,#FFFFFF", vm.ActivePattern.Fractal.ColorsCsv);

            // A saved fractal preset becomes a Custom chip; a saved pattern of another kind does not.
            vm.ActivePattern.Fractal.Zoom = 42;
            vm.NewPresetName = "My nebula";
            vm.SavePresetCommand.Execute(null);
            vm.ActivePattern.Kind = PatternKind.Grid;
            vm.NewPresetName = "A grid";
            vm.SavePresetCommand.Execute(null);
            var custom = vm.FractalSceneGroups.Single(g => g.Category == "Custom");
            Assert.Equal("My nebula", Assert.Single(custom.Chips).Name);
            vm.ActivePattern.Kind = PatternKind.Fractal;
            vm.ActivePattern.Fractal.Zoom = 1;
            vm.ApplyFractalChipCommand.Execute(custom.Chips[0]);
            Assert.Equal(42, vm.ActivePattern.Fractal.Zoom);
            Assert.Equal(FractalKind.Newton, vm.ActivePattern.Fractal.Kind);

            // The Library files every scene under Fractals by family.
            Assert.Contains("Fractals", vm.LibrarySections);
            var tile = vm.LibraryAll.Single(i => i.Name == "Domain warp aurora");
            Assert.Equal(("Fractals", "Domain warp"), (tile.Section, tile.Category));

            // The page: a chip per scene and the Custom one, the USE IT and BRAND KIT buttons.
            var host = new Window { DataContext = vm, Width = 900, Height = 1800, Content = new ScrollViewer { Content = new FractalsSection() } };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            using var frame = host.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var chips = host.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("chip")).ToList();
            Assert.True(chips.Count >= FractalPresets.Scenes.Count + 1, $"{chips.Count} chips");
            Assert.Contains(chips, c => Equals(c.Content, "Seahorse valley"));
            Assert.Contains(chips, c => Equals(c.Content, "My nebula"));
            var buttons = host.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Same(vm.UsePatternKindCommand, buttons.Single(x => x.Name == "UseFractal").Command);
            Assert.Same(vm.ApplyBrandPaletteToFractalCommand, buttons.Single(x => x.Name == "BrandPalette").Command);
            host.Close();
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThePageIsOnTheRailAfterParticlesAndThePatternPagePointsToIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            Assert.Equal(Shell.IndexOf("Particles") + 1, Shell.IndexOf("Fractals"));
            Assert.Equal(ShellGroup.Build, Shell.Pages[Shell.IndexOf("Fractals")].Group);
            Assert.Contains("fractals", Shell.Info(ShellGroup.Build).Hint);

            // OPEN FRACTALS on the Pattern page, shown while Fractal is the pattern, opens the page.
            vm.SelectPage(Shell.IndexOf("Pattern"));
            vm.ActivePattern.Kind = PatternKind.Fractal;
            Settle(window);
            var open = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "OpenFractals");
            Assert.True(open.IsEffectivelyVisible);
            open.Command!.Execute(open.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Shell.IndexOf("Fractals"), vm.SelectedPageIndex);
            Settle(window);
            Assert.Single(window.GetVisualDescendants().OfType<FractalsSection>(), s => s.IsEffectivelyVisible);

            // The pointer for the particles has the same button; a header that is not a page is ignored.
            vm.SelectPage(Shell.IndexOf("Pattern"));
            vm.ActivePattern.Kind = PatternKind.Particles;
            Settle(window);
            var particles = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "OpenParticles");
            Assert.True(particles.IsEffectivelyVisible);
            vm.OpenPageCommand.Execute("No such page");
            Assert.Equal(Shell.IndexOf("Pattern"), vm.SelectedPageIndex);
            vm.OpenPageCommand.Execute("Particles");
            Assert.Equal(Shell.IndexOf("Particles"), vm.SelectedPageIndex);

            // Help knows the page.
            var topic = HelpTopics.All.Single(t => t.Id == "fractals");
            Assert.Contains("Fractals", topic.Pages);
            Assert.Contains("mandelbrot", topic.Keywords);
            Assert.Contains("Fractals page", HelpBodies.Workflow);
            Assert.Contains("fractals", HelpBodies.Shell);
        }
        finally
        {
            b.Dispose();
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
