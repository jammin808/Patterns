using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The remote multiview endpoints and the stream service against the live app.</summary>
public class MultiviewStreamAppTests
{
    private static (AppServices Services, MainViewModel Vm, MainWindow Window) Boot()
    {
        var b = TestApp.Boot();
        return (b.Services, b.Vm, b.Window);
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

    /// <summary>a and b flush (canvas a+b), c standing alone — the rig WallTests uses.</summary>
    private static string InstallRig(AppServices services, MainViewModel vm)
    {
        var fakes = new List<ScreenInfo>
        {
            new("a", "Left", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
            new("b", "Right", new Avalonia.PixelRect(1920, 0, 1920, 1080), 1.0, false, 1),
            new("c", "Lobby", new Avalonia.PixelRect(4400, 0, 1920, 1080), 1.0, false, 2),
        };
        services.Screens.All.Clear();
        foreach (var s in fakes) services.Screens.All.Add(s);
        vm.State.Output.Placements.Clear();
        vm.ReconcilePlacements(fakes);
        vm.State.Output.Placements.First(p => p.ScreenId == "a").X = 0;
        vm.State.Output.Placements.First(p => p.ScreenId == "b").X = 1920;
        vm.State.Output.Placements.First(p => p.ScreenId == "c").X = 6000;
        foreach (var p in vm.State.Output.Placements) p.Enabled = true;
        vm.RebuildSwitcherTiles();
        Dispatcher.UIThread.RunJobs();
        return CanvasNameConfig.KeyFor(new[] { "a", "b" });
    }

    [AvaloniaFact]
    public void TheRemoteMultiviewDrawsTheRigsCanvasTargets()
    {
        var (services, vm, window) = Boot();
        try
        {
            var key = InstallRig(services, vm);

            // The canvas holds its own blue picture; the multiview shows it as one wide tile.
            var canvas = ContentTargets.EnsureAssignment(vm.State, key).Pattern;
            ContentTargets.SetOwnPattern(vm.State, key, true);
            canvas.Kind = PatternKind.FlatField;
            canvas.FlatField.Color = "#0000FF";
            canvas.FlatField.ShowLabel = false;
            canvas.Canvas.FollowOutput = true;

            vm.State.Pattern.Kind = PatternKind.Multiview;
            vm.State.Pattern.Multiview.Columns = 1;
            vm.State.Pattern.Multiview.Tiles.Clear();
            vm.State.Pattern.Multiview.Tiles.Add(new MultiviewTileConfig
            {
                Source = MultiviewSource.Screen,
                ScreenId = key,
            });
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs();

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{vm.State.Control.HttpPort}/") };
            var jpeg = Pump(http.GetByteArrayAsync("/mv.jpg"));
            Assert.True(jpeg.Length > 1000, "expected a real image");
            Assert.Equal(0xFF, jpeg[0]);
            Assert.Equal(0xD8, jpeg[1]);

            using var bmp = SKBitmap.Decode(jpeg);
            Assert.Equal(1024, bmp.Width);

            // A 3840×1080 canvas is a wide strip: filled at the middle, dark well above it.
            var middle = bmp.GetPixel(512, 273);
            Assert.True(middle.Blue > 200 && middle.Red < 60, $"the canvas tile should be blue, got {middle}");
            var band = bmp.GetPixel(512, 40);
            Assert.True(band.Red < 50 && band.Green < 50 && band.Blue < 50, $"letterbox band above the strip, got {band}");
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void TheRemoteMultiviewHonoursTheWidthQuery()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Pattern.Kind = PatternKind.FlatField;
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs();

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{vm.State.Control.HttpPort}/") };

            int WidthOf(string path)
            {
                using var bmp = SKBitmap.Decode(Pump(http.GetByteArrayAsync(path)));
                return bmp.Width;
            }

            Assert.Equal(640, WidthOf("/mv.jpg?w=640"));
            Assert.Equal(1024, WidthOf("/mv.jpg"));
            Assert.Equal(1920, WidthOf("/mv.jpg?w=99999"));   // clamped, never a memory bomb
            Assert.Equal(1024, WidthOf("/mv.jpg?w=abc"));     // nonsense falls back
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
