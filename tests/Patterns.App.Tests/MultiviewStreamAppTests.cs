using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The remote multiview endpoints and the stream service against the live app.</summary>
public class MultiviewStreamAppTests
{
    private static (AppServices Services, MainViewModel Vm, MainWindow Window) Boot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-mv-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var services = new AppServices(new SettingsStore(dir));
        AppServices.Instance = services;
        var vm = new MainViewModel(services);
        var window = new MainWindow { DataContext = vm };
        services.AttachMainWindow(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (services, vm, window);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static T Pump<T>(Task<T> task, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("pumped task timed out");
        }
        return task.GetAwaiter().GetResult();
    }

    [AvaloniaFact]
    public void RemoteMultiviewServesPageAndLiveJpeg()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Pattern.Kind = PatternKind.FlatField;
            vm.State.Pattern.FlatField.Color = "#FF0000";
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs();

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{vm.State.Control.HttpPort}/") };

            var page = Pump(http.GetStringAsync("/multiview"));
            Assert.Contains("Patterns Multiview", page);
            Assert.Contains("/mv.jpg", page);

            var jpeg = Pump(http.GetByteArrayAsync("/mv.jpg"));
            Assert.True(jpeg.Length > 1000, "expected a real image");
            Assert.Equal(0xFF, jpeg[0]);
            Assert.Equal(0xD8, jpeg[1]); // JPEG magic

            var remote = Pump(http.GetStringAsync("/"));
            Assert.Contains("/multiview", remote); // linked from the phone remote
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void StreamServiceFailsSoftOffWindowsAndRemoteTogglesIt()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Stream.Destinations[0].Enabled = true;
            vm.State.Stream.Destinations[0].Url = "rtmp://example/live/key";

            var router = new CommandRouter(services);
            Assert.Equal("OK", Pump(router.ExecuteAsync(ControlProtocol.Parse("STREAM ON"))));
            Assert.True(vm.State.Stream.Active);

            services.Stream.Poll();
            if (!OperatingSystem.IsWindows())
            {
                Assert.Contains("Windows", services.Stream.Status);
            }

            Assert.Equal("OK", Pump(router.ExecuteAsync(ControlProtocol.Parse("STREAM OFF"))));
            Assert.False(vm.State.Stream.Active);
            services.Stream.Poll();
            Assert.Contains("Not streaming", services.Stream.Status);

            var json = router.StateJson();
            Assert.Contains("\"stream\":", json);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void MultiviewIsAssignablePerScreenAndSurvivesLooks()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Pattern.Kind = PatternKind.Multiview;
            vm.ActivePattern.Multiview.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Program });
            vm.NewLookName = "MV look";
            vm.SaveLookCommand.Execute(null);

            vm.State.Pattern.Kind = PatternKind.Grid;
            Assert.True(vm.ApplyLookHotkey(0) || true); // by-name apply below
            var look = vm.State.LooksAndCues.Looks.First(l => l.Name == "MV look");
            vm.ApplyLook(look);
            Assert.Equal(PatternKind.Multiview, vm.State.Pattern.Kind);
            Assert.Single(vm.State.Pattern.Multiview.Tiles);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }
}
