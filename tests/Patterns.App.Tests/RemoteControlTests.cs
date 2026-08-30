using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NAudio.Wave;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The remote-control server end to end: real sockets against the live app.</summary>
public class RemoteControlTests
{
    private static (AppServices Services, MainViewModel Vm, MainWindow Window) Boot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-ui-tests-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Waits for a background task while pumping the (blocked) UI thread's queue.</summary>
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

    private static void Pump(Task task, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("pumped task timed out");
        }
        task.GetAwaiter().GetResult();
    }

    [AvaloniaFact]
    public void TcpProtocolControlsTheShowWithFeedback()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs(); // publish → listeners start

            using var client = new TcpClient();
            Pump(client.ConnectAsync(IPAddress.Loopback, vm.State.Control.TcpPort));
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Greeting carries the full state for feedback initialisation.
            var greet = Pump(reader.ReadLineAsync());
            Assert.StartsWith("STATE {", greet);

            void Send(string line)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                stream.Write(bytes);
            }

            string ReadResponse()
            {
                // Skip pushed STATE lines; return the next OK/ERR.
                while (true)
                {
                    var line = Pump(reader.ReadLineAsync());
                    Assert.NotNull(line);
                    if (!line!.StartsWith("STATE ")) return line;
                }
            }

            Send("PING");
            Assert.Equal("OK PONG", ReadResponse());

            Send("BLACKOUT ON");
            Assert.Equal("OK", ReadResponse());
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);

            Send("STATUS");
            var status = ReadResponse();
            Assert.StartsWith("OK {", status);
            Assert.Contains("\"blackout\":true", status);

            Send("LOOK 5");
            Assert.StartsWith("ERR", ReadResponse()); // nothing saved on F5

            Send("BLACKOUT OFF");
            Assert.Equal("OK", ReadResponse());
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);

            Send("NONSENSE");
            Assert.StartsWith("ERR", ReadResponse());
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void WebRemoteServesPageStateAndCommands()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs();

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{vm.State.Control.HttpPort}/") };

            var page = Pump(http.GetStringAsync("/"));
            Assert.Contains("PATTERNS", page);
            Assert.Contains("/api/cmd", page);

            var state = Pump(http.GetStringAsync("/api/state"));
            Assert.Contains("\"blackout\":false", state);

            var response = Pump(http.PostAsync("/api/cmd", new StringContent("BLACKOUT ON")));
            var body = Pump(response.Content.ReadAsStringAsync());
            Assert.Contains("\"ok\":true", body);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);

            var missing = Pump(http.GetAsync("/nope"));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void PresenterStepsApplyLooksInOrder()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.ActivePattern.Kind = PatternKind.ColorBars;
            vm.NewLookName = "One";
            vm.SaveLookCommand.Execute(null);
            vm.ActivePattern.Kind = PatternKind.Focus;
            vm.NewLookName = "Two";
            vm.SaveLookCommand.Execute(null);

            vm.State.Presenter.Steps.Add(new PresenterStepConfig { LookName = "One" });
            vm.State.Presenter.Steps.Add(new PresenterStepConfig { LookName = "Two" });

            Assert.True(vm.PresenterAdvance(+1));
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
            Assert.Equal(0, vm.State.Presenter.CurrentIndex);

            Assert.True(vm.PresenterAdvance(+1));
            Assert.Equal(PatternKind.Focus, vm.State.Pattern.Kind);

            Assert.False(vm.PresenterAdvance(+1)); // end, no loop
            vm.State.Presenter.Loop = true;
            Assert.True(vm.PresenterAdvance(+1)); // wraps
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);

            Assert.True(vm.PresenterAdvance(-1)); // back wraps too
            Assert.Equal(PatternKind.Focus, vm.State.Pattern.Kind);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void RemoteScreenAndGroupSwitching()
    {
        var (services, vm, window) = Boot();
        try
        {
            var fakes = new List<ScreenInfo>
            {
                new("a", "Left", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
                new("b", "Right", new Avalonia.PixelRect(1920, 0, 1920, 1080), 1.0, false, 1),
            };
            vm.State.Output.Placements.Clear();
            vm.ReconcilePlacements(fakes);
            // Arrange flush (detected screens start spaced apart = independent) → canvas A.
            var left = vm.State.Output.Placements.First(p => p.ScreenId == "a");
            var rightP = vm.State.Output.Placements.First(p => p.ScreenId == "b");
            left.X = 0; left.Y = 0;
            rightP.X = 1920; rightP.Y = 0;
            foreach (var p in vm.State.Output.Placements)
            {
                p.Enabled = true;
            }

            Assert.True(vm.SetScreenEnabled(2, false, fakes));
            var right = vm.State.Output.Placements.First(p => p.ScreenId == "b");
            Assert.False(right.Enabled);
            Assert.True(right.UserPinned);

            Assert.True(vm.SetScreenEnabled(2, null, fakes)); // toggle back
            Assert.True(right.Enabled);

            Assert.True(vm.SetGroupEnabled("A", false, fakes));
            Assert.All(vm.State.Output.Placements, p => Assert.False(p.Enabled));
            Assert.True(vm.SetGroupEnabled("A", true, fakes));
            Assert.All(vm.State.Output.Placements, p => Assert.True(p.Enabled));

            Assert.False(vm.SetGroupEnabled("B", true, fakes));  // only one canvas exists
            Assert.False(vm.SetScreenEnabled(9, true, fakes));

            var rows = vm.RemoteScreens(fakes);
            Assert.Equal(2, rows.Length);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void WarpedOutputPullsTheCornerIn()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Pattern.Kind = PatternKind.FlatField;
            vm.State.Pattern.FlatField.Color = "#FFFFFF";
            vm.State.Pattern.FlatField.ShowLabel = false;
            vm.State.Pattern.Canvas.FollowOutput = true;
            vm.State.Transition.Enabled = false;
            Dispatcher.UIThread.RunJobs();

            var viewport = new PipelineViewport(SinkKind.Output, SKSizeI.Empty, default, null, 1, "warp")
            {
                WarpTly = 60, // pull the top-left corner down — that region goes black
            };
            using var pipeline = new RenderPipeline(services.Bus, viewport);
            var info = new SKImageInfo(200, 150, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            pipeline.Render(surface.Canvas, 200, 150, 1.0);
            surface.Canvas.Flush();
            using var image = surface.Snapshot();
            using var bmp = SKBitmap.FromImage(image);

            Assert.True(bmp.GetPixel(4, 4).Red < 40, "above the warped top-left edge should be black");
            Assert.True(bmp.GetPixel(100, 100).Red > 220, "centre should still be white");
            Assert.True(bmp.GetPixel(195, 4).Red > 220, "top-right corner is unmoved");
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [Fact]
    public void LoopingWaveStreamWrapsForever()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using var source = new RawSourceWaveStream(new MemoryStream(data), new WaveFormat(8000, 8, 1));
        using var loop = new LoopingWaveStream(source);

        var buffer = new byte[20];
        var read = loop.Read(buffer, 0, buffer.Length);
        Assert.Equal(20, read); // 2.5 passes through the 8-byte source
        Assert.Equal(1, buffer[0]);
        Assert.Equal(1, buffer[8]);
        Assert.Equal(4, buffer[19]);
    }

    [Fact]
    public void AudioOutputDevicesEmptyOffWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(AudioPlayerService.OutputDevices());
        }
    }
}
