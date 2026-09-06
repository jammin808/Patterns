using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The Patterns badge on the desk: the Branding page's block, the drag on the PREVIEW pane, OVERLAYS OFF, the Help.</summary>
public class BadgeAppTests
{
    [AvaloniaFact]
    public void TheBrandingPageCarriesTheBadgeTheDragMovesItAndOverlaysOffTakesItWithTheRest()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            vm.SelectPage(Shell.IndexOf("Branding"));
            Settle();
            var page = window.GetVisualDescendants().OfType<BrandingSection>().Single();
            var on = page.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "BadgeOn");
            Assert.True(on.IsChecked);                         // on by default
            on.IsChecked = false;
            Settle();
            Assert.False(vm.State.Overlays.Badge.Enabled);
            on.IsChecked = true;
            Settle();
            Assert.True(vm.State.Overlays.Badge.Enabled);
            Assert.Contains(page.GetVisualDescendants().OfType<CheckBox>(), c => c.Content as string == "On media too (video, images, decks, web pages)");
            Assert.Contains(page.GetVisualDescendants().OfType<TextBox>(), t => t.Text == BadgeOverlay.DefaultLine);

            // A drag on the PREVIEW pane reads and writes the badge's nudge like every other overlay.
            Assert.Equal("The Patterns badge", MainViewModel.DragName(HitKind.Badge));
            vm.DragPlace(HitKind.Badge, 5, -20);
            Assert.Equal((5.0, -20.0), vm.DragPlaceOf(HitKind.Badge));
            Assert.Equal(5, vm.State.Overlays.Badge.OffsetXPct);
            Assert.Equal(-20, vm.State.Overlays.Badge.OffsetYPct);

            // OVERLAYS OFF — a clean picture — takes the badge with the rest, and says so.
            vm.IsSandboxActive = false;
            var result = services.Actions.Execute(ShowActionKind.OverlaysOff, ActionOrigin.Desk);
            Assert.True(result.Ok);
            Assert.Contains("Patterns badge", result.Message);
            Assert.False(vm.State.Overlays.Badge.Enabled);
            Assert.False(services.Bus.Current.State.Overlays.Badge.Enabled);

            // The Help knows it.
            Assert.Contains(HelpTopics.All, t => t.Id == "badge");
            Assert.Contains("lower third", HelpBodies.Badge);
        }
        finally
        {
            b.Dispose();
        }
    }

    private static void Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
