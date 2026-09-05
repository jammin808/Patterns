using System.IO.Pipes;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Services;

namespace Patterns.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            AppServices.Instance = services;
            HealthMonitor.Restarts = LaunchOptions.Restarts;

            var vm = new MainViewModel(services);
            var window = new MainWindow { DataContext = vm };
            services.AttachMainWindow(window);
            desktop.MainWindow = window;

            // Previews are sandboxed by default: the operator can touch any editor from the
            // first second without it reaching the audience.
            services.StartDefaultSandbox();

            // RESTART and UPDATE APPLY from a remote (or the management server) leave through the same door as the Machine page's button.
            services.ExitRequest = code =>
            {
                Dispatcher.UIThread.Post(() => desktop.Shutdown(code));
                return true;
            };
            desktop.ShutdownRequested += (_, _) => services.Shutdown();
            desktop.Exit += (_, _) =>
            {
                services.Shutdown();
                Log.Info("Clean exit.");
            };

            if (LaunchOptions.BeatHandle is { } handle) StartHeartbeat(handle);

            if (LaunchOptions.Recover)
            {
                // Give screen detection and side effects a moment, then put the show back.
                var recover = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                recover.Tick += (_, _) =>
                {
                    recover.Stop();
                    services.TryRecover(vm);
                };
                recover.Start();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// One byte per second to the watchdog, posted from the UI thread on purpose: a hung or
    /// deadlocked UI stops the beat, and that silence is what gets the app restarted.
    /// </summary>
    private static void StartHeartbeat(string pipeHandle)
    {
        try
        {
            var pipe = new AnonymousPipeClientStream(PipeDirection.Out, pipeHandle);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                try
                {
                    pipe.WriteByte(1);
                    pipe.Flush();
                }
                catch
                {
                    // The supervisor is gone — keep running standalone.
                    timer.Stop();
                    pipe.Dispose();
                }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("Watchdog heartbeat unavailable.", ex);
        }
    }
}
