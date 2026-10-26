using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Round 88 (Show Bus 1.2 §10): this install's certificate for the web remote's TLS — made at the first start (ECDSA
/// P-256, 825 days in all), kept beside the settings and renewed a month before it runs out, or at once when it spans more
/// than 825 days (Show Bus 1.3 §10: one made before 1.3 is replaced once). On Windows the key is sealed
/// with DPAPI to this Windows user, so a copy of the settings folder carries no usable key; elsewhere (the Ubuntu CI)
/// the file is readable by its owner only and the Super Check says the key is not sealed. A file that cannot be read is
/// replaced by a new certificate — every client that pinned the old one then sees CERTIFICATE CHANGED, which is the
/// truth. The hello carries the fingerprint; the certificate itself is served at /tls.cer.
/// </summary>
public sealed class TlsCertificateService : IDisposable
{
    public const string FileName = "patterns.tls.json";

    private readonly string _path;
    private readonly string _name;
    private X509Certificate2? _certificate;

    public TlsCertificateService(string folder, string name)
    {
        _path = Path.Combine(folder, FileName);
        _name = name;
    }

    /// <summary>The certificate the web remote serves, or null when none could be made (TLS then stays off and says why).</summary>
    public X509Certificate2? Certificate => _certificate ??= LoadOrCreate();

    /// <summary>"sha256:…" of <see cref="Certificate"/>, "" when there is none.</summary>
    public string Fingerprint => Certificate is { } c ? TlsFingerprint.Of(c) : "";

    /// <summary>How the key is kept, for the Super Check and the /tls page.</summary>
    public string Protection { get; private set; } = "";

    /// <summary>Why there is no certificate, when there is none.</summary>
    public string Problem { get; private set; } = "";

    private X509Certificate2? LoadOrCreate()
    {
        try
        {
            if (File.Exists(_path) && Read() is { } kept)
            {
                var why = TlsIdentity.RenewalReason(kept, DateTime.UtcNow);
                if (why.Length == 0) return kept;
                Log.Info($"TLS: {why} — a new one is made (every client that pinned {TlsFingerprint.Of(kept)} sees CERTIFICATE CHANGED once and is asked to accept it).");
                kept.Dispose();
            }
            using var made = TlsIdentity.Create(_name, Names(), Addresses(), DateTime.UtcNow);
            var pkcs12 = TlsIdentity.Export(made);
            Write(pkcs12);
            var usable = TlsIdentity.Import(pkcs12);
            Log.Info($"TLS: this desk's certificate is {TlsFingerprint.Of(usable)} ({Protection}).");
            return usable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
        {
            Problem = "no certificate could be made or read: " + ex.Message;
            Log.Warn("TLS: " + Problem, ex);
            return null;
        }
    }

    private X509Certificate2? Read()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var r = doc.RootElement;
            var protection = r.TryGetProperty("protection", out var p) ? p.GetString() ?? "" : "";
            var data = r.TryGetProperty("pkcs12", out var d) ? Convert.FromBase64String(d.GetString() ?? "") : Array.Empty<byte>();
            var raw = protection == "dpapi-user" ? Dpapi.Unprotect(data) : data;
            if (raw is null || raw.Length == 0) return null;
            Protection = protection == "dpapi-user" ? "the key sealed to this Windows user (DPAPI)" : "the key in a file only its owner reads — not sealed";
            return TlsIdentity.Import(raw);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            Log.Warn("TLS: the kept certificate could not be read — a new one is made.", ex);
            return null;
        }
    }

    private void Write(byte[] pkcs12)
    {
        var sealedBytes = Dpapi.Protect(pkcs12);
        var protection = sealedBytes is null ? "owner-only-file" : "dpapi-user";
        Protection = sealedBytes is null ? "the key in a file only its owner reads — not sealed" : "the key sealed to this Windows user (DPAPI)";
        var json = JsonSerializer.Serialize(new { v = 1, protection, pkcs12 = Convert.ToBase64String(sealedBytes ?? pkcs12) });
        AtomicFile.WriteAllText(_path, json);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Log.Warn("TLS: the certificate file's permissions could not be narrowed to its owner.", ex);
            }
        }
    }

    private static IEnumerable<string> Names()
    {
        yield return Environment.MachineName;
        yield return "localhost";
        string host;
        try
        {
            host = Dns.GetHostName();
        }
        catch (SocketException)
        {
            yield break;
        }
        yield return host;
    }

    private static IEnumerable<IPAddress> Addresses()
    {
        var list = new List<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                list.AddRange(nic.GetIPProperties().UnicastAddresses.Select(u => u.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork));
        }
        catch (NetworkInformationException)
        {
            // the names are enough: the pin, not the name, is what an app trusts
        }
        return list.Distinct();
    }

    public void Dispose() => _certificate?.Dispose();

    /// <summary>DPAPI, this Windows user's scope; null off Windows (the caller keeps the bytes unsealed and says so).</summary>
    private static class Dpapi
    {
        public static byte[]? Protect(byte[] data) => OperatingSystem.IsWindows() ? Run(data, protect: true) : null;

        public static byte[]? Unprotect(byte[] data) => OperatingSystem.IsWindows() ? Run(data, protect: false) : null;

        private static byte[]? Run(byte[] data, bool protect)
        {
            var input = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
            var output = default(DataBlob);
            try
            {
                Marshal.Copy(data, 0, input.pbData, data.Length);
                var ok = protect
                    ? CryptProtectData(ref input, "Patterns TLS", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1 /* UI forbidden */, ref output)
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
}
