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

        /// <summary>The test that booted this desk (file.member): the census line's name, and the late close's judge.</summary>
        public string Booter { get; init; } = "?";

        /// <summary>Set when the next test's boot closed this one because its own test forgot to.</summary>
        internal bool LateClosed;

        /// <summary>Closes the window, shuts the services down and reclaims the boot's memory; safe to call twice.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (Open) Open.Remove(this);
            try { Window.Close(); } catch { /* already closed */ }
            Services.Shutdown();
            ReclaimBoot(Booter, LateClosed);
        }
    }

    /// <summary>
    /// The boots not yet closed, oldest first. The tests run one at a time, so a boot whose test
    /// is not the one booting now belongs to a test that has finished and forgot to close it:
    /// the next boot closes it (<see cref="CloseLeftovers"/>) and its census line says
    /// <c>late_close</c>, so a leaked desk in the census is the product's, never a test's, and
    /// the forgetful test can be found and mended. The boots of the test's own file are kept — a
    /// restart test holds two desks at once on purpose.
    /// </summary>
    private static readonly List<Booted> Open = new();

    /// <summary>Boots still open (every test's), for the lifecycle test's own bookkeeping.</summary>
    public static int OpenCount { get { lock (Open) return Open.Count; } }

    private static void CloseLeftovers(string booter)
    {
        // The judge is the test file: a class that boots in its constructor and again in a test
        // holds both on purpose, and a helper beside its tests boots for them.
        var file = FileOf(booter);
        Booted[] stale;
        lock (Open) stale = Open.Where(b => FileOf(b.Booter) != file).ToArray();
        foreach (var b in stale)
        {
            b.LateClosed = true;
            b.Dispose();
        }
    }

    private static string FileOf(string booter) => booter.Split('.')[0];

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
    /// zero. Round 64 adds the census: <c>leaked_desks</c> is the desks alive beyond the one being
    /// closed (this record still holds it while the line is written), <c>timers</c> the dispatcher
    /// timers still running, <c>seats</c> the render seats still taken — every one nought after a
    /// clean close. A line marked <c>late_close</c> is a desk its own test forgot to close, closed
    /// by the next test's boot (<see cref="CloseLeftovers"/>): the name on that line is the test
    /// to mend. The collection itself is not the fix: the runtime's collector is tuned to the
    /// machine's memory rather than to what one test just dropped, so on a big box the host is
    /// let grow for minutes before it collects, and the forced one keeps a heavy host in bounds
    /// and makes the figure a true reading.
    /// </summary>
    /// <param name="booter">The test whose boot was closed (file.member), for the line.</param>
    /// <param name="late">Whether the next test's boot closed it, the test having forgotten to.</param>
    public static void ReclaimBoot(string? booter = null, bool late = false)
    {
        DrainDispatcher();
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
        var census = Services.DeskCensus.Take();
        var owners = census["timers"] > 0 ? " timers=[" + string.Join(", ", Services.DeskTimers.RunningOwners.Take(6)) + "]" : "";
        var line = $"{Clock.Elapsed.TotalSeconds:F0}s boot={n} test={booter ?? t_lastBooter ?? "?"}{(late ? " late_close" : "")} managed_mb={GC.GetTotalMemory(false) / 1_048_576} ws_mb={Environment.WorkingSet / 1_048_576} leaked_desks={Math.Max(0, census["desks"] - 1)} timers={census["timers"]}{owners} seats={census["pipelines"]} budgets={budgets.Count} [{(sinks.Length > 240 ? sinks[..240] + "…" : sinks)}]";
        try { File.AppendAllText(log, line + Environment.NewLine); } catch { /* a diagnostic never fails a test */ }
    }

    /// <summary>
    /// Runs the dispatcher's queue a few times with a breath between: a closed desk's async loops
    /// (the beacon's receive, a socket's completion) post their last continuation to the UI thread
    /// as their sockets close, and until it runs the queued operation holds the whole desk — a
    /// test that counts closed desks would count that one (round 64's census found it).
    /// </summary>
    public static void DrainDispatcher()
    {
        for (var i = 0; i < 12; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    /// <param name="prefix">The temp folder's name prefix.</param>
    /// <param name="prepare">Runs on the empty folder before the services read it — a marker the watchdog would have left, a settings file.</param>
    /// <summary>The test that asked for the boot being closed, for the memlog's line (the caller of <see cref="Boot"/>).</summary>
    [ThreadStatic] private static string? t_lastBooter;

    public static Booted Boot(string prefix = "patterns-tests-", Action<string>? prepare = null, Patterns.Core.Model.NodeKind profile = Patterns.Core.Model.NodeKind.Desk,
                              [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "", [System.Runtime.CompilerServices.CallerMemberName] string callerMember = "")
    {
        var booter = $"{Path.GetFileNameWithoutExtension(callerFile)}.{callerMember}";
        t_lastBooter = booter;
        CloseLeftovers(booter);                                                    // another test's forgotten desk goes before this one is built
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
        var booted = new Booted(services, vm, window, dir) { Booter = booter };
        lock (Open) Open.Add(booted);
        return booted;
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
