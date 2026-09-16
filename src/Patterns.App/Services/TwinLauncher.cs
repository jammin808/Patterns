using System.Globalization;
using System.Diagnostics;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// How a standby launched by this desk is started: the arguments the launch carries, the address
/// they name, and the settings a launched standby runs with whatever its own file says. Pure, so
/// the launch is unit tested without a process.
/// </summary>
public static class TwinLaunch
{
    /// <summary>The switches: its own folder, the main's address on this machine, the key, and no watchdog of its own — the main is its watchdog.</summary>
    public static IReadOnlyList<string> Arguments(string home, int port, string key)
    {
        var args = new List<string> { "--home", home, "--standby-of", $"127.0.0.1:{port}" };
        if (key.Length > 0)
        {
            args.Add("--key");
            args.Add(key);
        }
        args.Add("--no-watchdog");
        return args;
    }

    /// <summary>"host:port" — the pair, or null for anything else (a bracketed IPv6 address is fine).</summary>
    public static (string Host, int Port)? ParseStandbyOf(string? text)
    {
        var s = (text ?? "").Trim();
        var i = s.LastIndexOf(':');
        if (i <= 0 || i == s.Length - 1) return null;
        if (!int.TryParse(s[(i + 1)..], out var port) || port < 1024 || port > 65535) return null;
        var host = s[..i].Trim().Trim('[', ']');
        return host.Length == 0 ? null : (host, port);
    }

    /// <summary>
    /// A launched standby follows the main that started it, takes over by itself when that main
    /// stops beating, and keeps off the ports the main holds on this machine: no watchdog of its
    /// own (the main restarts it), no beacon (the main's names the machine), no remote-control
    /// ports (the main's). Its outputs are held closed by the role.
    /// </summary>
    public static void ConfigureStandby(ShowState state, string host, int port, string key)
    {
        state.Twin.Role = TwinRole.Standby;
        state.Twin.MainHost = host;
        state.Twin.Port = port;
        state.Twin.Key = key;
        state.Twin.AutoTakeOver = true;
        state.Twin.LocalStandby = false;
        state.Watchdog.Enabled = false;
        state.Watchdog.BeaconEnabled = false;
        state.Watchdog.BeaconListen = false;
        state.Control.Enabled = false;
        state.Control.OscEnabled = false;
    }
}

/// <summary>
/// The standby a main runs on this machine, as a process: started with the launch's arguments,
/// looked at once a second, started again after a growing pause when it exits — and left alone
/// when it has the show: a desk that closes never ends the process driving the room. No job
/// object here on purpose: the standby must outlive a main that crashes, which is the point of it.
/// A standby already alive in the folder (started by the main this one replaced) is adopted, not
/// doubled.
/// </summary>
public sealed class TwinLauncher : IDisposable
{
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(30);

    private readonly Func<DateTime> _clock;
    private IChildHandle? _child;
    private string? _home;
    private int _port;
    private string _key = "";
    private DateTime? _nextStartUtc;
    private TimeSpan _retry = FirstRetry;
    private int _starts;
    private int? _lastExitCode;
    private string? _lastStartError;
    private bool _adopted;

