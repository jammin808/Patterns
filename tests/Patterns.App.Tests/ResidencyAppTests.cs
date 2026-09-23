using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Rendering.Media;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 69: the residency ledger on the desk — every held picture has a reason or an idle clock, the
/// named ones stay while the idle ones go when their grace is up (at once under critical pressure), the
/// mounted sources carry their reason, and STATE, the Media page and the Eye read the same rows.
/// </summary>
public class ResidencyAppTests
{
    private static string Picture(DirectoryInfo dir, string name)
    {
        using var bmp = new SKBitmap(32, 32);
        bmp.Erase(SKColors.Red);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(dir.FullName, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [AvaloniaFact]
    public void EveryHeldThingHasAReasonAndAnIdlePictureGoesOnItsClock()
    {
        var b = TestApp.Boot();
        var dir = Directory.CreateTempSubdirectory("residency-app");
        try
        {
            var (services, vm, _) = b;
            var named = Picture(dir, "named.png");
            var stray = Picture(dir, "stray.png");
            vm.State.Pattern.Media.ImagePath = named;
            Assert.NotNull(ImageCache.Get(named));
            Assert.NotNull(ImageCache.Get(stray));

            var residency = services.Residency;
            var now = Environment.TickCount64;
            residency.Clock = () => now;
            residency.Poll(MemoryPressure.None);
            Assert.Contains(residency.Holds, h => h.Key == "pic:" + named && h.Reason == HoldReason.OnAir);      // fetched this second: drawn
            Assert.Contains(residency.Holds, h => h.Key == "pic:" + stray && h.Reason == HoldReason.OnAir);
            Assert.Equal(Residency.Grace(MemoryBudget.ClassOf(MemoryBudget.MachineMB), MemoryPressure.None), residency.Grace);

            // Half the grace on — the grace is the machine's own (20 s under 8 GB, 60 s under 32, 180 above), so the step is
            // read from it and never assumed: the show names one (it stays, drawn or not), the other is idle with a clock running.
            var half = (long)(residency.Grace.TotalMilliseconds / 2);
            now += half;
            residency.Poll(MemoryPressure.None);
            Assert.Contains(residency.Holds, h => h.Key == "pic:" + named && h.Reason == HoldReason.Named);
            Assert.Contains(residency.Holds, h => h.Key == "pic:" + stray && h.Reason == HoldReason.Idle && h.IdleSeconds >= half / 1000 - 1);
            Assert.Contains("idle", residency.Words);
            Assert.Contains("named by the show", residency.Words);

            // The grace up: the idle picture is let go — behind the fence — and the named one is kept.
            now += (long)residency.Grace.TotalMilliseconds + 1_000;
            residency.Poll(MemoryPressure.None);
            Assert.DoesNotContain(residency.Holds, h => h.Key == "pic:" + stray);
            Assert.Contains(residency.Holds, h => h.Key == "pic:" + named);
            Assert.True(residency.PicturesLetGo >= 1);

            // Under critical pressure the grace is nought: an idle picture goes at the next poll; a named one never does.
            Assert.NotNull(ImageCache.Get(stray));
            now += 5_000;
            residency.Poll(MemoryPressure.Critical);
            Assert.Equal(TimeSpan.Zero, residency.Grace);
            Assert.DoesNotContain(residency.Holds, h => h.Key == "pic:" + stray);
            Assert.Contains(residency.Holds, h => h.Key == "pic:" + named);

            // STATE carries the ledger; the Media page's line and the assistant's inputs read the same words.
            var router = new CommandRouter(services);
            var memory = JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("memory").GetProperty("residency");
            Assert.Equal(0, memory.GetProperty("grace").GetDouble());
            Assert.Contains(memory.GetProperty("holds").EnumerateArray(), h => h.GetProperty("key").GetString() == "pic:" + named && h.GetProperty("reason").GetString() == "named by the show");
            Assert.True(memory.GetProperty("letGo").GetInt32() >= 1);
            vm.Media.RefreshActiveInputs();
            Assert.StartsWith("In memory: ", vm.Media.ResidencyText);
            Assert.Contains("named by the show", vm.Media.ResidencyText);
        }
        finally
        {
            b.Dispose();
            ImageCache.ClearForTests();
            dir.Delete(recursive: true);
        }
    }

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void AMountedClipCarriesItsReasonAndAnArmedPageStaysPastItsWant()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            services.Video.SourceFactory = w => new FakeSource(w);
            var pages = new List<FakeWebSource>();
            services.WebIn.SourceFactory = w =>
            {
                var page = new FakeWebSource(w.Target, WebEngine.ParseSize(w.Format), SKColors.Blue);
                pages.Add(page);
                return page;
            };
            var router = new CommandRouter(services);

            // A clip on the programme: mounted, on air.
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Video;
            vm.State.Pattern.Media.VideoPath = Path.Combine(Path.GetTempPath(), "residency-clip.mp4");
            Settle(window);
            services.ReconcileInputs();
            services.Residency.Poll(MemoryPressure.None);
            var clip = Assert.Single(services.Residency.Holds, h => h.Kind == "clip");
            Assert.Equal(HoldReason.OnAir, clip.Reason);
            Assert.Equal("on air", services.Residency.ReasonWords(clip.Key));

            // EDIT SAFE on: a YouTube page on the preview's pattern, armed at a mark from the wire — the preview's, not on air.
            const string url = "https://www.youtube.com/watch?v=abc123residency";
            var key = "web:" + url;
            vm.IsSandboxActive = true;
            vm.State.Pattern.Media.Source = MediaSource.Web;
            vm.State.Pattern.Media.WebUrl = url;
            Settle(window);
            Assert.Single(pages);
            Assert.False(services.WebIn.VtFor(key).OnAir);
            Assert.Equal("OK", Send(router, "WEB ARM 1:23"));
            Assert.True(services.WebIn.ArmOf(key).Armed);
            services.Residency.Poll(MemoryPressure.None);
            Assert.Contains(services.Residency.Holds, h => h.Key == key && h.Reason == HoldReason.Preview);

            // The preview moves on: the page stays for its arm alone — set up, not thrown away — and the ledger says so.
            vm.State.Pattern.Media.WebUrl = "";
            vm.State.Pattern.Kind = PatternKind.Grid;
            Settle(window);
            services.ReconcileInputs();
            Assert.Equal(1, services.WebIn.PageCount);
            Assert.Equal(1, services.WebIn.ArmedKept);
            services.Residency.Poll(MemoryPressure.None);
            Assert.Contains(services.Residency.Holds, h => h.Key == key && h.Reason == HoldReason.Armed);
            Assert.Contains("armed at its mark", services.Residency.Words);

            // Critical pressure: the ladder lets armed pages go, and the page leaves like any other.
            services.WebIn.KeepArmed = false;
            services.ReconcileInputs();
            Assert.Equal(0, services.WebIn.PageCount);
            Assert.Equal(0, services.WebIn.ArmedKept);
            services.Residency.Poll(MemoryPressure.Critical);
            Assert.DoesNotContain(services.Residency.Holds, h => h.Key == key && h.Reason == HoldReason.Armed);
        }
        finally
        {
            InputBus.Clear();
            b.Dispose();
        }
    }
}
