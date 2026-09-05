namespace Patterns.Core.Services;

/// <summary>
/// One tile of the Machine page's HEALTH AT A GLANCE: what it watches, its light, a big value, a
/// line under it, and — when the value is a share of something — a bar from 0 to 1.
/// </summary>
public sealed record DashboardTile(string Id, string Title, CheckLight Light, string Value, string Detail, double Fraction = -1)
{
    public bool HasBar => Fraction >= 0;
}

/// <summary>The dashboard's one-line verdict: the worst light, a headline naming what set it, and a line under it.</summary>
public sealed record DashboardVerdict(CheckLight Light, string Headline, string Detail);

/// <summary>
/// The machine at a glance — the super-check's facts and the live sample turned into twelve tiles
/// with a light each, and one verdict over them and the advice. Pure, so the desk, STATE and the
/// tests read the same lights; the thresholds are the super-check's.
/// </summary>
public static class HealthDashboard
{
    public static IReadOnlyList<DashboardTile> Tiles(CheckFacts f, MetricSample? now = null)
    {
        return new[]
        {
            Outputs(f, now),
            Render(f, now),
            Cpu(f, now),
            Memory(f, now),
            Gpu(f, now),
            Ndi(f),
            Stream(f),
            Audio(f),
            Remote(f),
            Watchdog(f),
            Power(f, now),
            Disk(f, now),
        };
    }

    /// <summary>The worst light on the wall; grey tiles do not count, and a wall with nothing lit is green.</summary>
    public static CheckLight Overall(IReadOnlyList<DashboardTile> tiles)
    {
        var overall = CheckLight.Green;
        foreach (var tile in tiles)
        {
            if (tile.Light == CheckLight.Grey) continue;
            if (tile.Light > overall) overall = tile.Light;
        }
        return overall;
    }

    /// <summary>The headline over the wall: the worst of the tiles and the advice, naming the tiles that set it and counting what is below.</summary>
    public static DashboardVerdict Verdict(IReadOnlyList<DashboardTile> tiles, IReadOnlyList<HealthSuggestion> advice)
    {
        var light = Overall(tiles);
        var warnings = advice.Count(a => a.Severity == HealthSeverity.Warning);
        var advices = advice.Count(a => a.Severity == HealthSeverity.Advice);
        if (warnings > 0 && light < CheckLight.Red) light = CheckLight.Red;
        else if (advices > 0 && light < CheckLight.Amber) light = CheckLight.Amber;

        var lit = tiles.Where(t => t.Light == CheckLight.Red).Concat(tiles.Where(t => t.Light == CheckLight.Amber)).Select(t => t.Title).ToList();
        var named = lit.Count > 0 ? " — " + string.Join(", ", lit) : "";
        var below = (warnings, advices) switch
        {
            (0, 0) => "",
            (> 0, 0) => $"{warnings} warning{(warnings == 1 ? "" : "s")} below",
            (0, > 0) => $"{advices} suggestion{(advices == 1 ? "" : "s")} below",
            _ => $"{warnings} warning{(warnings == 1 ? "" : "s")} and {advices} suggestion{(advices == 1 ? "" : "s")} below",
        };

        switch (light)
        {
            case CheckLight.Red:
                return new DashboardVerdict(light, "Attention needed" + named,
                    below.Length > 0 ? $"{below} — the first thing to do is at the top of the list." : "a red tile says what.");
            case CheckLight.Amber:
                return new DashboardVerdict(light, "Ready, with cautions" + named,
                    below.Length > 0 ? $"{below}." : "an amber tile says what.");
            default:
            {
                var outputs = tiles.FirstOrDefault(t => t.Id == "outputs");
                var detail = outputs is null ? ""
                    : outputs.Light == CheckLight.Grey ? "outputs closed — OUTPUTS ON when the rig is cabled."
                    : $"outputs {outputs.Value}, {outputs.Detail}.";
                return new DashboardVerdict(CheckLight.Green, "All clear", detail);
            }
        }
    }

