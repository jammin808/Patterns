using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 15: "Layers should have their own area before Library and Assistant." The page on the
/// BUILD rail, its place, the editors bound to the editing target's own pattern, the page controls
/// for a web layer, and the Media page carrying no layers any more.
/// </summary>
public class LayersPageTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
    };

    private static void Rig(TestApp.Booted b)
    {
        var fakes = ThreeScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.RebuildSwitcherTiles();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static List<string?> VisibleTexts(Window window)
        => window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();

    [AvaloniaFact]
    public void TheLayersHaveTheirOwnPageBeforeTheLibraryAndTheAssistant()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            Rig(b);
            vm.IsSandboxActive = false;

            // The rail: BUILD's strip runs … Branding, Layers, Library, Assistant.
            var build = Shell.Pages.Where(p => p.Group == ShellGroup.Build).Select(p => p.Header).ToList();
            Assert.Equal(new[] { "Branding", "Layers", "Library", "Assistant" }, build.TakeLast(4));
            Assert.Equal(Shell.IndexOf("Library") - 1, Shell.IndexOf("Layers"));
            Assert.Contains("layers", Shell.Info(ShellGroup.Build).Hint);

            vm.SelectPage(Shell.IndexOf("Layers"));
            Settle(window);
            Assert.Equal(ShellGroup.Build, vm.SelectedGroup);
            Assert.Equal("Layers", vm.PageStrip.Single(p => p.IsCurrent).Header);
            var texts = VisibleTexts(window);
            Assert.Contains("Layers", texts);
            Assert.Contains("LAYER 1", texts);
            Assert.Contains("LAYER 2", texts);
            Assert.Contains(texts, t => t is not null && t.StartsWith("EDITING: PROGRAM", StringComparison.Ordinal));

            // Program alone: no target picker; OWN on the lobby brings it, and the editors follow the lobby's own pattern.
            Assert.False(vm.ShowEditTargets);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible), c => ReferenceEquals(c.ItemsSource, vm.EditTargets));
            vm.SwitcherTiles.First(t => t.TargetId == "c").IsOwn = true;
            Settle(window);
            Assert.True(vm.ShowEditTargets);
            Assert.Equal("c", vm.EditTarget.ScreenId);
            var picker = window.GetVisualDescendants().OfType<ComboBox>().First(c => ReferenceEquals(c.ItemsSource, vm.EditTargets));
            Assert.True(picker.IsEffectivelyVisible);
            Assert.Same(vm.EditTarget, picker.SelectedItem);
            var own = vm.ActivePattern;
            Assert.NotSame(vm.State.Pattern, own);
            var onBoxes = window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible && c.Content as string == "On").ToList();
            Assert.Equal(2, onBoxes.Count);
            Assert.Same(own.Layer1, onBoxes[0].DataContext);
            Assert.Same(own.Layer2, onBoxes[1].DataContext);

            // A web layer brings PAGE CONTROLS to this page, as on the Media page.
            Assert.DoesNotContain("PAGE CONTROLS", VisibleTexts(window));
            own.Layer1.Enabled = true;
            own.Layer1.Source = LayerSource.Web;
            own.Layer1.WebUrl = "https://example.org/board";
            vm.PollNow();
            Settle(window);
            Assert.True(vm.HasWebPage);
            Assert.Contains("PAGE CONTROLS", VisibleTexts(window));

            // The Media page carries no layers any more — its page block and the same controls stay.
            vm.SelectPage(Shell.IndexOf("Media"));
            Settle(window);
            var media = VisibleTexts(window);
            Assert.DoesNotContain("LAYERS", media);
            Assert.DoesNotContain("LAYER 1", media);
            Assert.Contains("PAGE CONTROLS", media);

            // The walkthrough for a page and layers points at the new page.
            Assert.Contains(Patterns.Core.Services.Walkthroughs.All.SelectMany(w => w.Steps), s => s.Page == "Layers");
        }
        finally
        {
            b.Dispose();
        }
    }
}
