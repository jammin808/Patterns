using System.Diagnostics;
using System.IO.Pipes;
using Patterns.Core.Services;

namespace Patterns.App;

/// <summary>Command-line switches the supervisor and its child pass between themselves.</summary>
internal static class LaunchOptions
{
    /// <summary>This process is the actual app, running under a supervisor.</summary>
    public static bool IsChild { get; private set; }

    /// <summary>Anonymous-pipe handle the child sends its UI-thread heartbeat on.</summary>
    public static string? BeatHandle { get; private set; }

    /// <summary>This launch follows a crash/hang — put the show back if it was live.</summary>
    public static bool Recover { get; private set; }

    /// <summary>Run without the supervisor even if the watchdog is enabled.</summary>
    public static bool NoWatchdog { get; private set; }

    /// <summary>How many times the watchdog has restarted the app this session.</summary>
    public static int Restarts { get; private set; }

    /// <summary>Anything not ours — forwarded to Avalonia (and to restarted children).</summary>
    public static string[] Passthrough { get; private set; } = Array.Empty<string>();

    public static void Parse(string[] args)
    {
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--child": IsChild = true; break;
                case "--no-watchdog": NoWatchdog = true; break;
                case "--recover": Recover = true; break;
                case "--beat" when i + 1 < args.Length: BeatHandle = args[++i]; break;
                case "--restarts" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n):
                    Restarts = n; i++; break;
                default: rest.Add(args[i]); break;
            }
        }
        Passthrough = rest.ToArray();
    }
}

/// <summary>
/// The watchdog: the plain launch becomes a tiny supervisor that runs the real app as a
/// child (same exe, --child), listens to a once-a-second heartbeat posted from the child's
/// UI thread, and restarts the child — with backoff, and a crash-loop cap decided by
/// <see cref="SupervisorPolicy"/> — when it crashes or the heartbeat goes silent. A clean
/// exit (the operator closed the app) ends both processes. No admin rights, no service
/// install, nothing to configure — it travels on the same USB stick.
/// </summary>
internal static class Supervisor
{
    /// <summary>Whether the plain launch should supervise (settings say so and we know our exe).</summary>
    public static bool ShouldSupervise()
    {
        if (Environment.ProcessPath is null) return false;
        try
        {
            return new SettingsStore().Load().Watchdog.Enabled;
        }
        catch
        {
            return true; // unreadable settings are exactly when a watchdog helps
        }
    }

    public static int Run()
    {
        var exe = Environment.ProcessPath!;
        var policy = new SupervisorPolicy();
        var restarts = 0;
        WLog($"Watchdog supervising {Path.GetFileName(exe)} (pid {Environment.ProcessId}).");

        while (true)
        {
            using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            psi.ArgumentList.Add("--child");
            psi.ArgumentList.Add("--beat");
            psi.ArgumentList.Add(pipe.GetClientHandleAsString());
            if (restarts > 0)
            {
                psi.ArgumentList.Add("--recover");
                psi.ArgumentList.Add("--restarts");
                psi.ArgumentList.Add(restarts.ToString());
            }
            foreach (var arg in LaunchOptions.Passthrough)
            {
                psi.ArgumentList.Add(arg);
            }

            Process child;
            try
            {
                child = Process.Start(psi) ?? throw new InvalidOperationException("no process");
            }
            catch (Exception ex)
            {
                WLog($"Could not start the app: {ex.Message}");
                return 1;
            }
            pipe.DisposeLocalCopyOfClientHandle();

            long lastBeatTicks = 0;
            var beatReader = new Thread(() =>
            {
                try
                {
                    var one = new byte[1];
                    while (pipe.Read(one, 0, 1) > 0)
                    {
                        Interlocked.Exchange(ref lastBeatTicks, DateTime.UtcNow.Ticks);
                    }
                }
                catch
                {
                    // Pipe closes with the child — the exit path takes over.
                }
            })
            { IsBackground = true, Name = "watchdog-heartbeat" };
            beatReader.Start();

            var startedUtc = DateTime.UtcNow;
            var killedForHang = false;
            while (!child.WaitForExit(1000))
            {
                var ticks = Interlocked.Read(ref lastBeatTicks);
                if (ticks != 0 && SupervisorPolicy.IsHung(new DateTime(ticks, DateTimeKind.Utc), DateTime.UtcNow))
                {
                    WLog($"UI heartbeat silent for {SupervisorPolicy.HangTimeout.TotalSeconds:0}s — ending the hung app.");
                    killedForHang = true;
                    try
                    {
                        child.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Racing a dying process is fine.
                    }
                    child.WaitForExit(10000);
                    break;
                }
            }

            int exitCode;
            try
            {
                exitCode = child.ExitCode;
            }
            catch
            {
                exitCode = -1;
            }
            child.Dispose();

            var verdict = policy.OnExit(killedForHang ? 1 : exitCode, killedForHang, DateTime.UtcNow - startedUtc, DateTime.UtcNow);
            switch (verdict.Action)
            {
                case SupervisorAction.Stop:
                    WLog("App closed cleanly — watchdog done.");
                    return exitCode;

                case SupervisorAction.GiveUp:
                    WLog("Crash loop: too many restarts in a short window. Standing down — check patterns.log.");
                    return exitCode == 0 ? 1 : exitCode;

                default:
                    restarts++;
                    WLog($"App {(killedForHang ? "hung" : $"crashed (exit {exitCode})")} after " +
                         $"{(DateTime.UtcNow - startedUtc).TotalSeconds:0}s — restart #{restarts} in {verdict.Delay.TotalSeconds:0}s.");
                    Thread.Sleep(verdict.Delay);
                    break;
            }
        }
    }

    /// <summary>The supervisor logs to its own file — no write races with the child's patterns.log.</summary>
    private static void WLog(string message)
    {
        try
        {
            var path = Path.Combine(new SettingsStore().BaseDirectory, "patterns.watchdog.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
            {
                File.Copy(path, path + ".old", overwrite: true);
                File.WriteAllText(path, "");
            }
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort, like the app log.
        }
    }
}
