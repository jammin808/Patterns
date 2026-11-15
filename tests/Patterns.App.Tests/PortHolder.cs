using System.Globalization;

namespace Patterns.App.Tests;

/// <summary>
/// Who holds a TCP port in LISTEN, for a failure's words beside <see cref="TestListeners"/>: the kernel's socket tables
/// (<c>/proc/net/tcp</c> and <c>tcp6</c>) give the listening socket's inode, and the process whose descriptors name that
/// inode is the holder — this test host, or another process by its pid and name. Where <c>/proc</c> is not there (Windows)
/// or nothing matches, the words say "not known"; a reading never throws and never fails a test by itself.
/// </summary>
internal static class PortHolder
{
    private const string Listen = "0A";

    /// <summary>"this test host (pid N)", "pid N (name)", "nobody listens on it" or "not known (why)".</summary>
    public static string Of(int port)
    {
        var tables = new[] { "/proc/net/tcp", "/proc/net/tcp6" };
        if (!tables.Any(File.Exists)) return "not known (no /proc/net/tcp on this machine)";
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in tables.Where(File.Exists))
        {
            foreach (var line in ReadLines(table).Skip(1))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 10 || f[3] != Listen) continue;
                var colon = f[1].LastIndexOf(':');
                if (colon < 0 || !int.TryParse(f[1][(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p) || p != port) continue;
                if (f[9] != "0") inodes.Add(f[9]);
            }
        }
        if (inodes.Count == 0) return "nobody listens on it";
        var self = Environment.ProcessId;
        if (Holds(self, inodes)) return string.Create(CultureInfo.InvariantCulture, $"this test host (pid {self})");
        foreach (var dir in Directories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == self) continue;
            if (Holds(pid, inodes)) return string.Create(CultureInfo.InvariantCulture, $"pid {pid} ({Name(pid)})");
        }
        return $"not known (socket {string.Join(", ", inodes)} is in no descriptor this host may read)";
    }

    private static bool Holds(int pid, HashSet<string> inodes)
    {
        foreach (var fd in Files($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/fd"))
        {
            string? target;
            try
            {
                target = new FileInfo(fd).LinkTarget;
            }
            catch (IOException)
            {
                continue;                                                        // the descriptor closed while it was read
            }
            catch (UnauthorizedAccessException)
            {
                return false;                                                    // another user's process: its descriptors are not ours to read
            }
            if (target is { Length: > 9 } t && t.StartsWith("socket:[", StringComparison.Ordinal) && inodes.Contains(t[8..^1])) return true;
        }
        return false;
    }

    private static string Name(int pid)
    {
        var lines = ReadLines($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/comm");
        return lines.Length > 0 ? lines[0].Trim() : "name not known";
    }

    private static string[] ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return Array.Empty<string>();                                        // gone between the listing and the read
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();                                        // not this host's to read
        }
    }

    private static string[] Directories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (IOException)
        {
            return Array.Empty<string>();                                        // no listing: the holder stays not known
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();                                        // no listing: the holder stays not known
        }
    }

    private static string[] Files(string path)
    {
        try
        {
            return Directory.GetFiles(path);
        }
        catch (IOException)
        {
            return Array.Empty<string>();                                        // the process ended while it was read
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();                                        // another user's process
        }
    }
}
