using Patterns.Core.Model;

namespace Patterns.Core.Services;

// ---------------------------------------------------------------------------------------------
// The administration layer: GPU choice policy, performance history, the health advisor and the
// small pure helpers behind the Admin tab. Everything here is platform-free and unit tested;
// the App layer feeds it real DXGI/Win32 numbers.
// ---------------------------------------------------------------------------------------------

/// <summary>One graphics adapter as enumerated from DXGI (or a test double).</summary>
public sealed record GpuAdapterInfo(
    string Name,
    uint VendorId,
    uint DeviceId,
    long DedicatedVideoMemoryMB,
    long Luid,
    bool IsSoftware)
{
    public const uint VendorNvidia = 0x10DE;
    public const uint VendorAmd = 0x1002;
    public const uint VendorIntel = 0x8086;
    public const uint VendorMicrosoft = 0x1414; // Basic Render Driver

    /// <summary>Discrete-class vendor — used only as a tie-break bonus, VRAM decides first.</summary>
    public bool IsDiscreteVendor => VendorId is VendorNvidia or VendorAmd;

    public string VendorName => VendorId switch
    {
        VendorNvidia => "NVIDIA",
        VendorAmd => "AMD",
        VendorIntel => "Intel",
        VendorMicrosoft => "Software",
        _ => "GPU",
    };
}

