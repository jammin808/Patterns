using System.Runtime.InteropServices;
using System.Text;
using Patterns.Core.Services;

namespace Patterns.Core.Ndi;

/// <summary>
/// Minimal P/Invoke surface over the NDI 5/6 runtime. The runtime is discovered at startup
/// (beside the exe, NDI_RUNTIME_DIR_V6/V5, standard install folders); when absent every
/// call is gated by <see cref="Available"/> and the app simply reports how to enable NDI.
/// </summary>
public static class NdiInterop
{
    private const string LibName = "ndi";

    public static readonly long SendTimecodeSynthesize = long.MaxValue;

    /// <summary>'B','G','R','X' — BGRA memory layout, alpha ignored.</summary>
    public const int FourCcBgrx = 'B' | ('G' << 8) | ('R' << 16) | ('X' << 24);

    public const int FrameFormatProgressive = 1;

    private static readonly object InitGate = new();
    private static bool _resolverInstalled;
    private static bool? _available;
    private static string _runtimePath = "";

    public static string RuntimePath => _runtimePath;

    /// <summary>True when the NDI runtime was found and initialised.</summary>
    public static bool Available
    {
        get
        {
            lock (InitGate)
            {
                if (_available is { } known) return known;
                try
                {
                    InstallResolver();
                    _available = NDIlib_initialize();
                    if (_available == false)
                    {
                        Log.Warn("NDI runtime loaded but NDIlib_initialize returned false (unsupported CPU?).");
                    }
                }
                catch (DllNotFoundException)
                {
                    _available = false;
                }
                catch (Exception ex)
                {
                    Log.Warn("NDI initialisation failed.", ex);
                    _available = false;
                }
                return _available.Value;
            }
        }
    }

    /// <summary>
    /// Forget a failed probe so enabling NDI again rechecks the disk — the runtime may have
    /// been installed (or the DLL dropped beside the exe) since startup, without a restart.
    /// </summary>
    public static void ReprobeIfUnavailable()
    {
        lock (InitGate)
        {
            if (_available == false) _available = null;
        }
    }

    private static void InstallResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;
        NativeLibrary.SetDllImportResolver(typeof(NdiInterop).Assembly, (name, asm, search) =>
        {
            if (name != LibName) return IntPtr.Zero;
            foreach (var candidate in CandidatePaths())
            {
                if (NativeLibrary.TryLoad(candidate, out var handle))
                {
                    _runtimePath = candidate;
                    Log.Info($"NDI runtime: {candidate}");
                    return handle;
                }
            }
            return IntPtr.Zero;
        });
    }

    private static IEnumerable<string> CandidatePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            const string dll = "Processing.NDI.Lib.x64.dll";
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            yield return Path.Combine(exeDir, dll);

            foreach (var env in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" })
            {
                var dir = Environment.GetEnvironmentVariable(env);
                if (!string.IsNullOrEmpty(dir)) yield return Path.Combine(dir, dll);
            }

            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(pf, "NDI", "NDI 6 Runtime", "v6", dll);
            yield return Path.Combine(pf, "NDI", "NDI 5 Runtime", "v5", dll);
            yield return dll; // PATH as a last resort
        }
        else
        {
            yield return "libndi.so.6";
            yield return "libndi.so.5";
            yield return "libndi.dylib";
        }
    }

    // ---- native structures (x64 layouts asserted by tests) ------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct SendCreate
    {
        public IntPtr NdiName;   // UTF-8
        public IntPtr Groups;    // UTF-8, may be null
        [MarshalAs(UnmanagedType.U1)] public bool ClockVideo;
        [MarshalAs(UnmanagedType.U1)] public bool ClockAudio;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrameV2
    {
        public int Xres;
        public int Yres;
        public int FourCc;
        public int FrameRateN;
        public int FrameRateD;
        public float PictureAspectRatio; // 0 = square pixels
        public int FrameFormatType;
        public long Timecode;
        public IntPtr Data;
        public int LineStrideInBytes;
        public IntPtr Metadata;
        public long Timestamp;
    }

    // ---- native entry points ------------------------------------------------

    [DllImport(LibName, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool NDIlib_initialize();

    [DllImport(LibName, ExactSpelling = true)]
    public static extern IntPtr NDIlib_send_create(ref SendCreate createSettings);

    [DllImport(LibName, ExactSpelling = true)]
    public static extern void NDIlib_send_destroy(IntPtr instance);

    [DllImport(LibName, ExactSpelling = true)]
    public static extern void NDIlib_send_send_video_v2(IntPtr instance, ref VideoFrameV2 frame);

    [DllImport(LibName, ExactSpelling = true)]
    public static extern int NDIlib_send_get_no_connections(IntPtr instance, uint timeoutMs);

    /// <summary>Allocates a UTF-8 copy of a string for native use. Free with <see cref="Marshal.FreeHGlobal"/>.</summary>
    public static IntPtr Utf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }
}
