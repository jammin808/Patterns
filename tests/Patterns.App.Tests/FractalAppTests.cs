using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NAudio.Wave;
using Patterns.App.Services;
using Patterns.App.Views.Sections;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The analyser's honesty, its feed path without a device, and the Fractal panel.</summary>
public class FractalAppTests
{
    private static readonly DateTime T0 = new(2026, 9, 4, 22, 30, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public void TheAnalyserListensOnlyWhenAFractalAsksAndSaysSoOffWindows()
    {
        var b = TestApp.Boot();
        try
        {
            var analyser = b.Services.Analyser;
            analyser.Poll();
            Assert.Equal("Off.", analyser.Status);
            Assert.Equal((AudioSourceKind.None, ""), analyser.Wanted());
            Assert.False(analyser.Listening);

            b.Vm.State.Pattern.Kind = PatternKind.Fractal;
            b.Vm.State.Pattern.Fractal.AudioSource = AudioSourceKind.Internal;
            Assert.Equal((AudioSourceKind.Internal, ""), analyser.Wanted());
            analyser.Poll();
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal("Sound-reactive effects listen on Windows only.", analyser.Status);
                Assert.False(analyser.Listening);
                Assert.Empty(AudioAnalyserService.CaptureDevices());
            }
            b.Vm.PollNow();
            Assert.Equal(analyser.Status, b.Vm.FractalAudioStatus);

            // An independent screen's fractal counts too; a fractal that is not listening does not.
            b.Vm.State.Pattern.Fractal.AudioSource = AudioSourceKind.None;
            b.Vm.State.Independent.Add(new OutputAssignment { ScreenId = "x" });
            b.Vm.State.Independent[0].Pattern.Kind = PatternKind.Fractal;
            b.Vm.State.Independent[0].Pattern.Fractal.AudioSource = AudioSourceKind.External;
            b.Vm.State.Independent[0].Pattern.Fractal.AudioDevice = "Desk mic";
            Assert.Equal((AudioSourceKind.External, "Desk mic"), analyser.Wanted());
            b.Vm.State.Independent.Clear();
            analyser.Poll();
            Assert.Equal("Off.", analyser.Status);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheFeedPathTurnsBytesIntoLevelsOnTheChannel()
    {
        var b = TestApp.Boot();
        try
        {
            var analyser = b.Services.Analyser;
            var clock = T0;
            analyser.NowUtc = () => clock;
            AudioLevels.Clear();

            // A 440 Hz stereo float buffer, three windows long.
            var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            var frames = Spectrum.Window * 3;
            var bytes = new byte[frames * 8];
            for (var i = 0; i < frames; i++)
            {
                var s = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
                BitConverter.GetBytes(s).CopyTo(bytes, i * 8);
                BitConverter.GetBytes(s).CopyTo(bytes, i * 8 + 4);
            }
            analyser.Feed(bytes, bytes.Length, format);
            var levels = AudioLevels.Read(clock);
            Assert.True(levels.Mid > levels.Low && levels.Mid > levels.High, $"{levels}");
            Assert.True(levels.Level > 0.3, $"{levels}");
            Assert.Equal(clock, AudioLevels.LastUtc);

            // 16-bit mono lands the same way; an odd width is ignored, never a throw.
            var pcm = new WaveFormat(48000, 16, 1);
            var pcmBytes = new byte[Spectrum.Window * 2 * 2];
            for (var i = 0; i < Spectrum.Window * 2; i++)
            {
                BitConverter.GetBytes((short)(16000 * Math.Sin(2 * Math.PI * 60 * i / 48000.0))).CopyTo(pcmBytes, i * 2);
            }
            clock = T0.AddSeconds(2);
            analyser.Feed(pcmBytes, pcmBytes.Length, pcm);
            var low = AudioLevels.Read(clock);
            Assert.True(low.Low > low.High, $"{low}");
            analyser.Feed(new byte[300], 300, new WaveFormat(48000, 24, 1));
            Assert.Equal(low, AudioLevels.Read(clock));

            // Read back through a render: the fractal on air sees the levels while they are fresh.
            b.Vm.State.Pattern.Kind = PatternKind.Fractal;
            b.Vm.State.Pattern.Fractal.AudioSource = AudioSourceKind.Internal;
            var listening = FractalView.Of(b.Vm.State.Pattern.Fractal, 1.0, AudioLevels.Read(clock));
            var silent = FractalView.Of(b.Vm.State.Pattern.Fractal, 1.0, AudioLevels.Read(clock.AddSeconds(5)));
            Assert.True(listening.Span < silent.Span);
        }
        finally
        {
            AudioLevels.Clear();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheFractalPanelRendersAndASceneAppliesFromTheChipsAndTheLibrary()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.ActivePattern.Kind = PatternKind.Fractal;
            vm.ApplyFractalPresetCommand.Execute("Julia swirl");
            Assert.Equal(FractalKind.Julia, vm.ActivePattern.Fractal.Kind);
            Assert.Equal("Julia swirl", vm.ActivePattern.Fractal.Preset);

            var tile = vm.LibraryAll.Single(i => i.Name == "Burning ship");
            Assert.Equal(("Patterns", "Effects"), (tile.Section, tile.Category));
            tile.Apply();
            Assert.Equal(FractalKind.BurningShip, vm.ActivePattern.Fractal.Kind);
            Assert.Equal(PatternKind.Fractal, vm.ActivePattern.Kind);

            vm.ActivePattern.Fractal.AudioSource = AudioSourceKind.External;
            vm.ActivePattern.Fractal.AudioDevice = "Desk mic";
            vm.RefreshAudioCaptureDevices();
            Assert.Contains("Desk mic", vm.AudioCaptureDevices); // the show's choice stays offered when the input is not here

            var host = new Window { DataContext = vm, Width = 900, Height = 1600, Content = new ScrollViewer { Content = new PatternSection() } };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            using var frame = host.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var chips = host.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("chip")).ToList();
            Assert.Contains(chips, c => Equals(c.Content, "Newton triad"));
            host.Close();
        }
        finally
        {
            b.Dispose();
        }
    }
}
