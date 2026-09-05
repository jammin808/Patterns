using Avalonia.Headless;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The shell: five groups on the rail, a page strip over the layout, PREP · SHOW · RUN in the
/// header, and the SHOW CONTROLS drawer. The page table and the window's TabControl are pinned
/// together, so a page can never sit in the wrong group or vanish from the strip.
/// </summary>
public class ShellTests
{
    private static TabControl Tabs(Avalonia.Controls.Window window) => window.GetVisualDescendants().OfType<TabControl>().First();

    private static string? Header(TabItem tab) => tab.Header switch
    {
        string s => s,
        StackPanel p => p.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
        _ => null,
    };

    [AvaloniaFact]
    public void ThePageTableMatchesTheWindowsTabsAndEveryGroupHasPages()
    {
        var b = TestApp.Boot();
        try
        {
            var headers = Tabs(b.Window).Items.OfType<TabItem>().Select(t => Header(t) ?? "").ToList();
            Assert.Equal(Shell.Pages.Select(p => p.Header).ToList(), headers);
            Assert.All(Shell.Pages.Select((p, i) => (p, i)), x => Assert.Equal(x.i, x.p.Index));
            Assert.Equal("Panel", Shell.Pages[Shell.PanelPage].Header);
            Assert.Equal("Run", Shell.Pages[Shell.RunPage].Header);
            foreach (var group in Enum.GetValues<ShellGroup>())
            {
                Assert.Contains(Shell.Pages, p => p.Group == group);
                Assert.Contains(Shell.Groups, g => g.Group == group);
            }
            Assert.Equal(new[] { "Cues", "Looks", "Install" }, Shell.Pages.Where(p => p.Group == ShellGroup.Plan).Select(p => p.Header));
            Assert.Equal(new[] { "Screens", "Audio", "NDI", "Stream", "Remote", "Interactive" }, Shell.Pages.Where(p => p.Group == ShellGroup.Setup).Select(p => p.Header));
            Assert.Equal(new[] { "Machine", "Help" }, Shell.Pages.Where(p => p.Group == ShellGroup.Admin).Select(p => p.Header));

            // The app opens on the show panel, in the Build layout, in SHOW mode.
            Assert.Equal(Shell.PanelPage, b.Vm.SelectedPageIndex);
            Assert.Equal(Shell.PanelPage, Tabs(b.Window).SelectedIndex);
            Assert.Equal(ShellGroup.Show, b.Vm.SelectedGroup);
            Assert.False(b.Vm.IsRunLayout);
            Assert.True(b.Vm.IsShowSelected);
            Assert.False(b.Vm.IsPrepSelected);
            Assert.False(b.Vm.IsRunSelected);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void GroupsRememberTheirPageAndTheStripFollows()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var tabs = Tabs(b.Window);

            vm.SelectGroup(ShellGroup.Build);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ShellGroup.Build, vm.SelectedGroup);
            Assert.Equal(new[] { "Pattern", "Media", "Overlays", "Lower thirds", "Countdown", "Particles", "Branding", "Library" }, vm.PageStrip.Select(p => p.Header));
            Assert.Equal("Pattern", vm.PageStrip.Single(p => p.IsCurrent).Header);
            Assert.Equal(Shell.IndexOf("Pattern"), tabs.SelectedIndex);
            Assert.Equal("BUILD", vm.GroupStrip.Single(g => g.IsCurrent).Label);

            vm.SelectPageCommand.Execute(Shell.IndexOf("Media"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Shell.IndexOf("Media"), tabs.SelectedIndex);
            Assert.Equal("Media", vm.PageStrip.Single(p => p.IsCurrent).Header);

            vm.SelectGroupCommand.Execute(ShellGroup.Setup);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Screens", vm.PageStrip.Single(p => p.IsCurrent).Header);
            Assert.Equal(Shell.IndexOf("Screens"), tabs.SelectedIndex);

            vm.SelectGroup(ShellGroup.Build);
            Assert.Equal("Media", vm.PageStrip.Single(p => p.IsCurrent).Header); // remembered

            // A test (or code) picking a tab by header lands in the right group.
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Header(t) == "Branding");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ShellGroup.Build, vm.SelectedGroup);
            Assert.Equal("Branding", vm.PageStrip.Single(p => p.IsCurrent).Header);
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Header(t) == "Machine");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ShellGroup.Admin, vm.SelectedGroup);
            Assert.Equal(new[] { "Machine", "Help" }, vm.PageStrip.Select(p => p.Header));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void RunIsThePageThatTakesTheWindowAndArmedRefusesToLeaveIt()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var tabs = Tabs(b.Window);
            vm.SelectGroup(ShellGroup.Build);

            vm.SelectRunCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsRunLayout);
            Assert.True(vm.IsRunSelected);
            Assert.False(vm.IsShowSelected);
            Assert.Equal(ShellGroup.Show, vm.SelectedGroup);
            Assert.Equal(new[] { "Panel", "Run" }, vm.PageStrip.Select(p => p.Header));
            Assert.Equal("Run", vm.PageStrip.Single(p => p.IsCurrent).Header);
            Assert.Equal(Shell.RunPage, tabs.SelectedIndex);

            // SHOW pressed again while in Run goes to the panel; the Build page is remembered on the way back.
            vm.SelectGroup(ShellGroup.Show);
            Assert.False(vm.IsRunLayout);
            Assert.Equal(Shell.PanelPage, vm.SelectedPageIndex);
            vm.IsRunLayout = true;
            Assert.Equal(Shell.RunPage, vm.SelectedPageIndex);
            vm.IsRunLayout = false;
            Assert.Equal(Shell.PanelPage, vm.SelectedPageIndex);
            vm.SelectGroup(ShellGroup.Build);
            vm.IsRunLayout = true;
            vm.IsRunLayout = false;
            Assert.Equal(Shell.IndexOf("Pattern"), vm.SelectedPageIndex);

            // Armed: every way out is refused and the strip snaps back — a stray click cannot
            // take the surface away mid-show.
            vm.SelectRunCommand.Execute(null);
            b.Services.CueStack.SetArmed(true, ActionOrigin.Desk);
            vm.SelectPage(Shell.IndexOf("Pattern"));
            Assert.True(vm.IsRunLayout);
            Assert.Contains("Disarm", vm.StatusMessage);
            Assert.Equal(Shell.RunPage, vm.SelectedPageIndex);
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Header(t) == "Cues");
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsRunLayout);
            Assert.Equal(Shell.RunPage, tabs.SelectedIndex);
            vm.SelectPrepCommand.Execute(null);
            Assert.True(vm.IsRunLayout);
            Assert.False(vm.IsPrepMode);
            vm.SelectGroupCommand.Execute(ShellGroup.Setup);
            Assert.True(vm.IsRunLayout);
            Assert.Equal(ShellGroup.Show, vm.SelectedGroup);

            b.Services.CueStack.SetArmed(false, ActionOrigin.Desk);
            vm.SelectPrepCommand.Execute(null);
            Assert.False(vm.IsRunLayout);
            Assert.True(vm.IsPrepMode);
            Assert.True(vm.IsPrepSelected);
            Assert.False(vm.IsShowSelected);
            vm.SelectShowCommand.Execute(null);
            Assert.False(vm.IsPrepMode);
            Assert.True(vm.IsShowSelected);

            // RUN from PREP is allowed (a rehearsal at the desk): the mode stays, the strip says so.
            vm.SelectPrepCommand.Execute(null);
            vm.SelectRunCommand.Execute(null);
            Assert.True(vm.IsRunLayout);
            Assert.True(vm.IsPrepMode);
            Assert.False(vm.IsPrepSelected);
            Assert.True(vm.IsRunSelected);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>Every page of every group renders in the window, and the strip agrees with the table.</summary>
    [AvaloniaFact]
    public void EveryPageRendersAndTheStripNamesIt()
    {
        var b = TestApp.Boot();
        try
        {
            var tabs = Tabs(b.Window);
            foreach (var page in Shell.Pages)
            {
                b.Vm.SelectPage(page.Index);
                Settle(b.Window);
                Assert.Equal(page.Index, tabs.SelectedIndex);
                Assert.Equal(page.Group, b.Vm.SelectedGroup);
                Assert.Equal(page.Header, b.Vm.PageStrip.Single(c => c.IsCurrent).Header);
                Assert.Equal(page.Index == Shell.RunPage, b.Vm.IsRunLayout);
                using var frame = b.Window.CaptureRenderedFrame();
                Assert.NotNull(frame);
            }
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>The laptop requirement: at the window's minimum size nothing the operator needs is off screen.</summary>
    [AvaloniaFact]
    public void TheShellFitsTheMinimumWindowInBothLayouts()
    {
        var b = TestApp.Boot();
        try
        {
            var window = b.Window;
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Settle(window);
            Assert.True(window.Bounds.Width <= window.MinWidth + 1, $"window is {window.Bounds.Width} wide");

            // Build layout: the transport (BLACKOUT is the rightmost), the rail's last group and the wall's TAKE.
            AssertInside(window, window.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Classes.Contains("blackout")), "BLACKOUT");
            AssertInside(window, window.GetVisualDescendants().OfType<Button>().Last(x => x.Classes.Contains("navGroup")), "ADMIN group");
            AssertInside(window, window.GetVisualDescendants().OfType<Button>().First(x => x.Classes.Contains("pageChip")), "first page chip");
            AssertInside(window, window.GetVisualDescendants().OfType<Button>().First(x => x.Content as string == "TAKE"), "TAKE");
            AssertInside(window, window.GetVisualDescendants().OfType<Button>().First(x => x.Content as string == "Load show…"), "Load show");

            // Run layout: GO, STOP ALL and the standby arrows sit in the transport row at the bottom.
            b.Vm.SelectRunCommand.Execute(null);
            Settle(window);
            AssertInside(window, window.GetVisualDescendants().OfType<Button>().First(x => x.Classes.Contains("runGo")), "GO");
            AssertInside(window, window.GetVisualDescendants().OfType<Button>().First(x => x.Classes.Contains("runStop")), "STOP ALL");
            AssertInside(window, window.GetVisualDescendants().OfType<Button>().Last(x => x.Classes.Contains("navGroup")), "ADMIN group (Run)");
        }
        finally
        {
            b.Dispose();
        }
    }

    private static void Settle(Avalonia.Controls.Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void AssertInside(Avalonia.Controls.Window window, Control control, string what)
    {
        Assert.True(control.IsEffectivelyVisible, $"{what} is not visible");
        var origin = control.TranslatePoint(new Avalonia.Point(0, 0), window);
        Assert.True(origin.HasValue, $"{what} is not in the window's tree");
        var right = origin!.Value.X + control.Bounds.Width;
        var bottom = origin.Value.Y + control.Bounds.Height;
        Assert.True(origin.Value.X >= 0 && origin.Value.Y >= 0 && right <= window.Bounds.Width + 0.5 && bottom <= window.Bounds.Height + 0.5,
            $"{what} runs off the window: {origin.Value.X:0},{origin.Value.Y:0} – {right:0},{bottom:0} in {window.Bounds.Width:0}×{window.Bounds.Height:0}");
    }

    [AvaloniaFact]
    public void ShowControlsSendThroughTheAirSeamAndAreJournaled()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            vm.ActivePattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            services.StartDefaultSandbox();
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsSandboxActive);

            var drawer = vm.ShowControls;
            drawer.IsOpen = true;
            Assert.Equal("off", drawer.MessageAirText);

            drawer.DraftMessage = "Doors open 19:00";
            drawer.MessageShowCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var air = services.Bus.Current.State;
            Assert.True(air.Overlays.Message.Enabled);
            Assert.Equal("Doors open 19:00", air.Overlays.Message.Text);
            Assert.False(vm.State.Overlays.Message.Enabled); // the sandbox is untouched
            Assert.True(drawer.MessageOnAir);
            Assert.Contains("Doors open", drawer.MessageAirText);
            Assert.Contains(services.Journal.Tail(3), e => e.Kind == "MessageOn" && e.Origin == "desk");

            drawer.MessageHideCommand.Execute(null);
            Assert.False(services.Bus.Current.State.Overlays.Message.Enabled);
            Assert.False(drawer.MessageOnAir);

            drawer.ClockShowCommand.Execute(null);
            Assert.True(services.Bus.Current.State.Overlays.Clock.Enabled);
            Assert.True(drawer.ClockOnAir);
            drawer.ClockHideCommand.Execute(null);
            Assert.False(services.Bus.Current.State.Overlays.Clock.Enabled);

            drawer.DraftMinutesText = "5";
            drawer.CountdownStartCommand.Execute(null);
            var countdown = services.Bus.Current.State.Countdown;
            Assert.True(countdown.Enabled);
            Assert.Equal(CountdownTargetKind.Duration, countdown.TargetKind);
            Assert.Equal(5, countdown.DurationMinutes);
            Assert.Contains("5 min", drawer.CountdownAirText);
            drawer.DraftMinutesText = "soon";
            drawer.CountdownStartCommand.Execute(null);
            Assert.Contains("minutes", vm.StatusMessage); // refused, nothing changed
            Assert.True(services.Bus.Current.State.Countdown.Enabled);
            drawer.CountdownStopCommand.Execute(null);
            Assert.False(services.Bus.Current.State.Countdown.Enabled);

            drawer.DraftVolume = 40;
            Assert.Equal("40%", drawer.DraftVolumeText);
            drawer.VolumeSendCommand.Execute(null);
            Assert.Equal(40, services.State.AudioPlayer.VolumePct);
            Assert.Equal("40%", drawer.VolumeAirText);
            Assert.Contains(services.Journal.Tail(2), e => e.Kind == "AudioVolume" && e.Origin == "desk");
            drawer.DraftVolume = 400;
            Assert.Equal(125, drawer.DraftVolume); // the player's ceiling

            // A cue can do what the drawer does.
            var result = services.Actions.Execute(new ShowAction(ShowActionKind.AudioVolume, "", "loud"), ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Refused, result.Status);
            Assert.Equal(40, services.State.AudioPlayer.VolumePct);
        }
        finally
        {
            b.Dispose();
        }
    }
}
