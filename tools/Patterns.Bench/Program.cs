using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.Bench;

/// <summary>
/// The bench host: the real desk — Patterns.App's services, view model and main window — booted headless off Windows
/// through <see cref="HeadlessDesk"/>, the boot the App suite runs, so Stagewatch's interop lane can run Patterns beside
/// Stagewatch, PlayMap and AGMessage on one Linux kernel. Its settings are written before the boot from a fresh show
/// and the options; the token and a Stagewatch key come from files. It is not the exe: no watchdog, no recovery after a
/// relaunch, no EDIT SAFE by default, no RESTART (refused with words, as a session with no way to restart refuses it),
/// no libVLC (clips are synthetic or none) and no WebView2 (web pages are Windows'). Standard output carries the ready
/// line first — its ports, its show network id, its build — then one line per control answer, and never a secret.
/// </summary>
internal static class Program
{
    /// <summary>How long the control listeners have to take their ports before the start is a failure: inside the lane's 60 s ready deadline.</summary>
    private static readonly TimeSpan ListenWithin = TimeSpan.FromSeconds(50);

    public static int Main(string[] args)
    {
        CultureGuard.Apply();                                                                  // the same numbers and words on every machine, before anything formats or parses
        using var output = new BenchOut(Console.OpenStandardOutput());
        var options = BenchOptions.Parse(args, out var refusal);
        if (options is null) return Refuse(output, refusal);
        var token = ReadSecret(options.TokenFile, "the token file", out refusal);
        if (token is null) return Refuse(output, refusal);
        if (!PairingToken.Needed(token)) return Refuse(output, "the token file holds no token: the bench never runs a desk open to the network");
        // The Remote page's entry rule (Show Bus 1.3 §13.5.1, §13.6.8), in its words and never the token's: the bench enters a
        // token as the desk's box does, so its two forms agree on letters and digits.
        if (PairingToken.EntryRefusal(token) is { } why) return Refuse(output, "the token file's token is not taken — " + why);
        var key = "";
        if (options.StagewatchKeyFile is { } keyFile)
        {
            var read = ReadSecret(keyFile, "the Stagewatch key file", out refusal);
            if (read is null) return Refuse(output, refusal);
            key = read;
        }

        var store = new SettingsStore(options.Home);
        try
        {
            if (!TakeHome(options.Home, out refusal)) return Refuse(output, refusal);
            store.Save(Settings(options, token, key));
        }
        catch (IOException ex)
        {
            return Refuse(output, $"the settings could not be written to the home folder: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Refuse(output, $"the settings could not be written to the home folder: {ex.Message}");
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Error($"Unhandled exception on the bench — {(ex is null ? e.ExceptionObject?.ToString() ?? "?" : FaultWords.Describe(ex))}", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };

        VideoEngine.LibVlcAllowed = false;                                                     // before the desk is built: its first validation asks for the decoder
        ControlService.TrustLoopback = options.TrustLoopback;
        AppBuilder.Configure<global::Patterns.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })     // real Skia drawing into a headless frame buffer, as the App suite draws
            .UseSkia()
            .SetupWithoutStarting();

        var (services, vm) = HeadlessDesk.Create(store);
        var clips = new BenchClips(options.Home, options.SyntheticClips);
        services.Video.SourceFactory = clips.Open;
        // The cue validator asks whether a video stinger can play: here it can exactly when the bench opens clips, as
        // every video file opens as a synthetic clip — never libVLC's answer, which this process never asks.
        services.ValidationVideoOverride = () => clips.Synthetic;
        var window = HeadlessDesk.Show(services, vm);

        using var stop = new CancellationTokenSource();
        var exitCode = 0;
        void Stop(int code)
        {
            if (stop.IsCancellationRequested) return;
            exitCode = code;
            stop.Cancel();
        }

        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Signalled);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Signalled);
        void Signalled(PosixSignalContext context)
        {
            context.Cancel = true;                                                             // the desk shuts down as it does at a close, never mid-step
            Dispatcher.UIThread.Post(() => Stop(0));
        }

        var control = new BenchControl(services, vm, output, clips, Stop);
        var since = ShowClock.Seconds;
        bool WaitForListeners()
        {
            if (stop.IsCancellationRequested) return false;
            if (services.Control.Listening)
            {
                output.Line(Ready(services, options, clips, key.Length > 0));
                if (options.ControlOnStdin) ReadControl(control);
                if (options.ExitAfterSeconds is { } seconds) DispatcherTimer.RunOnce(() => Stop(0), TimeSpan.FromSeconds(seconds));
                return false;
            }
            if (ShowClock.Seconds - since < ListenWithin.TotalSeconds) return true;
            output.Line(new { patterns = "failed", error = $"the control listeners did not open within {ListenWithin.TotalSeconds:0} s — {services.Control.Status}" });
            Stop(1);
            return false;
        }
        if (WaitForListeners()) DispatcherTimer.Run(WaitForListeners, TimeSpan.FromMilliseconds(100));

        Dispatcher.UIThread.MainLoop(stop.Token);

        window.Close();
        services.Shutdown();
        return exitCode;
    }

    /// <summary>A start refused before the desk is built: the words as stdout's first line, exit code 2.</summary>
    private static int Refuse(BenchOut output, string words)
    {
        output.Line(new { patterns = "refused", error = words });
        return 2;
    }

    /// <summary>The file a home the bench made or took holds; a folder without it that holds anything is not the bench's.</summary>
    private const string HomeMarker = "patterns.bench";

    /// <summary>The bits that let another user list, enter or change a folder.</summary>
    private const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// The home made or taken as the bench's own: the token and a Stagewatch key are written into its settings, so on Unix a
    /// home the bench makes is this user's alone (0700) and a home another user can enter is refused; a folder that holds
    /// anything the bench did not put there (a desk's own show) is refused, never written over. False and the words when refused.
    /// </summary>
    private static bool TakeHome(string home, out string refusal)
    {
        refusal = "";
        var marker = Path.Combine(home, HomeMarker);
        if (!Directory.Exists(home))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(home);
            else Directory.CreateDirectory(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(home) & Others) != 0)
            {
                refusal = "the home folder can be entered by other users, and the token and the Stagewatch key are written there: make it 0700, or name a folder the bench makes";
                return false;
            }
            if (!File.Exists(marker) && Directory.EnumerateFileSystemEntries(home).Any())
            {
                refusal = "the home folder holds files the bench did not write: --home is the bench's own folder, never a desk's";
                return false;
            }
        }
        File.WriteAllText(marker, "The Patterns bench's home: its show is written fresh at every start.\n");
        return true;
    }

    /// <summary>The first line of a secret's file, trimmed; null and the words when it cannot be read. The words never hold the file's contents.</summary>
    private static string? ReadSecret(string path, string what, out string refusal)
    {
        refusal = "";
        try
        {
            using var reader = new StreamReader(path);
            return (reader.ReadLine() ?? "").Trim();
        }
        catch (IOException ex)
        {
            refusal = $"{what} cannot be read: {ex.GetType().Name}";
        }
        catch (UnauthorizedAccessException ex)
        {
            refusal = $"{what} cannot be read: {ex.GetType().Name}";
        }
        return null;
    }

    /// <summary>The show the bench starts on: a fresh one, its doors, its token and its link as the options say; no mDNS and no OSC.</summary>
    private static ShowState Settings(BenchOptions options, string token, string key)
    {
        var s = SettingsStore.Fresh();
        if (options.Name is { } name)
        {
            s.Name = name;
            s.Watchdog.BeaconName = name;                                                      // the name the hello says
        }
        var c = s.Control;
        c.Enabled = true;
        c.HttpPort = options.Http;
        c.TcpPort = options.Wire;
        c.Bind = options.Bind;
        c.Token = token;
        c.Announce = false;
        c.OscEnabled = false;
        c.Stagewatch.Enabled = options.Stagewatch is not null;
        c.Stagewatch.Host = options.Stagewatch ?? "";
        c.Stagewatch.ApiKey = key;
        c.Stagewatch.FollowStage = true;
        // No caller node may link: the twin's port stays shut and no twin key is made — a door the ready line does not name
        // and a secret the desk would say aloud. With it the beacon goes quiet too (a fresh show's Machine page has it off).
        s.Twin.AcceptCallers = false;
        return s;
    }

    /// <summary>The ready line: what the listeners hold, who this desk is on the show network, what the bench is — and no secret.</summary>
    private static object Ready(AppServices services, BenchOptions options, BenchClips clips, bool keyed)
    {
        var c = services.State.Control;
        return new
        {
            patterns = "ready",
            http = c.HttpPort,
            wire = c.TcpPort,
            node = services.Kernel.ShowBusId,
            build = AppVersion.Current,
            clips = clips.Word,
            stagewatch = options.Stagewatch is null ? null : options.Stagewatch.Length == 0 ? "find" : options.Stagewatch,
            key = keyed,
            trustLoopback = ControlService.TrustLoopback,
            bind = c.Bind,
            hello = services.Beacon.Announcing,
        };
    }

    /// <summary>
    /// Control lines from standard input, read on a thread of their own and each run on the UI thread in the order read;
    /// the end of the input stops the bench as SIGTERM does, once the lines before it have answered. A line is held to
    /// <see cref="BenchControl.MaxLine"/> characters as it is read and the reader waits while
    /// <see cref="BenchControl.MaxWaiting"/> lines are unanswered, so a driver that floods the input holds a bounded amount.
    /// </summary>
    private static void ReadControl(BenchControl control)
    {
        var reader = new Thread(() =>
        {
            using var input = new StreamReader(Console.OpenStandardInput());
            while (true)
            {
                string? line;
                try
                {
                    line = BenchControl.ReadLine(input);
                }
                catch (IOException)
                {
                    line = null;                                                               // the input broke: as its end
                }
                if (line is not null) control.WaitForRoom();                                       // no more than MaxWaiting lines wait for the UI thread
                Dispatcher.UIThread.Post(() => control.Enqueue(line));                             // null, the end, is queued behind the lines before it
                if (line is null) return;
            }
        })
        {
            IsBackground = true,
            Name = "bench control",
        };
        reader.Start();
    }
}
