using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Stingers end to end against the live app: override, revert, takeover, remote firing.</summary>
public class StingerTests
{
    private static (AppServices Services, MainViewModel Vm, MainWindow Window) Boot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-stinger-tests-" + Guid.NewGuid().ToString("N"));
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

    private static string TempClip(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{name}");
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 1 });
        return path;
    }

    /// <summary>Waits for a dispatcher-queued task while pumping the (blocked) UI thread.</summary>
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

    private sealed class EndedSource : IVideoFrameSource
    {
        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint) => false;
        public SKSizeI? FrameSize => null;
        public bool IsPlaying => false;
        public bool IsEnded => true;
        public double DurationSeconds => 3;
        public string StatusText => "ended";
    }

    [AvaloniaFact]
    public void VideoStingerOverridesEveryScreenThenReverts()
    {
        var (services, vm, window) = Boot();
        var clip = TempClip("sting.mp4");
        try
        {
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.State.Pattern.Media.VideoPath = "";
            var placement = new ScreenPlacement { ScreenId = "s1", UseCustomPattern = true };
            vm.State.Output.Placements.Add(placement);
            Dispatcher.UIThread.RunJobs();

            var item = new StingerItemConfig { Path = clip, Name = "Walk-in sting" };
            vm.State.Stingers.Items.Add(item);
            Assert.True(services.Stingers.Fire(item));

            Assert.Equal(PatternKind.Media, vm.State.Pattern.Kind);
            Assert.Equal(MediaSource.Video, vm.State.Pattern.Media.Source);
            Assert.Equal(clip, vm.State.Pattern.Media.VideoPath);
            Assert.False(vm.State.Pattern.Media.Loop);
            Assert.False(placement.UseCustomPattern); // the clip owns every screen
            Assert.Equal("Walk-in sting", vm.State.Stingers.PlayingName);
            Assert.True(services.Stingers.ClipActive);

            // The decoder reports the natural end → the previous content comes back.
            VideoService.Current = new EndedSource();
            services.Stingers.Poll();

            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.Equal("", vm.State.Pattern.Media.VideoPath);
            Assert.True(placement.UseCustomPattern);
            Assert.Equal("", vm.State.Stingers.PlayingName);
            Assert.False(services.Stingers.ClipActive);
        }
        finally
        {
            VideoService.Current = null;
            File.Delete(clip);
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void OperatorTakeoverCancelsTheRevert()
    {
        var (services, vm, window) = Boot();
        var clip = TempClip("sting.mov");
        try
        {
            vm.State.Pattern.Kind = PatternKind.Grid;
            var item = new StingerItemConfig { Path = clip };
            Assert.True(services.Stingers.Fire(item));
            Assert.Equal(PatternKind.Media, vm.State.Pattern.Kind);

            // Mid-clip the operator switches to bars — their choice must stand.
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            services.Stingers.Poll();
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal("", vm.State.Stingers.PlayingName);

            VideoService.Current = new EndedSource();
            services.Stingers.Poll();
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind); // no zombie revert
        }
        finally
        {
            VideoService.Current = null;
            File.Delete(clip);
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void StopRevertsAClipImmediately()
    {
        var (services, vm, window) = Boot();
        var clip = TempClip("sting.mp4");
        try
        {
            vm.State.Pattern.Kind = PatternKind.Focus;
            var item = new StingerItemConfig { Path = clip };
            Assert.True(services.Stingers.Fire(item));
            Assert.Equal(PatternKind.Media, vm.State.Pattern.Kind);

            services.Stingers.Stop();
            Assert.Equal(PatternKind.Focus, vm.State.Pattern.Kind);
            Assert.False(services.Stingers.ClipActive);
        }
        finally
        {
            File.Delete(clip);
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void ChainedClipsRevertToThePreStingerContent()
    {
        var (services, vm, window) = Boot();
        var clipA = TempClip("a.mp4");
        var clipB = TempClip("b.mp4");
        try
        {
            vm.State.Pattern.Kind = PatternKind.Grid;
            Assert.True(services.Stingers.Fire(new StingerItemConfig { Path = clipA }));
            Assert.True(services.Stingers.Fire(new StingerItemConfig { Path = clipB }));
            Assert.Equal(clipB, vm.State.Pattern.Media.VideoPath);

            VideoService.Current = new EndedSource();
            services.Stingers.Poll();
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind); // not clip A
        }
        finally
        {
            VideoService.Current = null;
            File.Delete(clipA);
            File.Delete(clipB);
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void MissingFilesAndOffWindowsAudioFailSoftly()
    {
        var (services, vm, window) = Boot();
        try
        {
            Assert.False(services.Stingers.Fire(new StingerItemConfig { Path = "/nope/gone.mp3" }));
            Assert.Contains("missing", services.Stingers.Status, StringComparison.OrdinalIgnoreCase);

            if (!OperatingSystem.IsWindows())
            {
                var sound = TempClip("call.mp3");
                try
                {
                    Assert.False(services.Stingers.Fire(new StingerItemConfig { Path = sound }));
                    Assert.Equal("", vm.State.Stingers.PlayingName);
                }
                finally
                {
                    File.Delete(sound);
                }
            }
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void RemoteFiresStingersByNumber()
    {
        var (services, vm, window) = Boot();
        var clip = TempClip("remote.mp4");
        try
        {
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.State.Stingers.Items.Add(new StingerItemConfig { Path = clip, Name = "Opener" });
            var router = new CommandRouter(services);

            var bad = Pump(router.ExecuteAsync(ControlProtocol.Parse("STINGER 9")));
            Assert.StartsWith("ERR", bad);

            var ok = Pump(router.ExecuteAsync(ControlProtocol.Parse("STINGER 1")));
            Assert.Equal("OK", ok);
            Assert.Equal(PatternKind.Media, vm.State.Pattern.Kind);
            Assert.Equal("Opener", vm.State.Stingers.PlayingName);

            var stop = Pump(router.ExecuteAsync(ControlProtocol.Parse("STINGER STOP")));
            Assert.Equal("OK", stop);
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);

            var json = router.StateJson();
            Assert.Contains("\"stingers\":[{\"n\":1,\"name\":\"Opener\"}]", json);
            Assert.Contains("\"health\":\"Up ", json);
        }
        finally
        {
            File.Delete(clip);
            window.Close();
            services.Shutdown();
        }
    }
}
