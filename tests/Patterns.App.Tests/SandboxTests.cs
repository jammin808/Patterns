using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Sandbox look programming: outputs hold program while the preview follows the edits.</summary>
public class SandboxTests
{
    private static (AppServices Services, MainViewModel Vm, MainWindow Window) Boot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-sandbox-tests-" + Guid.NewGuid().ToString("N"));
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

    [AvaloniaFact]
    public void EditsStayInTheSandboxWhileOutputsHoldProgram()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();

            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.ColorBars;

            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);
            Assert.NotNull(services.Bus.Sandbox);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Sandbox!.State.Pattern.Kind);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void BlackoutCutsStraightThroughTheFreeze()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            vm.State.Blackout = true;

            Assert.True(services.Bus.Current.State.Blackout);              // an emergency is live
            Assert.NotEqual(PatternKind.ColorBars, services.Bus.Current.State.Pattern.Kind); // content isn't
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void SendAllMakesTheSandboxTheProgram()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.Focus;

            vm.SandboxSendAllCommand.Execute(null);

            // The look is on air, and EDIT SAFE re-arms itself for the next one.
            Assert.Equal(PatternKind.Focus, services.Bus.Current.State.Pattern.Kind);
            Assert.True(vm.IsSandboxActive);
            Assert.Equal(PatternKind.Focus, services.Bus.Sandbox!.State.Pattern.Kind);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void DiscardPutsEverythingBackUntouched()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.State.Overlays.Clock.Enabled = false;
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            vm.State.Overlays.Clock.Enabled = true;

            vm.IsSandboxActive = false; // the toggle off = discard

            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.False(vm.State.Overlays.Clock.Enabled);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);
            Assert.Null(services.Bus.Sandbox);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void SendToSelectedScreensBecomesTheirOwnPattern()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.State.Output.Placements.Add(new ScreenPlacement { ScreenId = "screen-a" });
            vm.State.Output.Placements.Add(new ScreenPlacement { ScreenId = "screen-b" });
            Dispatcher.UIThread.RunJobs();

            services.Sandbox.Enter();
            vm.State.Pattern.Kind = PatternKind.Focus;
            services.Sandbox.SendToScreens(new[] { "screen-a" });

            // Program back to what it was; screen-a carries the sandbox as its own pattern.
            Assert.True(services.Sandbox.Active); // EDIT SAFE re-armed for the next look
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            var a = vm.State.Output.Placements.First(p => p.ScreenId == "screen-a");
            var b = vm.State.Output.Placements.First(p => p.ScreenId == "screen-b");
            Assert.True(a.UseCustomPattern);
            Assert.False(b.UseCustomPattern);
            Assert.Equal(PatternKind.Focus,
                vm.State.Independent.First(x => x.ScreenId == "screen-a").Pattern.Kind);

            // And the published snapshot resolves it the same way the engine will.
            Assert.Equal(PatternKind.Focus, services.Bus.Current.PatternFor("screen-a").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("screen-b").Kind);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void PreviewRendersTheSandboxWhileOutputsRenderProgram()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Transition.Enabled = false;
            vm.State.Pattern.Kind = PatternKind.FlatField;
            vm.State.Pattern.FlatField.Color = "#FF0000";
            vm.State.Pattern.FlatField.ShowLabel = false;
            vm.State.Pattern.Canvas.FollowOutput = true;
            Dispatcher.UIThread.RunJobs();

            vm.IsSandboxActive = true;
            vm.State.Pattern.FlatField.Color = "#0000FF";

            SKColor Render(PipelineViewport viewport)
            {
                using var pipeline = new RenderPipeline(services.Bus, viewport);
                var info = new SKImageInfo(64, 48, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                pipeline.Render(surface.Canvas, 64, 48, 1.0);
                surface.Canvas.Flush();
                using var image = surface.Snapshot();
                using var bmp = SKBitmap.FromImage(image);
                return bmp.GetPixel(32, 24);
            }

            var preview = Render(PipelineViewport.Preview);
            Assert.True(preview.Blue > 240 && preview.Red < 15, $"preview should show the sandbox, got {preview}");

            var output = Render(new PipelineViewport(SinkKind.Output, SKSizeI.Empty, default, null, 1, "out"));
            Assert.True(output.Red > 240 && output.Blue < 15, $"output should hold program, got {output}");
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void CuesAndClipsFireToAirWhileTheSandboxKeepsBuilding()
    {
        var (services, vm, window) = Boot();
        var clip = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(clip, new byte[] { 1 });
        try
        {
            vm.ActivePattern.Kind = PatternKind.Grid;
            vm.NewLookName = "Cue look";
            vm.SaveLookCommand.Execute(null);
            vm.State.LooksAndCues.Cues.Add(new CueConfig
            {
                Time = $"{DateTime.Now:HH\\:mm}",
                LookName = "Cue look",
            });

            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.ColorBars;

            vm.PollNow(); // the 1 s status timer body, driven directly

            // The cue ran the show: air took the look, the operator's edit kept building.
            Assert.DoesNotContain("held", vm.NextCueText);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Sandbox!.State.Pattern.Kind);

            // A clip overrides air and leaves the sandbox alone.
            Assert.True(services.Stingers.Fire(new StingerItemConfig { Path = clip }));
            Assert.Equal(clip, services.Bus.Current.State.Pattern.Media.VideoPath);
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
        }
        finally
        {
            File.Delete(clip);
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void ComputerOutputRowIsPinnedAndStoredAsTheSentinel()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.RefreshAudioDevicesCommand.Execute(null);
            Assert.True(vm.AudioDevices.Count >= 1);
            var pinned = vm.AudioDevices[0];
            Assert.Equal(AudioPlayerService.DefaultDeviceKey, pinned.Name);
            Assert.Contains("Computer audio output", pinned.Label);

            pinned.IsSelected = true;
            Assert.Contains(AudioPlayerService.DefaultDeviceKey, vm.State.AudioPlayer.Devices);
            pinned.IsSelected = false;
            Assert.DoesNotContain(AudioPlayerService.DefaultDeviceKey, vm.State.AudioPlayer.Devices);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }
    // ---- EDIT SAFE by default -----------------------------------------------

    [AvaloniaFact]
    public void EditSafeIsArmedAtStartupByDefault()
    {
        var (services, vm, window) = Boot();
        try
        {
            Assert.True(services.State.Switcher.EditSafeByDefault); // the shipped default
            Assert.False(services.Sandbox.Active);                  // not until startup arms it

            services.StartDefaultSandbox();
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsSandboxActive);
            Assert.NotNull(services.Bus.Sandbox);

            // First touch of any editor is already safe.
            vm.State.Pattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(PatternKind.Focus, services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(PatternKind.Focus, services.Bus.Sandbox!.State.Pattern.Kind);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void StartupDoesNotArmWhenTheShowTurnedEditSafeOff()
    {
        var (services, vm, window) = Boot();
        try
        {
            services.State.Switcher.EditSafeByDefault = false;
            services.StartDefaultSandbox();
            Assert.False(vm.IsSandboxActive);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void WithEditSafeOffASendReturnsToTheLiveMirror()
    {
        var (services, vm, window) = Boot();
        try
        {
            services.State.Switcher.EditSafeByDefault = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.Focus;

            vm.SandboxSendAllCommand.Execute(null);

            Assert.False(vm.IsSandboxActive);      // no re-arm — edits are live again
            Assert.Null(services.Bus.Sandbox);
            Assert.Equal(PatternKind.Focus, services.Bus.Current.State.Pattern.Kind);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void TogglingEditSafeOffLeavesAirExactlyAsItIs()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.Focus; // an edit we are about to abandon

            vm.IsSandboxActive = false;

            Assert.False(services.Sandbox.Active);   // an explicit toggle-off stays off
            Assert.Null(services.Bus.Sandbox);
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void LookToPreviewLoadsIntoTheSandboxWhileOnAirFiresIt()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.ActivePattern.Kind = PatternKind.Focus;
            vm.NewLookName = "Focus look";
            vm.SaveLookCommand.Execute(null);
            var look = vm.State.LooksAndCues.Looks.First(l => l.Name == "Focus look");

            vm.State.Pattern.Kind = PatternKind.Grid;
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();

            // → PVW loads it into the editors; air keeps the program.
            vm.ApplyLookToPreviewCommand.Execute(look);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Focus, vm.State.Pattern.Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);

            // ON AIR fires it to the program without disturbing the preview.
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            vm.ApplyLookCommand.Execute(look);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Focus, services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void RemotePlaylistPartSwitchDrivesAirNotTheSandbox()
    {
        var (services, vm, window) = Boot();
        try
        {
            var playlist = vm.State.Pattern.Media.Playlist;
            PlaylistSequencer.Normalize(playlist);
            playlist.Sections.Add(new PlaylistSectionConfig { Name = "Break" });
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Playlist;
            Dispatcher.UIThread.RunJobs();

            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();

            var router = new CommandRouter(services);
            var task = router.ExecuteAsync(ControlProtocol.Parse("SECTION 2"));
            var deadline = Environment.TickCount64 + 8000;
            while (!task.IsCompleted && Environment.TickCount64 < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
            Assert.StartsWith("OK", task.GetAwaiter().GetResult());

            // Air switched part; the operator's sandbox copy is untouched.
            Assert.Equal(1, services.Bus.Current.State.Pattern.Media.Playlist.ActiveSection);
            Assert.Equal(0, vm.State.Pattern.Media.Playlist.ActiveSection);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }
}