    /// <summary>"up 2 h 05 min", "up 12 min", "up 40 s" — "" when the uptime is unknown.</summary>
    public static string Uptime(double seconds)
    {
        if (seconds < 0) return "";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"up {(int)t.TotalHours} h {t.Minutes:00} min";
        if (t.TotalMinutes >= 1) return $"up {t.Minutes} min";
        return $"up {t.Seconds} s";
    }

    // ---- the tiles ----------------------------------------------------------------------

    private static DashboardTile Outputs(CheckFacts f, MetricSample? now)
    {
        if (!f.OutputsLive) return new DashboardTile("outputs", "OUTPUTS", CheckLight.Grey, "closed", "OUTPUTS ON opens them");
        var windows = now?.OutputWindows ?? f.OutputWindows;
        var fps = now is not null ? now.OutputFps : f.OutputFps;
        var target = f.TargetFps > 0 ? f.TargetFps : 60;
        var value = $"{Math.Max(0, windows)} live";
        if (fps < 0) return new DashboardTile("outputs", "OUTPUTS", CheckLight.Green, value, "the frame rate reads once they run");
        var ratio = fps / target;
        var light = ratio >= 0.95 ? CheckLight.Green : ratio >= 0.8 ? CheckLight.Amber : CheckLight.Red;
        return new DashboardTile("outputs", "OUTPUTS", light, value, $"{fps:0} of {target:0} fps", Math.Clamp(ratio, 0, 1));
    }

    private static DashboardTile Render(CheckFacts f, MetricSample? now)
    {
        var faults = now is not null ? (int)Math.Min(int.MaxValue, now.Faults) : Math.Max(0, f.Faults);
        var faultsText = faults > 0 ? $"{faults} fault{(faults == 1 ? "" : "s")} contained" : "no faults";
        if (!f.OutputsLive)
        {
            return faults > 0
                ? new DashboardTile("render", "RENDER", CheckLight.Amber, $"{faults} fault{(faults == 1 ? "" : "s")}", "contained per frame — the log says which pattern")
                : new DashboardTile("render", "RENDER", CheckLight.Grey, "idle", "outputs closed · no faults");
        }
        var worst = Pick(now?.WorstFrameMs, f.WorstFrameMs);
        var slow = now?.SlowFrames ?? f.SlowFrames;
        if (worst < 0) return new DashboardTile("render", "RENDER", faults > 0 ? CheckLight.Amber : CheckLight.Green, "—", faultsText);
        var light = worst > 50 || faults > 0 ? CheckLight.Amber : CheckLight.Green;
        return new DashboardTile("render", "RENDER", light, $"{worst:0} ms", $"worst frame{(slow > 0 ? $" · {slow} slow" : "")} · {faultsText}");
    }

    private static DashboardTile Cpu(CheckFacts f, MetricSample? now)
    {
        var sys = Pick(now?.CpuSystemPct, f.CpuSystemPct);
        var app = Pick(now?.CpuAppPct, f.CpuAppPct);
        if (sys < 0) return new DashboardTile("cpu", "CPU", CheckLight.Grey, "n/a", "no reading yet");
        var light = sys > 85 ? CheckLight.Red : sys > 60 ? CheckLight.Amber : CheckLight.Green;
        return new DashboardTile("cpu", "CPU", light, $"{sys:0}%", app >= 0 ? $"this app {app:0}% · whole computer" : "whole computer", Math.Clamp(sys / 100, 0, 1));
    }

    private static DashboardTile Memory(CheckFacts f, MetricSample? now)
    {
        var pct = Pick(now?.RamSystemPct, f.RamUsedPct);
        var total = Pick(now?.RamTotalMB, f.RamTotalMB);
        if (total <= 0) total = f.RamTotalMB;
        var appMb = now?.RamAppMB ?? -1;
        if (pct < 0) return new DashboardTile("memory", "MEMORY", CheckLight.Grey, "n/a", total > 0 ? $"{total / 1024.0:0.0} GB" : "no reading yet");
        var light = pct > 85 ? CheckLight.Red : pct > 75 ? CheckLight.Amber : CheckLight.Green;
        var detail = (total > 0 ? $"of {total / 1024.0:0.0} GB" : "in use") + (appMb >= 0 ? $" · this app {Mb(appMb)}" : "");
        return new DashboardTile("memory", "MEMORY", light, $"{pct:0}%", detail, Math.Clamp(pct / 100, 0, 1));
    }

