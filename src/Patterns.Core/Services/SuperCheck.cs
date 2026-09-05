using System.Text;

namespace Patterns.Core.Services;

// ---------------------------------------------------------------------------------------------
// The one-button super-check: every fact the App layer can gather about the machine and the
// show, turned into rows with a light, a grade of what level of show the hardware is good for,
// and one headline. Pure and unit tested; the App feeds it real numbers.
// ---------------------------------------------------------------------------------------------

/// <summary>A row's light: green is fine, amber wants a look, red needs fixing, grey is unknown or not in use.</summary>
public enum CheckLight
{
    Grey,
    Green,
    Amber,
    Red,
}

/// <summary>One line of the report: where it sits, what it checked, the light, the value and a note.</summary>
public sealed record CheckRow(string Section, string Item, CheckLight Light, string Value, string Note = "");

/// <summary>One display as the check sees it.</summary>
public sealed record CheckDisplay(string Label, int Width, int Height, double Scaling, bool Primary, bool Enabled, bool Planned, int RefreshHz = -1);

/// <summary>What level of show the hardware is good for, from a points score; the live numbers still decide.</summary>
public sealed record ShowLevel(string Name, string Detail, int Score, IReadOnlyList<string> Reasons);

/// <summary>The finished report.</summary>
public sealed record CheckReport(DateTime GeneratedUtc, IReadOnlyList<CheckRow> Rows, ShowLevel Level, CheckLight Overall, string Headline);

/// <summary>Everything the App gathers for a check. -1, empty or null means unknown.</summary>
public sealed class CheckFacts
{
    public string AppVersion { get; init; } = "";
    public string Os { get; init; } = "";
    public string Machine { get; init; } = "";
    public string CpuName { get; init; } = "";
    public int CpuThreads { get; init; } = -1;
    public double RamTotalMB { get; init; } = -1;
    public double RamUsedPct { get; init; } = -1;
    public double DiskFreeGB { get; init; } = -1;
    public bool OnBattery { get; init; }
    public int BatteryPct { get; init; } = -1;
    public double UptimeSeconds { get; init; } = -1;
    public double CpuSystemPct { get; init; } = -1;
    public double CpuAppPct { get; init; } = -1;

    public IReadOnlyList<GpuAdapterInfo> Gpus { get; init; } = Array.Empty<GpuAdapterInfo>();
    public string ActiveGpu { get; init; } = "";
    public bool UsingBestGpu { get; init; } = true;
    public double VramUsedMB { get; init; } = -1;
    public double VramTotalMB { get; init; } = -1;
    public double GpuBusyPct { get; init; } = -1;

    public IReadOnlyList<CheckDisplay> Displays { get; init; } = Array.Empty<CheckDisplay>();

    public bool OutputsLive { get; init; }
    public int OutputWindows { get; init; } = -1;
    public double OutputFps { get; init; } = -1;
    public double TargetFps { get; init; } = 60;
    public double WorstFrameMs { get; init; } = -1;
    public int SlowFrames { get; init; } = -1;
    public int Faults { get; init; } = -1;
    public bool WatchdogEnabled { get; init; } = true;
    public int WatchdogRestarts { get; init; }

    public bool NdiRuntime { get; init; }
    public int NdiSendersConfigured { get; init; }
    public int NdiSendersActive { get; init; }
    public IReadOnlyList<string> NdiSenderLines { get; init; } = Array.Empty<string>();

    public bool StreamActive { get; init; }
    public int StreamDestinations { get; init; }
    public string StreamStatus { get; init; } = "";

    public int AudioOutputDevices { get; init; } = -1;
    public string AudioStatus { get; init; } = "";
    public string ToneStatus { get; init; } = "";

    public bool RemoteEnabled { get; init; }
    public string RemoteUrl { get; init; } = "";

    public bool VideoPlayback { get; init; }
    public string VideoNote { get; init; } = "";

    public IReadOnlyList<HealthSuggestion> Advice { get; init; } = Array.Empty<HealthSuggestion>();
}

/// <summary>The rules: facts to rows, rows to a light, hardware to a level.</summary>
public static class SuperCheck
{
    public const string FileName = "patterns.supercheck.txt";

    public static CheckReport Run(CheckFacts f, DateTime? generatedUtc = null)
    {
        var rows = new List<CheckRow>();
        Machine(f, rows);
        Graphics(f, rows);
        Displays(f, rows);
        Show(f, rows);
        Ndi(f, rows);
        Stream(f, rows);
        Audio(f, rows);
        Remote(f, rows);
        Video(f, rows);
        AdviceRows(f, rows);
        var level = Grade(f);
        rows.Add(new CheckRow("LEVEL", "Expected show", CheckLight.Grey, level.Name, level.Detail));
        var overall = Overall(rows);
        return new CheckReport(generatedUtc ?? DateTime.UtcNow, rows, level, overall, Headline(overall, level));
    }

