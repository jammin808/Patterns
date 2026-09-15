using System.Globalization;
using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>A graphics adapter as Windows describes it: the card, its memory, the driver that runs it.</summary>
public sealed record GpuFact(string Name, uint VendorId, uint DeviceId, long VramMB, string DriverVersion, string DriverDate, string Provider, bool Software)
{
    public string Vendor => VendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        _ => $"vendor {VendorId:X4}",
    };

    /// <summary>NVIDIA's own number (560.94) from the Windows driver version (32.0.15.6094); the version as it is for the others.</summary>
    public string FriendlyDriver
    {
        get
        {
            if (VendorId != 0x10DE || DriverVersion.Length == 0) return DriverVersion;
            var parts = DriverVersion.Split('.');
            if (parts.Length != 4 || parts[2].Length == 0 || parts[3].Length < 4) return DriverVersion;
            var digits = parts[2][^1..] + parts[3];
            return digits.Length >= 3 ? $"{digits[..^2]}.{digits[^2..]}" : DriverVersion;
        }
    }

    public string Words => $"{Name} · {Vendor}{(VramMB > 0 ? $" · {VramMB} MB" : "")} · driver {(DriverVersion.Length > 0 ? FriendlyDriver : "unknown")}{(DriverDate.Length > 0 ? $" ({DriverDate})" : "")}{(Software ? " · software" : "")}";
}

/// <summary>An audio endpoint as Windows describes it: the device, which way it flows, its shared-mode format.</summary>
public sealed record AudioEndpointFact(string Name, string Id, string Flow, bool IsDefault, int SampleRateHz, int Bits, int Channels)
{
    public string Format => SampleRateHz > 0 ? $"{SampleRateHz / 1000.0:0.#} kHz · {Bits}-bit · {Channels} ch" : "format unknown";

    public string Words => $"{Name} ({Flow}){(IsDefault ? " · default" : "")} · {Format}";
}

/// <summary>A display as Windows sends to it — the observation and the EDID it presents, as identity.</summary>
public sealed record MachineDisplayFact(string Monitor, int X, int Y, int Width, int Height, string Rate, string Connector, string EdidIdentity, string EdidHash, string Encoding, int Bits, string Hdr)
{
    /// <summary>What tells this display from the next: its name, or where it sits on the desktop.</summary>
    public string Key => Monitor.Length > 0 ? Monitor : $"display at {X},{Y}";

    public string Words => $"{Key} · {Width}×{Height}{(Rate.Length > 0 ? $" @ {Rate} Hz" : "")}{(Connector.Length > 0 ? $" · {Connector}" : "")}{(Encoding.Length > 0 ? $" · {Encoding}" : "")}{(Bits > 0 ? $" · {Bits}-bit" : "")}{(Hdr.Length > 0 ? $" · {Hdr}" : "")}{(EdidIdentity.Length > 0 ? $" · EDID {EdidIdentity} {ShortHash(EdidHash)}" : " · no EDID read")}";

    public static string ShortHash(string hash) => hash.Length >= 8 ? hash[..8] : hash;
}

