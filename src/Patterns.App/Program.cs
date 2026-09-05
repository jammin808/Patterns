using System.Diagnostics;
using Avalonia;
using Patterns.Core.Services;

namespace Patterns.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        LaunchOptions.Parse(args);

        // A plain launch becomes the watchdog and runs the real app as a child of the same
        // exe. `--no-watchdog` (or the Watchdog setting, or a debugger) runs it directly.
        if (!LaunchOptions.IsChild && !LaunchOptions.NoWatchdog && !Debugger.IsAttached &&
            Supervisor.ShouldSupervise())
        {
            return Supervisor.Run();
        }

        // Pick the GPU before Avalonia creates its D3D device (and before libVLC decodes).
        Services.GpuService.Initialize();
        // Then whether this start asks for the low-latency swap chain (direct output).
        Services.DirectOutputService.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception.", e.ExceptionObject as Exception);
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
            Log.Error("Fatal startup failure.", ex);
            return 1;
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
