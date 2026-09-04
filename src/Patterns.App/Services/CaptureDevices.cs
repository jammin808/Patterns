using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Patterns.Core.Media;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Enumerates DirectShow video input devices — HDMI/SDI capture cards (Elgato, Magewell,
/// Blackmagic WDM, AVerMedia…) and webcams. Playback itself goes through libVLC's dshow
/// input, so anything listed here plays through the normal video pipeline.
/// </summary>
public static class CaptureDevices
{
    /// <summary>Friendly names of video capture devices; empty off-Windows or on any COM trouble.</summary>
    public static IReadOnlyList<string> List()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        try
        {
            return EnumerateVideoInputs();
        }
        catch (Exception ex)
        {
            Log.Warn("Capture device enumeration failed.", ex);
            return Array.Empty<string>();
        }
    }

    private static readonly Guid ClsidSystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    private static readonly Guid ClsidVideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");

    /// <summary>
    /// The modes a capture device offers — every size and rate its driver advertises on its
    /// output pin (IAMStreamConfig), largest first. Empty off Windows, for a device that is not
    /// plugged in, or on any COM trouble: the picker then offers the device's default alone.
    /// </summary>
    public static IReadOnlyList<CaptureFormat> FormatsFor(string deviceName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(deviceName)) return Array.Empty<CaptureFormat>();
        try
        {
            return EnumerateFormats(deviceName);
        }
        catch (Exception ex)
        {
            Log.Warn($"Capture format enumeration failed for '{deviceName}'.", ex);
            return Array.Empty<CaptureFormat>();
        }
    }

    private static readonly Guid FormatVideoInfo = new("05589f80-c356-11ce-bf01-00aa0055595a");
    private static readonly Guid FormatVideoInfo2 = new("f72a76A0-eb0a-11d0-ace4-0000c0cc16ba");

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<CaptureFormat> EnumerateFormats(string deviceName)
    {
        var found = new HashSet<CaptureFormat>();
        var type = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum);
        if (type is null) return Array.Empty<CaptureFormat>();
        var devEnumObj = Activator.CreateInstance(type);
        if (devEnumObj is not ICreateDevEnum devEnum) return Array.Empty<CaptureFormat>();
        try
        {
            var category = ClsidVideoInputDeviceCategory;
            if (devEnum.CreateClassEnumerator(ref category, out var enumMoniker, 0) != 0 || enumMoniker is null)
            {
                return Array.Empty<CaptureFormat>();
            }
            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    var bagId = typeof(IPropertyBag).GUID;
                    moniker.BindToStorage(null!, null!, ref bagId, out var bagObj);
                    if (bagObj is not IPropertyBag bag) continue;
                    object? value = null;
                    var isIt = bag.Read("FriendlyName", ref value, IntPtr.Zero) == 0 &&
                               value is string name && string.Equals(name, deviceName, StringComparison.OrdinalIgnoreCase);
                    Marshal.ReleaseComObject(bag);
                    if (!isIt) continue;

                    var filterId = typeof(IBaseFilter).GUID;
                    moniker.BindToObject(null!, null!, ref filterId, out var filterObj);
                    if (filterObj is IBaseFilter filter)
                    {
                        try
                        {
                            ReadPinFormats(filter, found);
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(filter);
                        }
                    }
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn("Capture format probe failed for one device.", ex);
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                }
            }
            Marshal.ReleaseComObject(enumMoniker);
        }
        finally
        {
            Marshal.ReleaseComObject(devEnumObj);
        }
        return found
            .OrderByDescending(f => f.Width).ThenByDescending(f => f.Height).ThenByDescending(f => f.Fps)
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static void ReadPinFormats(IBaseFilter filter, HashSet<CaptureFormat> found)
    {
        if (filter.EnumPins(out var pins) != 0 || pins is null) return;
        try
        {
            var one = new IPin[1];
            while (pins.Next(1, one, IntPtr.Zero) == 0)
            {
                var pin = one[0];
                try
                {
                    if (pin.QueryDirection(out var dir) != 0 || dir != PinDirection.Output) continue;
                    if (pin is not IAMStreamConfig config) continue;
                    if (config.GetNumberOfCapabilities(out var count, out var capSize) != 0) continue;
                    var caps = Marshal.AllocHGlobal(Math.Max(capSize, 128));
                    try
                    {
                        for (var i = 0; i < count; i++)
                        {
                            if (config.GetStreamCaps(i, out var mediaType, caps) != 0 || mediaType == IntPtr.Zero) continue;
                            try
                            {
                                var mt = Marshal.PtrToStructure<AmMediaType>(mediaType);
                                if (mt.pbFormat == IntPtr.Zero) continue;
                                if (mt.formattype == FormatVideoInfo && mt.cbFormat >= 88)
                                {
                                    AddFormat(found, Marshal.ReadInt64(mt.pbFormat, 40), Marshal.ReadInt32(mt.pbFormat, 52), Marshal.ReadInt32(mt.pbFormat, 56));
                                }
                                else if (mt.formattype == FormatVideoInfo2 && mt.cbFormat >= 112)
                                {
                                    AddFormat(found, Marshal.ReadInt64(mt.pbFormat, 40), Marshal.ReadInt32(mt.pbFormat, 76), Marshal.ReadInt32(mt.pbFormat, 80));
                                }
                            }
                            finally
                            {
                                FreeMediaType(mediaType);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(caps);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(pin);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(pins);
        }
    }

    /// <summary>VIDEOINFOHEADER: AvgTimePerFrame at 40 (100 ns units), BITMAPINFOHEADER.biWidth/biHeight at 52/56 (76/80 for VIDEOINFOHEADER2).</summary>
    private static void AddFormat(HashSet<CaptureFormat> found, long avgTimePerFrame, int width, int height)
    {
        if (width <= 0 || avgTimePerFrame <= 0) return;
        var fps = Math.Round(10_000_000.0 / avgTimePerFrame, 2);
        if (fps < 1 || fps > 480) return;
        found.Add(new CaptureFormat(width, Math.Abs(height), fps));
    }

    [SupportedOSPlatform("windows")]
    private static void FreeMediaType(IntPtr mediaType)
    {
        var mt = Marshal.PtrToStructure<AmMediaType>(mediaType);
        if (mt.cbFormat > 0 && mt.pbFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mt.pbFormat);
        if (mt.pUnk != IntPtr.Zero) Marshal.Release(mt.pUnk);
        Marshal.FreeCoTaskMem(mediaType);
    }

    private enum PinDirection
    {
        Input,
        Output,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AmMediaType
    {
        public Guid majortype;
        public Guid subtype;
        [MarshalAs(UnmanagedType.Bool)] public bool bFixedSizeSamples;
        [MarshalAs(UnmanagedType.Bool)] public bool bTemporalCompression;
        public int lSampleSize;
        public Guid formattype;
        public IntPtr pUnk;
        public int cbFormat;
        public IntPtr pbFormat;
    }

    [ComImport]
    [Guid("56a86895-0ad4-11ce-b03a-0020af0ba770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBaseFilter
    {
        // IPersist
        [PreserveSig] int GetClassID(out Guid classId);
        // IMediaFilter
        [PreserveSig] int Stop();
        [PreserveSig] int Pause();
        [PreserveSig] int Run(long start);
        [PreserveSig] int GetState(int milliseconds, out int state);
        [PreserveSig] int SetSyncSource(IntPtr clock);
        [PreserveSig] int GetSyncSource(out IntPtr clock);
        // IBaseFilter
        [PreserveSig] int EnumPins(out IEnumPins? enumPins);
        [PreserveSig] int FindPin([MarshalAs(UnmanagedType.LPWStr)] string id, out IPin? pin);
        [PreserveSig] int QueryFilterInfo(IntPtr info);
        [PreserveSig] int JoinFilterGraph(IntPtr graph, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int QueryVendorInfo([MarshalAs(UnmanagedType.LPWStr)] out string vendorInfo);
    }

    [ComImport]
    [Guid("56a86892-0ad4-11ce-b03a-0020af0ba770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumPins
    {
        [PreserveSig] int Next(int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IPin[] pins, IntPtr fetched);
        [PreserveSig] int Skip(int count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumPins? clone);
    }

    [ComImport]
    [Guid("56a86891-0ad4-11ce-b03a-0020af0ba770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPin
    {
        [PreserveSig] int Connect(IPin receivePin, IntPtr mediaType);
        [PreserveSig] int ReceiveConnection(IPin receivePin, IntPtr mediaType);
        [PreserveSig] int Disconnect();
        [PreserveSig] int ConnectedTo(out IPin? pin);
        [PreserveSig] int ConnectionMediaType(IntPtr mediaType);
        [PreserveSig] int QueryPinInfo(IntPtr info);
        [PreserveSig] int QueryDirection(out PinDirection direction);
        [PreserveSig] int QueryId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int QueryAccept(IntPtr mediaType);
        [PreserveSig] int EnumMediaTypes(out IntPtr enumMediaTypes);
        [PreserveSig] int QueryInternalConnections(IntPtr pins, ref int count);
        [PreserveSig] int EndOfStream();
        [PreserveSig] int BeginFlush();
        [PreserveSig] int EndFlush();
        [PreserveSig] int NewSegment(long start, long stop, double rate);
    }

    [ComImport]
    [Guid("C6E13340-30AC-11d0-A18C-00A0C9118956")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMStreamConfig
    {
        [PreserveSig] int SetFormat(IntPtr mediaType);
        [PreserveSig] int GetFormat(out IntPtr mediaType);
        [PreserveSig] int GetNumberOfCapabilities(out int count, out int size);
        [PreserveSig] int GetStreamCaps(int index, out IntPtr mediaType, IntPtr streamConfigCaps);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> EnumerateVideoInputs()
    {
        var names = new List<string>();
        var type = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum);
        if (type is null) return names;
        var devEnumObj = Activator.CreateInstance(type);
        if (devEnumObj is not ICreateDevEnum devEnum) return names;
        try
        {
            var category = ClsidVideoInputDeviceCategory;
            if (devEnum.CreateClassEnumerator(ref category, out var enumMoniker, 0) != 0 || enumMoniker is null)
            {
                return names; // no devices in the category
            }

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    var bagId = typeof(IPropertyBag).GUID;
                    moniker.BindToStorage(null!, null!, ref bagId, out var bagObj);
                    if (bagObj is IPropertyBag bag)
                    {
                        object? value = null;
                        if (bag.Read("FriendlyName", ref value, IntPtr.Zero) == 0 &&
                            value is string name && !string.IsNullOrWhiteSpace(name))
                        {
                            names.Add(name);
                        }
                        Marshal.ReleaseComObject(bag);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Capture device probe failed for one device.", ex);
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                }
            }
            Marshal.ReleaseComObject(enumMoniker);
        }
        finally
        {
            Marshal.ReleaseComObject(devEnumObj);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    [ComImport]
    [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator([In] ref Guid deviceClass, out IEnumMoniker? enumMoniker, [In] int flags);
    }

    [ComImport]
    [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read(
            [In, MarshalAs(UnmanagedType.LPWStr)] string propertyName,
            [In, Out, MarshalAs(UnmanagedType.Struct)] ref object? value,
            IntPtr errorLog);

        [PreserveSig]
        int Write(
            [In, MarshalAs(UnmanagedType.LPWStr)] string propertyName,
            [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
