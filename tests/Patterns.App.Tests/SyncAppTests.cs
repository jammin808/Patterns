using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NAudio.Wave;
using Patterns.App.Services;
using Patterns.App.Views.Sections;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The audio chain's new stages, the clicks, the video delay reaching every clip, and the Audio page's sync block.</summary>
public class SyncAppTests
{
    /// <summary>A stereo sine at 48 kHz that never ends.</summary>
    private sealed class SineSource : ISampleProvider
    {
        private long _frame;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i += 2)
            {
                var s = (float)Math.Sin(2 * Math.PI * 440 * _frame++ / 48000.0);
                buffer[offset + i] = s;
                buffer[offset + i + 1] = s;
            }
            return count;
        }
    }

    [Fact]
    public void TheChainDelaysAndResamples()
    {
        var delayed = new DelaySampleProvider(new SineSource(), 10);
        var buf = new float[4800 * 2];
        Assert.Equal(buf.Length, delayed.Read(buf, 0, buf.Length));
        Assert.Equal(10, delayed.DelayMs);
        for (var i = 0; i < 480 * 2; i++) Assert.Equal(0f, buf[i]);       // ten milliseconds of silence first
        Assert.Contains(buf.Skip(960), x => Math.Abs(x) > 0.5);          // then the sound
        Assert.Equal(0f, buf[960], 5);                                  // the very first input sample, a sine's zero

        var asrc = new AsrcSampleProvider(new SineSource());
        Assert.Equal(1.0, asrc.Ratio);
        var chunk = new float[1024 * 2];
        Assert.Equal(chunk.Length, asrc.Read(chunk, 0, chunk.Length));
        Assert.Contains(chunk, x => Math.Abs(x) > 0.5);
        asrc.Ratio = 1.001;
        for (var i = 0; i < 40; i++) asrc.Read(chunk, 0, chunk.Length);
        Assert.InRange(asrc.RatioInForce, 1.0009, 1.0011);
        Assert.True(asrc.InputFramesConsumed < asrc.OutputFramesProduced, "fewer input frames than output frames at a ratio above one");
        Assert.InRange(asrc.OutputFramesProduced / (double)asrc.InputFramesConsumed, 1.0005, 1.0015);
    }

    [Fact]
    public void TheClicksLandOnTheScheduledFrames()
    {
        var tone = new ToneSampleProvider();
        tone.SetTargets(0, 0);
        tone.ScheduleClick(480);
        tone.ScheduleClick(480);   // the same frame twice is one click
        Assert.Equal(1, tone.PendingClicks);
        var buf = new float[2000 * 2];
        Assert.Equal(buf.Length, tone.Read(buf, 0, buf.Length));
        Assert.Equal(2000, tone.FramesRendered);
        Assert.Equal(0, tone.PendingClicks);
        static double Energy(float[] b, int fromFrame, int toFrame)
        {
            double e = 0;
            for (var f = fromFrame; f < toFrame; f++) e += b[f * 2] * b[f * 2];
            return e;
        }
        Assert.Equal(0, Energy(buf, 0, 480));
        Assert.True(Energy(buf, 480, 480 + ToneSampleProvider.ClickFrames) > 1, "the click");
        Assert.Equal(0, Energy(buf, 480 + ToneSampleProvider.ClickFrames + 1, 2000), 6);
        tone.ScheduleClick(100); // already past: dropped
        Assert.Equal(0, tone.PendingClicks);
    }

    [AvaloniaFact]
    public void TheClicksFollowTheMasterGridAndTheVideoDelayReachesEveryClip()
    {
        var b = TestApp.Boot();
        var clip = AudioFakes.TempFile("walkin.mp4");
        try
        {
            var vm = b.Vm;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);

            // The clicks: for a stream that started sounding at master 10.0, the mark at 12.0 is frame 96000.
            var tone = new ToneSampleProvider();
            b.Services.Audio.SeedForTests(tone, streamStartMaster: 10.0);
            SyncMarks.Enabled = true;
            b.Services.Audio.ScheduleSyncClicks(masterNow: 11.0);
            Assert.Equal(1, tone.PendingClicks);
            b.Services.Audio.ScheduleSyncClicks(masterNow: 11.0); // idempotent
            Assert.Equal(1, tone.PendingClicks);
            b.Services.Audio.ScheduleSyncClicks(masterNow: 13.4);  // 14.0 is within the look-ahead, 16.0 is not
            Assert.Equal(2, tone.PendingClicks);
            SyncMarks.Enabled = false;

            // The video delay: every mounted clip is told, and a clip mounted later is told on mount.
            vm.ActivePattern.Kind = PatternKind.Media;
            vm.ActivePattern.Media.Source = MediaSource.Video;
            vm.ActivePattern.Media.VideoPath = clip;
            b.Services.RepublishNow();
            Dispatcher.UIThread.RunJobs();
            Assert.NotEmpty(fakes.Sources);
            vm.State.AudioPlayer.VideoAudioDelayMs = 80;
            b.Services.AudioPlayer.Poll();
            Assert.All(fakes.Sources, s => Assert.Equal(80, s.AudioDelayMs));
            Assert.Equal(80, b.Services.Video.AudioDelayMs);

            // The Audio page shows the master-clock block; the check toggles the channel and the sinks flash.
            vm.PollNow();
            Assert.StartsWith("Locked to the master clock.", vm.SyncStatus);
            vm.State.AudioPlayer.SyncLock = false;
            vm.PollNow();
            Assert.StartsWith("Outputs free-run", vm.SyncStatus);
            var host = new Window { DataContext = vm, Width = 900, Height = 1800, Content = new ScrollViewer { Content = new AudioSection() } };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var toggle = host.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Content as string == "SYNC CHECK");
            Assert.False(SyncMarks.Enabled);
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.SyncCheck);
            Assert.True(SyncMarks.Enabled);
            Assert.Contains("Sync check on", vm.StatusMessage);
            toggle.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(SyncMarks.Enabled);
            Assert.Contains(host.GetVisualDescendants().OfType<Slider>(), s => Math.Abs(s.Maximum - 2000) < 0.5 && Math.Abs(s.Minimum + 1000) < 0.5); // the video sound delay
            host.Close();

            // A device's delay lives on the show and the page's row (filled on refresh) reads and writes it.
            vm.RefreshAudioDevicesCommand.Execute(null);
            vm.State.AudioPlayer.SetDelay(AudioPlayerService.DefaultDeviceKey, 150);
            Assert.Equal(150, vm.State.AudioPlayer.DelayFor(AudioPlayerService.DefaultDeviceKey));
            Assert.Equal(150, vm.AudioDevices.First(d => d.Name == AudioPlayerService.DefaultDeviceKey).DelayMs);
            vm.AudioDevices.First(d => d.Name == AudioPlayerService.DefaultDeviceKey).DelayMs = 0;
            Assert.Empty(vm.State.AudioPlayer.OutputDelays);
        }
        finally
        {
            SyncMarks.Enabled = false;
            b.Dispose();
            try { File.Delete(clip); } catch { }
        }
    }
}
