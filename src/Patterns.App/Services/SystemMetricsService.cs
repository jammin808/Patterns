using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Platform.Windows;

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
    private bool _csvHeaderRead;

    public MetricsHistory History { get; } = new();
    public MetricSample? Current { get; private set; }
    public IReadOnlyList<HealthSuggestion> Suggestions { get; private set; } = Array.Empty<HealthSuggestion>();

    /// <summary>The owners this desk registers on the ledger, unregistered with it (round 64: a static reader that captured the desk kept it alive).</summary>
    private static readonly string[] LedgerOwners = { "pictures", "frame pools", "retiring", "deck pages", "managed heap" };

    private readonly Func<long> _extra;

    public SystemMetricsService(AppServices services)
    {
        _services = services;
        RegisterLedger();
        Pressure = new MemoryPressureLadder(services);
        _extra = () => _services.DeckIn.PageBytes;
        MediaMemory.Extra = _extra;
        _timer = global::Patterns.App.Services.DeskTimers.Make(TimeSpan.FromSeconds(1));
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>The memory pressure ladder: read and applied with every poll.</summary>
    public MemoryPressureLadder Pressure { get; }

    /// <summary>The owners the memory ledger reads: the pictures, the frame pools, the frames held, the decks' pages, the managed heap and what the runtime committed for it.</summary>
    private void RegisterLedger()
    {
        MemoryLedger.Register("pictures", () => (Patterns.Rendering.Media.ImageCache.Bytes, $"{Patterns.Rendering.Media.ImageCache.Count} cached"));
        MemoryLedger.Register("frame pools", () => (Patterns.Rendering.Media.FramePools.Bytes, $"{Patterns.Rendering.Media.FramePools.Count} source{(Patterns.Rendering.Media.FramePools.Count == 1 ? "" : "s")}"));
        MemoryLedger.Register("retiring", () => (Patterns.Rendering.Media.RetiredFrames.Bytes + Patterns.Rendering.Media.FramePools.RetiringBytes,
            $"{Patterns.Rendering.Media.RetiredFrames.CountOf(Patterns.Rendering.Media.RetiredFrames.Kind.Frame)} frames, {Patterns.Rendering.Media.RetiredFrames.CountOf(Patterns.Rendering.Media.RetiredFrames.Kind.Picture)} pictures, {Patterns.Rendering.Media.FramePools.PendingFree} pools behind the fence"));
        MemoryLedger.Register("deck pages", () => (_services.DeckIn.PageBytes, $"{_services.DeckIn.DeckCount} deck{(_services.DeckIn.DeckCount == 1 ? "" : "s")}"));
        MemoryLedger.Register("managed heap", () => (GC.GetTotalMemory(false), $"{GC.GetGCMemoryInfo().TotalCommittedBytes / (1024 * 1024)} MB committed"));
    }

    /// <summary>
    /// The one-second sampler. Off, the service reads only what is fed to it (<see cref="Ingest"/>):
    /// for tests and shots that feed their own samples, so a live reading of this machine never
    /// lands between the feed and the read.
    /// </summary>
    public bool Live
    {
        get => _timer.IsEnabled;
        set
        {
            if (value) _timer.Start();
            else _timer.Stop();
        }
    }

    /// <summary>One sampling tick — also the deterministic entry point for tests and shots.</summary>
    public void Poll()
    {
        var utcNow = DateTime.UtcNow;
        var elapsed = Math.Clamp((utcNow - _lastTickUtc).TotalSeconds, 0.25, 5.0);
        _lastTickUtc = utcNow;
        // The order is the tick's truth: the ladder's rung from the bytes first, the GPU governor's limit from that rung
        // (and the last sample's card figures), then the sample — which carries the limit decided this tick — and the ledger.
        try
        {
            Pressure.Apply(MediaMemory.Read(MemoryBudget.MachineMB));                                   // the ladder: the rung from the bytes, its steps taken, every second
        }
        catch (Exception ex)
        {
            Log.Warn("Memory pressure ladder failed.", ex);
        }
        try
        {
            GovernGpuCache();                                                                            // round 69: the GPU cache's limit for this card and rung, applied by the sinks' next draw
        }
        catch (Exception ex)
        {
            Log.Warn("GPU cache governor failed.", ex);
        }
        try
        {
            Ingest(Sample(utcNow, elapsed));
        }
        catch (Exception ex)
        {
            Log.Warn("Metrics sample failed.", ex);
        }
        try
        {
            _services.Residency.Poll(Pressure.Level);                                                    // round 69: the ledger's rows, and idle pictures let go on their clock
        }
        catch (Exception ex)
        {
            Log.Warn("Residency ledger failed.", ex);
        }
    }

    /// <summary>The rung the GPU cache is governed at now: the worse of the media ladder's and the card's own (what it uses of the budget the OS grants).</summary>
    public MemoryPressure GpuRung { get; private set; }

    /// <summary>The dedicated memory of the card the desk draws with (MB), 0 unknown — the machine's reading, the largest hardware adapter.</summary>
    public static long DedicatedVramMB()
    {
        try
        {
            var facts = MachineProbe.Read();
            return facts.IsEmpty ? 0 : facts.Gpus.Where(g => !g.Software).Select(g => g.VramMB).DefaultIfEmpty(0L).Max();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Round 69: the policy's limit and purge for this second, handed to the sinks' draws.</summary>
    private void GovernGpuCache()
    {
        var sample = Current;
        var vram = GpuGovernor.VramPressure(sample?.VramUsedMB ?? -1, sample?.VramTotalMB ?? -1);
        GpuRung = GpuGovernor.Worse(Pressure.Level, vram);
        var limit = GpuGovernor.LimitBytes(MemoryBudget.ClassOf(MemoryBudget.MachineMB), DedicatedVramMB(), GpuRung);
        Patterns.App.Rendering.GpuCacheGovernor.Want(limit, GpuGovernor.PurgeAt(GpuRung));
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

    /// <summary>The advisor's view of the rig for one sample; the target rate follows the show's master frame rate (60 when unlimited).</summary>
    public AdvisorContext BuildContext(MetricSample sample) => new()
    {
        OutputsLive = _services.Outputs.IsLive,
        // Continuous by what the content asks for, not only by what was drawn: a render path that has
        // stopped draws nothing, and that is the one case the frozen-outputs rule must still see.
        ContentContinuous = sample.OutputWindows > 0 && (sample.OutputFps > 15 || ContentWantsFrames()),
        StreamError = _services.State.Stream.LastError,
        TargetFps = _services.State.Output.MasterFps > 0 ? _services.State.Output.MasterFps : 60,
        WatchdogEnabled = _services.State.Watchdog.Enabled,
        WatchdogRestarts = HealthMonitor.Restarts,
        DiscreteGpuPresent = GpuService.DiscreteGpuPresent,
        UsingDiscreteGpu = GpuService.UsingBestGpu,
        BestGpuName = GpuService.BestGpuName,
    };

    /// <summary>Whether the program on air is continuously animated (a clip, a feed, motion, particles…).</summary>
    private bool ContentWantsFrames()
    {
        try
        {
            return Patterns.Rendering.PatternEngine.CadenceOf(_services.Bus.Current, null, DateTime.UtcNow) == Patterns.Rendering.RedrawCadence.Continuous;
        }
        catch
        {
            return false;
        }
    }

    // ---- the real sampler -------------------------------------------------------------------

    private MetricSample Sample(DateTime utcNow, double elapsedSeconds)
    {
        var (previewFps, outputFps, windows, worstMs, slow) = RenderStats.Drain(elapsedSeconds);
        var outputs = FrameBudgets.Readings(ShowClock.Seconds).Where(r => r.Kind == SinkKind.Output).ToList();   // the last minute's p95 and drops, per output

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

        double cpuApp = -1, ramApp = -1, privateMB = -1, managedMB = -1, committedMB = -1;
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
            privateMB = _process.PrivateMemorySize64 / (1024.0 * 1024.0);
            managedMB = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            committedMB = GC.GetGCMemoryInfo().TotalCommittedBytes / (1024.0 * 1024.0);
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
        var gc = ShowGc.Facts();                                                                          // round 69: the collector's generations, the large-object heap, the last pause
        var gpuCache = Patterns.App.Rendering.GpuCacheGovernor.Facts;                                     // round 69: Skia's GPU cache as governed — limit, fill, purges

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
            P95FrameMs = outputs.Count > 0 ? outputs.Max(r => r.P95Ms) : -1,
            MissedSlots = outputs.Sum(r => r.Missed),
            RenderFaults = outputs.Sum(r => r.Faults),
            SwitchWorstMs = _services.Switches.Worst?.TotalMs ?? -1,
            SlowSwitches = (int)Math.Min(int.MaxValue, _services.Switches.SlowSwitches),
            GoWorstMs = _services.CueStack.GoClock.Worst?.TotalMs ?? -1,
            LagWorstMs = outputs.Count > 0 ? outputs.Max(r => r.LagMs) : -1,
            LiveAgeWorstMs = outputs.Count > 0 ? outputs.Max(r => r.LiveAgeMs) : -1,
            RetiringMB = (Patterns.Rendering.Media.RetiredFrames.Bytes + Patterns.Rendering.Media.FramePools.RetiringBytes) / (1024.0 * 1024.0),
            PoolStarved = Patterns.Rendering.Media.FramePools.Starved,
            Threads = threads,
            Handles = handles,
            GcPausePct = gcPause,
            GcGen0 = gc.Gen0,
            GcGen1 = gc.Gen1,
            GcGen2 = gc.Gen2,
            LohMB = gc.LohMB,
            GcLastPauseMs = gc.LastPauseMs,
            GpuCacheLimitMB = gpuCache.HasContext || gpuCache.LimitBytes > 0 ? Math.Round(gpuCache.LimitBytes / (1024.0 * 1024.0)) : -1,
            GpuCacheUsedMB = gpuCache.HasContext ? Math.Round(gpuCache.UsedBytes / (1024.0 * 1024.0), 1) : -1,
            GpuCachePurges = gpuCache.Purges,
            PrivateMB = privateMB,
            ManagedMB = managedMB,
            GcCommittedMB = committedMB,
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
            if (File.Exists(path) && !_csvHeaderRead)
            {
                // A file from a build with other columns is kept as .old and started again: a row never lands under the wrong header.
                _csvHeaderRead = true;
                using var reader = new StreamReader(path);
                var first = reader.ReadLine();
                if (first is not null && first != MetricsCsv.Header)
                {
                    reader.Dispose();
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

    // ---- the super-check ----------------------------------------------------------------------

    /// <summary>The last report run (the Admin page shows it; the file beside the exe carries it).</summary>
    public CheckReport? LastReport { get; private set; }

    /// <summary>Where the last report was written, or "" when the write failed.</summary>
    public string LastReportPath { get; private set; } = "";

    /// <summary>
    /// One button: gathers every fact the app can reach right now — the machine, the card, the
    /// displays, the outputs, NDI, the stream, audio, the remote, video, the advice — runs the
    /// pure rules and writes the report beside the exe. Never throws; a probe that fails leaves
    /// its row unknown.
    /// </summary>
    public CheckReport RunSuperCheck()
    {
        var report = SuperCheck.Run(GatherFacts());
        LastReport = report;
        try
        {
            var path = Path.Combine(_services.Store.BaseDirectory, SuperCheck.FileName);
            File.WriteAllText(path, SuperCheck.ToText(report));
            LastReportPath = path;
        }
        catch (Exception ex)
        {
            Log.Warn("Super-check report write failed.", ex);
            LastReportPath = "";
        }
        Log.Info($"Super-check: {report.Overall} — {report.Headline}");
        return report;
    }

    /// <summary>The facts as the app sees them now. Every probe is guarded; unknown stays unknown.</summary>
    /// <summary>The MEMORY CEILINGS line: the app against a quarter of the machine, the picture cache, the decoder pool, the frames held for fades.</summary>
    public string MemoryCeilingLine()
    {
        var sample = Current;
        var ceilings = MemoryBudget.For(sample?.RamTotalMB ?? -1, Patterns.Rendering.Media.ImageCache.Capacity, VideoEngine.MaxMounts);
        var line = MemoryBudget.Describe(sample?.RamAppMB ?? -1, ceilings, Patterns.Rendering.Media.ImageCache.Count, _services.Video.MountCount, VlcFrameSource.RetiredImageCount,
            Patterns.Rendering.Media.ImageCache.Bytes, Patterns.Rendering.Media.FramePools.Bytes, Patterns.Rendering.Media.FramePools.Count,
            Patterns.Rendering.Media.FramePools.RetiringBytes, Patterns.Rendering.Media.RetiredFrames.Bytes,
            Patterns.Rendering.Media.FramePools.OverTarget(ceilings.FramePoolBytesPerSource), _services.Video.RetiredCount);
        var media = Pressure.Reading ?? MediaMemory.Read(MemoryBudget.MachineMB);
        return line + " · " + media.Words + " · placed: " + MemoryLedger.Describe();
    }

    public CheckFacts GatherFacts()
    {
        var s = Current;
        var state = _services.State;
        var version = "";
        double uptime = -1;
        try
        {
            version = typeof(SystemMetricsService).Assembly.GetName().Version?.ToString() ?? "dev";
            uptime = (DateTime.UtcNow - _process.StartTime.ToUniversalTime()).TotalSeconds;              // the process's start is a wall-clock fact; UTC keeps a daylight-saving step out of it
        }
        catch
        {
            // A process probe that refuses leaves the row unknown.
        }

        var cpuName = "";
        try
        {
            cpuName = WinRegistry.ReadCpuName();
        }
        catch
        {
            // Registry access can refuse.
        }

        double ramTotal = s?.RamTotalMB ?? -1, ramPct = s?.RamSystemPct ?? -1;
        if (ramTotal <= 0 && Win32Perf.TryGetMemoryStatus(out var load, out var total, out _))
        {
            ramTotal = total;
            ramPct = load;
        }

        double diskFree = -1;
        try
        {
            var root = Path.GetPathRoot(_services.Store.BaseDirectory);
            if (!string.IsNullOrEmpty(root)) diskFree = new DriveInfo(root).AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
        }
        catch
        {
            // Network/removable drives can refuse.
        }

        var (onBattery, batteryPct) = Win32Perf.GetPowerStatus();

        var displays = new List<CheckDisplay>();
        try
        {
            foreach (var sc in _services.Screens.All)
            {
                var placement = state.Output.Placements.FirstOrDefault(p => p.ScreenId == sc.Id);
                var hz = -1;
                if (!sc.IsPlanned && DisplayModes.Supported)
                {
                    try
                    {
                        var device = DisplayModes.DeviceFor(sc.Bounds.ToRaster());
                        if (device is not null && DisplayModes.Current(device) is { } mode) hz = mode.Hz;
                    }
                    catch
                    {
                        // The mode probe is a courtesy.
                    }
                }
                displays.Add(new CheckDisplay(placement is null ? sc.Label : Rig.LabelFor(placement, sc), sc.Bounds.Width, sc.Bounds.Height, sc.Scaling, sc.IsPrimary,
                    placement?.Enabled ?? false, sc.IsPlanned, hz,
                    Missing: placement is not null && HotPlugWatch.IsLost(placement) ? HotPlugWatch.LostWords(placement) : ""));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Super-check: display probe failed.", ex);
        }

        IReadOnlyList<SignalReport> signals = Array.Empty<SignalReport>();
        try
        {
            signals = _services.Actions.SignalReports();      // round 65: each screen's contract against what Windows sends
        }
        catch (Exception ex)
        {
            Log.Warn("Super-check: signal probe failed.", ex);
        }

        // Round 65.9: the rig of the day against the commissioned one — no probe on this thread; with a
        // rig saved, the section waits until the first reading of the machine has landed.
        RigDrift? rigDrift = null;
        var rigChecked = false;
        try
        {
            rigDrift = _services.Actions.RigDriftNow();
            rigChecked = _services.Kernel.KnownGood.Known is null || rigDrift is not null;
        }
        catch (Exception ex)
        {
            Log.Warn("Super-check: rig probe failed.", ex);
        }

        var senders = state.Ndi.Senders.Where(x => x.Enabled).ToList();
        var senderLines = new List<string>();
        foreach (var cfg in senders)
        {
            var status = _services.Ndi.StatusFor(cfg.Id);
            senderLines.Add($"{cfg.Name} · {cfg.Width}×{cfg.Height}{(status.Length > 0 ? $" · {status}" : "")}");
        }

        var audioDevices = -1;
        try
        {
            if (OperatingSystem.IsWindows()) audioDevices = AudioPlayerService.OutputDevices().Count;
        }
        catch
        {
            // No audio stack — unknown.
        }

        var remoteUrl = "";
        try
        {
            if (state.Control.Enabled) remoteUrl = _services.Control.ReachableUrl();
        }
        catch
        {
            // The remote's address list is a courtesy.
        }

        var video = false;
        try
        {
            video = _services.Video.SharedVlc is not null;
        }
        catch
        {
            // libVLC missing: the note says so.
        }

        // The engine's frame budget and the start-up, read once for the facts.
        var frames = FrameBudgets.Readings(ShowClock.Seconds);
        var worstFrame = FrameBudgets.Worst(frames);
        var startup = _services.Startup;
        var startupPhases = startup.Phases;

        return new CheckFacts
        {
            AppVersion = version.Length > 0 ? $"Patterns {version}" : "",
            Os = RuntimeInformation.OSDescription,
            Machine = Environment.MachineName,
            CpuName = cpuName,
            CpuThreads = Environment.ProcessorCount,
            RamTotalMB = ramTotal,
            RamUsedPct = ramPct,
            DiskFreeGB = diskFree,
            OnBattery = onBattery,
            BatteryPct = batteryPct,
            UptimeSeconds = uptime,
            CpuSystemPct = s?.CpuSystemPct ?? -1,
            CpuAppPct = s?.CpuAppPct ?? -1,
            Gpus = GpuService.Adapters,
            ActiveGpu = GpuService.ActiveAdapterName.Length > 0 ? GpuService.ActiveAdapterName : GpuService.RequestedName,
            UsingBestGpu = GpuService.UsingBestGpu,
            VramUsedMB = s?.VramUsedMB ?? -1,
            VramTotalMB = s?.VramTotalMB ?? -1,
            GpuBusyPct = s?.GpuBusyPct ?? -1,
            DirectOutputsAsking = DirectOutputService.Asking(state),
            DirectOutputInForce = DirectOutputService.ModeInForce == DirectOutputMode.LowLatencySwapChain,
            DirectOutputSummary = DirectOutputService.Summary(state),
            Displays = displays,
            Signals = signals,
            RigChecked = rigChecked,
            RigDrift = rigDrift,
            OutputsLive = _services.Outputs.IsLive,
            OutputWindows = s?.OutputWindows ?? -1,
            OutputFps = History.Recent.Count > 0 ? History.AvgRecent(60, x => x.OutputFps) : -1,
            TargetFps = state.Output.MasterFps > 0 ? state.Output.MasterFps : 60,
            WorstFrameMs = s?.WorstFrameMs ?? -1,
            SlowFrames = s?.SlowFrames ?? -1,
            Faults = (int)Math.Min(int.MaxValue, HealthMonitor.Faults),
            DeskTickAverageMs = _services.DeskTick.AverageMs,
            DeskTickWorstMs = _services.DeskTick.WorstMs,
            DeskTickWorstArea = _services.DeskTick.WorstArea,
            DeskSlowTicks = (int)Math.Min(int.MaxValue, _services.DeskTick.SlowTicks),
            DeskTickFaults = (int)Math.Min(int.MaxValue, _services.DeskTick.Faults),
            SwitchWorstMs = _services.Switches.Worst?.TotalMs ?? -1,
            SwitchWorstWords = _services.Switches.Worst?.Words ?? "",
            SwitchAverageMs = _services.Switches.AverageMs,
            SlowSwitches = (int)Math.Min(int.MaxValue, _services.Switches.SlowSwitches),
            GoWorstMs = _services.CueStack.GoClock.Worst?.TotalMs ?? -1,
            GoWorstWords = _services.CueStack.GoClock.Worst?.Words ?? "",
            GoAverageMs = _services.CueStack.GoClock.AverageMs,
            SlowGos = (int)Math.Min(int.MaxValue, _services.CueStack.GoClock.SlowGos),
            RenderWorstMs = worstFrame?.WorstMs ?? -1,
            RenderWorstStage = worstFrame?.WorstStage ?? "",
            RenderWorstSink = worstFrame?.Name ?? "",
            RenderAverageMs = frames.Count > 0 ? frames.Average(r => r.AverageMs) : -1,
            RenderSlowFrames = frames.Count > 0 ? FrameBudgets.SlowFrames(frames) : -1,
            RenderSinks = frames.Count,
            RenderFaults = frames.Sum(r => r.Faults),
            RenderConsecutiveFaults = frames.Count > 0 ? frames.Max(r => r.ConsecutiveFaults) : 0,
            RenderLastFault = frames.Where(r => r.ConsecutiveFaults > 0 || r.Faults > 0).Select(r => $"{r.Name}: {r.LastFault}").FirstOrDefault() ?? "",
            RenderClockHz = FrameBudgets.ClockHz(frames),
            ClockLimited = FrameBudgets.ClockLimited(frames),
            LiveAgeWorstMs = frames.Count > 0 ? frames.Max(r => r.LiveAgeMs) : -1,
            SideEffectPasses = _services.Reconciles.Passes,
            SideEffectWorstMs = _services.Reconciles.WorstPassMs,
            SideEffectWorstSections = _services.Reconciles.WorstPassSections,
            SlowSideEffectPasses = _services.Reconciles.SlowPasses,
            StartupSeconds = startup.TotalMs >= 0 ? startup.TotalMs / 1000 : -1,
            StartupPhases = startupPhases.Count > 0 ? string.Join(" · ", startupPhases.Select(p => $"{p.Phase} {StartupBudget.Span(p.Ms)}")) : "",
            StartupComplete = startup.Complete,
            QualityMode = _services.Quality.Ladder.Mode,
            QualityLevel = _services.Quality.Ladder.Level,
            QualityWords = _services.Quality.Describe(),
            RamAppMB = s?.RamAppMB ?? -1,
            ImagesCached = Patterns.Rendering.Media.ImageCache.Count,
            Decoders = _services.Video.MountCount,
            DecoderCap = VideoEngine.MaxMounts,
            HeldFrames = VlcFrameSource.RetiredImageCount,
            PictureBytes = Patterns.Rendering.Media.ImageCache.Bytes,
            FramePoolBytes = Patterns.Rendering.Media.FramePools.Bytes,
            FramePools = Patterns.Rendering.Media.FramePools.Count,
            PoolsPendingFree = Patterns.Rendering.Media.FramePools.PendingFree,
            RetiringPoolBytes = Patterns.Rendering.Media.FramePools.RetiringBytes,
            RetiringFrameBytes = Patterns.Rendering.Media.RetiredFrames.Bytes,
            FenceOldestMs = Math.Max(Patterns.Rendering.Media.FramePools.OldestRetiredMs, Patterns.Rendering.Media.RetiredFrames.OldestMs),
            FenceLiveSinks = Patterns.Rendering.Media.RenderFence.LiveSinks,
            PoolStarved = Patterns.Rendering.Media.FramePools.Starved,
            HungSinks = Patterns.Rendering.Media.RenderFence.HungSinks,
            HungFrames = Patterns.Rendering.Media.RenderFence.HungFrames,
            FenceRefused = Patterns.Rendering.Media.RenderFence.Refused,
            QuarantinedBytes = Patterns.Rendering.Media.RetiredFrames.QuarantinedBytes + Patterns.Rendering.Media.FramePools.QuarantinedBytes,
            MediaBytes = (Pressure.Reading ?? MediaMemory.Read(MemoryBudget.MachineMB)).Total,
            MediaBudgetBytes = MediaMemory.BudgetBytes(MemoryBudget.MachineMB),
            Pressure = Pressure.Level,
            PoolsOverTarget = Patterns.Rendering.Media.FramePools.OverTarget(MemoryBudget.FramePoolBytesPerSource(MemoryBudget.MachineMB)),
            RetiringDecoders = _services.Video.RetiredCount,
            PrivateMB = s?.PrivateMB ?? -1,
            ManagedMB = s?.ManagedMB ?? -1,
            WatchdogEnabled = state.Watchdog.Enabled,
            WatchdogRestarts = HealthMonitor.Restarts,
            BeaconSending = _services.Beacon.Sending,
            BeaconListening = _services.Beacon.Listening,
            BeaconWatch = _services.Beacon.WatchText,
            TwinRole = state.Twin.Role,
            TwinPhase = _services.Twin.Phase,
            TwinWords = _services.Twin.Status,
            ShowLock = _services.ShowLock.Report,
            NdiRuntime = Patterns.Ndi.NdiSender.RuntimeAvailable,
            NdiSendersConfigured = senders.Count,
            NdiSendersActive = _services.Ndi.ActiveCount,
            NdiSenderLines = senderLines,
            StreamActive = state.Stream.Active,
            StreamDestinations = state.Stream.Destinations.Count,
            StreamStatus = _services.Stream.Status,
            AudioOutputDevices = audioDevices,
            AudioStatus = _services.AudioPlayer.Status,
            ToneStatus = _services.Audio.Status,
            SyncLock = state.AudioPlayer.SyncLock,
            SyncLines = _services.AudioPlayer.SyncReport(),
            SyncWorstLagMs = _services.AudioPlayer.SyncWorstLagMs,
            RemoteEnabled = state.Control.Enabled,
            RemoteUrl = remoteUrl,
            RemoteBind = state.Control.Bind,
            RemoteToken = PairingToken.Needed(state.Control.Token),
            VideoPlayback = video,
            VideoNote = Patterns.Rendering.Media.VideoService.AvailabilityNote,
            Advice = Suggestions,
        };
    }

    // ---- support info -----------------------------------------------------------------------

    /// <summary>Everything a support email needs, as plain text (the Copy button's payload).</summary>
    public string SupportInfo()
    {
        var sb = new System.Text.StringBuilder();
        var s = Current;
        sb.AppendLine("PATTERNS SUPPORT INFO");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
        try
        {
            var version = typeof(SystemMetricsService).Assembly.GetName().Version?.ToString() ?? "dev";
            sb.AppendLine(CultureInfo.InvariantCulture, $"App: Patterns {version} · .NET {Environment.Version} · {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"OS: {RuntimeInformation.OSDescription}");
            sb.AppendLine(Modules.Words());
            sb.AppendLine(CultureInfo.InvariantCulture, $"Machine: {Environment.MachineName}");
            var cpuName = WinRegistry.ReadCpuName();
            sb.AppendLine(CultureInfo.InvariantCulture, $"CPU: {(cpuName.Length > 0 ? cpuName : "unknown")} · {Environment.ProcessorCount} threads");
            if (s is { RamTotalMB: > 0 })
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"RAM: {s.RamTotalMB / 1024.0:0.0} GB total · {s.RamSystemPct:0}% in use");
            }
            if (GpuService.Adapters.Count > 0)
            {
                foreach (var g in GpuService.Adapters)
                {
                    var active = string.Equals(g.Name, GpuService.ActiveAdapterName, StringComparison.OrdinalIgnoreCase)
                        ? " — in use" : "";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"GPU: {g.Name} · {g.DedicatedVideoMemoryMB / 1024.0:0.#} GB · {g.VendorName}{active}");
                }
            }
            var screens = _services.Screens.All;
            sb.AppendLine(CultureInfo.InvariantCulture, $"Screens: {screens.Count}");
            foreach (var sc in screens)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {sc.Label}: {sc.Bounds.Width}×{sc.Bounds.Height} @ {sc.Scaling:0.##}x{(sc.IsPrimary ? " · primary" : "")}");
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"Lifetime census: {DeskCensus.Take(_services).Describe()}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Folder: {_services.Store.BaseDirectory}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Watchdog: {(_services.State.Watchdog.Enabled ? "on" : "off")} · {HealthMonitor.Summary(DateTime.UtcNow)}");
            var (onBattery, pct) = (s?.OnBattery ?? false, s?.BatteryPct ?? -1);
            sb.AppendLine(CultureInfo.InvariantCulture, $"Power: {(onBattery ? $"battery{(pct >= 0 ? $" {pct}%" : "")}" : "mains/unknown")}");
            if (s is not null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Now: app CPU {P(s.CpuAppPct)} · system CPU {P(s.CpuSystemPct)} · app RAM {P(s.RamAppMB, " MB")} · " +
                              $"VRAM {P(s.VramUsedMB, " MB")}/{P(s.VramTotalMB, " MB")} · GPU {P(s.GpuBusyPct)} · " +
                              $"outputs {s.OutputFps:0} fps ×{s.OutputWindows} · worst frame {s.WorstFrameMs:0.0} ms · " +
                              $"threads {s.Threads} · handles {s.Handles} · disk free {P(s.DiskFreeGB, " GB")}");
            }
            foreach (var advice in Suggestions)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Advice [{advice.Severity}]: {advice.Title} — {advice.Detail}");
            }

            // Round 65.9: the machine as Windows describes it, and the rig against the commissioned one.
            var machine = MachineProbe.Read();
            sb.AppendLine();
            sb.AppendLine("MACHINE (as Windows describes it)");
            if (machine.IsEmpty) sb.AppendLine("not read yet");
            else foreach (var line in machine.Lines) sb.AppendLine(line);
            sb.AppendLine();
            sb.AppendLine("KNOWN GOOD RIG");
            var drift = _services.Actions.RigDriftNow();
            sb.AppendLine(_services.Kernel.KnownGood.Words);
            if (drift is not null) foreach (var line in drift.Words) sb.AppendLine(line);
        }
        catch (Exception ex)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"(support info incomplete: {ex.Message})");
        }
        return sb.ToString();

        static string P(double v, string unit = "%") => v < 0 ? "n/a" : $"{v:0.#}{unit}";
    }

    public void Dispose()
    {
        _timer.Stop();
        if (ReferenceEquals(MediaMemory.Extra, _extra)) MediaMemory.Extra = null;         // the static hook let go of this desk
        foreach (var owner in LedgerOwners) MemoryLedger.Unregister(owner);                // and the ledger's readers, which read through it
        if (OperatingSystem.IsWindows())
        {
            _gpuCounter?.Dispose();
        }
        _process.Dispose();
    }
}
