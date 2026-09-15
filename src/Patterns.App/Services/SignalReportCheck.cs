using System.Text;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// <c>Patterns.exe --signal-report &lt;file&gt;</c> (round 65): without a window, what Windows says it
/// sends every active display — the desktop rectangle, the signal's raster and exact rate, the
/// encoding, the depth and HDR where the driver answers, the connector, the monitor — and the
/// EDID each display presented, parsed: identity, preferred timing, extensions, checksums, hash.
/// The Windows lane runs it so the P/Invoke layouts and the registry read are proven on a real
/// Windows; a rig's first look reads the same file. Exit 0 with a report, whatever the displays;
/// 1 only when the report could not be written.
/// </summary>
public static class SignalReportCheck
{
    public static int Run(string[] args)
    {
        var at = Array.IndexOf(args, "--signal-report");
        var path = at >= 0 && at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal) ? args[at + 1] : null;
        var text = Report();
        Console.Out.Write(text);
        if (path is null) return 0;
        try
        {
            File.WriteAllText(path, text);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"the signal report could not be written to {path}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The report's text: one paragraph per active display path, or the reason there is none.</summary>
    public static string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Patterns signal report · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {Environment.MachineName} · {Environment.OSVersion}");
        if (!DisplayObservation.Supported)
        {
            sb.AppendLine("observation: not on Windows — nothing to ask");
            return sb.ToString();
        }
        IReadOnlyList<SignalObservation> observations;
        try
        {
            observations = DisplayObservation.Snapshot();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"observation: failed — {ex.GetType().Name}: {ex.Message}");
            return sb.ToString();
        }
        sb.AppendLine($"observation: {observations.Count} active display path{(observations.Count == 1 ? "" : "s")}");
        var n = 0;
        foreach (var o in observations)
        {
            n++;
            sb.AppendLine($"[{n}] desktop {o.X},{o.Y} {o.Width}×{o.Height} · signal {SignalTruth.ObservedWords(o)}");
            sb.AppendLine($"    monitor: {(o.Monitor.Length > 0 ? o.Monitor : "unnamed")} · connector: {(o.Connector.Length > 0 ? o.Connector : "unknown")} · EDID ids {o.EdidManufacturerId:X4}/{o.EdidProductCode:X4}");
            sb.AppendLine($"    device path: {(o.DevicePath.Length > 0 ? o.DevicePath : "none")}");
            var edid = EdidReader.For(o);
            if (edid is null)
            {
                sb.AppendLine("    EDID: none read");
                continue;
            }
            sb.AppendLine($"    EDID: {edid.Summary}");
            sb.AppendLine($"    blocks: {string.Join(", ", edid.Blocks)} · checksums {(edid.ChecksumsValid ? "valid" : "BAD")}{(edid.Problems.Count > 0 ? " · problems: " + string.Join("; ", edid.Problems) : "")}");
            foreach (var line in edid.AdvertisedWords.Split('\n')) sb.AppendLine("    advertised: " + line);
        }
        return sb.ToString();
    }
}
