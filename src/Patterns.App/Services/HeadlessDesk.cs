using Avalonia.Threading;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The real desk booted without the exe's start: the services over a folder, the view model, and the main window
/// attached and shown on whatever Avalonia platform the caller set up — the headless one, for the test boot
/// (<c>TestApp.Boot</c>) and the bench host (<c>tools/Patterns.Bench</c>). Both boot through these two calls in this
/// order and through nothing else, so the desk the interop lane drives is the desk the suites prove, built the same
/// way; the only thing between the two calls is a caller's own setting (the tests' housekeeping budget). The exe's
/// start (<see cref="App"/>) is not this: it adds the watchdog's beat, the recovery after a relaunch, EDIT SAFE by
/// default and the exit door, none of which a headless desk has. Neither call sleeps or sets a knob of its own.
/// </summary>
public static class HeadlessDesk
{
    /// <summary>The desk's services over <paramref name="store"/>'s folder, made the process's instance, and its view model.</summary>
    public static (AppServices Services, MainViewModel Vm) Create(SettingsStore store, NodeKind profile = NodeKind.Desk)
    {
        ArgumentNullException.ThrowIfNull(store);
        var services = new AppServices(store, profile: profile);
        AppServices.Instance = services;
        var vm = new MainViewModel(services);
        return (services, vm);
    }

    /// <summary>The main window over <paramref name="vm"/>, attached to <paramref name="services"/> and shown; the dispatcher's queue run once, so the window's opening has happened when this returns (UI thread).</summary>
    public static MainWindow Show(AppServices services, MainViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(vm);
        var window = new MainWindow { DataContext = vm };
        services.AttachMainWindow(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
