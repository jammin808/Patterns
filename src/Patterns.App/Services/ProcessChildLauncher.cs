using System.Diagnostics;
using System.Runtime.InteropServices;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Starts a host as a real process: the same exe with <c>--host &lt;role&gt;</c> when this process
/// is Patterns.exe, or <c>dotnet Patterns.dll --host &lt;role&gt;</c> when it is not (the tests run
/// under a test host). Its stdin and stdout carry the <see cref="HostProtocol"/>; its stderr goes
/// to the log. On Windows the child joins a job object that ends it when this process ends, so a
/// desk that crashes never leaves an encoder streaming on its own.
/// </summary>
public sealed class ProcessChildLauncher : IChildLauncher
{
    public static readonly ProcessChildLauncher Default = new();

    public IChildHandle Launch(string role, Action<string> log)
    {
        var (file, lead) = HostCommand.Resolve();
        // UTF-8 both ways, said outright: without it Windows reads the host's lines in the console code
        // page and every dash and × in an ERROR or STATUS line reaches the Stream page as garbage.
        var utf8 = new System.Text.UTF8Encoding(false);
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var arg in lead) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(HostEntry.Switch);
        psi.ArgumentList.Add(role);
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"the {role} host did not start");
        ChildJob.Adopt(process);
        var stderr = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = process.StandardError.ReadLine()) is not null)
                {
                    if (line.Trim().Length > 0) log($"{role} stderr: {line}");
                }
            }
            catch
            {
                // The pipe closes with the host.
            }
        })
        { IsBackground = true, Name = $"{role}-host-stderr" };
        stderr.Start();
        return new ProcessChildHandle(process);
    }

    private sealed class ProcessChildHandle : IChildHandle
    {
        private readonly Process _process;

        public ProcessChildHandle(Process process) => _process = process;

        public int Pid => _process.Id;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch
                {
                    return true;
                }
            }
        }

        public int ExitCode
        {
            get
            {
                try
                {
                    return _process.ExitCode;
                }
                catch
                {
                    return -1;
                }
            }
        }

        public bool WriteLine(string line)
        {
            try
            {
                _process.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public string? ReadLine()
        {
            try
            {
                return _process.StandardOutput.ReadLine();
            }
            catch
            {
                return null;
            }
        }

        public void Kill()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }
        }

        public void Dispose()
        {
            try
            {
                _process.Dispose();
            }
            catch
            {
                // Nothing to release twice.
            }
        }
    }
}

/// <summary>How to start this very build again as a host: the exe itself, or dotnet with the app's dll.</summary>
public static class HostCommand
{
    public static (string File, IReadOnlyList<string> Lead) Resolve()
    {
        var exe = Environment.ProcessPath ?? "";
        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.Equals("Patterns", StringComparison.OrdinalIgnoreCase)) return (exe, Array.Empty<string>());
        // Not the published exe (a test host, dotnet itself): the app's dll under the dotnet muxer — this
        // process's own muxer when that is what runs it. Inside a single-file bundle Location is empty,
        // and there ProcessPath is the exe, taken above.
        var dll = typeof(AppServices).Assembly.Location;
        if (dll.Length > 0 && File.Exists(dll))
        {
            var muxer = name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? exe : "dotnet";
            return (muxer, new[] { dll });
        }
        return (exe, Array.Empty<string>());
    }
}

/// <summary>
/// Windows only: one job object for this process, closed by the kernel when the process ends,
/// with kill-on-close set — every host adopted into it goes when the desk goes, crash or not. On
/// the other platforms a host ends itself when its stdin closes, which a dead parent does.
/// </summary>
internal static class ChildJob
{
    private const uint KillOnJobClose = 0x00002000;
    private const int ExtendedLimitInformation = 9;
    private static readonly object Gate = new();
    private static IntPtr _job;
    private static bool _tried;

    public static void Adopt(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            lock (Gate)
            {
                if (!_tried)
                {
                    _tried = true;
                    _job = Create();
                }
            }
            if (_job == IntPtr.Zero) return;
            if (!AssignProcessToJobObject(_job, process.Handle))
            {
                Log.Warn($"Job object: could not adopt pid {process.Id} (error {Marshal.GetLastWin32Error()}) — a host outliving the desk would be ended by the next start instead.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Job object unavailable — a host outliving the desk would be ended by the next start instead.", ex);
        }
    }

    private static IntPtr Create()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JobObjectExtendedLimitInformation { BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, ExtendedLimitInformation, buffer, (uint)size))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