/// <summary>
/// The machine as Windows describes it (round 65.9): the build, Windows itself, the CPU and the
/// memory, every graphics adapter with its driver, every display with its signal and its EDID,
/// every audio endpoint with its format, the power plan, hardware GPU scheduling, Game DVR, the
/// overlay planes — everything the operating system will say about graphics, audio and
/// processing, so Patterns can read it, save it as the commissioned rig and say what moved.
/// </summary>
public sealed record MachineFacts(
    DateTime TakenUtc,
    string Build,
    string Machine,
    string Windows,
    string DotNet,
    string Cpu,
    int Cores,
    double RamGB,
    IReadOnlyList<GpuFact> Gpus,
    IReadOnlyList<MachineDisplayFact> Displays,
    IReadOnlyList<AudioEndpointFact> Audio,
    string PowerPlan,
    bool OnBattery,
    string HardwareScheduling,
    string GameDvr,
    string MultiplaneOverlay,
    IReadOnlyList<string> Notes)
{
    public static readonly MachineFacts Empty = new(DateTime.MinValue, "", "", "", "", "", 0, 0, Array.Empty<GpuFact>(), Array.Empty<MachineDisplayFact>(), Array.Empty<AudioEndpointFact>(), "", false, "", "", "", Array.Empty<string>());

    public bool IsEmpty => TakenUtc == DateTime.MinValue;

    /// <summary>The inventory as lines — the Machine page and the support bundle.</summary>
    public IReadOnlyList<string> Lines => LinesFor(withMachineName: true);

    /// <summary>The inventory as lines; without the machine's name for the assistant's brief, which names nothing that identifies the machine.</summary>
    public IReadOnlyList<string> LinesFor(bool withMachineName)
    {
        var lines = new List<string>();
        if (Build.Length > 0 || DotNet.Length > 0) lines.Add($"Build: {Build}{(DotNet.Length > 0 ? $" · .NET {DotNet}" : "")}");
        if (Windows.Length > 0) lines.Add($"Windows: {Windows}{(withMachineName && Machine.Length > 0 ? $" · {Machine}" : "")}");
        if (Cpu.Length > 0 || Cores > 0) lines.Add($"CPU: {(Cpu.Length > 0 ? Cpu : "unknown")}{(Cores > 0 ? $" · {Cores} logical cores" : "")}{(RamGB > 0 ? $" · RAM {RamGB:0.#} GB" : "")}");
        foreach (var g in Gpus) lines.Add("GPU: " + g.Words);
        if (Gpus.Count == 0 && Windows.Length > 0) lines.Add("GPU: none enumerated");
        foreach (var d in Displays) lines.Add("Display: " + d.Words);
        foreach (var a in Audio.Where(a => a.Flow == "out")) lines.Add("Audio out: " + a.Words);
        foreach (var a in Audio.Where(a => a.Flow == "in")) lines.Add("Audio in: " + a.Words);
        if (PowerPlan.Length > 0 || OnBattery) lines.Add($"Power: {(PowerPlan.Length > 0 ? PowerPlan : "plan unknown")} · {(OnBattery ? "ON BATTERY" : "mains")}");
        if (HardwareScheduling.Length > 0 || GameDvr.Length > 0 || MultiplaneOverlay.Length > 0)
        {
            lines.Add($"GPU scheduling: {Or(HardwareScheduling)} · Game DVR: {Or(GameDvr)} · overlay planes: {Or(MultiplaneOverlay)}");
        }
        foreach (var n in Notes) lines.Add("Note: " + n);
        return lines;

        static string Or(string s) => s.Length > 0 ? s : "unknown";
    }

    /// <summary>One line for a status strip: "NVIDIA RTX A4000 · driver 560.94 · 2 displays · 3 audio outputs · High performance".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            var gpu = Gpus.FirstOrDefault(g => !g.Software) ?? Gpus.FirstOrDefault();
            if (gpu is not null) parts.Add($"{gpu.Name} · driver {(gpu.DriverVersion.Length > 0 ? gpu.FriendlyDriver : "unknown")}");
            parts.Add($"{Displays.Count} display{(Displays.Count == 1 ? "" : "s")}");
            var outs = Audio.Count(a => a.Flow == "out");
            parts.Add($"{outs} audio output{(outs == 1 ? "" : "s")}");
            if (PowerPlan.Length > 0) parts.Add(PowerPlan);
            if (OnBattery) parts.Add("ON BATTERY");
            return string.Join(" · ", parts);
        }
    }
}

public sealed record RigGpu(string Name, string DriverVersion, string DriverDate, long VramMB);

public sealed record RigDisplay(string Key, int Width, int Height, string Rate, string Connector, string EdidIdentity, string EdidHash);

public sealed record RigContract(string Screen, string Words);