/// <summary>
/// Which GPU the render engine (and, via the Windows per-app preference, video decode) runs on.
/// </summary>
public static class GpuSelector
{
    /// <summary>
    /// The best adapter for a show: most dedicated video memory wins, with a bonus for
    /// discrete vendors so a small dGPU still beats a large shared-memory iGPU. Software
    /// adapters lose to any hardware. -1 when the list is empty.
    /// </summary>
    public static int ChooseBest(IReadOnlyList<GpuAdapterInfo> gpus)
    {
        var best = -1;
        long bestScore = long.MinValue;
        for (var i = 0; i < gpus.Count; i++)
        {
            var score = Score(gpus[i]);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }

    /// <summary>The lowest-power hardware adapter (the integrated GPU on a laptop).</summary>
    public static int ChoosePowerSaving(IReadOnlyList<GpuAdapterInfo> gpus)
    {
        var pick = -1;
        long pickScore = long.MaxValue;
        for (var i = 0; i < gpus.Count; i++)
        {
            if (gpus[i].IsSoftware) continue;
            var score = Score(gpus[i]);
            if (score < pickScore)
            {
                pickScore = score;
                pick = i;
            }
        }
        return pick >= 0 ? pick : ChooseBest(gpus);
    }

    private static long Score(GpuAdapterInfo g)
    {
        if (g.IsSoftware) return long.MinValue + 1;
        return g.DedicatedVideoMemoryMB + (g.IsDiscreteVendor ? 8192 : 0);
    }

    /// <summary>
    /// The adapter index the settings ask for, or -1 for "no override" (let Windows decide).
    /// A saved adapter name that is no longer present falls back to the best adapter, so a
    /// show file moved to another machine still gets a sensible GPU.
    /// </summary>
    public static int Resolve(GraphicsConfig config, IReadOnlyList<GpuAdapterInfo> gpus)
    {
        if (gpus.Count == 0) return -1;
        switch (config.Preference)
        {
            case GpuPreferenceKind.PowerSaving:
                return ChoosePowerSaving(gpus);
            case GpuPreferenceKind.LetWindowsDecide:
                return -1;
            case GpuPreferenceKind.Specific:
                for (var i = 0; i < gpus.Count; i++)
                {
                    if (string.Equals(gpus[i].Name, config.AdapterName, StringComparison.OrdinalIgnoreCase)) return i;
                }
                return ChooseBest(gpus);
            default:
                return ChooseBest(gpus);
        }
    }

    /// <summary>Find an adapter by its 8-byte LUID (as handed to the Avalonia adapter callback).</summary>
    public static int MatchLuid(byte[]? luid, IReadOnlyList<GpuAdapterInfo> gpus)
    {
        if (luid is not { Length: 8 }) return -1;
        var value = BitConverter.ToInt64(luid, 0);
        for (var i = 0; i < gpus.Count; i++)
        {
            if (gpus[i].Luid == value) return i;
        }
        return -1;
    }

    /// <summary>
    /// The value for Windows' per-app GPU preference (HKCU …\DirectX\UserGpuPreferences):
    /// "GpuPreference=2;" high performance, "1" power saving, "0" let Windows decide,
    /// "" = remove the entry. Kept in step with the chosen adapter so in-process video
    /// decode (libVLC) lands on the same GPU as the renderer.
    /// </summary>
    public static string RegistryValue(GraphicsConfig config, IReadOnlyList<GpuAdapterInfo> gpus)
    {
        switch (config.Preference)
        {
            case GpuPreferenceKind.PowerSaving: return "GpuPreference=1;";
            case GpuPreferenceKind.LetWindowsDecide: return "";
            case GpuPreferenceKind.Specific:
                var chosen = Resolve(config, gpus);
                if (chosen < 0 || gpus.Count == 0) return "";
                if (chosen == ChooseBest(gpus)) return "GpuPreference=2;";
                if (chosen == ChoosePowerSaving(gpus)) return "GpuPreference=1;";
                return "GpuPreference=0;";
            default: return "GpuPreference=2;";
        }
    }
}

/// <summary>One second of performance numbers. Unknown values are -1 (never 0, which is a reading).</summary>
public sealed record MetricSample
{
    public DateTime Utc { get; init; }
    public double CpuAppPct { get; init; } = -1;
    public double CpuSystemPct { get; init; } = -1;
    public double RamAppMB { get; init; } = -1;
    public double RamSystemPct { get; init; } = -1;
    public double RamUsedMB { get; init; } = -1;
    public double RamTotalMB { get; init; } = -1;
    public double VramUsedMB { get; init; } = -1;
    public double VramTotalMB { get; init; } = -1;
    /// <summary>This process's 3D-engine GPU utilisation, when the counters exist.</summary>
    public double GpuBusyPct { get; init; } = -1;
    public double PreviewFps { get; init; }
    public double OutputFps { get; init; }
    public int OutputWindows { get; init; }
    public double WorstFrameMs { get; init; }
    public int SlowFrames { get; init; }
    public int Threads { get; init; }
    public int Handles { get; init; }
    public double GcPausePct { get; init; } = -1;
    public double DiskFreeGB { get; init; } = -1;
    public bool OnBattery { get; init; }
    public int BatteryPct { get; init; } = -1;
    public long Faults { get; init; }
}

/// <summary>
/// Rolling performance history: one sample a second for the last ten minutes, and a
/// 30-second aggregate for the last 24 hours — enough to see a show day without
/// growing without bound (≈3,500 small records at full depth).
/// </summary>
public sealed class MetricsHistory
{
    public const int RecentCapacity = 600;    // 10 min at 1/s
    public const int LongTermCapacity = 2880; // 24 h at 1/30s
    public const int AggregateEvery = 30;

    private readonly List<MetricSample> _recent = new();
    private readonly List<MetricSample> _longTerm = new();
    private readonly List<MetricSample> _pending = new();

    public IReadOnlyList<MetricSample> Recent => _recent;
    public IReadOnlyList<MetricSample> LongTerm => _longTerm;

    public void Add(MetricSample sample)
    {
        _recent.Add(sample);
        if (_recent.Count > RecentCapacity) _recent.RemoveAt(0);

        _pending.Add(sample);
        if (_pending.Count >= AggregateEvery)
        {
            _longTerm.Add(Aggregate(_pending));
            _pending.Clear();
            if (_longTerm.Count > LongTermCapacity) _longTerm.RemoveAt(0);
        }
    }

    /// <summary>Averages the window (max for the worst frame, sum for slow-frame counts).</summary>
    public static MetricSample Aggregate(IReadOnlyList<MetricSample> window)
    {
        var last = window[^1];
        return last with
        {
            CpuAppPct = Avg(window, s => s.CpuAppPct),
            CpuSystemPct = Avg(window, s => s.CpuSystemPct),
            RamAppMB = Avg(window, s => s.RamAppMB),
            RamSystemPct = Avg(window, s => s.RamSystemPct),
            VramUsedMB = Avg(window, s => s.VramUsedMB),
            GpuBusyPct = Avg(window, s => s.GpuBusyPct),
            PreviewFps = Avg(window, s => s.PreviewFps),
            OutputFps = Avg(window, s => s.OutputFps),
            WorstFrameMs = window.Max(s => s.WorstFrameMs),
            SlowFrames = window.Sum(s => s.SlowFrames),
        };
    }

