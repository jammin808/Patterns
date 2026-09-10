using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.App.Views.Controls;
using Patterns.App.Views.Sections;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 18: a faster start and restart — the window builds the page on the rail and the rest in
/// idle time, the settings read once before the desk are handed to it, the show comes back after
/// a watchdog restart as soon as the window opens, and the way out runs once.
/// </summary>
public class StartupTests
{
    [AvaloniaFact]
    public void ThePagesAreBuiltWhenShownAndTheRestInIdleTime()
    {
        LazyPage.AutoWarmUp = false;
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            var pages = LazyPage.In(window);
            Assert.Equal(Shell.Pages.Count - 1, pages.Count);          // every page but Run, which is inline
            Assert.All(pages, p => Assert.Contains(p.Page, LazyPage.Known));
            Assert.Equal(Shell.Pages.Where(p => p.Header != "Run").Select(p => p.Header), pages.Select(p => p.Page));

            // Only the page on the rail is built after the start.
            Assert.Equal(1, LazyPage.BuiltIn(window));
            Assert.True(pages.Single(p => p.Page == "Panel").IsBuilt);
            Assert.Single(window.GetVisualDescendants().OfType<ShowSection>());

            // A page the rail shows is built the moment it shows, and a click during the first seconds after a start builds its page.
            vm.SelectPage(Shell.IndexOf("Fractals"));
            Settle(window);
            Assert.True(pages.Single(p => p.Page == "Fractals").IsBuilt);
            Assert.Equal(2, LazyPage.BuiltIn(window));
            Assert.Single(window.GetVisualDescendants().OfType<FractalsSection>());

            // The warm-up builds the rest, one per idle turn, below rendering; every page ends up built.
            LazyPage.WarmUp(window);
            for (var i = 0; i < 40 && LazyPage.BuiltIn(window) < pages.Count; i++) Dispatcher.UIThread.RunJobs();
            Assert.Equal(pages.Count, LazyPage.BuiltIn(window));
            Assert.All(pages, p => Assert.True(p.IsBuilt, p.Page));

            // A page built is a page, not a placeholder: the rail lands on real content.
            vm.SelectPage(Shell.IndexOf("Machine"));
            Settle(window);
            Assert.Single(window.GetVisualDescendants().OfType<AdminSection>());
            var lazy = new LazyPage { Page = "No such page" };
            lazy.Build();
            Assert.IsType<TextBlock>(lazy.Content);
        }
        finally
        {
            LazyPage.AutoWarmUp = true;
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheSettingsReadBeforeTheDeskAreHandedToItAndTheWayOutRunsOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-preload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SettingsStore(dir);
        var early = store.Load();
        early.Name = "Handed over";
        AppServices.Preloaded = (store, early);
        var services = new AppServices();
        try
        {
            Assert.Null(AppServices.Preloaded);                       // taken once
            Assert.Same(store, services.Store);
            Assert.Same(early, services.State);                       // the state Main read, not a second read
            Assert.Equal("Handed over", services.State.Name);
            Assert.Contains(StartupBudget.Settings, services.Startup.Phases.Select(p => p.Phase));

            var vm = new MainViewModel(services);
            var window = new MainWindow { DataContext = vm };
            services.AttachMainWindow(window);
            Assert.Contains(StartupBudget.Pages, services.Startup.Phases.Select(p => p.Phase));
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { StartupBudget.Settings, StartupBudget.Services, StartupBudget.ViewModel, StartupBudget.Pages, StartupBudget.Window },
                services.Startup.Phases.Select(p => p.Phase).Take(5));

            services.Shutdown();
            services.Shutdown();                                       // Avalonia raises ShutdownRequested and then Exit: one exit, not two
            window.Close();
        }
        finally
        {
            AppServices.Preloaded = null;
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void AfterAWatchdogRestartTheShowComesBackWhenTheWindowOpensNotAfterATimer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-recover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        new RecoveryStore(dir).Write(live: false, audioPlaying: false);   // what was running at the crash: the desk, nothing on
        var services = new AppServices(new SettingsStore(dir));
        try
        {
            services.State.Watchdog.AutoRestore = true;
            var vm = new MainViewModel(services);
            var window = new MainWindow { DataContext = vm };
            services.AttachMainWindow(window);
            services.RecoverWhenReady(vm);                             // asked before the window opened: held until it has
            Assert.DoesNotContain("Restarted", vm.StatusMessage);
            window.Show();
            Dispatcher.UIThread.RunJobs();                             // opened, the screens attached, the side effects applied, then the recovery
            Assert.Contains("Restarted", vm.StatusMessage);

            // Asked once the window is open: straight away, on the next idle turn.
            vm.StatusMessage = "";
            services.RecoverWhenReady(vm);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Restarted", vm.StatusMessage);
            window.Close();
        }
        finally
        {
            services.Shutdown();
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
