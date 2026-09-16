using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Patterns.Core.Services;

namespace Patterns.Platform.Windows;

/// <summary>
/// Everything Windows will say about this machine's graphics, audio and processing (round 65.9),
/// read into <see cref="MachineFacts"/>: Windows itself (the edition, the version, the build) from
/// the registry; the CPU, the cores and the memory; every adapter DXGI enumerates with the driver
/// the display class key names for it (version, date, provider — NVIDIA's own number derived);
/// every display path with its signal and the EDID it presents; every audio endpoint, out and
/// in, with its shared-mode format and the default; the active power plan (powrprof); hardware
/// GPU scheduling, Game DVR and the overlay-plane switch from the registry. Best-effort: a probe
/// that fails is a note, never a throw; read at most every half minute; a test hands in facts
/// through <see cref="Source"/>. Off Windows: the OS description, the cores and the displays the
/// observation layer knows, nothing else.
/// <para>
/// The desk thread never waits for Windows: <see cref="Read"/> answers with the reading it keeps
/// and, when that is stale, starts one refresh on a worker — the next ask has it. Only
/// <see cref="ReadNow"/> (SAVE KNOWN GOOD, the bundle, a test) probes in the caller's thread.
/// </para>
/// </summary>
public static class MachineProbe
{
    /// <summary>The facts, when something other than Windows supplies them — the tests.</summary>
    public static Func<MachineFacts>? Source { get; set; }

    /// <summary>How long a reading is kept before Windows is asked again.</summary>
    public static TimeSpan KeptFor { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The build's own version for the facts — the host sets it (the desk's update service knows it); the entry assembly's otherwise.</summary>
    public static Func<string> BuildVersion { get; set; } = () => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "dev";

    private static readonly object Gate = new();
    private static MachineFacts _kept = MachineFacts.Empty;
    private static DateTime _keptAtUtc = DateTime.MinValue;
    private static int _refreshing;

