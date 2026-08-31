using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The Admin tab's engine room: samples the machine once a second (CPU, RAM, VRAM, GPU busy,
/// render pacing, disk, power), keeps the rolling <see cref="MetricsHistory"/>, refreshes the
/// <see cref="HealthAdvisor"/> suggestions, and appends the 30-second CSV record. All numbers
/// are best-effort — a failed probe reads "unknown", never throws.
/// </summary>
public sealed class SystemMetricsService : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private GpuEngineCounter? _gpuCounter;
    private readonly Process _process = Process.GetCurrentProcess();

    private DateTime _lastTickUtc = DateTime.UtcNow;
    private ulong _prevIdle, _prevKernel, _prevUser;
    private TimeSpan _prevProcessCpu = TimeSpan.Zero;
    private DateTime _prevProcessWallUtc = DateTime.UtcNow;
    private int _sinceCsv;
    private bool _csvHeaderChecked;

    public MetricsHistory History { get; } = new();
    public MetricSample? Current { get; private set; }
    public IReadOnlyList<HealthSuggestion> Suggestions { get; private set; } = Array.Empty<HealthSuggestion>();

    public SystemMetricsService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>One sampling tick — also the deterministic entry point for tests and shots.</summary>
    public void Poll()
    {
        var utcNow = DateTime.UtcNow;
        var elapsed = Math.Clamp((utcNow - _lastTickUtc).TotalSeconds, 0.25, 5.0);
        _lastTickUtc = utcNow;
        try
        {
            Ingest(Sample(utcNow, elapsed));
        }
        catch (Exception ex)
        {
            Log.Warn("Metrics sample failed.", ex);
        }
    }

    /// <summary>History + advisor + CSV for one sample. Public so tests can feed synthetic data.</summary>
    public void Ingest(MetricSample sample)
    {
        History.Add(sample);
        Current = sample;
        Suggestions = HealthAdvisor.Advise(History, BuildContext(sample));

        if (++_sinceCsv >= MetricsHistory.AggregateEvery)
        {
            _sinceCsv = 0;
            if (_services.State.Admin.MetricsCsv) AppendCsv(sample);
        }
    }

    internal AdvisorContext BuildContext(MetricSample sample) => new()
    {
        OutputsLive = _services.Outputs.IsLive,
        ContentContinuous = sample.OutputWindows > 0 && sample.OutputFps > 15,
        TargetFps = 60,
        WatchdogEnabled = _services.State.Watchdog.Enabled,
        WatchdogRestarts = HealthMonitor.Restarts,
        DiscreteGpuPresent = GpuService.DiscreteGpuPresent,
        UsingDiscreteGpu = GpuService.UsingBestGpu,
        BestGpuName = GpuService.BestGpuName,
    };

    // ---- the real sampler -------------------------------------------------------------------

    private MetricSample Sample(DateTime utcNow, double elapsedSeconds)
    {
        var (previewFps, outputFps, windows, worstMs, slow) = RenderStats.Drain(elapsedSeconds);

        double cpuSys = -1;
        if (Win32Perf.TryGetSystemTimes(out var idle, out var kernel, out var user))
        {
            var idleD = idle - _prevIdle;
            var totalD = (kernel - _prevKernel) + (user - _prevUser); // kernel includes idle
            if (_prevKernel != 0 && totalD > 0)
            {
                cpuSys = Math.Clamp(100.0 * (totalD - idleD) / totalD, 0, 100);
            }
            (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);
        }

        double cpuApp = -1, ramApp = -1;
        int threads = 0, handles = 0;
        try
        {
            _process.Refresh();
            var cpuNow = _process.TotalProcessorTime;
            var wallDelta = (utcNow - _prevProcessWallUtc).TotalSeconds;
            if (_prevProcessCpu > TimeSpan.Zero && wallDelta > 0.2)
            {
                cpuApp = Math.Clamp(
                    (cpuNow - _prevProcessCpu).TotalSeconds / wallDelta / Environment.ProcessorCount * 100.0, 0, 100);
            }
            _prevProcessCpu = cpuNow;
            _prevProcessWallUtc = utcNow;
            ramApp = _process.WorkingSet64 / (1024.0 * 1024.0);
            threads = _process.Threads.Count;
            handles = OperatingSystem.IsWindows() ? _process.HandleCount : 0;
        }
        catch
        {
            // Process probes are allowed to fail (teardown) — the sample carries what it has.
        }

        Win32Perf.TryGetMemoryStatus(out var ramLoad, out var ramTotal, out var ramAvail);
        Dxgi.TryQueryVideoMemory(GpuService.WatchedLuid, out var vramUsed, out var vramBudget);
        var (onBattery, batteryPct) = Win32Perf.GetPowerStatus();

        double gpuBusy = -1;
        if (OperatingSystem.IsWindows())
        {
            _gpuCounter ??= new GpuEngineCounter();
            gpuBusy = _gpuCounter.Read(utcNow);
        }

        double gcPause = -1;
        try
        {
            gcPause = GC.GetGCMemoryInfo().PauseTimePercentage;
        }
        catch
        {
            // Not available on every runtime configuration.
        }

        double diskFree = -1;
        try
        {
            var root = Path.GetPathRoot(_services.Store.BaseDirectory);
            if (!string.IsNullOrEmpty(root))
            {
                diskFree = new DriveInfo(root).AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
            }
        }
        catch
        {
            // Network/removable drives can refuse — unknown is fine.
        }

        return new MetricSample
        {
            Utc = utcNow,
            CpuAppPct = cpuApp,
            CpuSystemPct = cpuSys,
            RamAppMB = ramApp,
            RamSystemPct = ramLoad,
            RamUsedMB = ramTotal > 0 ? ramTotal - ramAvail : -1,
            RamTotalMB = ramTotal,
            VramUsedMB = vramUsed,
            VramTotalMB = vramBudget,
            GpuBusyPct = gpuBusy,
            PreviewFps = previewFps,
            OutputFps = outputFps,
            OutputWindows = windows,
            WorstFrameMs = worstMs,
            SlowFrames = slow,
            Threads = threads,
            Handles = handles,
            GcPausePct = gcPause,
            DiskFreeGB = diskFree,
            OnBattery = onBattery,
            BatteryPct = batteryPct,
            Faults = HealthMonitor.Faults,
        };
    }

    // ---- rolling CSV ------------------------------------------------------------------------

    private string CsvPath => Path.Combine(_services.Store.BaseDirectory, "patterns.metrics.csv");

    private void AppendCsv(MetricSample sample)
    {
        try
        {
            var path = CsvPath;
            if (!_csvHeaderChecked)
            {
                _csvHeaderChecked = true;
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                {
                    File.Copy(path, path + ".old", overwrite: true);
                    File.Delete(path);
                }
            }
            if (!File.Exists(path))
            {
                File.WriteAllText(path, MetricsCsv.Header + Environment.NewLine);
            }
            else if (new FileInfo(path).Length > 1024 * 1024)
            {
                File.Copy(path, path + ".old", overwrite: true);
                File.WriteAllText(path, MetricsCsv.Header + Environment.NewLine);
            }
            File.AppendAllText(path, MetricsCsv.Line(sample) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Log.Warn("Metrics CSV write failed.", ex);
        }
    }

    // ---- support info -----------------------------------------------------------------------

    /// <summary>Everything a support email needs, as plain text (the Copy button's payload).</summary>
    public string SupportInfo()
    {
        var sb = new System.Text.StringBuilder();
        var s = Current;
        sb.AppendLine("PATTERNS SUPPORT INFO");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
        try
        {
            var version = typeof(SystemMetricsService).Assembly.GetName().Version?.ToString() ?? "dev";
            sb.AppendLine($"App: Patterns {version} · .NET {Environment.Version} · {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
            sb.AppendLine($"Machine: {Environment.MachineName}");
            var cpuName = WinRegistry.ReadCpuName();
            sb.AppendLine($"CPU: {(cpuName.Length > 0 ? cpuName : "unknown")} · {Environment.ProcessorCount} threads");
            if (s is { RamTotalMB: > 0 })
            {
                sb.AppendLine($"RAM: {s.RamTotalMB / 1024.0:0.0} GB total · {s.RamSystemPct:0}% in use");
            }
            if (GpuService.Adapters.Count > 0)
            {
                foreach (var g in GpuService.Adapters)
                {
                    var active = string.Equals(g.Name, GpuService.ActiveAdapterName, StringComparison.OrdinalIgnoreCase)
                        ? " — in use" : "";
                    sb.AppendLine($"GPU: {g.Name} · {g.DedicatedVideoMemoryMB / 1024.0:0.#} GB · {g.VendorName}{active}");
                }
            }
            var screens = _services.Screens.All;
            sb.AppendLine($"Screens: {screens.Count}");
            foreach (var sc in screens)
            {
                sb.AppendLine($"  {sc.Label}: {sc.Bounds.Width}×{sc.Bounds.Height} @ {sc.Scaling:0.##}x{(sc.IsPrimary ? " · primary" : "")}");
            }
            sb.AppendLine($"Folder: {_services.Store.BaseDirectory}");
            sb.AppendLine($"Watchdog: {(_services.State.Watchdog.Enabled ? "on" : "off")} · {HealthMonitor.Summary(DateTime.UtcNow)}");
            var (onBattery, pct) = (s?.OnBattery ?? false, s?.BatteryPct ?? -1);
            sb.AppendLine($"Power: {(onBattery ? $"battery{(pct >= 0 ? $" {pct}%" : "")}" : "mains/unknown")}");
            if (s is not null)
            {
                sb.AppendLine($"Now: app CPU {P(s.CpuAppPct)} · system CPU {P(s.CpuSystemPct)} · app RAM {P(s.RamAppMB, " MB")} · " +
                              $"VRAM {P(s.VramUsedMB, " MB")}/{P(s.VramTotalMB, " MB")} · GPU {P(s.GpuBusyPct)} · " +
                              $"outputs {s.OutputFps:0} fps ×{s.OutputWindows} · worst frame {s.WorstFrameMs:0.0} ms · " +
                              $"threads {s.Threads} · handles {s.Handles} · disk free {P(s.DiskFreeGB, " GB")}");
            }
            foreach (var advice in Suggestions)
            {
                sb.AppendLine($"Advice [{advice.Severity}]: {advice.Title} — {advice.Detail}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(support info incomplete: {ex.Message})");
        }
        return sb.ToString();

        static string P(double v, string unit = "%") => v < 0 ? "n/a" : $"{v:0.#}{unit}";
    }

    public void Dispose()
    {
        _timer.Stop();
        if (OperatingSystem.IsWindows())
        {
            _gpuCounter?.Dispose();
        }
        _process.Dispose();
    }
}