    /// <summary>The worst light on the report; grey rows do not count, and a report with nothing lit is green.</summary>
    public static CheckLight Overall(IReadOnlyList<CheckRow> rows)
    {
        var overall = CheckLight.Green;
        foreach (var row in rows)
        {
            if (row.Light == CheckLight.Grey) continue;
            if (row.Light > overall) overall = row.Light;
        }
        return overall;
    }

    public static string Headline(CheckLight overall, ShowLevel level) => overall switch
    {
        CheckLight.Red => $"Attention needed — {level.Name}: {level.Detail}",
        CheckLight.Amber => $"Ready, with cautions — {level.Name}: {level.Detail}",
        _ => $"All clear — {level.Name}: {level.Detail}",
    };

    /// <summary>
    /// Points from the hardware: threads, memory, the best graphics card, minus a card left idle
    /// or a battery. The level is what to expect; the live rows say what is happening.
    /// </summary>
    public static ShowLevel Grade(CheckFacts f)
    {
        var score = 0;
        var reasons = new List<string>();

        if (f.CpuThreads >= 12) score += 3;
        else if (f.CpuThreads >= 8) score += 2;
        else if (f.CpuThreads >= 4) score += 1;
        else if (f.CpuThreads > 0) reasons.Add("few CPU threads");
        else score += 1;

        var ramGB = f.RamTotalMB / 1024.0;
        if (f.RamTotalMB < 0) score += 1;
        else if (ramGB >= 32) score += 3;
        else if (ramGB >= 16) score += 2;
        else if (ramGB >= 8) score += 1;
        else reasons.Add("under 8 GB of memory");

        var best = GpuSelector.ChooseBest(f.Gpus);
        if (best < 0)
        {
            score += 1; // nothing enumerated (not Windows, or no DXGI): assume a modest card
        }
        else
        {
            var gpu = f.Gpus[best];
            if (gpu.IsSoftware) reasons.Add("no hardware graphics card — software rendering");
            else if (gpu.IsDiscreteVendor && gpu.DedicatedVideoMemoryMB >= 6 * 1024) score += 4;
            else if (gpu.IsDiscreteVendor) score += 3;
            else score += 1;
            if (!gpu.IsSoftware && !f.UsingBestGpu)
            {
                score -= 1;
                reasons.Add($"{gpu.Name} is not the renderer");
            }
        }

        if (f.OnBattery)
        {
            score -= 1;
            reasons.Add("running on battery");
        }

        var (name, detail) = score switch
        {
            >= 8 => ("Big show", "up to 4 × 1080p60 outputs or 2 × 4K, several NDI sends and a stream, particles and fractals at full rate"),
            >= 5 => ("Full show", "2–3 × 1080p60 outputs, an NDI send and a stream; fractals at Balanced, 4K at 30"),
            >= 3 => ("Small show", "1–2 × 1080p outputs; one NDI send or a stream, not both; particles moderate, fractals on Fast"),
            _ => ("Rehearsal", "a single output at 30; NDI and streaming not advised until the hardware improves"),
        };
        return new ShowLevel(name, detail, score, reasons);
    }

