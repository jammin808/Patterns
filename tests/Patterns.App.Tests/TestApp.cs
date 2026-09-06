using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Services;

namespace Patterns.App.Tests;

/// <summary>
/// Boots the real app headlessly against a fresh temp folder: services, view model and (by
/// default) the main window, exactly as App.axaml.cs does. One copy for every test file.
/// </summary>
public static class TestApp
{
    public sealed record Booted(AppServices Services, MainViewModel Vm, MainWindow Window, string Dir)
    {
        public void Deconstruct(out AppServices services, out MainViewModel vm, out MainWindow window)
        {
            services = Services;
            vm = Vm;
            window = Window;
        }

        /// <summary>Closes the window and shuts the services down; safe to call twice.</summary>
        public void Dispose()
        {
            try { Window.Close(); } catch { /* already closed */ }
            Services.Shutdown();
        }
    }

    /// <param name="prefix">The temp folder's name prefix.</param>
    /// <param name="prepare">Runs on the empty folder before the services read it — a marker the watchdog would have left, a settings file.</param>
    public static Booted Boot(string prefix = "patterns-tests-", Action<string>? prepare = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        prepare?.Invoke(dir);
        var services = new AppServices(new SettingsStore(dir));
        AppServices.Instance = services;
        var vm = new MainViewModel(services);
        var window = new MainWindow { DataContext = vm };
        services.AttachMainWindow(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Booted(services, vm, window, dir);
    }

    /// <summary>Runs the dispatcher until a task completes (remote commands hop to the UI thread).</summary>
    public static T Pump<T>(Task<T> task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        return task.Result;
    }
}
