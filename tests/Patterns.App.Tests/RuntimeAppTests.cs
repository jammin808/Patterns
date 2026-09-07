using System.Runtime;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NAudio.Wave;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The runtime on the desk: the collector follows the outputs, the Machine page says so, the capture feed reads its bytes as samples.</summary>
public class RuntimeAppTests
{
    private static readonly DateTime T0 = new(2026, 9, 7, 19, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public void TheCollectorFollowsTheOutputsAndTheMachinePageSaysSo()
    {
        var b = TestApp.Boot();
        var before = GCSettings.LatencyMode;
        try
        {
            var (services, vm, window) = b;
            Assert.NotEqual(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);

            // Outputs on: sustained low latency for the length of the show.
            services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Outputs.IsLive);
            Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
            vm.PollNow();
            Assert.StartsWith("Runtime .NET", vm.RuntimeText);
            Assert.Contains("sustained low latency (outputs live)", vm.RuntimeText);

            // Off air: the resting mode comes back, and the line says so.
            services.Actions.Execute(ShowActionKind.OutputsOff, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Outputs.IsLive);
            Assert.Equal(ShowGc.RestingMode, GCSettings.LatencyMode);
            Assert.NotEqual(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
            vm.PollNow();
            Assert.Contains("off air", vm.RuntimeText);

            // The Machine page carries the line under the start-up line.
            window.Width = 1420;
            window.Height = 900;
            vm.SelectPage(Shell.IndexOf("Machine"));
            Dispatcher.UIThread.RunJobs();
            var page = window.GetVisualDescendants().OfType<AdminSection>().First();
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.RuntimeText);
        }
        finally
        {
            b.Dispose();
            GCSettings.LatencyMode = before;
        }
    }

    [AvaloniaFact]
    public void TheFeedReadsFloatAndPcmBuffersAsSamplesAndDropsAnUnevenTail()
    {
        var b = TestApp.Boot();
        try
        {
            var analyser = b.Services.Analyser;
            var clock = T0;
            analyser.NowUtc = () => clock;
            AudioLevels.Clear();

            // Float mono, exactly one window: the same levels the analysis gives the samples directly.
            var mono = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
            var samples = new float[Spectrum.Window];
            for (var i = 0; i < samples.Length; i++) samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
            var bytes = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            analyser.Feed(bytes, bytes.Length, mono);
            var direct = new LevelSmoother().Follow(Spectrum.Analyse(samples, 48000), Spectrum.Window / 48000.0);
            var fed = AudioLevels.Read(clock);
            Assert.Equal(direct.Level, fed.Level, 4);
            Assert.Equal(direct.Mid, fed.Mid, 4);
            Assert.True(fed.Mid > fed.Low && fed.Mid > fed.High, $"{fed}");

            // Sixteen-bit stereo, two windows (the ring keeps the newest half of the last window,
            // so the second analysis is all this sound) with an uneven tail (three bytes past the
            // last whole frame): the tail is dropped, never read as a sample, and the levels follow.
            var stereo = new WaveFormat(48000, 16, 2);
            var pcm = new byte[Spectrum.Window * 2 * 4 + 3];
            for (var i = 0; i < Spectrum.Window * 2; i++)
            {
                var s = (short)(20000 * Math.Sin(2 * Math.PI * 60 * i / 48000.0));
                BitConverter.GetBytes(s).CopyTo(pcm, i * 4);
                BitConverter.GetBytes(s).CopyTo(pcm, i * 4 + 2);
            }
            pcm[^1] = 0x7F;
            pcm[^2] = 0x7F;
            pcm[^3] = 0x7F;
            clock = T0.AddSeconds(1);
            analyser.Feed(pcm, pcm.Length, stereo);
            var low = AudioLevels.Read(clock);
            Assert.Equal(clock, AudioLevels.LastUtc);
            Assert.True(low.Low > low.Mid && low.Low > low.High, $"{low}");

            // A count past the buffer's end is clamped to the buffer; a short buffer publishes nothing new.
            clock = T0.AddSeconds(2);
            analyser.Feed(new byte[64], 4096, mono);
            Assert.Equal(T0.AddSeconds(1), AudioLevels.LastUtc);
        }
        finally
        {
            AudioLevels.Clear();
            b.Dispose();
        }
    }
}