    /// <summary>Average over the last <paramref name="seconds"/> recent samples; -1 when nothing valid.</summary>
    public double AvgRecent(int seconds, Func<MetricSample, double> pick)
    {
        double sum = 0;
        var n = 0;
        for (var i = Math.Max(0, _recent.Count - seconds); i < _recent.Count; i++)
        {
            var v = pick(_recent[i]);
            if (v < 0) continue;
            sum += v;
            n++;
        }
        return n == 0 ? -1 : sum / n;
    }

    /// <summary>Sum over the last <paramref name="seconds"/> recent samples.</summary>
    public double SumRecent(int seconds, Func<MetricSample, double> pick)
    {
        double sum = 0;
        for (var i = Math.Max(0, _recent.Count - seconds); i < _recent.Count; i++)
        {
            sum += pick(_recent[i]);
        }
        return sum;
    }

    /// <summary>The last N values of one metric, oldest first — sparkline food.</summary>
    public IReadOnlyList<double> Tail(int count, Func<MetricSample, double> pick)
    {
        var start = Math.Max(0, _recent.Count - count);
        var result = new List<double>(_recent.Count - start);
        for (var i = start; i < _recent.Count; i++)
        {
            result.Add(Math.Max(0, pick(_recent[i])));
        }
        return result;
    }

