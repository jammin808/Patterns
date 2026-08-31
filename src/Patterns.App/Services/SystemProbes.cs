using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Patterns.Core.Services;

namespace Patterns.App.Services;

// ---------------------------------------------------------------------------------------------
// The Win32/DXGI probes behind the Admin tab. Everything here is best-effort and guarded:
// a failed probe returns "unknown" (-1 / empty), never throws into the app.
// ---------------------------------------------------------------------------------------------

/// <summary>DXGI adapter enumeration and video-memory queries (Windows only).</summary>
internal static class Dxgi
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint AdapterFlagSoftware = 2;

    public static List<GpuAdapterInfo> Enumerate()
    {
        var result = new List<GpuAdapterInfo>();
        if (!OperatingSystem.IsWindows()) return result;
        object? factoryObj = null;
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out factoryObj) < 0 || factoryObj is not IDXGIFactory1 factory)
            {
                return result;
            }
            for (uint i = 0; i < 16; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter) == DxgiErrorNotFound || adapter is null) break;
                try
                {
                    if (adapter.GetDesc1(out var desc) < 0) continue;
                    var software = (desc.Flags & AdapterFlagSoftware) != 0 ||
                                   desc.VendorId == GpuAdapterInfo.VendorMicrosoft;
                    result.Add(new GpuAdapterInfo(
                        desc.Description.TrimEnd('\0'),
                        desc.VendorId,
                        desc.DeviceId,
                        (long)((ulong)desc.DedicatedVideoMemory / (1024 * 1024)),
                        desc.AdapterLuid,
                        software));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("GPU enumeration failed.", ex);
        }
        finally
        {
            if (factoryObj is not null) Marshal.ReleaseComObject(factoryObj);
        }
        return result;
    }

    private static IDXGIAdapter3? _vramAdapter;
    private static long _vramLuid;

    /// <summary>Current usage and OS budget for one adapter's local (video) memory segment.</summary>
    public static bool TryQueryVideoMemory(long luid, out double usedMB, out double budgetMB)
    {
        usedMB = -1;
        budgetMB = -1;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (_vramAdapter is null || _vramLuid != luid)
            {
                ReleaseVramAdapter();
                var iid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref iid, out var factoryObj) < 0 || factoryObj is not IDXGIFactory1 factory)
                {
                    return false;
                }
                try
                {
                    for (uint i = 0; i < 16; i++)
                    {
                        if (factory.EnumAdapters1(i, out var adapter) == DxgiErrorNotFound || adapter is null) break;
                        if (adapter.GetDesc1(out var desc) >= 0 && (luid == 0 || desc.AdapterLuid == luid) &&
                            adapter is IDXGIAdapter3 a3)
                        {
                            _vramAdapter = a3;
                            _vramLuid = luid;
                            break;
                        }
                        Marshal.ReleaseComObject(adapter);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(factoryObj);
                }
            }

            if (_vramAdapter is null) return false;
            if (_vramAdapter.QueryVideoMemoryInfo(0, 0 /* local segment */, out var info) < 0)
            {
                ReleaseVramAdapter();
                return false;
            }
            usedMB = info.CurrentUsage / (1024.0 * 1024.0);
            budgetMB = info.Budget / (1024.0 * 1024.0);
            return true;
        }
        catch
        {
            ReleaseVramAdapter();
            return false;
        }
    }

    private static void ReleaseVramAdapter()
    {
        if (_vramAdapter is not null && OperatingSystem.IsWindows())
        {
            try { Marshal.ReleaseComObject(_vramAdapter); } catch { /* teardown race */ }
        }
        _vramAdapter = null;
        _vramLuid = 0;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    // Vtable-order interop: unused slots are declared (never called) so the used ones line up.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIFactory1
    {
        int _SetPrivateData();
        int _SetPrivateDataInterface();
        int _GetPrivateData();
        int _GetParent();
        int _EnumAdapters();
        int _MakeWindowAssociation();
        int _GetWindowAssociation();
        int _CreateSwapChain();
        int _CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1? adapter);
        int _IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter1
    {
        int _SetPrivateData();
        int _SetPrivateDataInterface();
        int _GetPrivateData();
        int _GetParent();
        int _EnumOutputs();
        int _GetDesc();
        int _CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [ComImport, Guid("645967A4-1392-4310-A798-8053CE3E93FD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter3
    {
        int _SetPrivateData();
        int _SetPrivateDataInterface();
        int _GetPrivateData();
        int _GetParent();
        int _EnumOutputs();
        int _GetDesc();
        int _CheckInterfaceSupport();
        int _GetDesc1();
        int _GetDesc2();
        int _RegisterHardwareContentProtectionTeardownStatusEvent();
        int _UnregisterHardwareContentProtectionTeardownStatus();
        [PreserveSig] int QueryVideoMemoryInfo(uint nodeIndex, int segmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO info);
        int _SetVideoMemoryReservation();
        int _RegisterVideoMemoryBudgetChangeNotificationEvent();
        int _UnregisterVideoMemoryBudgetChangeNotification();
    }
}

/// <summary>Registry writes for Windows' per-app GPU preference, plus small HKLM reads.</summary>
internal static class WinRegistry
{
    private const string GpuPrefSubkey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private static readonly IntPtr HkeyCurrentUser = new(unchecked((int)0x80000001));
    private static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));
    private const uint RegSz = 1;
    private const uint KeyWrite = 0x20006;

    /// <summary>Sets (or with an empty value removes) the per-app GPU preference for an exe path.</summary>
    public static void SetUserGpuPreference(string exePath, string value)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (value.Length == 0)
        {
            RegDeleteKeyValueW(HkeyCurrentUser, GpuPrefSubkey, exePath);
            return;
        }
        if (RegCreateKeyExW(HkeyCurrentUser, GpuPrefSubkey, 0, null, 0, KeyWrite, IntPtr.Zero,
                out var key, out _) != 0)
        {
            throw new InvalidOperationException("Could not open the GPU preferences key.");
        }
        try
        {
            var data = System.Text.Encoding.Unicode.GetBytes(value + "\0");
            if (RegSetValueExW(key, exePath, 0, RegSz, data, (uint)data.Length) != 0)
            {
                throw new InvalidOperationException("Could not write the GPU preference value.");
            }
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    public static void DeleteUserGpuPreference(string exePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        RegDeleteKeyValueW(HkeyCurrentUser, GpuPrefSubkey, exePath);
    }

    /// <summary>The marketing CPU name ("13th Gen Intel(R) Core(TM) i7-13700K"), or "".</summary>
    public static string ReadCpuName()
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            var size = 512u;
            var buffer = new byte[size];
            if (RegGetValueW(HkeyLocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString", 0x0000FFFF /* RRF_RT_ANY */, out _, buffer, ref size) != 0)
            {
                return "";
            }
            return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim();
        }
        catch
        {
            return "";
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyExW(IntPtr hKey, string subKey, uint reserved, string? cls,
        uint options, uint samDesired, IntPtr securityAttributes, out IntPtr result, out uint disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(IntPtr hKey, string valueName, uint reserved, uint type,
        byte[] data, uint cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegDeleteKeyValueW(IntPtr hKey, string subKey, string valueName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegGetValueW(IntPtr hKey, string subKey, string value, uint flags,
        out uint type, byte[] data, ref uint cbData);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);
}

/// <summary>System-wide CPU, memory and power readings (Windows; partial elsewhere).</summary>
internal static class Win32Perf
{
    public static bool TryGetSystemTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        idle = kernel = user = 0;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return GetSystemTimes(out idle, out kernel, out user);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetMemoryStatus(out double loadPct, out double totalMB, out double availMB)
    {
        loadPct = totalMB = availMB = -1;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status)) return false;
            loadPct = status.dwMemoryLoad;
            totalMB = status.ullTotalPhys / (1024.0 * 1024.0);
            availMB = status.ullAvailPhys / (1024.0 * 1024.0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>onBattery=false with pct=-1 when the state is unknown (desktops, failures).</summary>
    public static (bool OnBattery, int BatteryPct) GetPowerStatus()
    {
        if (!OperatingSystem.IsWindows()) return (false, -1);
        try
        {
            if (!GetSystemPowerStatus(out var status)) return (false, -1);
            var onBattery = status.ACLineStatus == 0;
            var pct = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : -1;
            return (onBattery, pct);
        }
        catch
        {
            return (false, -1);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}

/// <summary>
/// This process's 3D-engine GPU utilisation from the "GPU Engine" performance counters —
/// the same numbers Task Manager shows. Purely best-effort: some machines don't have the
/// category, and a broken counter store must never cost more than a "n/a".
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GpuEngineCounter : IDisposable
{
    private readonly List<System.Diagnostics.PerformanceCounter> _counters = new();
    private DateTime _refreshedUtc = DateTime.MinValue;
    private bool _unavailable;

    public double Read(DateTime utcNow)
    {
        if (_unavailable) return -1;
        try
        {
            if (utcNow - _refreshedUtc > TimeSpan.FromSeconds(20))
            {
                Refresh();
                _refreshedUtc = utcNow;
            }
            if (_counters.Count == 0) return -1;
            double sum = 0;
            foreach (var c in _counters)
            {
                sum += c.NextValue();
            }
            return Math.Min(100, sum);
        }
        catch
        {
            // One bad read disables the probe for this run — the rest of the sample survives.
            _unavailable = true;
            DisposeCounters();
            return -1;
        }
    }

    private void Refresh()
    {
        DisposeCounters();
        var marker = $"pid_{Environment.ProcessId}_";
        var category = new System.Diagnostics.PerformanceCounterCategory("GPU Engine");
        foreach (var name in category.GetInstanceNames())
        {
            if (!name.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                !name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var counter = new System.Diagnostics.PerformanceCounter("GPU Engine", "Utilization Percentage", name, readOnly: true);
            counter.NextValue(); // prime — the first read of a rate counter is always 0
            _counters.Add(counter);
        }
    }

    private void DisposeCounters()
    {
        foreach (var c in _counters)
        {
            try { c.Dispose(); } catch { /* counter store teardown */ }
        }
        _counters.Clear();
    }

    public void Dispose() => DisposeCounters();
}
