using Microsoft.Win32;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The EDID a display presented, as Windows keeps it (round 65.7): the monitor's device path
/// from QueryDisplayConfig ("\\?\DISPLAY#DELA0C0#5&amp;2a3c8f1&amp;0&amp;UID4353#{…}") names the
/// PnP id and the instance, and the bytes sit in the registry under
/// SYSTEM\CurrentControlSet\Enum\DISPLAY\&lt;id&gt;\&lt;instance&gt;\Device Parameters\EDID. When the
/// path does not lead there, every display's key is tried and the one whose manufacturer and
/// product bytes match the path's is taken. Read once per device path and kept; a test hands in
/// bytes through <see cref="Source"/>. Off Windows there is nothing to read.
/// </summary>
public static class EdidReader
{
    /// <summary>The bytes for a monitor device path, when something other than the registry supplies them — the tests.</summary>
    public static Func<string, byte[]?>? Source { get; set; }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (byte[]? Bytes, EdidInfo? Info)> Kept = new(StringComparer.OrdinalIgnoreCase);

    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>The raw EDID for a monitor device path; null when none could be read.</summary>
    public static byte[]? Read(string devicePath, int manufacturerId = 0, int productCode = 0)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return null;
        if (Source is { } source)
        {
            try
            {
                return source(devicePath);
            }
            catch (Exception)
            {
                return null;
            }
        }
        if (!OperatingSystem.IsWindows()) return null;
        lock (Gate)
        {
            if (Kept.TryGetValue(devicePath, out var kept)) return kept.Bytes;
            byte[]? bytes = null;
            try
            {
                bytes = ReadRegistry(devicePath) ?? FindByIds(manufacturerId, productCode);
            }
            catch (Exception ex)
            {
                Log.Warn("EDID read failed.", ex);
            }
            Kept[devicePath] = (bytes, bytes is null ? null : Edid.Parse(bytes));
            return bytes;
        }
    }

    /// <summary>The parsed EDID for a monitor device path; null when none could be read.</summary>
    public static EdidInfo? Info(string devicePath, int manufacturerId = 0, int productCode = 0)
    {
        if (Source is not null)
        {
            var bytes = Read(devicePath);
            return bytes is null ? null : Edid.Parse(bytes);
        }
        if (Read(devicePath, manufacturerId, productCode) is null) return null;
        lock (Gate)
        {
            return Kept.TryGetValue(devicePath, out var kept) ? kept.Info : null;
        }
    }

    /// <summary>The parsed EDID for an observed display.</summary>
    public static EdidInfo? For(SignalObservation? observed)
        => observed is null || observed.DevicePath.Length == 0 ? null : Info(observed.DevicePath, observed.EdidManufacturerId, observed.EdidProductCode);

    /// <summary>Drops what was kept so the next ask reads the registry again (a display re-plugged, a test).</summary>
    public static void Forget()
    {
        lock (Gate)
        {
            Kept.Clear();
        }
    }

    /// <summary>The registry key path a monitor device path names: "DELA0C0\5&amp;2a3c8f1&amp;0&amp;UID4353"; "" when the path is not a monitor's.</summary>
    public static string InstanceKey(string devicePath)
    {
        var parts = devicePath.Split('#');
        if (parts.Length < 3 || !parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)) return "";
        return $@"{parts[1]}\{parts[2]}";
    }

    private static byte[]? ReadRegistry(string devicePath)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var instance = InstanceKey(devicePath);
        if (instance.Length == 0) return null;
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{instance}\Device Parameters");
        return key?.GetValue("EDID") is byte[] bytes && Edid.LooksLikeEdid(bytes) ? bytes : null;
    }

    /// <summary>Every display key's EDID whose manufacturer and product bytes match — the fallback when the path did not lead to the key.</summary>
    private static byte[]? FindByIds(int manufacturerId, int productCode)
    {
        if (!OperatingSystem.IsWindows() || manufacturerId == 0) return null;
        using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
        if (display is null) return null;
        foreach (var id in display.GetSubKeyNames())
        {
            using var idKey = display.OpenSubKey(id);
            if (idKey is null) continue;
            foreach (var instance in idKey.GetSubKeyNames())
            {
                using var parameters = idKey.OpenSubKey($@"{instance}\Device Parameters");
                if (parameters?.GetValue("EDID") is not byte[] bytes || !Edid.LooksLikeEdid(bytes)) continue;
                var manufacturer = bytes[8] | (bytes[9] << 8);       // Windows' edidManufactureId is the two bytes as stored
                var product = bytes[10] | (bytes[11] << 8);
                if (manufacturer == manufacturerId && (productCode == 0 || product == productCode)) return bytes;
            }
        }
        return null;
    }
}
