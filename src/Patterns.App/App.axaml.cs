using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

            var vm = new MainViewModel(services);
            var window = new MainWindow { DataContext = vm };
            services.AttachMainWindow(window);
            desktop.MainWindow = window;

            desktop.ShutdownRequested += (_, _) => services.Shutdown();
            desktop.Exit += (_, _) =>
            {
                services.Shutdown();
                Log.Info("Clean exit.");
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
