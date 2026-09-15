using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// What Windows says it sends each display (round 65): QueryDisplayConfig's active paths — the
/// source mode (the desktop rectangle, which ties a path to a screen), the target mode (the
/// signal's active raster and its exact vertical rate as a rational), the connector, the
/// monitor's name and its EDID ids — and, where the driver answers, the advanced-colour facts:
/// the colour encoding, the bits per channel, whether HDR is on. Nothing here is inferred: a
/// field the API did not fill stays unknown, and the words say so. Best-effort and guarded like
/// the other probes; off Windows there are no observations, and a test hands in its own through
/// <see cref="Source"/>. Read at most every <see cref="KeptFor"/>: the facts, STATE and the
/// Screens page all ask, and the answer does not change between them.
/// </summary>
public static class DisplayObservation
{
    /// <summary>The observations, when something other than Windows supplies them — the tests.</summary>
    public static Func<IReadOnlyList<SignalObservation>>? Source { get; set; }

    /// <summary>How long a reading is kept before Windows is asked again.</summary>
    public static TimeSpan KeptFor { get; set; } = TimeSpan.FromSeconds(2);

    private static readonly object Gate = new();
    private static IReadOnlyList<SignalObservation> _kept = Array.Empty<SignalObservation>();
    private static DateTime _keptAtUtc = DateTime.MinValue;

    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>Every active display path as Windows describes it; empty off Windows or when the query failed.</summary>
    public static IReadOnlyList<SignalObservation> Snapshot()
    {
        if (Source is { } source)
        {
            try
            {
                return source();
            }
            catch (Exception)
            {
                return Array.Empty<SignalObservation>();
            }
        }
        if (!OperatingSystem.IsWindows()) return Array.Empty<SignalObservation>();
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (now - _keptAtUtc < KeptFor) return _kept;
            try
            {
                _kept = Query();
            }
            catch (Exception ex)
            {
                Log.Warn("Display observation failed.", ex);
                _kept = Array.Empty<SignalObservation>();
            }
            _keptAtUtc = now;
            return _kept;
        }
    }

    /// <summary>Drops the kept reading so the next ask goes to Windows (a topology change, a test).</summary>
    public static void Forget()
    {
        lock (Gate)
        {
            _keptAtUtc = DateTime.MinValue;
        }
    }

    /// <summary>The observation for the display showing this desktop rectangle — the whole rectangle, or its origin alone on a scaled desktop; null when none.</summary>
    public static SignalObservation? For(PixelRect bounds)
    {
        var all = Snapshot();
        return all.FirstOrDefault(o => o.Covers(bounds.X, bounds.Y, bounds.Width, bounds.Height))
            ?? all.FirstOrDefault(o => o.X == bounds.X && o.Y == bounds.Y);
    }

    // ---- Win32 ---------------------------------------------------------------------------

    private const uint QdcOnlyActivePaths = 0x2;
    private const uint ModeInfoSource = 1;
    private const uint ModeInfoTarget = 2;
    private const uint DeviceInfoGetTargetName = 2;
    private const uint DeviceInfoGetAdvancedColorInfo = 9;

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<SignalObservation> Query()
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0 || pathCount == 0) return Array.Empty<SignalObservation>();
        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return Array.Empty<SignalObservation>();

        var list = new List<SignalObservation>();
        for (var i = 0; i < pathCount; i++)
        {
            var path = paths[i];

            // The source mode: the desktop rectangle this path shows — what ties it to a screen.
            int x = 0, y = 0, w = 0, h = 0;
            var srcIdx = path.sourceInfo.modeInfoIdx;
            if (srcIdx < modeCount && modes[srcIdx].infoType == ModeInfoSource)
            {
                var sm = modes[srcIdx].u.sourceMode;
                x = sm.positionX;
                y = sm.positionY;
                w = (int)sm.width;
                h = (int)sm.height;
            }

            // The target mode: the signal itself — its active raster and its exact vertical rate.
            int tw = 0, th = 0;
            var rate = SignalRate.None;
            var interlaced = false;
            var tgtIdx = path.targetInfo.modeInfoIdx;
            if (tgtIdx < modeCount && modes[tgtIdx].infoType == ModeInfoTarget)
            {
                var vs = modes[tgtIdx].u.targetMode.targetVideoSignalInfo;
                tw = (int)vs.activeSizeCx;
                th = (int)vs.activeSizeCy;
                rate = SignalRate.Of(vs.vSyncFreqNumerator, vs.vSyncFreqDenominator);
                interlaced = vs.scanLineOrdering >= 2;
            }
            if (!rate.IsSet) rate = SignalRate.Of(path.targetInfo.refreshRateNumerator, path.targetInfo.refreshRateDenominator);

            // The target's name: the monitor, its EDID ids, its device path (round 65.7 reads the EDID itself from it).
            var monitor = "";
            var devicePath = "";
            var manufacturer = 0;
            var product = 0;
            var name = new DisplayConfigTargetDeviceName
            {
                header = new DisplayConfigDeviceInfoHeader
                {
                    type = DeviceInfoGetTargetName,
                    size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                    adapterIdLow = path.targetInfo.adapterIdLow,
                    adapterIdHigh = path.targetInfo.adapterIdHigh,
                    id = path.targetInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref name) == 0)
            {
                monitor = name.monitorFriendlyDeviceName ?? "";
                devicePath = name.monitorDevicePath ?? "";
                manufacturer = name.edidManufactureId;
                product = name.edidProductCodeId;
            }

            // Advanced colour: the encoding, the depth and HDR as the driver answers — or nothing, which stays nothing.
            PixelEncoding? encoding = null;
            var bits = 0;
            bool? hdrActive = null;
            bool? hdrCapable = null;
            var colour = new DisplayConfigGetAdvancedColorInfo
            {
                header = new DisplayConfigDeviceInfoHeader
                {
                    type = DeviceInfoGetAdvancedColorInfo,
                    size = (uint)Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo>(),
                    adapterIdLow = path.targetInfo.adapterIdLow,
                    adapterIdHigh = path.targetInfo.adapterIdHigh,
                    id = path.targetInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref colour) == 0)
            {
                hdrCapable = (colour.value & 0x1) != 0;
                hdrActive = (colour.value & 0x2) != 0;
                encoding = colour.colorEncoding switch
                {
                    0 => PixelEncoding.RGB,
                    1 => PixelEncoding.YCbCr444,
                    2 => PixelEncoding.YCbCr422,
                    3 => PixelEncoding.YCbCr420,
                    _ => null,
                };
                bits = (int)colour.bitsPerColorChannel;
            }

            list.Add(new SignalObservation(x, y, w, h, tw, th, rate, encoding, bits, hdrActive, hdrCapable, interlaced, monitor, manufacturer, product, devicePath, Connector(path.targetInfo.outputTechnology)));
        }
        return list;
    }

    /// <summary>The connector's word for DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.</summary>
    public static string Connector(uint technology) => technology switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        8 => "SDI",
        9 => "DisplayPort",
        10 => "eDP",
        11 or 12 => "UDI",
        14 => "Miracast",
        15 => "indirect wired",
        16 => "virtual",
        17 => "DisplayPort over USB",
        0x80000000 => "internal",
        0xFFFFFFFF => "other",
        _ => "",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public uint adapterIdLow;
        public int adapterIdHigh;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public uint adapterIdLow;
        public int adapterIdHigh;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo sourceInfo;
        public DisplayConfigPathTargetInfo targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigVideoSignalInfo
    {
        public ulong pixelRate;
        public uint hSyncFreqNumerator;
        public uint hSyncFreqDenominator;
        public uint vSyncFreqNumerator;
        public uint vSyncFreqDenominator;
        public uint activeSizeCx;
        public uint activeSizeCy;
        public uint totalSizeCx;
        public uint totalSizeCy;
        public uint videoStandard;
        public uint scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigTargetMode
    {
        public DisplayConfigVideoSignalInfo targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSourceMode
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public int positionX;
        public int positionY;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct DisplayConfigModeInfoUnion
    {
        [FieldOffset(0)] public DisplayConfigTargetMode targetMode;
        [FieldOffset(0)] public DisplayConfigSourceMode sourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigModeInfo
    {
        public uint infoType;
        public uint id;
        public uint adapterIdLow;
        public int adapterIdHigh;
        public DisplayConfigModeInfoUnion u;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint type;
        public uint size;
        public uint adapterIdLow;
        public int adapterIdHigh;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigGetAdvancedColorInfo
    {
        public DisplayConfigDeviceInfoHeader header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DisplayConfigPathInfo[] pathArray, ref uint numModeInfoArrayElements, [Out] DisplayConfigModeInfo[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigGetAdvancedColorInfo requestPacket);
}