    private static double Avg(IReadOnlyList<MetricSample> window, Func<MetricSample, double> pick)
    {
        double sum = 0;
        var n = 0;
        foreach (var s in window)
        {
            var v = pick(s);
            if (v < 0) continue;
            sum += v;
            n++;
        }
        return n == 0 ? -1 : sum / n;
    }
}

public enum HealthSeverity
{
    Info,
    Advice,
    Warning,
}

/// <summary>One line of operator guidance. Ids are stable so the UI list doesn't churn.</summary>
public sealed record HealthSuggestion(string Id, HealthSeverity Severity, string Title, string Detail);

/// <summary>Facts the advisor needs that aren't in the metric samples themselves.</summary>
public sealed class AdvisorContext
{
    public bool OutputsLive { get; init; }
    /// <summary>Whether continuously-animated content is on air (fps expectations apply).</summary>
    public bool ContentContinuous { get; init; }
    public double TargetFps { get; init; } = 60;
    public bool WatchdogEnabled { get; init; } = true;
    public int WatchdogRestarts { get; init; }
    public bool DiscreteGpuPresent { get; init; }
    public bool UsingDiscreteGpu { get; init; } = true;
    public string BestGpuName { get; init; } = "";
    /// <summary>Why the stream stopped by itself, when it did; "" otherwise.</summary>
    public string StreamError { get; init; } = "";
}

/// <summary>
/// Turns the performance history into plain-language suggestions: what's wrong, why it
/// matters mid-show, and the concrete next action. Pure rules — every one unit tested.
/// </summary>
public static class HealthAdvisor
{
    public static IReadOnlyList<HealthSuggestion> Advise(MetricsHistory history, AdvisorContext ctx)
    {
        var list = new List<HealthSuggestion>();
        var now = history.Recent.Count > 0 ? history.Recent[^1] : null;

        var cpuSys = history.AvgRecent(60, s => s.CpuSystemPct);
        if (cpuSys > 85)
        {
            list.Add(new HealthSuggestion("cpu-high", HealthSeverity.Warning,
                $"The computer's CPU is at {cpuSys:0}%",
                "Something on this machine is working very hard. Close background apps (browsers, sync clients, " +
                "updates); if it's Patterns itself, lower particle counts, blur and the number of multiview tiles."));
        }
        else
        {
            var cpuApp = history.AvgRecent(60, s => s.CpuAppPct);
            if (cpuApp > 60)
            {
                list.Add(new HealthSuggestion("cpu-app-high", HealthSeverity.Advice,
                    $"Patterns is using {cpuApp:0}% CPU",
                    "Heavy content — fewer particles, less blur, fewer multiview tiles or a lower stream " +
                    "resolution will bring it down."));
            }
        }

        if (now is { RamSystemPct: > 90 })
        {
            list.Add(new HealthSuggestion("ram-high", HealthSeverity.Warning,
                $"The computer's memory is {now.RamSystemPct:0}% full",
                "Windows will start paging and everything stutters. Close other applications — browsers " +
                "are the usual culprit."));
        }

        // Leak check: compare the first and last 30-second aggregates once ≥10 minutes exist.
        var lt = history.LongTerm;
        if (lt.Count >= 20)
        {
            var early = lt.Take(4).Average(s => s.RamAppMB);
            var late = lt.Skip(lt.Count - 4).Average(s => s.RamAppMB);
            if (early > 0 && late - early > 400)
            {
                var minutes = (lt.Count * MetricsHistory.AggregateEvery) / 60;
                list.Add(new HealthSuggestion("ram-leak", HealthSeverity.Warning,
                    $"Patterns' memory grew {late - early:0} MB in {minutes} min",
                    "That looks like a leak. At the next break, use Restart app below — the watchdog puts the " +
                    "show straight back. Save the show file first, and mention what content was running."));
            }

            var earlyHandles = lt.Take(4).Average(s => (double)s.Handles);
            var lateHandles = lt.Skip(lt.Count - 4).Average(s => (double)s.Handles);
            if (earlyHandles > 0 && lateHandles > earlyHandles * 2 && lateHandles - earlyHandles > 2000)
            {
                list.Add(new HealthSuggestion("handles-growth", HealthSeverity.Warning,
                    "Windows handle count keeps climbing",
                    $"{earlyHandles:0} → {lateHandles:0} — a resource is not being released. Restart at the " +
                    "next break and check patterns.log."));
            }
        }

        if (now is { VramTotalMB: > 0, VramUsedMB: >= 0 } && now.VramUsedMB / now.VramTotalMB > 0.85)
        {
            list.Add(new HealthSuggestion("vram-high", HealthSeverity.Advice,
                $"Graphics memory is {now.VramUsedMB / now.VramTotalMB * 100:0}% full",
                "Close other GPU apps, or reduce output count, multiview tiles and stream resolution. A full " +
                "GPU drops frames without warning."));
        }

        if (ctx.OutputsLive && ctx.ContentContinuous)
        {
            var fps = history.AvgRecent(60, s => s.OutputFps);
            if (history.Recent.Count >= 30 && history.SumRecent(30, s => s.OutputFps) <= 0)
            {
                // The watchdog's heartbeat is the UI thread's, so a render path that has stopped while the
                // desk still answers is exactly what it cannot see — this rule is the eye it lacks.
                list.Add(new HealthSuggestion("outputs-frozen", HealthSeverity.Warning,
                    "Outputs are open but drew no frames for 30 s",
                    "Moving content is up and nothing is being drawn: the render path is stuck while the desk still " +
                    "responds. OUTPUTS OFF and ON first; if the picture stays frozen, RESTART APP (Stability below) puts " +
                    "the show back in seconds."));
            }
            else if (fps > 0 && fps < ctx.TargetFps * 0.8)
            {
                list.Add(new HealthSuggestion("fps-low", HealthSeverity.Advice,
                    $"Outputs are averaging {fps:0} fps",
                    $"Below the {ctx.TargetFps:0} fps this content wants. In order of effect: fewer particles, " +
                    "lower blur, fewer multiview tiles, fewer NDI senders, and check the GPU choice under Graphics."));
            }
            else if (history.SumRecent(60, s => s.SlowFrames) > 12)
            {
                list.Add(new HealthSuggestion("frame-spikes", HealthSeverity.Info,
                    "Occasional slow frames",
                    "Average rate is fine but some frames run long — close background apps and set the Windows " +
                    "power plan to High performance."));
            }
        }

        if (ctx.StreamError.Length > 0)
        {
            list.Add(new HealthSuggestion("stream-stopped", HealthSeverity.Warning,
                $"The stream stopped by itself: {ctx.StreamError}",
                "It switched itself off so a dead encoder could not take the app down, and nothing else noticed. " +
                "Check the destination URL, the key and the bandwidth on the Stream page, then start it again."));
        }

        if (now is { DiskFreeGB: >= 0 and < 2 })
        {
            list.Add(new HealthSuggestion("disk-low", HealthSeverity.Warning,
                $"Only {now.DiskFreeGB:0.0} GB free on this drive",
                "Logs, settings and recovery files live here. Free some space before the show."));
        }

        if (now is { OnBattery: true })
        {
            var pct = now.BatteryPct >= 0 ? $" ({now.BatteryPct}%)" : "";
            list.Add(new HealthSuggestion("battery", HealthSeverity.Warning,
                $"Running on battery{pct}",
                "Plug the computer in. Laptops throttle the CPU and GPU on battery, and a mid-show sleep or " +
                "flat battery takes every screen down."));
        }

        if (ctx.DiscreteGpuPresent && !ctx.UsingDiscreteGpu)
        {
            list.Add(new HealthSuggestion("igpu", HealthSeverity.Advice,
                "Rendering on the integrated GPU",
                $"A {ctx.BestGpuName} is available but not in use. Under Graphics below, choose Best " +
                "performance and restart the app."));
        }

        if (!ctx.WatchdogEnabled)
        {
            list.Add(new HealthSuggestion("watchdog-off", HealthSeverity.Info,
                "The watchdog is off",
                "For show days, turn it on (Stability below): a crash or freeze then self-recovers in seconds."));
        }

        if (now is { Faults: > 0 })
        {
            list.Add(new HealthSuggestion("faults", now.Faults >= 5 ? HealthSeverity.Advice : HealthSeverity.Info,
                $"{now.Faults} render fault{(now.Faults == 1 ? "" : "s")} caught and contained",
                "The show kept running. If the count keeps climbing, check patterns.log and note which content " +
                "was up when it moved."));
        }

        if (ctx.WatchdogRestarts > 0)
        {
            list.Add(new HealthSuggestion("restarts", HealthSeverity.Info,
                $"The watchdog restarted the app {ctx.WatchdogRestarts} time{(ctx.WatchdogRestarts == 1 ? "" : "s")}",
                "The show came back by itself. patterns.watchdog.log records when and why."));
        }

        if (list.Count == 0)
        {
            list.Add(new HealthSuggestion("all-clear", HealthSeverity.Info,
                "All clear",
                "CPU, memory, storage and rendering all look healthy."));
        }

        return list.OrderByDescending(s => s.Severity).ToList();
    }
}

/// <summary>Point math for the little history charts — pure so the shapes are testable.</summary>
public static class SparklinePath
{
    /// <summary>
    /// Maps a series onto a width×height box, oldest left. Auto-ranges from 0 (or the series
    /// minimum when negative) to the series max with 10% headroom; a flat series draws a
    /// mid-height line rather than hugging an edge.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Points(
        IReadOnlyList<double> values, double width, double height, double? fixedMax = null)
    {
        var result = new List<(double, double)>(values.Count);
        if (values.Count == 0 || width <= 0 || height <= 0) return result;

        var max = fixedMax ?? values.Max() * 1.1;
        if (max <= 0) max = 1;

        if (values.Count == 1)
        {
            var y1 = height - Math.Clamp(values[0] / max, 0, 1) * height;
            result.Add((0, y1));
            result.Add((width, y1));
            return result;
        }

        for (var i = 0; i < values.Count; i++)
        {
            var x = i * width / (values.Count - 1);
            var y = height - Math.Clamp(values[i] / max, 0, 1) * height;
            result.Add((x, y));
        }
        return result;
    }

