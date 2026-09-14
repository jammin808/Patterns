using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Services;

namespace Patterns.App.Tests;

/// <summary>
/// Boots the real app headlessly against a fresh temp folder: services, view model and (by
/// default) the main window, exactly as App.axaml.cs does. One copy for every test file.
/// </summary>
public static class TestApp
{
    public sealed record Booted(AppServices Services, MainViewModel Vm, MainWindow Window, string Dir)
    {
        public void Deconstruct(out AppServices services, out MainViewModel vm, out MainWindow window)
        {
            services = Services;
            vm = Vm;
            window = Window;
        }

        /// <summary>Closes the window and shuts the services down; safe to call twice.</summary>
        public void Dispose()
        {
            try { Window.Close(); } catch { /* already closed */ }
            Services.Shutdown();
        }
    }

    /// <param name="prefix">The temp folder's name prefix.</param>
    /// <param name="prepare">Runs on the empty folder before the services read it — a marker the watchdog would have left, a settings file.</param>
    public static Booted Boot(string prefix = "patterns-tests-", Action<string>? prepare = null, Patterns.Core.Model.NodeKind profile = Patterns.Core.Model.NodeKind.Desk)
    {
        // The tests assume a desk-class machine with the headroom to warm every page: a small
        // container or a slow headless tick must not change what a test sees built.
        Views.Controls.LazyPage.MachineGB = 32;
        Views.Controls.LazyPage.PauseOverride ??= static () => false;
        QualityService.MachineGB = 32;                                          // the ladder starts at full: a small container is not a small desk
        Patterns.Core.Media.RenderFence.ResetForTests();                        // the sinks of the last test's windows are gone with them

        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        prepare?.Invoke(dir);
        var services = new AppServices(new SettingsStore(dir), profile: profile);
        AppServices.Instance = services;
        var vm = new MainViewModel(services);
        // The housekeeping lane whole every tick: a test reads the Install page or the machine's
        // lines after one poll, whatever the machine running the tests is doing. The lanes' own
        // test sets the desk's real budget back.
        vm.PollLanes.BudgetMs = 10_000;
        var window = new MainWindow { DataContext = vm };
        services.AttachMainWindow(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Booted(services, vm, window, dir);
    }

    /// <summary>Waits for the desk's file lane — the autosaves and the recovery record's writes on their worker — so a test can read the files the desk just asked for.</summary>
    public static void FlushFiles(AppServices services) => Pump(services.PendingSaves.ContinueWith(_ => true));

    /// <summary>Runs the dispatcher until a task completes (remote commands hop to the UI thread).</summary>
    public static T Pump<T>(Task<T> task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        return task.Result;
    }
}