    private static DashboardTile Gpu(CheckFacts f, MetricSample? now)
    {
        var busy = Pick(now?.GpuBusyPct, f.GpuBusyPct);
        var used = Pick(now?.VramUsedMB, f.VramUsedMB);
        var total = Pick(now?.VramTotalMB, f.VramTotalMB);
        var vramPct = total > 0 && used >= 0 ? 100.0 * used / total : -1;
        var wrongCard = !f.UsingBestGpu && f.Gpus.Count > 1;
        if (busy < 0 && vramPct < 0)
        {
            return wrongCard
                ? new DashboardTile("gpu", "GPU", CheckLight.Amber, "wrong card", "not on the best card — GRAPHICS CARD below")
                : new DashboardTile("gpu", "GPU", CheckLight.Grey, "n/a", f.ActiveGpu.Length > 0 ? f.ActiveGpu : "no counters on this machine");
        }
        var light = busy > 85 || vramPct > 85 || wrongCard ? CheckLight.Amber : CheckLight.Green;
        var value = busy >= 0 ? $"{busy:0}%" : $"{vramPct:0}%";
        var detail = wrongCard ? "not on the best card — GRAPHICS CARD below"
            : vramPct >= 0 ? $"video memory {used:0} of {total:0} MB" : "busy · video memory n/a";
        return new DashboardTile("gpu", "GPU", light, value, detail, busy >= 0 ? busy / 100 : vramPct / 100);
    }

    private static DashboardTile Ndi(CheckFacts f)
    {
        if (f.NdiSendersConfigured == 0) return new DashboardTile("ndi", "NDI", CheckLight.Grey, "none", f.NdiRuntime ? "no sends set · runtime found" : "no sends set");
        if (!f.NdiRuntime) return new DashboardTile("ndi", "NDI", CheckLight.Red, "no runtime", $"{f.NdiSendersConfigured} configured — install the NDI runtime");
        var running = f.NdiSendersActive >= f.NdiSendersConfigured;
        return new DashboardTile("ndi", "NDI", running ? CheckLight.Green : CheckLight.Amber,
            $"{f.NdiSendersActive} of {f.NdiSendersConfigured}",
            running ? "sends running" : "a send is not running — PREP holds them, or its status says why",
            Math.Clamp((double)f.NdiSendersActive / f.NdiSendersConfigured, 0, 1));
    }

    private static DashboardTile Stream(CheckFacts f)
    {
        if (!f.StreamActive) return new DashboardTile("stream", "STREAM", CheckLight.Grey, "off", f.StreamDestinations > 0 ? $"{f.StreamDestinations} destination{(f.StreamDestinations == 1 ? "" : "s")} set" : "no destination set");
        var trouble = StatusWords.ReadsAsFailure(f.StreamStatus);
        return new DashboardTile("stream", "STREAM", trouble ? CheckLight.Red : CheckLight.Green, trouble ? "trouble" : "on",
            f.StreamStatus.Length > 0 ? f.StreamStatus : $"{f.StreamDestinations} destination{(f.StreamDestinations == 1 ? "" : "s")}");
    }

