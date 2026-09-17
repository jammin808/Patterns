using System.Diagnostics;
using Avalonia;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Patterns.Core.Services.CultureGuard.Apply();                                              // round 70: the same numbers and words on every machine, before anything formats or parses
        // A host launch — this same exe doing one native job for the desk (the stream encoder) in a
        // process of its own — speaks its protocol on stdin/stdout and never touches the desk's world.
        if (Services.HostEntry.IsHostLaunch(args)) return Services.HostEntry.Run(args);
        // The runtime check (round 64): the built exe proves Skia, libVLC, the audio endpoints and
        // its modules without a window, for the release's CI and a rig's first look.
        if (Array.IndexOf(args, "--verify-runtime") >= 0) return Services.RuntimeCheck.Run(args);
        // The signal report (round 65): what Windows says it sends every display, and the EDID each
        // presented — proof of the observation layer on a real Windows, for CI and a rig's first look.
        if (Array.IndexOf(args, "--signal-report") >= 0) return Services.SignalReportCheck.Run(args);

        StartupBudget.MarkProcessStart();
        LaunchOptions.Parse(args);
        // A node: the same build as a separate process — the caller's desk, the arcade, a stage
        // timer — booting a fraction of the desk's services and never opening an output.
        if (LaunchOptions.Node is { } nodeWord)
        {
            if (NodeKinds.ParseLaunch(nodeWord) is { } kind) Services.AppServices.LaunchProfile = kind;
            else Log.Warn($"--node '{nodeWord}' is not a node kind (caller, arcade, timer) — this process starts as the desk.");
        }
        // A launch that named its folder (the standby twin a main runs on this machine lives in
        // its own, beside the main's): every store made from here on is that folder's.
        if (LaunchOptions.Home is { } home) SettingsStore.HomeOverride = home;

        // A plain launch becomes the watchdog and runs the real app as a child of the same
        // exe. `--no-watchdog` (or the Watchdog setting, or a debugger) runs it directly.
        if (!LaunchOptions.IsChild && !LaunchOptions.NoWatchdog && !Debugger.IsAttached &&
            Supervisor.ShouldSupervise())
        {
            return Supervisor.Run();
        }

        // A millisecond timer for the desk's paced loops (a sleep is a 15.6 ms tick without it).
        Services.TimerResolution.Raise();
        // One settings read before the desk: the GPU choice, the direct-output decision and the
        // desk itself (AppServices takes the state as read) — three reads of the show file were one.
        var store = new SettingsStore();
        ShowState? early = null;
        try
        {
            early = store.Load();
        }
        catch (Exception ex)
        {
            Log.Warn("The settings could not be read before the desk — the desk reads them itself.", ex);
        }
        // A standby launched by a main: it follows that main whatever its own file says, takes over
        // when the main stops beating, and keeps off the ports the main holds on this machine.
        if (LaunchOptions.StandbyOf is { } standbyOf)
        {
            if (Services.TwinLaunch.ParseStandbyOf(standbyOf) is { } main)
            {
                early ??= SettingsStore.Fresh();
                Services.TwinLaunch.ConfigureStandby(early, main.Host, main.Port, LaunchOptions.Key ?? "");
            }
            else
            {
                Log.Warn($"--standby-of '{standbyOf}' is not host:port — this desk starts as its file says.");
            }
        }
        StartupBudget.MarkEarly(StartupBudget.Settings);
        // Pick the GPU before Avalonia creates its D3D device (and before libVLC decodes).
        Services.GpuService.Initialize(early?.Admin.Graphics);
        // Then whether this start asks for the low-latency swap chain (direct output).
        Services.DirectOutputService.Initialize(store, early);
        StartupBudget.MarkEarly(StartupBudget.Graphics);
        // A crash or a hang leaves the previous run's render windows playing — the room keeps its
        // picture, which is right — but they answer to nobody. Take them back before a single
        // window of this run opens, so OUTPUTS ON never opens a second set behind the first.
        // Round 76: deferred — a run still playing keeps its windows up while this one boots and opens
        // its own over them; the ask (and the ending of a run that never answers) comes after, from the
        // desk, so the room sees one picture become the next and never the desktop between them.
        Services.OutputTakeover.ClaimAtStart(store.BaseDirectory, early?.Watchdog.TakeOverOutputs ?? true, defer: true);
        if (early is not null) Services.AppServices.Preloaded = (store, early);

        // An exception no handler contained (a worker thread's, or one the UI guard let through)
        // ends the process: the log gets the stack and the next start's health line gets the
        // exception's own words through the crash note, beside the exit code the watchdog sees.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Error($"Unhandled exception — {(ex is null ? e.ExceptionObject?.ToString() ?? "?" : FaultWords.Describe(ex))}", ex);
            Services.UiFaults.NoteFatal(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(LaunchOptions.Passthrough);
        }
        catch (Exception ex)
        {
            // Before the desk exists this is a start that failed; after it, the main loop ended in an
            // exception the UI guard could not reach (input arrives outside the dispatcher's jobs).
            var deskWasUp = Services.AppServices.Instance is not null;
            Log.Error((deskWasUp ? "The desk's main loop ended in an exception" : "Fatal startup failure") + $" — {FaultWords.Describe(ex)}", ex);
            Services.UiFaults.NoteFatal(ex);
            return deskWasUp ? ExitCodes.ClrException : 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                // Called when the compositor creates its D3D11 device (AngleEgl, the default):
                // answer with the adapter the settings resolved to (best card by default).
                GraphicsAdapterSelectionCallback = Services.GpuService.SelectAdapter,
                // Direct output: the flip-model swap chain first when an output asked for it at the
                // last save (and the card and the fuse allow it); the defaults are the fallbacks.
                CompositionMode = Services.DirectOutputService.CompositionModes(),
            })
            .With(new SkiaOptions
            {
                // Skia's GPU resource cache — textures, the compositor's surfaces — sized to the
                // machine: 64 MB small, 128 standard, 256 big, where Avalonia's default is one
                // number for every machine.
                MaxGpuResourceSizeBytes = Patterns.Core.Services.MemoryBudget.GpuCacheBytes(Patterns.Core.Services.MemoryBudget.MachineMB),
            })
            .WithInterFont()
            .LogToTrace();
}
