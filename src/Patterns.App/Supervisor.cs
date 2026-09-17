using System.Globalization;
using Patterns.Devices;
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

    /// <summary>The folder this desk lives in — its settings, logs, backups — when the launch named one; null is the portable folder beside the exe.</summary>
    public static string? Home { get; private set; }

    /// <summary>This desk is a standby launched by a main: "host:port" of the main it follows and takes over from.</summary>
    public static string? StandbyOf { get; private set; }

    /// <summary>The twin key the launch carries for a standby.</summary>
    public static string? Key { get; private set; }
    /// <summary>This process is a node — "caller", "arcade", "timer": a separate process of this build that boots a fraction of the desk (<see cref="Patterns.Core.Model.NodeKind"/>); null is the desk.</summary>
    public static string? Node { get; private set; }

    /// <summary>--footprint &lt;file&gt;: a node writes its composition and the modules it loaded to the file and exits — the separate-process footprint claim, read by the Windows lane (round 64).</summary>
    public static string? Footprint { get; private set; }

    /// <summary>Anything not ours — forwarded to Avalonia (and to restarted children).</summary>
    public static string[] Passthrough { get; private set; } = Array.Empty<string>();

    /// <summary>The switches a supervised child needs again: the folder and the standby's main travel with it.</summary>
    public static IEnumerable<string> Forwarded()
    {
        if (Home is { } home) { yield return "--home"; yield return home; }
        if (StandbyOf is { } of) { yield return "--standby-of"; yield return of; }
        if (Key is { } key) { yield return "--key"; yield return key; }
        if (Node is { } node) { yield return "--node"; yield return node; }
    }

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
                case "--home" when i + 1 < args.Length: Home = args[++i]; break;
                case "--standby-of" when i + 1 < args.Length: StandbyOf = args[++i]; break;
                case "--key" when i + 1 < args.Length: Key = args[++i]; break;
                case "--node" when i + 1 < args.Length: Node = args[++i]; break;
                case "--footprint" when i + 1 < args.Length: Footprint = args[++i]; break;
                case "--restarts" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n):
                    Restarts = n; i++; break;
                default: rest.Add(args[i]); break;
            }
        }
        Passthrough = rest.ToArray();
    }
}

/// <summary>
/// Round 76: the byte the child's UI thread writes to the supervisor once a second — alive, or
/// alive and asking to be replaced (a restart with the outputs live: the replacement boots beside
/// this desk, which keeps its windows up until the replacement has its own over them).
/// </summary>
public static class WatchdogBeat
{
    private static volatile byte _value = SupervisorPolicy.AliveBeat;