/// <summary>
/// The commissioned rig (round 65.9): what the machine, its displays, their EDIDs, the contracts,
/// the audio, the network and the clocks were when the engineer said SAVE KNOWN GOOD. Saved as
/// JSON beside the settings; every boot compares the rig of the day against it.
/// </summary>
public sealed record RigSnapshot
{
    public DateTime TakenUtc { get; init; }
    public string Note { get; init; } = "";
    public string Build { get; init; } = "";
    public string Windows { get; init; } = "";
    public string Cpu { get; init; } = "";
    public IReadOnlyList<RigGpu> Gpus { get; init; } = Array.Empty<RigGpu>();
    public IReadOnlyList<RigDisplay> Displays { get; init; } = Array.Empty<RigDisplay>();
    public IReadOnlyList<RigContract> Contracts { get; init; } = Array.Empty<RigContract>();
    public IReadOnlyList<string> AudioOut { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AudioIn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NdiSenders { get; init; } = Array.Empty<string>();
    public string Bindings { get; init; } = "";
    public string PowerPlan { get; init; } = "";
    public string HardwareScheduling { get; init; } = "";
    public string RenderClock { get; init; } = "";

    /// <summary>The snapshot of the moment, from the machine's facts and what the desk knows of the show.</summary>
    public static RigSnapshot From(MachineFacts m, IEnumerable<RigContract> contracts, IEnumerable<string> ndiSenders, string bindings, string renderClock, string note, DateTime takenUtc)
        => new()
        {
            TakenUtc = takenUtc,
            Note = note ?? "",
            Build = m.Build,
            Windows = m.Windows,
            Cpu = m.Cpu,
            Gpus = m.Gpus.Select(g => new RigGpu(g.Name, g.DriverVersion, g.DriverDate, g.VramMB)).ToList(),
            Displays = m.Displays.Select(d => new RigDisplay(d.Key, d.Width, d.Height, d.Rate, d.Connector, d.EdidIdentity, d.EdidHash)).ToList(),
            Contracts = contracts.OrderBy(c => c.Screen, StringComparer.Ordinal).ToList(),
            AudioOut = m.Audio.Where(a => a.Flow == "out").Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
            AudioIn = m.Audio.Where(a => a.Flow == "in").Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
            NdiSenders = ndiSenders.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            Bindings = bindings ?? "",
            PowerPlan = m.PowerPlan,
            HardwareScheduling = m.HardwareScheduling,
            RenderClock = renderClock ?? "",
        };

    public string ToJson() => JsonSerializer.Serialize(this, JsonUtil.Options);

    public static RigSnapshot? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RigSnapshot>(json, JsonUtil.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>One item of the comparison: the same, or what changed and how.</summary>
public sealed record RigDriftLine(bool Same, string Item, string Words, bool Severe = false);

/// <summary>The rig of the day against the commissioned one: every item as a line, the changes counted.</summary>
public sealed record RigDrift(DateTime KnownUtc, string Note, IReadOnlyList<RigDriftLine> Lines)
{
    public int Changed => Lines.Count(l => !l.Same);

    public bool Same => Changed == 0;

    public string Headline => Same
        ? $"unchanged since {KnownUtc.ToLocalTime():yyyy-MM-dd HH:mm}{(Note.Length > 0 ? $" ({Note})" : "")}"
        : $"{Changed} change{(Changed == 1 ? "" : "s")} since {KnownUtc.ToLocalTime():yyyy-MM-dd HH:mm}{(Note.Length > 0 ? $" ({Note})" : "")}";

    /// <summary>The lines with their marks: "✓ GPU unchanged (…)", "! GPU driver changed …".</summary>
    public IReadOnlyList<string> Words => Lines.Select(l => (l.Same ? "✓ " : "! ") + l.Words).ToList();

    /// <summary>The changed lines alone, for the journal and the brief.</summary>
    public IReadOnlyList<string> Changes => Lines.Where(l => !l.Same).Select(l => l.Words).ToList();

    public static RigDrift Compare(RigSnapshot known, RigSnapshot now)
    {
        var lines = new List<RigDriftLine>();
        Field("build", known.Build, now.Build);
        Field("Windows", known.Windows, now.Windows);
        Field("CPU", known.Cpu, now.Cpu);

        foreach (var g in known.Gpus)
        {
            var seen = now.Gpus.FirstOrDefault(x => x.Name == g.Name);
            if (seen is null)
            {
                lines.Add(new RigDriftLine(false, "GPU", $"GPU gone: {g.Name}", true));
                continue;
            }
            if (seen.DriverVersion != g.DriverVersion) lines.Add(new RigDriftLine(false, "GPU driver", $"GPU driver changed on {g.Name}: {Or(g.DriverVersion)}{Date(g.DriverDate)} → {Or(seen.DriverVersion)}{Date(seen.DriverDate)}"));
            else if (seen.VramMB != g.VramMB && g.VramMB > 0 && seen.VramMB > 0) lines.Add(new RigDriftLine(false, "GPU memory", $"GPU memory changed on {g.Name}: {g.VramMB} MB → {seen.VramMB} MB"));
            else lines.Add(new RigDriftLine(true, "GPU", $"GPU unchanged: {g.Name} · driver {Or(g.DriverVersion)}"));
        }
        foreach (var g in now.Gpus.Where(x => known.Gpus.All(k => k.Name != x.Name))) lines.Add(new RigDriftLine(false, "GPU", $"GPU added: {g.Name} · driver {Or(g.DriverVersion)}"));

        foreach (var d in known.Displays)
        {
            var seen = now.Displays.FirstOrDefault(x => x.Key == d.Key);
            if (seen is null)
            {
                lines.Add(new RigDriftLine(false, "display", $"display missing: {d.Key} ({d.Width}×{d.Height} @ {d.Rate} Hz)", true));
                continue;
            }
            var changes = new List<string>();
            if (seen.Width != d.Width || seen.Height != d.Height) changes.Add($"raster {d.Width}×{d.Height} → {seen.Width}×{seen.Height}");
            if (seen.Rate != d.Rate) changes.Add($"rate {Or(d.Rate)} → {Or(seen.Rate)} Hz");
            if (seen.Connector != d.Connector && d.Connector.Length > 0 && seen.Connector.Length > 0) changes.Add($"connector {d.Connector} → {seen.Connector}");
            if (!string.Equals(seen.EdidHash, d.EdidHash, StringComparison.OrdinalIgnoreCase) && (d.EdidHash.Length > 0 || seen.EdidHash.Length > 0))
            {
                changes.Add(d.EdidHash.Length == 0 ? $"EDID now read ({seen.EdidIdentity} {MachineDisplayFact.ShortHash(seen.EdidHash)})"
                    : seen.EdidHash.Length == 0 ? "EDID no longer read"
                    : $"EDID changed {MachineDisplayFact.ShortHash(d.EdidHash)} → {MachineDisplayFact.ShortHash(seen.EdidHash)}{(seen.EdidIdentity != d.EdidIdentity ? $" ({d.EdidIdentity} → {seen.EdidIdentity})" : "")}");
            }
            lines.Add(changes.Count == 0
                ? new RigDriftLine(true, "display", $"{d.Key} unchanged: {d.Width}×{d.Height} @ {Or(d.Rate)} Hz{(d.EdidHash.Length > 0 ? $" · EDID {MachineDisplayFact.ShortHash(d.EdidHash)}" : "")}")
                : new RigDriftLine(false, "display", $"{d.Key} changed: {string.Join("; ", changes)}"));
        }
        foreach (var d in now.Displays.Where(x => known.Displays.All(k => k.Key != x.Key))) lines.Add(new RigDriftLine(false, "display", $"display added: {d.Key} ({d.Width}×{d.Height} @ {Or(d.Rate)} Hz)"));

        foreach (var c in known.Contracts)
        {
            var seen = now.Contracts.FirstOrDefault(x => x.Screen == c.Screen);
            if (seen is null) lines.Add(new RigDriftLine(false, "contract", $"contract gone: {c.Screen} ({c.Words})"));
            else if (seen.Words != c.Words) lines.Add(new RigDriftLine(false, "contract", $"contract changed on {c.Screen}: {Or(c.Words)} → {Or(seen.Words)}"));
            else lines.Add(new RigDriftLine(true, "contract", $"contract unchanged: {c.Screen} · {Or(c.Words)}"));
        }
        foreach (var c in now.Contracts.Where(x => known.Contracts.All(k => k.Screen != x.Screen))) lines.Add(new RigDriftLine(false, "contract", $"contract added: {c.Screen} ({Or(c.Words)})"));

        Set("audio outputs", known.AudioOut, now.AudioOut);
        Set("audio inputs", known.AudioIn, now.AudioIn);
        Set("NDI senders", known.NdiSenders, now.NdiSenders);
        Field("network bindings", known.Bindings, now.Bindings);
        Field("power plan", known.PowerPlan, now.PowerPlan);
        Field("GPU scheduling", known.HardwareScheduling, now.HardwareScheduling);
        if (Hz(known.RenderClock) is { } a && Hz(now.RenderClock) is { } b)
        {
            lines.Add(Math.Abs(a - b) > 0.5
                ? new RigDriftLine(false, "render clock", $"render clock changed: {a:0.0} → {b:0.0} Hz")
                : new RigDriftLine(true, "render clock", $"render clock unchanged: {b:0.0} Hz"));
        }
        return new RigDrift(known.TakenUtc, known.Note, lines);

        void Field(string item, string was, string is_)
        {
            if (was.Length == 0 && is_.Length == 0) return;
            lines.Add(was == is_
                ? new RigDriftLine(true, item, $"{item} unchanged: {is_}")
                : new RigDriftLine(false, item, $"{item} changed: {Or(was)} → {Or(is_)}"));
        }

        void Set(string item, IReadOnlyList<string> was, IReadOnlyList<string> is_)
        {
            if (was.Count == 0 && is_.Count == 0) return;
            var gone = was.Except(is_, StringComparer.Ordinal).ToList();
            var added = is_.Except(was, StringComparer.Ordinal).ToList();
            if (gone.Count == 0 && added.Count == 0)
            {
                lines.Add(new RigDriftLine(true, item, $"{item} unchanged ({is_.Count})"));
                return;
            }
            var parts = new List<string>();
            if (gone.Count > 0) parts.Add("gone: " + string.Join(", ", gone));
            if (added.Count > 0) parts.Add("added: " + string.Join(", ", added));
            lines.Add(new RigDriftLine(false, item, $"{item} changed — {string.Join("; ", parts)}", gone.Count > 0));
        }

        static string Or(string s) => s.Length > 0 ? s : "unknown";
        static string Date(string d) => d.Length > 0 ? $" ({d})" : "";
        static double? Hz(string words) => double.TryParse(words.Replace("Hz", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }
}
