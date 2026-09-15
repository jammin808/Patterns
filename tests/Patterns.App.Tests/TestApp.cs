using System.Diagnostics;
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

        private bool _disposed;

        /// <summary>Closes the window, shuts the services down and reclaims the boot's memory; safe to call twice.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Window.Close(); } catch { /* already closed */ }
            Services.Shutdown();
            ReclaimBoot();
        }
    }

    private static int _boots;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>
    /// After a boot is closed: a full collection with its finalizers when the host has grown heavy
    /// or when a run is measuring, and one line of the measure per boot with
    /// PATTERNS_TEST_MEMLOG=&lt;file&gt; — the managed bytes left after the collection, the working set,
    /// and every frame budget still attached to the process-wide registry with its sink's name.
    /// The line is how round 62's host crash was read: the managed figure climbed ~40 MB a boot
    /// after a full collection, so something rooted every closed desk, and the budgets column
    /// named it — the RUN monitor's tile, whose viewport arrived while its surface, the Run
    /// layout, had never been laid out, so the pipeline it made was off the tree with no detach in
    /// its future (<see cref="Views.Controls.MonitorTileControl"/>). A rooted desk shows here as a managed
    /// figure that climbs and a budget that stays; <c>MonitorTileLifetimeTests</c> keeps that at
    /// zero. The collection itself is not the fix: the runtime's collector is tuned to the
    /// machine's memory rather than to what one test just dropped, so on a big box the host is
    /// let grow for minutes before it collects, and the forced one keeps a heavy host in bounds
    /// and makes the figure a true reading.
    /// </summary>
    public static void ReclaimBoot()
    {
        var log = Environment.GetEnvironmentVariable("PATTERNS_TEST_MEMLOG");
        var heavy = Environment.WorkingSet > 3L * 1024 * 1024 * 1024;
        if (string.IsNullOrEmpty(log) && !heavy) return;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        if (string.IsNullOrEmpty(log)) return;
        var n = Interlocked.Increment(ref _boots);
        var budgets = Patterns.Core.Services.FrameBudgets.Attached;
        var sinks = string.Join(", ", budgets.Select(b => $"{b.Kind}:{b.SinkIndex}:{b.Label}"));
        var line = $"{Clock.Elapsed.TotalSeconds:F0}s boot={n} managed_mb={GC.GetTotalMemory(false) / 1_048_576} ws_mb={Environment.WorkingSet / 1_048_576} budgets={budgets.Count} [{(sinks.Length > 240 ? sinks[..240] + "…" : sinks)}]";
        try { File.AppendAllText(log, line + Environment.NewLine); } catch { /* a diagnostic never fails a test */ }
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
        Patterns.Rendering.Media.RenderFence.ResetForTests();                        // the sinks of the last test's windows are gone with them

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