    /// <summary>The kept reading, at once; a stale one is refreshed on a worker for the next ask. Empty until the first probe lands.</summary>
    public static MachineFacts Read()
    {
        if (Source is { } source) return FromSource(source);
        MachineFacts kept;
        bool stale;
        lock (Gate)
        {
            kept = _kept;
            stale = kept.IsEmpty || DateTime.UtcNow - _keptAtUtc >= KeptFor;
        }
        if (stale && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Keep(Probe(DateTime.UtcNow));
                }
                catch (Exception ex)
                {
                    Log.Warn("The machine probe failed.", ex);
                }
                finally
                {
                    Volatile.Write(ref _refreshing, 0);
                }
            });
        }
        return kept;
    }

    /// <summary>The reading of the moment: the kept one while it is fresh, else a probe here and now.</summary>
    public static MachineFacts ReadNow()
    {
        if (Source is { } source) return FromSource(source);
        lock (Gate)
        {
            if (!_kept.IsEmpty && DateTime.UtcNow - _keptAtUtc < KeptFor) return _kept;
        }
        var facts = Probe(DateTime.UtcNow);
        Keep(facts);
        return facts;
    }

    private static MachineFacts FromSource(Func<MachineFacts> source)
    {
        try
        {
            return source();
        }
        catch (Exception)
        {
            return MachineFacts.Empty;
        }
    }

    private static void Keep(MachineFacts facts)
    {
        lock (Gate)
        {
            _kept = facts;
            _keptAtUtc = facts.TakenUtc;
        }
    }

    /// <summary>Drops the kept reading so the next ask probes again (a display re-plugged, a driver installed, a test).</summary>
    public static void Forget()
    {
        lock (Gate)
        {
            _keptAtUtc = DateTime.MinValue;
        }
    }

    private static MachineFacts Probe(DateTime nowUtc)
    {
        var notes = new List<string>();
        var windows = OperatingSystem.IsWindows() ? WindowsWords(notes) : RuntimeInformation.OSDescription;
        var cpu = "";
        double ram = 0;
        var gpus = new List<GpuFact>();
        var audio = new List<AudioEndpointFact>();
        var power = "";
        var battery = false;
        var hags = "";
        var dvr = "";
        var mpo = "";
        try
        {
            cpu = WinRegistry.ReadCpuName();
            if (Win32Perf.TryGetMemoryStatus(out _, out var totalMB, out _)) ram = totalMB / 1024.0;
            battery = Win32Perf.GetPowerStatus().OnBattery;
        }
        catch (Exception ex)
        {
            notes.Add($"CPU / memory probe failed: {ex.Message}");
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                gpus = Gpus(notes);
            }
            catch (Exception ex)
            {
                notes.Add($"GPU probe failed: {ex.Message}");
            }
            try
            {
                audio = AudioEndpoints(notes);
            }
            catch (Exception ex)
            {
                notes.Add($"audio endpoint probe failed: {ex.Message}");
            }
            try
            {
                power = PowerPlan();
            }
            catch (Exception ex)
            {
                notes.Add($"power plan probe failed: {ex.Message}");
            }
            try
            {
                hags = ReadRegistryDword(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") switch { 2 => "on", 1 => "off", null => "not offered by this driver", var v => $"mode {v}" };
                dvr = ReadRegistryDword(@"System\GameConfigStore", "GameDVR_Enabled", user: true) switch { 1 => "on — background recording costs frames; turn it off for a show machine", 0 => "off", _ => "not set" };
                mpo = ReadRegistryDword(@"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode") switch { 5 => "disabled by registry (OverlayTestMode 5)", null => "as Windows decides", var v => $"OverlayTestMode {v}" };
            }
            catch (Exception ex)
            {
                notes.Add($"registry probe failed: {ex.Message}");
            }
        }
        var displays = Displays(notes);
        return new MachineFacts(nowUtc, BuildVersion(), Environment.MachineName, windows, Environment.Version.ToString(), cpu, Environment.ProcessorCount, ram,
            gpus, displays, audio, power, battery, hags, dvr, mpo, notes);
    }

    /// <summary>"Windows 11 Pro 23H2 (build 22631.4317)" from the CurrentVersion key; the OS description when the key does not answer.</summary>
    [SupportedOSPlatform("windows")]
    private static string WindowsWords(List<string> notes)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null) return RuntimeInformation.OSDescription;
            var product = key.GetValue("ProductName") as string ?? "Windows";
            var display = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string ?? "";
            var build = key.GetValue("CurrentBuild") as string ?? key.GetValue("CurrentBuildNumber") as string ?? "";
            var ubr = key.GetValue("UBR");
            if (int.TryParse(build, out var buildNumber) && buildNumber >= 22000 && product.StartsWith("Windows 10", StringComparison.Ordinal)) product = "Windows 11" + product["Windows 10".Length..];   // the key still says 10 on 11
            return $"{product}{(display.Length > 0 ? " " + display : "")}{(build.Length > 0 ? $" (build {build}{(ubr is int u ? "." + u : "")})" : "")}";
        }
        catch (Exception ex)
        {
            notes.Add($"the Windows version key did not read: {ex.Message}");
            return RuntimeInformation.OSDescription;
        }
    }

    /// <summary>DXGI's adapters, each with the driver the display class key names for it.</summary>
    [SupportedOSPlatform("windows")]
    private static List<GpuFact> Gpus(List<string> notes)
    {
        var drivers = DriverEntries(notes);
        var list = new List<GpuFact>();
        foreach (var a in Dxgi.Enumerate())
        {
            var driver = drivers.FirstOrDefault(d => d.VendorId == a.VendorId && d.DeviceId == a.DeviceId)
                ?? drivers.FirstOrDefault(d => string.Equals(d.Description, a.Name, StringComparison.OrdinalIgnoreCase));
            list.Add(new GpuFact(a.Name, a.VendorId, a.DeviceId, a.DedicatedVideoMemoryMB, driver?.Version ?? "", driver?.Date ?? "", driver?.Provider ?? "", a.IsSoftware));
        }
        return list;
    }

    private sealed record DriverEntry(string Description, uint VendorId, uint DeviceId, string Version, string Date, string Provider);

    /// <summary>The display class key's entries: every graphics driver installed, with the PCI ids it matched.</summary>
    [SupportedOSPlatform("windows")]
    private static List<DriverEntry> DriverEntries(List<string> notes)
    {
        var list = new List<DriverEntry>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return list;
            foreach (var name in cls.GetSubKeyNames())
            {
                if (name.Length != 4 || !int.TryParse(name, out _)) continue;
                using var entry = cls.OpenSubKey(name);
                if (entry is null) continue;
                var description = entry.GetValue("DriverDesc") as string ?? "";
                var matching = entry.GetValue("MatchingDeviceId") as string ?? "";
                var (vendor, device) = PciIds(matching);
                list.Add(new DriverEntry(description, vendor, device, entry.GetValue("DriverVersion") as string ?? "", entry.GetValue("DriverDate") as string ?? "", entry.GetValue("ProviderName") as string ?? ""));
            }
        }
        catch (Exception ex)
        {
            notes.Add($"the display class key did not read: {ex.Message}");
        }
        return list;
    }

    /// <summary>"pci\ven_10de&amp;dev_2684&amp;subsys_…" → (0x10DE, 0x2684); zeros when the id is not a PCI one.</summary>
    public static (uint VendorId, uint DeviceId) PciIds(string matchingDeviceId)
    {
        uint vendor = 0, device = 0;
        foreach (var part in (matchingDeviceId ?? "").ToUpperInvariant().Split('\\', '&'))
        {
            if (part.StartsWith("VEN_", StringComparison.Ordinal) && uint.TryParse(part[4..], System.Globalization.NumberStyles.HexNumber, null, out var v)) vendor = v;
            else if (part.StartsWith("DEV_", StringComparison.Ordinal) && uint.TryParse(part[4..], System.Globalization.NumberStyles.HexNumber, null, out var d)) device = d;
        }
        return (vendor, device);
    }

    /// <summary>Every active render and capture endpoint with its shared-mode format; the defaults marked.</summary>
    [SupportedOSPlatform("windows")]
    private static List<AudioEndpointFact> AudioEndpoints(List<string> notes)
    {
        var list = new List<AudioEndpointFact>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var (flow, word) in new[] { (DataFlow.Render, "out"), (DataFlow.Capture, "in") })
        {
            var defaultId = "";
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = def.ID;
            }
            catch (Exception)
            {
                // No default of this flow.
            }
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    int rate = 0, bits = 0, channels = 0;
                    try
                    {
                        var format = device.AudioClient.MixFormat;
                        rate = format.SampleRate;
                        bits = format.BitsPerSample;
                        channels = format.Channels;
                    }
                    catch (Exception ex)
                    {
                        notes.Add($"the format of '{device.FriendlyName}' did not read: {ex.Message}");
                    }
                    list.Add(new AudioEndpointFact(device.FriendlyName, device.ID, word, device.ID == defaultId, rate, bits, channels));
                }
            }
        }
        return list;
    }

    /// <summary>The displays as the observation layer and the EDID reader know them.</summary>
    private static List<MachineDisplayFact> Displays(List<string> notes)
    {
        var list = new List<MachineDisplayFact>();
        try
        {
            foreach (var o in DisplayObservation.Snapshot())
            {
                var edid = EdidReader.For(o);
                list.Add(new MachineDisplayFact(o.Monitor, o.X, o.Y, o.RasterWidth, o.RasterHeight, o.Rate.Words, o.Connector, edid?.Identity ?? "", edid?.Hash ?? "",
                    o.Encoding is { } e ? SignalTruth.EncodingWords(e) : "", o.BitsPerChannel, o.HdrActive switch { true => "HDR on", false => "SDR", _ => "" }));
            }
        }
        catch (Exception ex)
        {
            notes.Add($"display probe failed: {ex.Message}");
        }
        return list;
    }

    // ---- power ---------------------------------------------------------------------------

    private static readonly Dictionary<Guid, string> KnownPlans = new()
    {
        [new Guid("381b4222-f694-41f0-9685-ff5bb260df2e")] = "Balanced",
        [new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")] = "High performance",
        [new Guid("a1841308-3541-4fab-bc81-f71556f20b4a")] = "Power saver",
        [new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61")] = "Ultimate performance",
    };

    /// <summary>The active power plan's name, from the known set or the plan's own friendly name.</summary>
    [SupportedOSPlatform("windows")]
    private static string PowerPlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var guidPtr) != 0 || guidPtr == IntPtr.Zero) return "";
        try
        {
            var guid = Marshal.PtrToStructure<Guid>(guidPtr);
            if (KnownPlans.TryGetValue(guid, out var known)) return known;
            uint size = 512;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0 && size > 2)
                {
                    return Marshal.PtrToStringUni(buffer)?.Trim() ?? guid.ToString();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return guid.ToString();
        }
        finally
        {
            LocalFree(guidPtr);
        }
    }

    [SupportedOSPlatform("windows")]
    private static int? ReadRegistryDword(string path, string name, bool user = false)
    {
        using var key = (user ? Registry.CurrentUser : Registry.LocalMachine).OpenSubKey(path);
        return key?.GetValue(name) is int v ? v : null;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
