using System.Runtime.InteropServices;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The one seam this desk keeps its secrets at rest through (the TLS key, round 88; the app keys, Show Bus 1.3 §13.5.4): DPAPI in this
/// Windows user's scope, so a copy of the settings folder carries nothing usable; off Windows no seal is made (null), and the caller
/// keeps the bytes in a file only its owner reads (<see cref="OwnerOnly"/>) and says so. No UI is ever shown, and a seal that cannot
/// be opened (another user, another machine) reads as null — the caller's own words say what that means for it.
/// </summary>
public static class SecretSeal
{
    /// <summary>The words every reader says for a sealed store.</summary>
    public const string SealedWords = "sealed to this Windows user (DPAPI)";

    /// <summary>The words every reader says for an owner-only file.</summary>
    public const string OwnerOnlyWords = "in a file only its owner reads — not sealed";

    /// <summary>The bytes sealed to this Windows user, described as <paramref name="description"/>; null off Windows or when DPAPI refuses.</summary>
    public static byte[]? Seal(byte[] data, string description) => OperatingSystem.IsWindows() ? Run(data, description, protect: true) : null;

    /// <summary>The bytes a seal of this user's holds; null off Windows or when it cannot be opened.</summary>
    public static byte[]? Unseal(byte[] data) => OperatingSystem.IsWindows() ? Run(data, null, protect: false) : null;

    /// <summary>Off Windows: the file narrowed to its owner's read and write; a failure is logged, never thrown.</summary>
    public static void OwnerOnly(string path, string what)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Log.Warn($"{what}: the file's permissions could not be narrowed to its owner.", ex);
        }
    }

    private static byte[]? Run(byte[] data, string? description, bool protect)
    {
        var input = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var output = default(DataBlob);
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            var ok = protect
                ? CryptProtectData(ref input, description ?? "Patterns", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1 /* UI forbidden */, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, ref output);
            if (!ok) return null;
            var bytes = new byte[output.cbData];
            Marshal.Copy(output.pbData, bytes, 0, output.cbData);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
