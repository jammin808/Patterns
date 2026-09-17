using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.App.Views.Panels;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 73: "Make the collapsible column to the right of info and programming draggable. Tidy up
/// long areas like Admin (or the others) to make good use of the collapsible column."
///
/// The pop-out settings column's handle drags its width, the show remembers it (clamped, absent
/// in an older file), and the page keeps its own width whatever the column is set to; the Machine
/// and Audio pages keep their settings groups in the column, so the pages are the health lines and
/// the lists — with the same close-and-reopen manners as a selection's column, and NAV SETTINGS
/// from the wire opening and closing it.
/// </summary>
public class PopOutWidthTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Drag(Thumb handle, double dx) => handle.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent, Vector = new Vector(dx, 0) });

    [AvaloniaFact]
    public void TheColumnsHandleDragsItsWidthAndTheShowRemembersIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            window.Width = 1600;
            window.Height = 900;
            Settle(window);
            var host = window.GetVisualDescendants().OfType<PopOutHost>().Single();
            var pageWidth = vm.State.Desk.EditorWidth;

            // Open on a cue: the default width, and the page column grown by exactly it.
            vm.SelectPage(Shell.IndexOf("Cues"));
            vm.Cues.AddCueCommand.Execute(null);
            Settle(window);
            Assert.True(vm.PopOut.IsOpen);
            Assert.Equal(DeskLayoutConfig.DefaultPopOutWidth, host.Width);
            Assert.Equal(PopOutHost.ColumnWidth, DeskLayoutConfig.DefaultPopOutWidth);
            Assert.Equal(pageWidth + DeskLayoutConfig.DefaultPopOutWidth, window.EditorColumnWidth);

            // The show's number moves the column; the page's own width never moves with it.
            vm.State.Desk.PopOutWidth = 520;
            Settle(window);
            Assert.Equal(520, host.Width);
            Assert.Equal(pageWidth + 520, window.EditorColumnWidth);
            Assert.Equal(pageWidth, window.PageColumnWidth);
            Assert.Equal(pageWidth, vm.State.Desk.EditorWidth);

            // The handle: dragging left widens, dragging right narrows, within the bounds — and the show has the number at once.
            var handle = host.GetVisualDescendants().OfType<Thumb>().Single(t => t.Classes.Contains("popOutHandle"));
            Drag(handle, -60);
            Settle(window);
            Assert.Equal(580, vm.State.Desk.PopOutWidth);
            Assert.Equal(580, host.Width);
            Assert.Equal(pageWidth + 580, window.EditorColumnWidth);
            Drag(handle, 1000);
            Settle(window);
            Assert.Equal(DeskLayoutConfig.MinPopOutWidth, vm.State.Desk.PopOutWidth);
            Drag(handle, -1000);
            Settle(window);
            Assert.Equal(DeskLayoutConfig.MaxPopOutWidth, vm.State.Desk.PopOutWidth);
            Assert.Equal(DeskLayoutConfig.MaxPopOutWidth, host.Width);

            // The number is clamped and never NaN, and it travels with the show; an older file opens at the default.
            vm.State.Desk.PopOutWidth = 5000;
            Assert.Equal(DeskLayoutConfig.MaxPopOutWidth, vm.State.Desk.PopOutWidth);
            vm.State.Desk.PopOutWidth = double.NaN;
            Assert.Equal(DeskLayoutConfig.DefaultPopOutWidth, vm.State.Desk.PopOutWidth);
            vm.State.Desk.PopOutWidth = 610;
            var back = JsonUtil.Deserialize<ShowState>(JsonUtil.Serialize(vm.State))!;
            Assert.Equal(610, back.Desk.PopOutWidth);
            Assert.Equal(DeskLayoutConfig.DefaultPopOutWidth, JsonUtil.Deserialize<ShowState>("{}")!.Desk.PopOutWidth);

            // Closed, the page column is the page's own width again.
            vm.ClosePopOutCommand.Execute(null);
            Settle(window);
            Assert.False(vm.PopOut.IsOpen);
            Assert.Equal(pageWidth, window.EditorColumnWidth);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheMachineAndAudioPagesKeepTheirSettingsInTheColumn()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            window.Width = 1600;
            window.Height = 900;
            Settle(window);
            var host = window.GetVisualDescendants().OfType<PopOutHost>().Single();

            // The Machine page: the column opens on the machine's settings; the page keeps the health and performance lines.
            vm.SelectPage(Shell.IndexOf("Machine"));
            Settle(window);
            Assert.True(vm.PopOut.IsOpen);
            Assert.Equal("machine", vm.PopOut.Key);
            Assert.StartsWith("MACHINE SETTINGS", vm.PopOut.Title, StringComparison.Ordinal);
            Assert.Equal("hue-machine", vm.PopOut.Hue);
            Assert.IsType<MachineSettingsPanel>(host.Panel);
            var page = window.GetVisualDescendants().OfType<AdminSection>().First();
            foreach (var band in new[] { "SHOW LOCK (NOTHING INTERRUPTS THE SHOW)", "RIG DAY GAMES (OPT-IN)", "WATCHDOG", "BEACON (A SECOND MACHINE)", "TWIN (A SECOND PATTERNS, IN STEP)", "EARLIER VERSIONS OF THE SHOW" })
            {
                Assert.Contains(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == band);
                Assert.DoesNotContain(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == band);
            }
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "STABILITY (WATCHDOG)");
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.RuntimeText);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "HEALTH AT A GLANCE");
            Assert.Contains(host.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "RESTART APP (SHOW COMES BACK)");
            Assert.Contains(page.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "SETTINGS ▸" && !x.IsVisible);

            // ◀ CLOSE: closed for the page until SETTINGS ▸ — another page and back does not reopen it.
            vm.ClosePopOutCommand.Execute(null);
            Settle(window);
            Assert.False(vm.PopOut.IsOpen);
            Assert.Contains(page.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "SETTINGS ▸" && x.IsEffectivelyVisible);
            vm.SelectPage(Shell.IndexOf("Help"));
            vm.SelectPage(Shell.IndexOf("Machine"));
            Settle(window);
            Assert.False(vm.PopOut.IsOpen);
            vm.OpenPopOutCommand.Execute(null);
            Settle(window);
            Assert.True(vm.PopOut.IsOpen);
            Assert.Equal("machine", vm.PopOut.Key);

            // The Audio page: the clock, the sync check and the tone generator in the column; the lists and the routing on the page.
            vm.SelectPage(Shell.IndexOf("Audio"));
            Settle(window);
            Assert.True(vm.PopOut.IsOpen);
            Assert.Equal("audio", vm.PopOut.Key);
            Assert.Equal("hue-audio", vm.PopOut.Hue);
            Assert.IsType<AudioSettingsPanel>(host.Panel);
            var audio = window.GetVisualDescendants().OfType<AudioSection>().First();
            Assert.Contains(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "TONE GENERATOR");
            Assert.Contains(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "MASTER CLOCK & SYNC");
            Assert.DoesNotContain(audio.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "TONE GENERATOR");
            Assert.Contains(audio.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "BREAK MUSIC (SPOTIFY)");

            // From the wire (round 74): the column closes and opens for the page too.
            var router = new CommandRouter(services);
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("NAV SETTINGS OFF"))), StringComparison.Ordinal);
            Assert.False(vm.PopOut.IsOpen);
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("NAV SETTINGS ON"))), StringComparison.Ordinal);
            Assert.True(vm.PopOut.IsOpen);
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("NAV Machine"))), StringComparison.Ordinal);
            Assert.Equal("machine", vm.PopOut.Key);
        }
        finally
        {
            b.Dispose();
        }
    }
}
