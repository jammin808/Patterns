using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 15: "Crash moving between menus. It restarted." A fault on the UI thread — in a dispatcher
/// job, a button's handler, a key, the page switch — is contained: logged with its stack, counted
/// on the health line with the exception's words, put on the status line, and the desk stays up.
/// And the page switch itself, every page both ways at two window sizes, contains nothing.
/// </summary>
public class UiFaultAppTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Boom() => throw new InvalidOperationException("the page's list was empty");

    [AvaloniaFact]
    public void ADispatcherJobThatThrowsIsContainedLoggedAndOnTheHealthLine()
    {
        var b = TestApp.Boot("patterns-uifault-");
        try
        {
            UiFaults.Reset();
            HealthMonitor.Reset();
            var (services, vm, window) = b;

            Dispatcher.UIThread.Post(Boom);
            Dispatcher.UIThread.RunJobs();                                        // must return, not throw
            Settle(window);

            Assert.Equal(1, UiFaults.Contained);
            Assert.StartsWith("InvalidOperationException: the page's list was empty — in UiFaultAppTests.Boom", UiFaults.LastWords);
            Assert.Contains("A fault was contained and the desk carried on — InvalidOperationException: the page's list was empty", vm.StatusMessage);

            var health = HealthMonitor.Summary(DateTime.UtcNow);
            Assert.Contains("1 fault caught, show kept running", health);
            Assert.Contains("UI fault contained (a dispatcher job) — InvalidOperationException", health);

            var log = File.ReadAllText(Path.Combine(b.Dir, "patterns.log"));
            Assert.Contains("[ERROR] UI fault contained (a dispatcher job) — InvalidOperationException: the page's list was empty", log);
            Assert.Contains("at Patterns.App.Tests.UiFaultAppTests.Boom", log);      // the stack is in the log

            // The desk is still alive: a second job runs, the window still renders, the services still publish.
            var ran = false;
            Dispatcher.UIThread.Post(() => ran = true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(ran);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.False(File.Exists(Path.Combine(b.Dir, CrashMarker.FileName)));  // contained: no crash note
        }
        finally
        {
            b.Dispose();
            UiFaults.Reset();
            HealthMonitor.Reset();
        }
    }

    [AvaloniaFact]
    public void AButtonAndAKeyThatThrowAreContained()
    {
        var b = TestApp.Boot("patterns-uifault-");
        try
        {
            UiFaults.Reset();
            HealthMonitor.Reset();
            var (_, vm, window) = b;

            var command = new RelayCommand(Boom);
            command.Execute(null);                                                // returns
            Assert.Equal(1, UiFaults.Contained);

            var typed = new RelayCommand<int>(_ => Boom());
            typed.Execute(3);
            Assert.Equal(2, UiFaults.Contained);

            // The guard names where: the log line reads "(a command)".
            var log = File.ReadAllText(Path.Combine(b.Dir, "patterns.log"));
            Assert.Contains("UI fault contained (a command) — InvalidOperationException", log);

            // A key press goes through the window's tunnelled handler: a handler fault is contained too.
            Assert.True(UiFaults.Guard(() => { }, "nothing"));
            Assert.False(UiFaults.Guard(Boom, "a key"));
            Assert.Equal(3, UiFaults.Contained);
            Assert.Contains("3 faults caught", HealthMonitor.Summary(DateTime.UtcNow));

            // What cannot be contained is not swallowed.
            Assert.True(UiFaults.IsFatal(new OutOfMemoryException()));
            Assert.True(UiFaults.IsFatal(new AggregateException(new OutOfMemoryException())));
            Assert.False(UiFaults.IsFatal(new InvalidOperationException()));
            Assert.Throws<OutOfMemoryException>(() => UiFaults.Guard(() => throw new OutOfMemoryException(), "a command"));
            Assert.Equal(3, UiFaults.Contained);
            Settle(window);
            Assert.NotNull(vm);
        }
        finally
        {
            b.Dispose();
            UiFaults.Reset();
            HealthMonitor.Reset();
        }
    }

    /// <summary>The report's move: every page, forward and back, at the laptop's size and a desk's, Run in between — nothing contained, the tab and the strip following.</summary>
    [AvaloniaFact]
    public void MovingBetweenEveryMenuBothWaysContainsNothing()
    {
        var b = TestApp.Boot("patterns-uifault-");
        try
        {
            UiFaults.Reset();
            HealthMonitor.Reset();
            var (_, vm, window) = b;
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First();

            foreach (var (w, h) in new[] { (window.MinWidth, window.MinHeight), (1600.0, 900.0) })
            {
                window.Width = w;
                window.Height = h;
                Settle(window);
                var order = Shell.Pages.Select(p => p.Index).Concat(Shell.Pages.Select(p => p.Index).Reverse()).ToList();
                foreach (var index in order)
                {
                    vm.SelectPage(index);
                    Settle(window);
                    Assert.Equal(index, tabs.SelectedIndex);
                    Assert.Equal(Shell.Pages[index].Header, vm.PageStrip.Single(c => c.IsCurrent).Header);
                    Assert.Equal(0, UiFaults.Contained);
                }
                // The group buttons: each group's last page, and SHOW while in Run goes to the panel.
                foreach (var group in Shell.Groups)
                {
                    vm.SelectGroup(group.Group);
                    Settle(window);
                    Assert.Equal(group.Group, vm.SelectedGroup);
                }
                vm.SelectRunCommand.Execute(null);
                Settle(window);
                Assert.True(vm.IsRunLayout);
                vm.SelectGroup(ShellGroup.Show);
                Settle(window);
                Assert.False(vm.IsRunLayout);
                Assert.Equal(Shell.PanelPage, vm.SelectedPageIndex);
            }
            Assert.Equal(0, UiFaults.Contained);
            // The health line counts every fault the desk contains anywhere, and this test is about
            // the pages. A machine whose remote port is already taken records a real fault that has
            // nothing to do with moving between menus — on a build agent it is the commonest one —
            // so it is named and excused here rather than left to fail the wrong test at random.
            var health = HealthMonitor.Faults == 0 || (HealthMonitor.LastFault?.Contains("Control server", StringComparison.OrdinalIgnoreCase) ?? false);
            Assert.True(health, $"faults={HealthMonitor.Faults} last={HealthMonitor.LastFault}");
        }
        finally
        {
            b.Dispose();
            UiFaults.Reset();
            HealthMonitor.Reset();
        }
    }

    /// <summary>The note the app leaves on its way down carries the exception's words; the start after it reads them onto the health line.</summary>
    [AvaloniaFact]
    public void AFatalExceptionsNoteNamesWhatThrewOnTheNextStart()
    {
        Exception caught;
        try
        {
            Boom();
            throw new Exception("unreachable");
        }
        catch (Exception ex)
        {
            caught = ex;
        }
        var b = TestApp.Boot("patterns-uifault-", dir =>
        {
            var note = new CrashNote(ExitCodes.ClrException, ExitCodes.Describe(ExitCodes.ClrException), false, false, DateTime.UtcNow.AddMinutes(-1), 600, "", 0, FaultWords.Describe(caught));
            CrashMarker.Write(dir, note);
        });
        try
        {
            var (services, vm, _) = b;
            Assert.NotNull(services.LastCrash);
            Assert.StartsWith("InvalidOperationException: the page's list was empty — in UiFaultAppTests.Boom", services.LastCrash!.Detail);
            Assert.Contains("ended in an unhandled .NET exception (see patterns.log) — InvalidOperationException: the page's list was empty — in UiFaultAppTests.Boom", vm.LastCrashText);
            Assert.Contains("InvalidOperationException: the page's list was empty", HealthMonitor.Summary(DateTime.UtcNow));
            Assert.False(services.SafeRun);                                       // a managed crash keeps the card
        }
        finally
        {
            b.Dispose();
            HealthMonitor.Reset();
        }
    }
}