    private static DashboardTile Audio(CheckFacts f)
    {
        if (f.AudioOutputDevices < 0) return new DashboardTile("audio", "AUDIO", CheckLight.Grey, "n/a", "the device list needs Windows audio");
        if (f.AudioOutputDevices == 0) return new DashboardTile("audio", "AUDIO", CheckLight.Amber, "none", "no playback device — sounds have nowhere to go");
        var value = $"{f.AudioOutputDevices} device{(f.AudioOutputDevices == 1 ? "" : "s")}";
        if (f.AudioStatus.Length > 0 && StatusWords.ReadsAsFailure(f.AudioStatus)) return new DashboardTile("audio", "AUDIO", CheckLight.Amber, value, f.AudioStatus);
        if (!f.SyncLock) return new DashboardTile("audio", "AUDIO", CheckLight.Amber, value, "outputs free-run — the sync lock is off");
        var lag = f.SyncWorstLagMs;
        var light = lag >= 0 && Math.Abs(lag) > 2 ? CheckLight.Amber : CheckLight.Green;
        return new DashboardTile("audio", "AUDIO", light, value, lag >= 0 ? $"master clock · worst lag {lag:0.#} ms" : "master clock locked");
    }

    private static DashboardTile Remote(CheckFacts f)
        => f.RemoteEnabled
            ? new DashboardTile("remote", "REMOTE", CheckLight.Green, "on", f.RemoteUrl.Length > 0 ? f.RemoteUrl : "the phone, Companion, OSC")
            : new DashboardTile("remote", "REMOTE", CheckLight.Grey, "off", "the phone, Companion and OSC need it on");

    private static DashboardTile Watchdog(CheckFacts f)
    {
        if (f.BeaconListening && f.BeaconWatch.StartsWith("MAIN MACHINE", StringComparison.Ordinal))
        {
            return new DashboardTile("watchdog", "WATCHDOG", CheckLight.Red, "MAIN SILENT", f.BeaconWatch);
        }
        if (!f.WatchdogEnabled) return new DashboardTile("watchdog", "WATCHDOG", CheckLight.Amber, "off", "a crash would end the show — STABILITY below");
        if (f.WatchdogRestarts > 0)
        {
            return new DashboardTile("watchdog", "WATCHDOG", CheckLight.Amber, $"{f.WatchdogRestarts} restart{(f.WatchdogRestarts == 1 ? "" : "s")}", "it restarted the app — patterns.watchdog.log says when");
        }
        var detail = f.BeaconSending ? "on · beacon sending"
            : f.BeaconListening ? "on · " + (f.BeaconWatch.Length > 0 ? f.BeaconWatch : "listening for the main machine")
            : "on · no restarts";
        return new DashboardTile("watchdog", "WATCHDOG", CheckLight.Green, "on", detail);
    }

    private static DashboardTile Power(CheckFacts f, MetricSample? now)
    {
        var onBattery = now?.OnBattery ?? f.OnBattery;
        var pct = now is not null ? now.BatteryPct : f.BatteryPct;
        if (!onBattery) return new DashboardTile("power", "POWER", CheckLight.Green, "mains", "");
        return new DashboardTile("power", "POWER", CheckLight.Red, pct >= 0 ? $"battery {pct}%" : "battery", "plug in — Windows throttles on battery", pct >= 0 ? pct / 100.0 : -1);
    }

    private static DashboardTile Disk(CheckFacts f, MetricSample? now)
    {
        var gb = Pick(now?.DiskFreeGB, f.DiskFreeGB);
        return gb switch
        {
            < 0 => new DashboardTile("disk", "DISK", CheckLight.Grey, "n/a", "free space unknown"),
            >= 10 => new DashboardTile("disk", "DISK", CheckLight.Green, $"{gb:0} GB", "free on this drive"),
            >= 2 => new DashboardTile("disk", "DISK", CheckLight.Amber, $"{gb:0.0} GB", "getting tight — logs and recovery need room"),
            _ => new DashboardTile("disk", "DISK", CheckLight.Red, $"{gb:0.0} GB", "the settings and recovery files may fail to write"),
        };
    }

    /// <summary>The live reading when there is one (-1 is "unknown"), else the fact.</summary>
    private static double Pick(double? live, double fallback) => live is >= 0 ? live.Value : fallback;

    private static string Mb(double v) => v >= 1024 ? $"{v / 1024.0:0.0} GB" : $"{v:0} MB";
}
