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
            .WithInterFont()
            .LogToTrace();
}
