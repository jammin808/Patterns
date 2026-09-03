using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The Admin tab end to end: metrics → advisor → UI, GPU choice, restart, CSV, support info.</summary>
public class AdminTests
{
    private static (AppServices Services, MainViewModel Vm, MainWindow Window, string Dir) Boot()
    {
        var b = TestApp.Boot();
        return (b.Services, b.Vm, b.Window, b.Dir);
    }

    private static MetricSample Healthy(int i) => new()
    {
        Utc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc).AddSeconds(i),
        CpuAppPct = 9,
        CpuSystemPct = 22,
        RamAppMB = 640,
        RamSystemPct = 48,
        RamTotalMB = 16384,
        OutputFps = 60,
        OutputWindows = 1,
        PreviewFps = 60,
        DiskFreeGB = 90,
        Threads = 40,
        Handles = 900,
    };

    [AvaloniaFact]
    public void AdminTabExistsAndSectionBinds()
    {
        var (services, _, window, _) = Boot();
        try
        {
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
            var admin = tabs.Items.OfType<TabItem>().FirstOrDefault(t =>
                t.Header is StackPanel sp && sp.Children.OfType<TextBlock>().Any(tb => tb.Text == "Admin"));
            Assert.NotNull(admin);
            tabs.SelectedItem = admin;
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void MetricsFlowIntoTextsSparklinesAndSuggestions()
    {
        var (services, vm, _, _) = Boot();
        try
        {
            for (var i = 0; i < 70; i++) services.Metrics.Ingest(Healthy(i));
            vm.PollNow();

            Assert.Contains("this app 9%", vm.AdminCpuText);
            Assert.Contains("whole computer 22%", vm.AdminCpuText);
            Assert.Contains("640 MB", vm.AdminMemText);
            Assert.Contains("outputs 60 fps", vm.AdminRenderText);
            Assert.True(vm.AdminCpuSpark.Count > 10);
            Assert.True(vm.AdminRamSpark.Count > 10);
            Assert.Single(vm.AdminSuggestions);
            Assert.Contains("All clear", vm.AdminSuggestions[0].Title);
        }
        finally
        {
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void AdvisorRowsFollowTheMetrics()
    {
        var (services, vm, _, _) = Boot();
        try
        {
            for (var i = 0; i < 70; i++) services.Metrics.Ingest(Healthy(i));
            vm.PollNow();
            Assert.Contains("All clear", vm.AdminSuggestions[0].Title);

            // The machine goes to battery and the CPU pins — the advice list must move with it.
            for (var i = 70; i < 140; i++)
            {
                services.Metrics.Ingest(Healthy(i) with { OnBattery = true, BatteryPct = 41, CpuSystemPct = 96 });
            }
            vm.PollNow();
            var titles = vm.AdminSuggestions.Select(s => s.Title).ToList();
            Assert.Contains(titles, t => t.Contains("battery", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(titles, t => t.Contains("CPU", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(titles, t => t.Contains("All clear"));
        }
        finally
        {
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void GraphicsChoiceUpdatesVisibilityAndStatus()
    {
        var (services, vm, _, _) = Boot();
        try
        {
            Assert.False(vm.GpuSpecificVisible);
            vm.State.Admin.Graphics.Preference = GpuPreferenceKind.Specific;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.GpuSpecificVisible);
            Assert.Contains("Restart app", vm.GraphicsApplyStatus);
            Assert.NotEmpty(vm.GpuRows); // "No adapters detected" placeholder on non-Windows
            Assert.NotEmpty(vm.GpuAdapterNames);
        }
        finally
        {
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void PrepareRestartKeepsTheRecoveryFileThroughShutdown()
    {
        var (services, _, _, dir) = Boot();
        var recoveryPath = Path.Combine(dir, "patterns.recovery.json");

        var code = services.PrepareRestart();
        Assert.Equal(0, code); // not running under the supervisor in tests
        Assert.True(File.Exists(recoveryPath));

        services.Shutdown();
        Assert.True(File.Exists(recoveryPath)); // the relaunch reads it — a restart must not clear it
    }

    [AvaloniaFact]
    public void CleanShutdownStillClearsRecovery()
    {
        var (services, _, _, dir) = Boot();
        services.Recovery.Write(live: true, audioPlaying: false);
        services.Shutdown();
        Assert.False(File.Exists(Path.Combine(dir, "patterns.recovery.json")));
    }

    [AvaloniaFact]
    public void RestartWithoutSupervisorExplainsInsteadOfExiting()
    {
        var (services, vm, _, _) = Boot();
        try
        {
            vm.RestartAppCommand.Execute(null);
            Assert.Contains("watchdog", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void ThirtySamplesAppendOneCsvLine()
    {
        var (services, _, _, dir) = Boot();
        try
        {
            services.State.Admin.MetricsCsv = true;
            for (var i = 0; i < MetricsHistory.AggregateEvery; i++) services.Metrics.Ingest(Healthy(i));

            var path = Path.Combine(dir, "patterns.metrics.csv");
            Assert.True(File.Exists(path));
            var lines = File.ReadAllLines(path);
            Assert.Equal(MetricsCsv.Header, lines[0]);
            Assert.Equal(2, lines.Length);
            Assert.Equal(MetricsCsv.Header.Split(',').Length, lines[1].Split(',').Length);
        }
        finally
        {
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void CsvOffWritesNothing()
    {
        var (services, _, _, dir) = Boot();
        try
        {
            services.State.Admin.MetricsCsv = false;
            for (var i = 0; i < 40; i++) services.Metrics.Ingest(Healthy(i));
            Assert.False(File.Exists(Path.Combine(dir, "patterns.metrics.csv")));
        }
        finally
        {
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void SupportInfoCarriesTheEssentials()
    {
        var (services, vm, _, dir) = Boot();
        try
        {
            services.Metrics.Ingest(Healthy(0));
            var info = vm.BuildSupportInfo();
            Assert.Contains("PATTERNS SUPPORT INFO", info);
            Assert.Contains("OS:", info);
            Assert.Contains("CPU:", info);
            Assert.Contains(dir, info);
            Assert.Contains("Watchdog:", info);
            Assert.Contains("Advice", info);
        }
        finally
        {
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void StateJsonCarriesTheMachineRow()
    {
        var (services, _, _, _) = Boot();
        try
        {
            for (var i = 0; i < 5; i++) services.Metrics.Ingest(Healthy(i));
            var router = new CommandRouter(services);
            var json = router.StateJson();
            Assert.Contains("\"machine\":", json);
            Assert.Contains("\"cpu\":22", json);
            Assert.Contains("\"fps\":60", json);
            Assert.Contains("\"advice\":0", json);
        }
        finally
        {
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void SettingsRoundTripKeepsAdminChoices()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-admin-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SettingsStore(dir);
        var state = store.Load();
        state.Admin.Graphics.Preference = GpuPreferenceKind.PowerSaving;
        state.Admin.Graphics.AdapterName = "Intel(R) UHD Graphics";
        state.Admin.MetricsCsv = false;
        store.Save(state);

        var back = new SettingsStore(dir).Load();
        Assert.Equal(GpuPreferenceKind.PowerSaving, back.Admin.Graphics.Preference);
        Assert.Equal("Intel(R) UHD Graphics", back.Admin.Graphics.AdapterName);
        Assert.False(back.Admin.MetricsCsv);
    }
}