    public static byte Value
    {
        get => _value;
        set => _value = value;
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
        var nativeFaultsInARow = 0;
        UpdateRequest? pendingUpdate = null;   // the app exited to be updated: swap the files before the next start
        string? provingBackup = null;          // an update just landed: where the old files are, until the new app proves itself
        var provingVersion = "";
        var baseDirectory = new SettingsStore().BaseDirectory;
        WLog($"Watchdog supervising {Path.GetFileName(exe)} (pid {Environment.ProcessId}).");

        // A crash nobody can reproduce still leaves something a debugger reads: the runtime writes
        // a mini-dump of a native fault when createdump sits beside the exe; the newest few are kept.
        var dumpEnvironment = CrashDumps.Environment(baseDirectory);
        var dumpsKept = CrashDumps.Sweep(baseDirectory);
        var createDump = Path.Combine(Path.GetDirectoryName(exe)!, "createdump.exe");
        WLog(File.Exists(createDump)
            ? $"Mini-dumps on a native crash: on, into {CrashDumps.DirectoryFor(baseDirectory)} ({dumpsKept.Count} kept)."
            : "Mini-dumps on a native crash: off — createdump.exe is not beside the exe.");

        // Round 76: a replacement the running child asked for, booting beside it. When the child
        // leaves after the handover the replacement is the child — nothing is started.
        ChildRun? next = null;

        while (true)
        {
            ChildRun child;
            if (next is not null)
            {
                child = next;
                next = null;
            }
            else
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

                var started = ChildRun.Start(exe, restarts, dumpEnvironment, out var startError);
                if (started is null)
                {
                    WLog($"Could not start the app: {startError}");
                    StandDown($"The watchdog could not start the app at {DateTime.Now:HH:mm}: {startError} — see patterns.watchdog.log", "could-not-start");
                    return 1;
                }
                child = started;
            }

            var killedForHang = false;
            var startupHang = false;
            while (!child.WaitExit(1000))
            {
                // Round 76: the child asks to be replaced — a restart with the outputs live. The
                // replacement starts now, beside it, with --recover; the child keeps its windows and
                // its sound up until the replacement has its own over them and asks for the screens.
                if (child.HandoverAsked && next is null && !child.HandoverRefused)
                {
                    var replacement = ChildRun.Start(exe, restarts + 1, dumpEnvironment, out var error);
                    if (replacement is null)
                    {
                        child.HandoverRefused = true;
                        WLog($"App asked to be replaced but the replacement could not start: {error} — the app carries on (it says so itself).");
                    }
                    else
                    {
                        restarts++;
                        next = replacement;
                        WLog($"App asked to be replaced — the replacement (pid {replacement.Pid}) is starting beside it as restart #{restarts}; the screens stay lit.");
                    }
                }
                // A replacement that ended before it took the screens: the running app carries on.
                if (next is not null && next.HasExited)
                {
                    WLog($"The replacement (pid {next.Pid}) exited with {next.ExitCode} before taking the screens — the running app carries on.");
                    next.Dispose();
                    next = null;
                    child.HandoverRefused = true;
                }

                var ticks = child.LastBeatUtc;
                // Before the first beat the startup deadline judges the child, after it the hang
                // timeout (round 65): a child that wedges in the graphics device, a takeover or the
                // desk's construction never beats at all, and used to be waited for forever.
                var phase = SupervisorPolicy.Phase(child.StartedUtc, ticks, DateTime.UtcNow);
                if (phase is SupervisorPolicy.ChildPhase.Starting or SupervisorPolicy.ChildPhase.Beating) continue;
                {
                    startupHang = phase == SupervisorPolicy.ChildPhase.StartupHang;
                    WLog(startupHang
                        ? $"No first heartbeat in {SupervisorPolicy.StartupDeadline.TotalSeconds:0}s — the app never became a desk; ending it."
                        : $"UI heartbeat silent for {SupervisorPolicy.HangTimeout.TotalSeconds:0}s — ending the hung app.");
                    killedForHang = true;
                    child.Kill();
                    break;
                }
            }

            var exitCode = child.ExitCodeOrUnknown();
            var startedUtc = child.StartedUtc;
            child.Dispose();
            var ranFor = DateTime.UtcNow - startedUtc;

            // Round 76: the child left with its replacement up — the handover happened (84), or the
            // old desk crashed on its way out. Either way the replacement is the desk now, and
            // nothing is started; the loop goes on watching it.
            if (next is not null)
            {
                WLog(exitCode == SupervisorPolicy.ReplacedExitCode && !killedForHang
                    ? $"App handed its screens to the replacement (pid {next.Pid}) after {ranFor.TotalSeconds:0}s and left — the room never saw the desktop."
                    : $"App {(killedForHang ? "hung" : $"exited with {exitCode} ({ExitCodes.Describe(exitCode)})")} while its replacement (pid {next.Pid}) is up — the replacement is the app now.");
                continue;
            }

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
                    var native = !killedForHang && ExitCodes.IsNativeFault(exitCode);
                    nativeFaultsInARow = native ? nativeFaultsInARow + 1 : 0;
                    var why = killedForHang ? (startupHang ? "hung before its first heartbeat" : "hung")
                        : exitCode == SupervisorPolicy.RestartRequestExitCode ? "asked to restart (Machine page)"
                        : exitCode == SupervisorPolicy.UpdateRequestExitCode ? "asked to be updated"
                        : exitCode == SupervisorPolicy.ReplacedExitCode ? "left after a handover whose replacement is not running"
                        : $"crashed (exit {exitCode} = {ExitCodes.Hex(exitCode)}, {ExitCodes.Describe(exitCode)})";
                    WLog($"App {why} after {(DateTime.UtcNow - startedUtc).TotalSeconds:0}s — " +
                         $"restart #{restarts} in {verdict.Delay.TotalSeconds:0}s.");
                    if (killedForHang || exitCode is not (SupervisorPolicy.RestartRequestExitCode or SupervisorPolicy.UpdateRequestExitCode or SupervisorPolicy.ReplacedExitCode))
                    {
                        // The next start reads this onto its health line and into its log, and after a
                        // native fault decodes clips in software for that run.
                        var dump = native ? CrashDumps.NewestSince(baseDirectory, startedUtc) : "";
                        // A managed crash: the app wrote a note on its way down with the exception's own
                        // words — those are kept under the exit code this process saw.
                        var own = CrashMarker.Peek(baseDirectory);
                        var detail = own is { } o && o.AtUtc >= startedUtc.AddSeconds(-5) ? o.Detail : "";
                        if (startupHang && detail.Length == 0) detail = $"startup hang: no first heartbeat within {SupervisorPolicy.StartupDeadline.TotalSeconds:0} s";
                        CrashMarker.Write(baseDirectory, new CrashNote(exitCode, ExitCodes.Describe(exitCode), native, killedForHang,
                            DateTime.UtcNow, ranFor.TotalSeconds, dump, nativeFaultsInARow, detail));
                        if (detail.Length > 0) WLog($"The app's own note: {detail}");
                        if (dump.Length > 0) WLog($"Mini-dump written: {dump}");
                    }
#pragma warning disable RS0030 // the supervisor's restart back-off, on its own main thread: waiting is this thread's job
                    Thread.Sleep(verdict.Delay);
#pragma warning restore RS0030
                    break;
            }
        }
    }

    /// <summary>
    /// One child of the supervisor: its process, the pipe its UI thread beats on, the reader of
    /// that pipe, and what it said last — alive, or alive and asking to be replaced (round 76).
    /// </summary>
    private sealed class ChildRun : IDisposable
    {
        private readonly Process _process;
        private readonly AnonymousPipeServerStream _pipe;
        private readonly ManualResetEventSlim _exited = new(false);
        private long _lastBeatTicks;
        private volatile bool _handoverAsked;

        private ChildRun(Process process, AnonymousPipeServerStream pipe)
        {
            _process = process;
            _pipe = pipe;
            StartedUtc = DateTime.UtcNow;
        }

        public DateTime StartedUtc { get; }

        public int Pid
        {
            get
            {
                try { return _process.Id; } catch { return 0; }
            }
        }

        /// <summary>The child beat the handover byte: it asks for a replacement beside it.</summary>
        public bool HandoverAsked => _handoverAsked;

        /// <summary>A replacement was tried for this child and failed; a second ask starts nothing more — the child carries on and says so itself.</summary>
        public bool HandoverRefused { get; set; }

        public DateTime? LastBeatUtc
        {
            get
            {
                var ticks = Interlocked.Read(ref _lastBeatTicks);
                return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        public bool HasExited
        {
            get
            {
                try { return _exited.IsSet || _process.HasExited; } catch { return true; }
            }
        }

        public int ExitCode => ExitCodeOrUnknown();

        public static ChildRun? Start(string exe, int restarts, IReadOnlyDictionary<string, string> environment, out string error)
        {
            error = "";
            var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
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
                psi.ArgumentList.Add(restarts.ToString(CultureInfo.InvariantCulture));
            }
            foreach (var arg in LaunchOptions.Forwarded())
            {
                psi.ArgumentList.Add(arg);
            }
            foreach (var arg in LaunchOptions.Passthrough)
            {
                psi.ArgumentList.Add(arg);
            }
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }

            Process process;
            try
            {
                process = Process.Start(psi) ?? throw new InvalidOperationException("no process");
            }
            catch (Exception ex)
            {
                error = ex.Message;
                pipe.Dispose();
                return null;
            }
            pipe.DisposeLocalCopyOfClientHandle();
            var run = new ChildRun(process, pipe);

            // The child's exit wakes the loop at once: a manual restart used to wait out the rest
            // of a one-second poll before the next start.
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => run._exited.Set();
                if (process.HasExited) run._exited.Set();
            }
            catch
            {
                // No exit event on this host: the poll still notices within a second.
            }

            var beatReader = new Thread(() =>
            {
                try
                {
                    var one = new byte[1];
                    while (pipe.Read(one, 0, 1) > 0)
                    {
                        Interlocked.Exchange(ref run._lastBeatTicks, DateTime.UtcNow.Ticks);
                        if (one[0] == SupervisorPolicy.HandoverBeat) run._handoverAsked = true;
                    }
                }
                catch
                {
                    // Pipe closes with the child — the exit path takes over.
                }
            })
            { IsBackground = true, Name = "watchdog-heartbeat" };
            beatReader.Start();
            return run;
        }

        /// <summary>True once the child has exited; waits up to the time given.</summary>
        public bool WaitExit(int milliseconds)
        {
            try
            {
                return _exited.Wait(milliseconds) || _process.WaitForExit(0);
            }
            catch
            {
                return true;
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
                // Racing a dying process is fine.
            }
            try { _process.WaitForExit(10000); } catch { /* gone */ }
        }

        public int ExitCodeOrUnknown()
        {
            try
            {
                _process.WaitForExit();   // the exit is known; this lets the runtime finish reading it
                return _process.ExitCode;
            }
            catch
            {
                return -1;
            }
        }

        public void Dispose()
        {
            try { _process.Dispose(); } catch { /* already gone */ }
            try { _pipe.Dispose(); } catch { /* closed with the child */ }
            _exited.Dispose();
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
            if (cfg.BeaconEnabled) BeaconService.SendEvent(cfg, eventName);
        }
        catch (Exception ex)
        {
            WLog($"Could not leave the stand-down note: {ex.Message}");
        }
    }

    private static string? _logDirectory;

    /// <summary>The supervisor logs to its own file — no write races with the child's patterns.log.</summary>
    private static void WLog(string message)
    {
        try
        {
            _logDirectory ??= new SettingsStore().BaseDirectory;   // resolved once, not per line
            var path = Path.Combine(_logDirectory, "patterns.watchdog.log");
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