    public TwinLauncher(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>How a process is started: the file and its arguments in, a handle out; the tests hand in a fake.</summary>
    public Func<string, IReadOnlyList<string>, IChildHandle?> Spawn { get; set; } = SpawnProcess;

    /// <summary>Whether a Patterns already owns the folder — its instance mutex is held; the tests answer without one.</summary>
    public Func<string, bool> FolderOwned { get; set; } = IsFolderOwned;

    public bool Wanted => _home is not null;

    public string? Home => _home;

    /// <summary>How many processes this desk started.</summary>
    public int Starts => _starts;

    /// <summary>The running child's id, or null: none, exited, or one adopted from the previous main.</summary>
    public int? Pid => _child is { } c && !Exited(c) ? c.Pid : null;

    /// <summary>What the main wants: a standby in this folder, dialled to this port with this key — or none.</summary>
    public void Want(string? home, int port, string key, bool standbyHoldsShow)
    {
        if (home == _home && port == _port && key == _key) return;
        if (_home is not null) End(standbyHoldsShow);
        _home = home;
        _port = port;
        _key = key;
        _retry = FirstRetry;
        _nextStartUtc = null;
        _adopted = false;
        _lastStartError = null;
    }

    /// <summary>Once a second: start it, or start it again after it exited — never while a standby has the show or one already owns the folder.</summary>
    public void Tick(bool standbyHoldsShow)
    {
        if (_home is null) return;
        var now = _clock();
        if (_child is { } child)
        {
            if (!Exited(child)) return;
            _lastExitCode = ExitCode(child);
            child.Dispose();
            _child = null;
            _nextStartUtc = now + _retry;
            Log.Warn($"Twin: the standby process exited (code {_lastExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}); starting it again in {_retry.TotalSeconds:0} s.");
            _retry = TimeSpan.FromTicks(Math.Min(_retry.Ticks * 2, MaxRetry.Ticks));
            return;
        }
        if (standbyHoldsShow) return;
        if (_nextStartUtc is { } at && now < at) return;
        if (FolderOwned(_home))
        {
            // The standby the previous main started is still up: it is the one, and it dials this desk by itself.
            if (!_adopted) Log.Info("Twin: a standby already owns the twin-standby folder — adopted, not started again.");
            _adopted = true;
            _nextStartUtc = now + FirstRetry;
            return;
        }
        _adopted = false;
        Start();
    }

    private void Start()
    {
        var (file, lead) = HostCommand.Resolve();
        var args = lead.Concat(TwinLaunch.Arguments(_home!, _port, _key)).ToList();
        try
        {
            Directory.CreateDirectory(_home!);
            _child = Spawn(file, args) ?? throw new InvalidOperationException("no process");
            _starts++;
            _nextStartUtc = null;
            _lastStartError = null;
            Log.Info($"Twin: started the standby process (pid {_child.Pid}) in {_home}.");
        }
        catch (Exception ex)
        {
            // A folder that cannot be made (a read-only show drive, a path the account may not
            // write), an exe that will not start: said on the Machine page, not only in the log.
            _lastStartError = ex.Message;
            _nextStartUtc = _clock() + _retry;
            Log.Warn($"Twin: the standby process could not be started — {ex.Message}; trying again in {_retry.TotalSeconds:0} s.", ex);
            _retry = TimeSpan.FromTicks(Math.Min(_retry.Ticks * 2, MaxRetry.Ticks));
        }
    }

    /// <summary>The launcher's clause of the Machine page's line.</summary>
    public string Words
    {
        get
        {
            if (_home is null) return "";
            if (_child is { } c && !Exited(c)) return $"Standby process running (pid {c.Pid}).";
            if (_adopted) return "Standby process running (started by the previous main).";
            if (_nextStartUtc is { } at)
            {
                var wait = Math.Max(0, (at - _clock()).TotalSeconds);
                if (_lastStartError is { } why) return $"Standby process could not be started — {why}; trying again in {wait:0} s.";
                return _lastExitCode is { } code ? $"Standby process exited (code {code}); starting again in {wait:0} s." : $"Standby process starting in {wait:0} s.";
            }
            return "Standby process starting…";
        }
    }

    /// <summary>A clean exit of the main, or the standby switched off: the process ends with it — unless it has the show.</summary>
    public void End(bool standbyHoldsShow)
    {
        if (_child is { } c)
        {
            if (!standbyHoldsShow && !Exited(c))
            {
                try
                {
                    c.Kill();
                    Log.Info($"Twin: ended the standby process (pid {c.Pid}).");
                }
                catch (Exception ex)
                {
                    Log.Warn("Twin: the standby process could not be ended.", ex);
                }
            }
            c.Dispose();
            _child = null;
        }
        _home = null;
        _nextStartUtc = null;
        _adopted = false;
    }

    public void Dispose() => End(standbyHoldsShow: false);

    private static bool Exited(IChildHandle c)
    {
        try { return c.HasExited; }
        catch { return true; }
    }

    private static int? ExitCode(IChildHandle c)
    {
        try { return c.ExitCode; }
        catch { return null; }
    }

    private static bool IsFolderOwned(string home)
    {
        try
        {
            using var mutex = Mutex.OpenExisting("PatternsApp-" + AppServices.StableFolderKey(home));
            return true;
        }
        catch (Exception)
        {
            return false; // no such mutex: nobody has the folder
        }
    }

    private static IChildHandle? SpawnProcess(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var process = Process.Start(psi);
        return process is null ? null : new PlainProcessHandle(process);
    }

    /// <summary>A process with nothing piped: the standby is a whole desk with its own window.</summary>
    private sealed class PlainProcessHandle : IChildHandle
    {
        private readonly Process _process;

        public PlainProcessHandle(Process process) => _process = process;

        public int Pid => _process.Id;

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch { return true; }
            }
        }

        public int ExitCode
        {
            get
            {
                try { return _process.ExitCode; }
                catch { return -1; }
            }
        }

        public bool WriteLine(string line) => false;

        public string? ReadLine() => null;

        public void Kill()
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }

        public void Dispose()
        {
            try { _process.Dispose(); }
            catch { /* nothing to release twice */ }
        }
    }
}