    /// <summary>The report as plain text: the headline, the level, every row — for the clipboard and the file beside the exe.</summary>
    public static string ToText(CheckReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PATTERNS SUPER-CHECK");
        sb.AppendLine($"Generated: {report.GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} (local)");
        sb.AppendLine($"[{report.Overall.ToString().ToUpperInvariant()}] {report.Headline}");
        if (report.Level.Reasons.Count > 0) sb.AppendLine($"Level notes: {string.Join("; ", report.Level.Reasons)}");
        var section = "";
        foreach (var row in report.Rows)
        {
            if (row.Section != section)
            {
                section = row.Section;
                sb.AppendLine();
                sb.AppendLine(section);
            }
            sb.Append($"  [{row.Light.ToString().ToUpperInvariant()}] {row.Item}: {row.Value}");
            if (row.Note.Length > 0) sb.Append($" — {row.Note}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ---- the sections ---------------------------------------------------------------------

    private static void Machine(CheckFacts f, List<CheckRow> rows)
    {
        const string s = "MACHINE";
        rows.Add(new CheckRow(s, "Patterns", CheckLight.Grey, f.AppVersion.Length > 0 ? f.AppVersion : "unknown version",
            f.UptimeSeconds >= 0 ? $"running {Span(f.UptimeSeconds)}" : ""));
        rows.Add(new CheckRow(s, "Computer", CheckLight.Grey, Or(f.Machine, "unknown"), f.Os));
        rows.Add(f.CpuThreads switch
        {
            < 0 => new CheckRow(s, "CPU", CheckLight.Grey, Or(f.CpuName, "unknown")),
            >= 8 => new CheckRow(s, "CPU", CheckLight.Green, $"{Or(f.CpuName, "CPU")} · {f.CpuThreads} threads"),
            >= 4 => new CheckRow(s, "CPU", CheckLight.Amber, $"{Or(f.CpuName, "CPU")} · {f.CpuThreads} threads", "fine for one or two outputs; more threads keep video decode and the desk apart"),
            _ => new CheckRow(s, "CPU", CheckLight.Red, $"{Or(f.CpuName, "CPU")} · {f.CpuThreads} threads", "too few threads for a show with video"),
        });
        if (f.CpuSystemPct >= 0)
        {
            rows.Add(new CheckRow(s, "CPU load now", f.CpuSystemPct > 85 ? CheckLight.Red : f.CpuSystemPct > 60 ? CheckLight.Amber : CheckLight.Green,
                $"{f.CpuSystemPct:0}% whole computer{(f.CpuAppPct >= 0 ? $" · {f.CpuAppPct:0}% this app" : "")}",
                f.CpuSystemPct > 60 ? "close other programs before the show" : ""));
        }
        if (f.RamTotalMB > 0)
        {
            var gb = f.RamTotalMB / 1024.0;
            var light = gb >= 16 ? CheckLight.Green : gb >= 8 ? CheckLight.Amber : CheckLight.Red;
            if (f.RamUsedPct > 85) light = CheckLight.Red;
            rows.Add(new CheckRow(s, "Memory", light, $"{gb:0.0} GB{(f.RamUsedPct >= 0 ? $" · {f.RamUsedPct:0}% in use" : "")}",
                f.RamUsedPct > 85 ? "nearly full — close other programs" : gb < 16 ? "16 GB keeps a video-heavy show comfortable" : ""));
        }
        else
        {
            rows.Add(new CheckRow(s, "Memory", CheckLight.Grey, "unknown"));
        }
        rows.Add(f.DiskFreeGB switch
        {
            < 0 => new CheckRow(s, "Disk free", CheckLight.Grey, "unknown"),
            >= 10 => new CheckRow(s, "Disk free", CheckLight.Green, $"{f.DiskFreeGB:0} GB"),
            >= 2 => new CheckRow(s, "Disk free", CheckLight.Amber, $"{f.DiskFreeGB:0.0} GB", "logs, the metrics record and recovery files need room"),
            _ => new CheckRow(s, "Disk free", CheckLight.Red, $"{f.DiskFreeGB:0.0} GB", "the settings and recovery files may fail to write"),
        });
        rows.Add(f.OnBattery
            ? new CheckRow(s, "Power", CheckLight.Red, $"battery{(f.BatteryPct >= 0 ? $" {f.BatteryPct}%" : "")}", "plug in: Windows throttles the GPU and CPU on battery")
            : new CheckRow(s, "Power", CheckLight.Green, "mains"));
    }

    private static void Graphics(CheckFacts f, List<CheckRow> rows)
    {
        const string s = "GRAPHICS";
        if (f.Gpus.Count == 0)
        {
            rows.Add(new CheckRow(s, "Graphics card", CheckLight.Grey, "not enumerated", "the card list needs Windows"));
        }
        var best = GpuSelector.ChooseBest(f.Gpus);
        for (var i = 0; i < f.Gpus.Count; i++)
        {
            var g = f.Gpus[i];
            var active = string.Equals(g.Name, f.ActiveGpu, StringComparison.OrdinalIgnoreCase);
            var value = $"{g.Name} · {g.DedicatedVideoMemoryMB / 1024.0:0.#} GB · {g.VendorName}{(active ? " · renderer" : "")}";
            CheckLight light;
            var note = "";
            if (g.IsSoftware)
            {
                light = active ? CheckLight.Red : CheckLight.Grey;
                if (active) note = "software rendering — no hardware card is driving the show";
            }
            else if (i == best && !f.UsingBestGpu)
            {
                light = CheckLight.Amber;
                note = "the best card is not the renderer — Machine page, GRAPHICS CARD, then restart";
            }
            else if (active || i == best)
            {
                light = CheckLight.Green;
            }
            else
            {
                light = CheckLight.Grey;
            }
            rows.Add(new CheckRow(s, i == best ? "Best card" : "Card", light, value, note));
        }
        if (f.VramTotalMB > 0 && f.VramUsedMB >= 0)
        {
            var pct = 100.0 * f.VramUsedMB / f.VramTotalMB;
            rows.Add(new CheckRow(s, "Video memory", pct > 85 ? CheckLight.Amber : CheckLight.Green, $"{f.VramUsedMB:0} of {f.VramTotalMB:0} MB in use",
                pct > 85 ? "nearly full — fewer 4K sources, or a smaller particle count" : ""));
        }
        if (f.GpuBusyPct >= 0)
        {
            rows.Add(new CheckRow(s, "GPU load now", f.GpuBusyPct > 85 ? CheckLight.Amber : CheckLight.Green, $"{f.GpuBusyPct:0}%",
                f.GpuBusyPct > 85 ? "the card is near its limit — a lower master rate or Fast fractals keep the headroom" : ""));
        }
    }

    private static void Displays(CheckFacts f, List<CheckRow> rows)
    {
        const string s = "DISPLAYS";
        var real = f.Displays.Count(d => !d.Planned);
        var planned = f.Displays.Count - real;
        rows.Add(new CheckRow(s, "Connected", real == 0 ? CheckLight.Amber : CheckLight.Green,
            $"{real} display{(real == 1 ? "" : "s")}{(planned > 0 ? $" · {planned} planned" : "")}",
            real == 0 ? "no display is attached — outputs cannot open" : ""));
        foreach (var d in f.Displays)
        {
            var mode = $"{d.Width}×{d.Height}{(d.RefreshHz > 0 ? $" @ {d.RefreshHz} Hz" : "")}";
            if (d.Planned)
            {
                rows.Add(new CheckRow(s, d.Label, CheckLight.Amber, $"{mode} · planned", "waiting for a display — adopt it on the Screens page when it is attached"));
                continue;
            }
            var value = $"{mode}{(d.Scaling != 1 ? $" · {d.Scaling * 100:0}% scaling" : "")}{(d.Primary ? " · primary" : "")} · {(d.Enabled ? "output on" : "output off")}";
            var light = d.Enabled ? CheckLight.Green : CheckLight.Grey;
            var note = "";
            if (d.Enabled && d.RefreshHz > 0 && f.TargetFps > 0 && d.RefreshHz < f.TargetFps - 0.5)
            {
                light = CheckLight.Amber;
                note = $"the display refreshes at {d.RefreshHz} Hz but the show asks for {f.TargetFps:0} — pick a mode on the Screens page or lower the master rate";
            }
            rows.Add(new CheckRow(s, d.Label, light, value, note));
        }
    }

    private static void Show(CheckFacts f, List<CheckRow> rows)
    {
        const string s = "SHOW";
        if (!f.OutputsLive)
        {
            rows.Add(new CheckRow(s, "Outputs", CheckLight.Grey, "closed", "OUTPUTS ON opens them; the frame rate reads once they run"));
        }
        else
        {
            rows.Add(new CheckRow(s, "Outputs", CheckLight.Green, $"live · {Math.Max(0, f.OutputWindows)} window{(f.OutputWindows == 1 ? "" : "s")}"));
            if (f.OutputFps >= 0 && f.TargetFps > 0)
            {
                var ratio = f.OutputFps / f.TargetFps;
                rows.Add(new CheckRow(s, "Frame rate", ratio >= 0.95 ? CheckLight.Green : ratio >= 0.8 ? CheckLight.Amber : CheckLight.Red,
                    $"{f.OutputFps:0.#} of {f.TargetFps:0} fps",
                    ratio < 0.95 ? "the outputs are dropping frames — see the advice below" : ""));
            }
            if (f.WorstFrameMs >= 0)
            {
                rows.Add(new CheckRow(s, "Worst frame", f.WorstFrameMs > 50 ? CheckLight.Amber : CheckLight.Green, $"{f.WorstFrameMs:0.0} ms{(f.SlowFrames > 0 ? $" · {f.SlowFrames} slow" : "")}",
                    f.WorstFrameMs > 50 ? "a visible stutter — a background task or a heavy source" : ""));
            }
        }
        if (f.Faults > 0) rows.Add(new CheckRow(s, "Render faults", CheckLight.Amber, $"{f.Faults} this session", "contained per frame; the log says which pattern"));
        else if (f.Faults == 0) rows.Add(new CheckRow(s, "Render faults", CheckLight.Green, "none"));
        rows.Add(f.WatchdogEnabled
            ? new CheckRow(s, "Watchdog", f.WatchdogRestarts > 0 ? CheckLight.Amber : CheckLight.Green, f.WatchdogRestarts > 0 ? $"on · {f.WatchdogRestarts} restart(s)" : "on",
                f.WatchdogRestarts > 0 ? "it restarted the app — see patterns.watchdog.log" : "")
            : new CheckRow(s, "Watchdog", CheckLight.Amber, "off", "a crash would end the show — switch it on below"));
    }

    private static void Ndi(CheckFacts f, List<CheckRow> rows)
    {
        const string s = "NDI";
        if (f.NdiSendersConfigured == 0)
        {
            rows.Add(new CheckRow(s, "Sends", CheckLight.Grey, "none configured", f.NdiRuntime ? "runtime found" : "runtime not found — needed only if you add a send"));
            return;
        }
        if (!f.NdiRuntime)
        {
            rows.Add(new CheckRow(s, "Sends", CheckLight.Red, $"{f.NdiSendersConfigured} configured · runtime not found", "install the NDI runtime, or the sends stay silent"));
            return;
        }
        rows.Add(new CheckRow(s, "Sends", f.NdiSendersActive == f.NdiSendersConfigured ? CheckLight.Green : CheckLight.Amber,
            $"{f.NdiSendersActive} of {f.NdiSendersConfigured} running",
            f.NdiSendersActive < f.NdiSendersConfigured ? "a send that is on but not running: PREP holds them, or its status says why" : ""));
        foreach (var line in f.NdiSenderLines) rows.Add(new CheckRow(s, "Send", CheckLight.Grey, line));
    }

    private static void Stream(CheckFacts f, List<CheckRow> rows)
    {
        const string s = "STREAM";
        if (!f.StreamActive)
        {
            rows.Add(new CheckRow(s, "Stream", CheckLight.Grey, f.StreamDestinations > 0 ? $"off · {f.StreamDestinations} destination(s) set" : "off · no destination set"));
            return;
        }
        var trouble = StatusWords.ReadsAsFailure(f.StreamStatus);
        rows.Add(new CheckRow(s, "Stream", trouble ? CheckLight.Red : CheckLight.Green, $"on · {f.StreamDestinations} destination(s)", f.StreamStatus));
    }

    private static void Audio(CheckFacts f, List<CheckRow> rows)
    {
        const string s = "AUDIO";
        rows.Add(f.AudioOutputDevices switch
        {
            < 0 => new CheckRow(s, "Output devices", CheckLight.Grey, "unknown", "the device list needs Windows audio"),
            0 => new CheckRow(s, "Output devices", CheckLight.Amber, "none", "no playback device — the audio track, stingers and the tone have nowhere to go"),
            _ => new CheckRow(s, "Output devices", CheckLight.Green, $"{f.AudioOutputDevices} found"),
        });
        if (f.AudioStatus.Length > 0) rows.Add(new CheckRow(s, "Audio track", StatusWords.ReadsAsFailure(f.AudioStatus) ? CheckLight.Amber : CheckLight.Grey, f.AudioStatus));
        if (f.ToneStatus.Length > 0) rows.Add(new CheckRow(s, "Tone", CheckLight.Grey, f.ToneStatus));
    }

    private static void Remote(CheckFacts f, List<CheckRow> rows)
    {
        rows.Add(f.RemoteEnabled
            ? new CheckRow("REMOTE", "Remote control", CheckLight.Green, f.RemoteUrl.Length > 0 ? f.RemoteUrl : "on")
            : new CheckRow("REMOTE", "Remote control", CheckLight.Grey, "off", "the phone page, Companion and the tablet need it on"));
    }

    private static void Video(CheckFacts f, List<CheckRow> rows)
    {
        rows.Add(f.VideoPlayback
            ? new CheckRow("VIDEO", "Playback", CheckLight.Green, "libVLC ready")
            : new CheckRow("VIDEO", "Playback", CheckLight.Amber, "libVLC not available", f.VideoNote.Length > 0 ? f.VideoNote : "video files, capture, clips and the stream need the full build"));
    }

    private static void AdviceRows(CheckFacts f, List<CheckRow> rows)
    {
        foreach (var a in f.Advice)
        {
            var light = a.Severity switch
            {
                HealthSeverity.Warning => CheckLight.Red,
                HealthSeverity.Advice => CheckLight.Amber,
                _ => CheckLight.Green,
            };
            rows.Add(new CheckRow("ADVICE", a.Title, light, a.Detail));
        }
    }

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string Span(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{t.Minutes} min" : $"{t.Seconds} s";
    }
}
