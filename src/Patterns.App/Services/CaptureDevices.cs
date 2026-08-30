using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
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
