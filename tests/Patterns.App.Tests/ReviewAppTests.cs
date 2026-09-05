using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The review from the desk and the remote: the preview on the multiview thumbnail, the flag on the program snapshot and in STATE, the toggles following each other.</summary>
public class ReviewAppTests
{
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

    private static bool Red(SKColor c) => c.Red > 150 && c.Green < 100 && c.Blue < 100;
    private static bool Blue(SKColor c) => c.Blue > 150 && c.Red < 100 && c.Green < 100;

    [AvaloniaFact]
    public void TheReviewPutsThePreviewOnTheMultiviewFromTheDeskAndTheRemote()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();

            // The program: blue, with a Program tile and a Preview tile in its multiview; EDIT SAFE freezes it and the desk builds red.
            vm.ActivePattern.Kind = PatternKind.FlatField;
            vm.ActivePattern.FlatField.Color = "#0000FF";
            vm.ActivePattern.FlatField.ShowLabel = false;
            vm.ActivePattern.Canvas.FollowOutput = true;
            vm.ActivePattern.Multiview.ShowLabels = false;
            vm.ActivePattern.Multiview.ShowTally = false;
            vm.ActivePattern.Multiview.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Program });
            vm.ActivePattern.Multiview.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Preview });
            Dispatcher.UIThread.RunJobs();
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsSandboxActive);
            vm.ActivePattern.FlatField.Color = "#FF0000";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("#0000FF", services.Bus.Current.State.Pattern.FlatField.Color);
            Assert.Equal("#FF0000", services.Bus.Sandbox!.State.Pattern.FlatField.Color);

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{vm.State.Control.HttpPort}/") };
            using (var bmp = SKBitmap.Decode(Pump(http.GetByteArrayAsync("/mv.jpg?w=320"))))
            {
                Assert.True(Blue(bmp.GetPixel(80, 90)), $"the program tile is blue, got {bmp.GetPixel(80, 90)}");
                Assert.True(Red(bmp.GetPixel(240, 90)), $"the preview tile is red, got {bmp.GetPixel(240, 90)}");
            }

            // REVIEW from the desk: the flag rides the frozen program's snapshot, the thumbnail is the preview, STATE says so.
            Assert.False(vm.ReviewOnMultiview);
            vm.ReviewOnMultiview = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Bus.ReviewOnMultiview);
            Assert.True(services.Bus.Current.ReviewOnMultiview);
            Assert.Equal("#0000FF", services.Bus.Current.State.Pattern.FlatField.Color); // the program did not change
            using (var bmp = SKBitmap.Decode(Pump(http.GetByteArrayAsync("/mv.jpg?w=320"))))
            {
                Assert.True(Red(bmp.GetPixel(160, 90)), $"the review fills the multiview with the preview, got {bmp.GetPixel(160, 90)}");
                Assert.True(Red(bmp.GetPixel(80, 120)), "no program tile during a review");
            }
            var router = new CommandRouter(services);
            Assert.Contains("\"review\":true", router.StateJson());

            // Off from the remote: the desk's toggle follows on the next poll, the tiles come back.
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("REVIEW OFF"))));
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Bus.Current.ReviewOnMultiview);
            Assert.Contains("\"review\":false", router.StateJson());
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("REVIEW TOGGLE"))));
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Bus.Current.ReviewOnMultiview);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("REVIEW OFF"))));
            Dispatcher.UIThread.RunJobs();
            using (var bmp = SKBitmap.Decode(Pump(http.GetByteArrayAsync("/mv.jpg?w=320"))))
            {
                Assert.True(Blue(bmp.GetPixel(80, 90)), "the program tile is back");
            }

            // The flag is runtime only: the show file never carries it.
            Assert.DoesNotContain("ReviewOnMultiview", JsonUtil.Serialize(vm.State));
        }
        finally
        {
            b.Dispose();
        }
    }
}
