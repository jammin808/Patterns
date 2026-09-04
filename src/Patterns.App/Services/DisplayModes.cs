using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>A display mode Windows offers: size and refresh rate.</summary>
public readonly record struct DisplayMode(int Width, int Height, int Hz)
{
    public string Key => $"{Width}x{Height}@{Hz}";

    public string Label => $"{Width}×{Height} @ {Hz} Hz";
}

/// <summary>
/// The display modes behind the Screens page: what a display can do, what it is doing, and a
/// change that goes through Windows' own settings path (ChangeDisplaySettingsEx) — the same
/// change the operator would make in Display settings, so the driver, the EDID handshake and
/// the desktop layout all behave exactly as they do there. Best-effort and guarded like the
/// other probes: off Windows there are no modes and a change is refused with a sentence.
/// </summary>
public static class DisplayModes
{
    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>The GDI device name ("\\.\DISPLAY2") of the display whose desktop rectangle starts at <paramref name="bounds"/>; null when none does.</summary>
    public static string? DeviceFor(PixelRect bounds)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            foreach (var (device, mode, x, y) in EnumerateAttached())
            {
                if (x == bounds.X && y == bounds.Y && mode.Width == bounds.Width && mode.Height == bounds.Height) return device;
            }
            // A mode change moves neighbours: fall back to the origin alone.
            foreach (var (device, _, x, y) in EnumerateAttached())
            {
                if (x == bounds.X && y == bounds.Y) return device;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Display device lookup failed.", ex);
        }
        return null;
    }

    /// <summary>The mode a display is in right now; null when unknown.</summary>
    public static DisplayMode? Current(string device)
    {
        if (!OperatingSystem.IsWindows() || device.Length == 0) return null;
        try
        {
            return CurrentOf(device) is { } m ? m.Mode : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Display mode query failed.", ex);
            return null;
        }
    }

    /// <summary>Every 32-bit mode the display offers, largest first, each size's rates highest first; empty off Windows.</summary>
    public static IReadOnlyList<DisplayMode> List(string device)
    {
        if (!OperatingSystem.IsWindows() || device.Length == 0) return Array.Empty<DisplayMode>();
        try
        {
            return ListModes(device);
        }
        catch (Exception ex)
        {
            Log.Warn("Display mode enumeration failed.", ex);
            return Array.Empty<DisplayMode>();
        }
    }

    /// <summary>
    /// Switches the display to a mode. Returns "" on success, else the reason. The change is
    /// written to the registry first and applied with one global refresh, the way the Display
    /// settings page does it, so the desktop layout is re-laid out once.
    /// </summary>
    public static string Apply(string device, DisplayMode mode)
    {
        if (!OperatingSystem.IsWindows()) return "Display modes can only be changed on Windows.";
        if (device.Length == 0) return "This display could not be matched to a Windows display device.";
        try
        {
            return ApplyMode(device, mode);
        }
        catch (Exception ex)
        {
            Log.Warn("Display mode change failed.", ex);
            return $"The display refused the change: {ex.Message}";
        }
    }

    // ---- Win32 ---------------------------------------------------------------------------

    private const int EnumCurrentSettings = -1;
    private const uint AttachedToDesktop = 0x1;
    private const int DmPelsWidth = 0x00080000;
    private const int DmPelsHeight = 0x00100000;
    private const int DmDisplayFrequency = 0x00400000;
    private const uint CdsUpdateRegistry = 0x1;
    private const uint CdsNoReset = 0x10000000;
    private const int DispChangeSuccessful = 0;
    private const int DispChangeRestart = 1;
    private const int DispChangeBadMode = -2;
    private const int DispChangeBadFlags = -4;
    private const int DispChangeBadParam = -5;

    [SupportedOSPlatform("windows")]
    private static IEnumerable<(string Device, DisplayMode Mode, int X, int Y)> EnumerateAttached()
    {
        for (uint i = 0; i < 32; i++)
        {
            var dd = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) yield break;
            if ((dd.StateFlags & AttachedToDesktop) == 0) continue;
            if (CurrentOf(dd.DeviceName) is { } cur) yield return (dd.DeviceName, cur.Mode, cur.X, cur.Y);
        }
    }

    [SupportedOSPlatform("windows")]
    private static (DisplayMode Mode, int X, int Y)? CurrentOf(string device)
    {
        var dm = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettingsEx(device, EnumCurrentSettings, ref dm, 0)) return null;
        return (new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency), dm.dmPositionX, dm.dmPositionY);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<DisplayMode> ListModes(string device)
    {
        var set = new HashSet<DisplayMode>();
        for (var i = 0; ; i++)
        {
            var dm = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettingsEx(device, i, ref dm, 0)) break;
            if (dm.dmBitsPerPel != 32 || dm.dmPelsWidth < 640 || dm.dmDisplayFrequency < 23) continue;
            set.Add(new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
            if (i > 4096) break; // a driver that never ends its list must not hang the desk
        }
        return set
            .OrderByDescending(m => m.Width).ThenByDescending(m => m.Height).ThenByDescending(m => m.Hz)
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static string ApplyMode(string device, DisplayMode mode)
    {
        var dm = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettingsEx(device, EnumCurrentSettings, ref dm, 0)) return "The display's current mode could not be read.";
        dm.dmPelsWidth = mode.Width;
        dm.dmPelsHeight = mode.Height;
        dm.dmDisplayFrequency = mode.Hz;
        dm.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;

        var staged = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CdsUpdateRegistry | CdsNoReset, IntPtr.Zero);
        if (staged != DispChangeSuccessful) return Reason(staged);
        var applied = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        return applied == DispChangeSuccessful ? "" : Reason(applied);
    }

    private static string Reason(int code) => code switch
    {
        DispChangeRestart => "Windows wants a restart for that mode — it was not applied.",
        DispChangeBadMode => "The display does not support that mode.",
        DispChangeBadFlags or DispChangeBadParam => "Windows rejected the request.",
        _ => $"The display refused the change (code {code}).",
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DevMode lpDevMode, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DevMode lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
}
