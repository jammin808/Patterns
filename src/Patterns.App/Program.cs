using System.Diagnostics;
using Avalonia;
using Patterns.Core.Services;

namespace Patterns.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // A host launch — this same exe doing one native job for the desk (the stream encoder) in a
        // process of its own — speaks its protocol on stdin/stdout and never touches the desk's world.
        if (Services.HostEntry.IsHostLaunch(args)) return Services.HostEntry.Run(args);

        StartupBudget.MarkProcessStart();
        LaunchOptions.Parse(args);

        // A plain launch becomes the watchdog and runs the real app as a child of the same
        // exe. `--no-watchdog` (or the Watchdog setting, or a debugger) runs it directly.
        if (!LaunchOptions.IsChild && !LaunchOptions.NoWatchdog && !Debugger.IsAttached &&
            Supervisor.ShouldSupervise())
        {
            return Supervisor.Run();
        }

        // A millisecond timer for the desk's paced loops (a sleep is a 15.6 ms tick without it).
        Services.TimerResolution.Raise();
        // Pick the GPU before Avalonia creates its D3D device (and before libVLC decodes).
        Services.GpuService.Initialize();
        // Then whether this start asks for the low-latency swap chain (direct output).
        Services.DirectOutputService.Initialize();

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
            .WithInterFont()
            .LogToTrace();
}
