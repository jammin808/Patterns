using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The explanations fold away behind ? TIPS (the readouts stay), the tick brings them back and the
/// show remembers it; the header tells the mode from the layout, and PREP holds the stream too.
/// </summary>
public class HintsAndModesTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void TheExplanationsHideByDefaultAndTheTickBringsThemBack()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            Settle(window);
            Assert.False(vm.ShowHints);
            Assert.Contains("nohints", window.Classes);

            var tips = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("tip")).ToList();
            Assert.True(tips.Count >= 8, $"{tips.Count} tips on the panel page");
            Assert.All(tips, t => Assert.False(t.IsVisible));

            // A status readout wears the plain hint class and stays on screen.
            var status = window.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Classes.Contains("hint") && !t.Classes.Contains("tip") && t.Text == vm.OutputsStatus);
            Assert.True(status.IsVisible);

            vm.ShowHints = true;
            Settle(window);
            Assert.DoesNotContain("nohints", window.Classes);
            Assert.True(vm.State.Desk.ShowHints);
            Assert.All(tips, t => Assert.True(t.IsVisible));

            // Remembered in the show; an older file shows the clean desk.
            var back = JsonUtil.Deserialize<ShowState>(JsonUtil.Serialize(vm.State))!;
            Assert.True(back.Desk.ShowHints);
            Assert.False(JsonUtil.Deserialize<ShowState>("{}")!.Desk.ShowHints);

            vm.ShowHints = false;
            Settle(window);
            Assert.All(tips, t => Assert.False(t.IsVisible));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TipsReadsTheCurrentPageUnderItsHeadings()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            vm.SelectPage(Shell.IndexOf("Screens"));
            Settle(window);

            // The Screens page selects its first screen, so the settings column is open beside the page:
            // the tips are the page's under their headings, then the column's under its title.
            Assert.True(vm.PopOut.IsOpen);
            var tips = window.CurrentPageTips();
            Assert.True(tips.Count >= 8, $"{tips.Count} tips on the Screens page");
            Assert.Equal(vm.GroupHint, tips[0].Text);                                          // the group's line first
            Assert.Equal(tips.Count, tips.Select(t => t.Text).Distinct().Count());            // nothing twice
            Assert.Contains(tips, t => t.Heading.Length > 0);                                  // under their headings
            Assert.Contains(tips, t => t.Text.StartsWith("The show's frame rate", StringComparison.Ordinal));
            Assert.Contains(tips, t => t.Text.StartsWith("For displays mounted in portrait", StringComparison.Ordinal)
                                       && t.Heading.StartsWith("SELECTED SCREEN · ", StringComparison.Ordinal));
            Assert.DoesNotContain(tips, t => t.Text.StartsWith("{", StringComparison.Ordinal)); // words, never a binding

            // Nothing selected: the column closes and its tips go with it; the page's stay, in the same order.
            vm.SelectedPlacement = null;
            Settle(window);
            Assert.False(vm.PopOut.IsOpen);
            var pageOnly = window.CurrentPageTips();
            Assert.True(pageOnly.Count < tips.Count, $"{pageOnly.Count} tips with the column closed, {tips.Count} open");
            Assert.Equal(pageOnly.Select(t => t.Text), tips.Take(pageOnly.Count).Select(t => t.Text));
            Assert.DoesNotContain(pageOnly, t => t.Text.StartsWith("For displays mounted in portrait", StringComparison.Ordinal));

            // A screen selected again brings the column and its tips back.
            vm.SelectedPlacement = vm.AddPlannedScreen(1920, 1080, "Side");
            Settle(window);
            Assert.True(vm.PopOut.IsOpen);
            Assert.Equal(tips.Count, window.CurrentPageTips().Count);

            // The button sits on the strip, opens without a fault, and leaves with the Run layout.
            var button = window.GetVisualDescendants().OfType<Button>().First(x => x.Classes.Contains("help"));
            Assert.True(button.IsEffectivelyVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(window);
            vm.SelectRunCommand.Execute(null);
            Settle(window);
            Assert.False(button.IsEffectivelyVisible);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void PrepHoldsTheStreamLikeTheOutputsAndTheHeaderTellsModeFromLayout()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            vm.State.Stream.Destinations[0].Enabled = true;
            vm.State.Stream.Destinations[0].Url = "rtmp://example/live/key";
            vm.State.Stream.Active = true;

            vm.IsPrepMode = true;
            services.Stream.Poll();
            Assert.StartsWith("PREP", services.Stream.Status);
            Assert.True(vm.State.Stream.Active);   // held, not switched off: it comes back with SHOW

            vm.IsPrepMode = false;
            services.Stream.Poll();
            Assert.DoesNotContain("PREP", services.Stream.Status);

            // MODE and LAYOUT are two things: RUN lights alone, and leaving it relights the mode.
            Assert.True(vm.IsShowSelected);
            vm.SelectRunCommand.Execute(null);
            Assert.True(vm.IsRunSelected);
            Assert.False(vm.IsShowSelected);
            Assert.False(vm.IsPrepSelected);
            vm.SelectPrepCommand.Execute(null);
            Assert.True(vm.IsPrepSelected);
            Assert.False(vm.IsRunSelected);
            Assert.True(vm.IsPrepMode);
            Settle(window);
            var captions = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("modeCaption")).Select(t => t.Text).ToList();
            Assert.Equal(new[] { "MODE", "LAYOUT" }, captions);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheTypeStepsUpAndTheTogglesWearTheSameSizeAsTheButtons()
    {
        var b = TestApp.Boot();
        try
        {
            var window = b.Window;
            Settle(window);
            Assert.Equal(15, window.FontSize);
            var wide = window.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Content as string == "◧ WIDE");
            var mini = window.GetVisualDescendants().OfType<Button>().First(x => x.Classes.Contains("mini") && x is not ToggleButton);
            Assert.Equal(mini.FontSize, wide.FontSize);
            Assert.Equal(mini.Padding, wide.Padding);
            var h1 = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("h1"));
            Assert.Equal(22, h1.FontSize);
        }
        finally
        {
            b.Dispose();
        }
    }
}
