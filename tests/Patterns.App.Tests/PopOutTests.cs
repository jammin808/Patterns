using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.App.Views.Panels;
using Patterns.App.Views.Sections;
using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The pop-out settings column beside the page: it opens for the selected cue on the Cues page,
/// the selected screen on the Screens page and the selected element on the Lower thirds page,
/// grows the page column by its width, closes with the page or the selection, stays closed for a
/// selection the operator closed it on until the selection moves or SETTINGS ▸ is pressed, and
/// keeps out of the Run layout.
/// </summary>
public class PopOutTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static bool WearsHue(TextBlock band, string page)
    {
        var hue = Color.Parse(Shell.Pages[Shell.IndexOf(page)].Hue);
        return band.Foreground is ISolidColorBrush s && s.Color.R == hue.R && s.Color.G == hue.G && s.Color.B == hue.B;
    }

    [AvaloniaFact]
    public void TheSettingsColumnFollowsThePageAndItsSelection()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            window.Width = 1600;
            window.Height = 900;
            Settle(window);
            var host = window.GetVisualDescendants().OfType<PopOutHost>().Single();
            var pageWidth = vm.State.Desk.EditorWidth;

            // The Panel page: nothing to pop out.
            Assert.False(vm.PopOut.IsOpen);
            Assert.False(host.IsEffectivelyVisible);
            Assert.Equal(pageWidth, window.EditorColumnWidth);

            // The Cues page with no cue: closed. A cue added is selected, and the column opens on it.
            vm.SelectPage(Shell.IndexOf("Cues"));
            Settle(window);
            Assert.False(vm.PopOut.IsOpen);
            vm.Cues.AddCueCommand.Execute(null);
            Settle(window);
            Assert.True(vm.PopOut.IsOpen);
            Assert.Equal("cue", vm.PopOut.Key);
            Assert.StartsWith("SELECTED CUE · ", vm.PopOut.Title);
            Assert.True(host.IsEffectivelyVisible);
            Assert.IsType<CueSettingsPanel>(host.Panel);
            // The column wears the page's hue: its title band and the panel's bands in the Cues neon, like the page's own.
            Assert.Equal("hue-cues", vm.PopOut.Hue);
            Assert.Contains("hue-cues", host.Classes);
            var bands = host.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("h2")).ToList();
            Assert.Equal(vm.PopOut.Title, bands[0].Text);
            Assert.True(bands.Count >= 2, $"{bands.Count} bands in the column");
            Assert.All(bands, band => Assert.True(WearsHue(band, "Cues"), $"'{band.Text}' in the Cues neon"));
            Assert.Equal(pageWidth + PopOutHost.ColumnWidth, window.EditorColumnWidth);
            Assert.Equal(pageWidth, window.PageColumnWidth);
            Assert.Equal(pageWidth, vm.State.Desk.EditorWidth);   // the show remembers the page's own width
            // The cue's settings live in the column, not under the list.
            var cuesPage = window.GetVisualDescendants().OfType<CuesSection>().First();
            Assert.DoesNotContain(cuesPage.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "ACTIONS (RUN IN ORDER)");
            Assert.Contains(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "ACTIONS (RUN IN ORDER)");
            Assert.Contains(cuesPage.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "SETTINGS ▸" && !x.IsVisible);

            // ◀ CLOSE: closed for this cue; the page's SETTINGS ▸ shows; another cue selected opens it again.
            vm.ClosePopOutCommand.Execute(null);
            Settle(window);
            Assert.False(vm.PopOut.IsOpen);
            Assert.Equal(pageWidth, window.EditorColumnWidth);
            Assert.Contains(cuesPage.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "SETTINGS ▸" && x.IsEffectivelyVisible);
            vm.OpenPopOutCommand.Execute(null);
            Assert.True(vm.PopOut.IsOpen);
            vm.ClosePopOutCommand.Execute(null);
            var first = vm.Cues.SelectedCue!;
            vm.Cues.AddCueCommand.Execute(null);
            Assert.NotSame(first, vm.Cues.SelectedCue);
            Assert.True(vm.PopOut.IsOpen);
            Assert.Contains(vm.Cues.SelectedCue!.Number, vm.PopOut.Title);
            Assert.Same(host.Panel, host.Panel);   // the same panel bound to the new cue, not rebuilt

            // Another page closes it; back on Cues it opens again for the selection.
            vm.SelectPage(Shell.IndexOf("Looks"));
            Settle(window);
            Assert.False(vm.PopOut.IsOpen);
            Assert.False(host.IsEffectivelyVisible);
            vm.SelectPage(Shell.IndexOf("Cues"));
            Assert.True(vm.PopOut.IsOpen);

            // The Screens page: the selected screen's settings.
            var side = vm.AddPlannedScreen(1920, 1080, "Side");
            vm.SelectPage(Shell.IndexOf("Screens"));
            vm.SelectedPlacement = side;
            Settle(window);
            Assert.Equal("screen", vm.PopOut.Key);
            Assert.Equal("SELECTED SCREEN · " + vm.SelectedScreenTitle, vm.PopOut.Title);
            Assert.IsType<ScreenSettingsPanel>(host.Panel);
            Assert.Contains(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Output trims");
            Assert.Equal("hue-screens", vm.PopOut.Hue);
            Assert.DoesNotContain("hue-cues", host.Classes);   // the Cues hue went with the page
            Assert.True(WearsHue(host.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("h2")), "Screens"));
            vm.SelectedPlacement = null;
            Assert.False(vm.PopOut.IsOpen);

            // The Lower thirds page: the selected element's settings.
            var design = LowerThirdPresets.Create("Clean");
            vm.State.LowerThirds.Designs.Add(design);
            vm.SelectedLowerThird = design;
            vm.SelectPage(Shell.IndexOf("Lower thirds"));
            vm.SelectedElement = design.Elements[0];
            Settle(window);
            Assert.Equal("element", vm.PopOut.Key);
            Assert.StartsWith("SELECTED ELEMENT · ", vm.PopOut.Title);
            Assert.IsType<ElementSettingsPanel>(host.Panel);

            // The Run layout keeps the column out of the way; back on a page it returns.
            vm.SelectRunCommand.Execute(null);
            Settle(window);
            Assert.False(vm.PopOut.IsOpen);
            Assert.False(host.IsEffectivelyVisible);
            vm.SelectPage(Shell.IndexOf("Lower thirds"));
            Assert.True(vm.PopOut.IsOpen);
        }
        finally
        {
            b.Dispose();
        }
    }
}
