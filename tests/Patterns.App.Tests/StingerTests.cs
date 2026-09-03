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
        var b = TestApp.Boot();
        return (b.Services, b.Vm, b.Window);
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
    public void AVideoStingerFiredFromACueStaysInFrontOfBlackout()
    {
        // RunCue puts blackout back after a cue unless the cue switched it; a clip that took the
        // screens lifted it on purpose, so the audience must not be left looking at black.
        var (services, vm, window) = Boot();
        var clip = TempClip("cue-sting.mp4");
        try
        {
            services.ValidationVideoOverride = () => true; // headless: no libVLC, the clip is a stub the decoder never opens
            vm.State.Pattern.Kind = PatternKind.Grid;
            var item = new StingerItemConfig { Path = clip, Name = "Opening sting" };
            vm.State.Stingers.Items.Add(item);
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "1", Name = "Sting", Actions = { new CueActionConfig { Kind = CueActionKind.StingerFire, Target = item.Id } } };
            stack.Cues.Add(cue);
            vm.State.Blackout = true;
            Dispatcher.UIThread.RunJobs();

            var result = services.Actions.Execute(new ShowAction(ShowActionKind.CueFire, cue.Id), ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(result.Ok, result.Message);
            Assert.True(services.Stingers.ClipActive);
            Assert.False(vm.State.Blackout);
            Assert.False(services.Bus.Current.State.Blackout);
            Assert.Equal(clip, services.Bus.Current.State.Pattern.Media.VideoPath);
            Assert.Equal("STING: Opening sting", services.AirLabel);

            // The clip ends: the previous content comes back, and blackout stays off — the clip lifted it.
            InputBus.Mount(InputKeys.Video(clip), new EndedSource());
            services.Stingers.Poll();
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void TheLiveStripGetsItsLabelBackWhenAStingerEnds()
    {
        var (services, vm, window) = Boot();
        var clip = TempClip("label-sting.mp4");
        try
        {
            vm.State.Pattern.Kind = PatternKind.Grid;
            var item = new StingerItemConfig { Path = clip, Name = "Winner sting" };
            vm.State.Stingers.Items.Add(item);
            services.AirLabel = "Walk-in";

            // A natural end gives the label back.
            Assert.True(services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            Assert.Equal("STING: Winner sting", services.AirLabel);
            InputBus.Mount(InputKeys.Video(clip), new EndedSource());
            services.Stingers.Poll();
            Assert.Equal("Walk-in", services.AirLabel);
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);

            // So does STOP.
            Assert.True(services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            Assert.Equal("STING: Winner sting", services.AirLabel);
            services.Actions.Execute(ShowActionKind.StingerStop, ActionOrigin.Desk);
            Assert.Equal("Walk-in", services.AirLabel);

            // A claim made meanwhile (a look, a cue) stands.
            Assert.True(services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, item.Id).Ok);
            services.AirLabel = "Awards holding";
            services.Stingers.Poll();
            Assert.False(services.Stingers.ClipActive);
            Assert.Equal("Awards holding", services.AirLabel);
        }
        finally
        {
            InputBus.Clear();
            File.Delete(clip);
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void DeletingAStingerACueStillFiresIsRefused()
    {
        var (services, vm, window) = Boot();
        var clip = TempClip("seats.wav");
        try
        {
            var item = new StingerItemConfig { Path = clip, Name = "Take your seats" };
            vm.State.Stingers.Items.Add(item);
            var stack = CueStacks.Caller(vm.State);
            stack.Cues.Add(new RunCueConfig { Number = "1", Name = "Call", Actions = { new CueActionConfig { Kind = CueActionKind.StingerFire, Target = item.Id } } });

            vm.RemoveStingerCommand.Execute(item);
            Assert.Contains(item, vm.State.Stingers.Items);
            Assert.Contains("Take your seats", vm.StatusMessage);
            Assert.Contains("cue 1 Call", vm.StatusMessage);

            stack.Cues.Clear();
            vm.RemoveStingerCommand.Execute(item);
            Assert.DoesNotContain(item, vm.State.Stingers.Items);
        }
        finally
        {
            File.Delete(clip);
            window.Close();
            services.Shutdown();
        }
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
            InputBus.Mount(InputKeys.Video(vm.State.Pattern.Media.VideoPath), new EndedSource());
            services.Stingers.Poll();

            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.Equal("", vm.State.Pattern.Media.VideoPath);
            Assert.True(placement.UseCustomPattern);
            Assert.Equal("", vm.State.Stingers.PlayingName);
            Assert.False(services.Stingers.ClipActive);
        }
        finally
        {
            InputBus.Clear();
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

            InputBus.Mount(InputKeys.Video(vm.State.Pattern.Media.VideoPath), new EndedSource());
            services.Stingers.Poll();
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind); // no zombie revert
        }
        finally
        {
            InputBus.Clear();
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

            InputBus.Mount(InputKeys.Video(vm.State.Pattern.Media.VideoPath), new EndedSource());
            services.Stingers.Poll();
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind); // not clip A
        }
        finally
        {
            InputBus.Clear();
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
