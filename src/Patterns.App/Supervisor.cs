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
        UpdateRequest? pendingUpdate = null;   // the app exited to be updated: swap the files before the next start
        string? provingBackup = null;          // an update just landed: where the old files are, until the new app proves itself
        var provingVersion = "";
        WLog($"Watchdog supervising {Path.GetFileName(exe)} (pid {Environment.ProcessId}).");

        while (true)
        {
            if (pendingUpdate is { } update)
            {
                pendingUpdate = null;
                var (backup, version) = ApplyUpdate(update, exe);
                if (backup is not null)
                {
                    provingBackup = backup;
                    provingVersion = version;
                }
            }

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
                StandDown($"The watchdog could not start the app at {DateTime.Now:HH:mm}: {ex.Message} — see patterns.watchdog.log", "could-not-start");
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
            var ranFor = DateTime.UtcNow - startedUtc;

            // The first run of an updated build: it stays when it ran through the proving period (or was closed cleanly); otherwise the old files come back.
            if (provingBackup is { } proving)
            {
                var updatesDir = UpdatesDirectory();
                if (UpdateApply.Verdict(exitCode, killedForHang, ranFor) == "rollback")
                {
                    var back = UpdateApply.RollBack(proving, Path.GetDirectoryName(exe)!, Array.Empty<string>());
                    var note = $"Update to {provingVersion} rolled back at {DateTime.Now:HH:mm}: the new build {(killedForHang ? "hung" : $"exited with {exitCode}")} after {ranFor.TotalSeconds:0} s — {back.Message}";
                    WLog(note);
                    UpdateApply.WriteNote(updatesDir, note);
                    WatchdogMarker.Write(new SettingsStore().BaseDirectory, note);
                    provingBackup = null;
                    restarts++;
                    continue;   // the old build, straight away
                }
                var kept = $"Updated to {provingVersion} at {DateTime.Now:HH:mm} — the old files are in {proving}";
                WLog(kept);
                UpdateApply.WriteNote(updatesDir, kept);
                provingBackup = null;
            }

            if (exitCode == SupervisorPolicy.UpdateRequestExitCode && !killedForHang)
            {
                pendingUpdate = UpdateApply.ReadRequest(UpdatesDirectory());
                if (pendingUpdate is null) WLog("The app asked for an update but left no request — restarting as it is.");
            }

            var verdict = policy.OnExit(killedForHang ? 1 : exitCode, killedForHang, ranFor, DateTime.UtcNow);
            switch (verdict.Action)
            {
                case SupervisorAction.Stop:
                    WLog("App closed cleanly — watchdog done.");
                    return exitCode;

                case SupervisorAction.GiveUp:
                    WLog("Crash loop: too many restarts in a short window. Standing down — check patterns.log.");
                    StandDown($"The watchdog gave up at {DateTime.Now:HH:mm} after {restarts} restart{(restarts == 1 ? "" : "s")} in a short window — see patterns.watchdog.log", "gave-up");
                    return exitCode == 0 ? 1 : exitCode;

                default:
                    restarts++;
                    var why = killedForHang ? "hung"
                        : exitCode == SupervisorPolicy.RestartRequestExitCode ? "asked to restart (Machine page)"
                        : exitCode == SupervisorPolicy.UpdateRequestExitCode ? "asked to be updated"
                        : $"crashed (exit {exitCode})";
                    WLog($"App {why} after {(DateTime.UtcNow - startedUtc).TotalSeconds:0}s — " +
                         $"restart #{restarts} in {verdict.Delay.TotalSeconds:0}s.");
                    Thread.Sleep(verdict.Delay);
                    break;
            }
        }
    }

    private static string UpdatesDirectory() => UpdatePackage.Folder(new SettingsStore().BaseDirectory);

    /// <summary>
    /// The swap, between two starts of the app: the package's files in, the old ones into a backup
    /// folder (a rename, which Windows allows for the exe this very process runs from). Returns
    /// the backup folder to prove the new build against, or null when nothing changed — a package
    /// that does not read, a file that would not move — with the reason logged and noted.
    /// </summary>
    private static (string? Backup, string Version) ApplyUpdate(UpdateRequest request, string exe)
    {
        var updatesDir = UpdatesDirectory();
        UpdateApply.ClearRequest(updatesDir);
        var appDir = Path.GetDirectoryName(exe)!;
        var info = UpdatePackage.Inspect(request.Package, Path.GetFileName(exe));
        if (!info.Ok)
        {
            var refused = $"Update refused at {DateTime.Now:HH:mm}: {string.Join("; ", info.Problems)}";
            WLog(refused);
            UpdateApply.WriteNote(updatesDir, refused);
            return (null, info.Version);
        }
        var backup = UpdateApply.BackupFolderFor(updatesDir, DateTime.Now);
        WLog($"Applying update {info.Version} from {info.FileName}: {info.Files.Count} file(s), the old ones into {backup}.");
        var report = UpdateApply.Run(request.Package, appDir, backup, Path.GetFileName(exe));
        if (!report.Ok)
        {
            var failed = $"Update to {info.Version} failed at {DateTime.Now:HH:mm}: {report.Message}";
            WLog(failed);
            UpdateApply.WriteNote(updatesDir, failed);
            return (null, info.Version);
        }
        WLog($"Update {info.Version} in place: {report.Message}. Starting it — it has {UpdateApply.ProvingPeriod.TotalMinutes:0} minutes to prove itself.");
        try
        {
            File.Delete(request.Package);   // applied; a second apply would be the same files again
        }
        catch
        {
            // A package that cannot be deleted stays; the page reads it as staged and the same version.
        }
        return (backup, info.Version);
    }

    /// <summary>
    /// Standing down is never silent: a note beside the settings that the next start reads onto
    /// the health line, and — when the beacon is on — one last datagram so a backup machine hears
    /// that the show is down here, not merely quiet.
    /// </summary>
    private static void StandDown(string note, string eventName)
    {
        try
        {
            var store = new SettingsStore();
            WatchdogMarker.Write(store.BaseDirectory, note);
            var cfg = store.Load().Watchdog;
            if (cfg.BeaconEnabled) Services.BeaconService.SendEvent(cfg, eventName);
        }
        catch (Exception ex)
        {
            WLog($"Could not leave the stand-down note: {ex.Message}");
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