    /// <summary>Average-buckets a long series down to at most <paramref name="maxPoints"/> values.</summary>
    public static IReadOnlyList<double> Downsample(IReadOnlyList<double> values, int maxPoints)
    {
        if (maxPoints <= 0 || values.Count <= maxPoints) return values;
        var result = new List<double>(maxPoints);
        for (var b = 0; b < maxPoints; b++)
        {
            var from = b * values.Count / maxPoints;
            var to = Math.Max(from + 1, (b + 1) * values.Count / maxPoints);
            double sum = 0;
            for (var i = from; i < to; i++) sum += values[i];
            result.Add(sum / (to - from));
        }
        return result;
    }
}

/// <summary>The rolling on-disk record (patterns.metrics.csv) — one line per 30 s, tiny, rotated.</summary>
public static class MetricsCsv
{
    public const string Header =
        "utc,cpuAppPct,cpuSysPct,ramAppMB,ramSysPct,vramUsedMB,gpuBusyPct,outputFps,worstFrameMs,slowFrames,threads,handles,onBattery,faults";

    public static string Line(MetricSample s) => string.Join(',',
        s.Utc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        R(s.CpuAppPct), R(s.CpuSystemPct), R(s.RamAppMB), R(s.RamSystemPct), R(s.VramUsedMB),
        R(s.GpuBusyPct), R(s.OutputFps), R(s.WorstFrameMs),
        s.SlowFrames.ToString(System.Globalization.CultureInfo.InvariantCulture),
        s.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture),
        s.Handles.ToString(System.Globalization.CultureInfo.InvariantCulture),
        s.OnBattery ? "1" : "0",
        s.Faults.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string R(double v)
        => v < 0 ? "" : Math.Round(v, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Per-frame render timing shared by every pipeline, drained once a second by the metrics
/// service. Interlocked counters — the render path adds two atomic ops per frame, nothing more.
/// </summary>
public static class RenderStats
{
    /// <summary>A frame slower than this counts as a visible hitch at show frame rates.</summary>
    public const double SlowFrameMs = 25;

    private static long _previewFrames;
    private static long _outputFrames;
    private static long _slowFrames;
    private static long _worstTenthsMs;
    private static long _outputSinkBits; // bit per SinkIndex (0–63) seen since the last drain

    public static void Record(SinkKind kind, int sinkIndex, double frameMs)
    {
        if (kind == SinkKind.Preview)
        {
            Interlocked.Increment(ref _previewFrames);
        }
        else if (kind == SinkKind.Output)
        {
            Interlocked.Increment(ref _outputFrames);
            var bit = 1L << Math.Clamp(sinkIndex, 0, 63);
            long seen;
            do
            {
                seen = Interlocked.Read(ref _outputSinkBits);
            }
            while ((seen & bit) == 0 && Interlocked.CompareExchange(ref _outputSinkBits, seen | bit, seen) != seen);
        }
        else
        {
            return; // NDI/thumbnail sinks pace themselves — not a smoothness signal
        }

        if (frameMs > SlowFrameMs) Interlocked.Increment(ref _slowFrames);
        var tenths = (long)(frameMs * 10);
        long worst;
        do
        {
            worst = Interlocked.Read(ref _worstTenthsMs);
        }
        while (tenths > worst && Interlocked.CompareExchange(ref _worstTenthsMs, tenths, worst) != worst);
    }

    /// <summary>Frame counts since the last drain, normalised by the elapsed seconds.</summary>
    public static (double PreviewFps, double OutputFps, int OutputWindows, double WorstMs, int Slow) Drain(double elapsedSeconds)
    {
        var seconds = Math.Max(0.25, elapsedSeconds);
        var preview = Interlocked.Exchange(ref _previewFrames, 0) / seconds;
        var frames = Interlocked.Exchange(ref _outputFrames, 0);
        var bits = Interlocked.Exchange(ref _outputSinkBits, 0);
        var windows = System.Numerics.BitOperations.PopCount((ulong)bits);
        var worst = Interlocked.Exchange(ref _worstTenthsMs, 0) / 10.0;
        var slow = (int)Interlocked.Exchange(ref _slowFrames, 0);
        var perWindow = windows > 0 ? frames / seconds / windows : 0;
        return (preview, perWindow, windows, worst, slow);
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _previewFrames, 0);
        Interlocked.Exchange(ref _outputFrames, 0);
        Interlocked.Exchange(ref _slowFrames, 0);
        Interlocked.Exchange(ref _worstTenthsMs, 0);
        Interlocked.Exchange(ref _outputSinkBits, 0);
    }
}
