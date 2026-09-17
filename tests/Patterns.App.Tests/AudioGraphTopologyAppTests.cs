using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The audio graph's topology is rebuilt when something in it moved — a crosspoint through the
/// side effects, a tap with the inputs — and never on the tick: the 50 ms tick advances the
/// envelopes and the meters alone while nothing changes.
/// </summary>
public class AudioGraphTopologyAppTests
{
    [AvaloniaFact]
    public void TheTopologyIsRebuiltOnAChangeAndTheTickOnlyAdvancesTheEnvelopes()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var graph = services.AudioGraph!;
            vm.State.AudioRouting.Enabled = true;                                                        // the matrix on: a side effect rebuilds the plan
            AudioRouting.SetRoute(vm.State, "programme", "dev:(computer output)");
            Settle(window);
            Assert.True((services.Reconciles.Of("audio graph")?.Runs ?? 0) > 0, "the matrix change reached the graph through the side effects");
            var rebuilds = graph.TopologyRebuilds;
            Assert.True(rebuilds >= 1);

            var quiet = graph.QuietTicks;
            for (var i = 0; i < 5; i++) graph.Poll();                                                    // the ticks: nothing moved
            Assert.Equal(rebuilds, graph.TopologyRebuilds);
            Assert.Equal(quiet + 5, graph.QuietTicks);

            graph.Reconcile();                                                                           // an explicit ask (the Audio page): one rebuild
            Assert.Equal(rebuilds + 1, graph.TopologyRebuilds);

            var runs = services.Reconciles.Of("audio graph")!.Runs;
            vm.State.Countdown.Enabled = !vm.State.Countdown.Enabled;                                    // not the graph's: skipped
            Settle(window);
            Assert.Equal(runs, services.Reconciles.Of("audio graph")!.Runs);
            vm.State.Monitor.VolumePct = 33;                                                             // the monitor rule: the graph's
            Settle(window);
            Assert.True(services.Reconciles.Of("audio graph")!.Runs > runs);
        }
        finally
        {
            b.Dispose();
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>
    /// Round 76: a picture that leaves the programme takes its sound with it the way it took its frames — the
    /// lane's input fades to silence over the show's transition and closes when the fade lands, where it used to
    /// be cut from the mixer mid-word — and the status says so while it fades.
    /// </summary>
    [AvaloniaFact]
    public void ALeavingClipsSoundFadesOutOverTheTransitionAndClosesWhenItLands()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var fakes = AudioFakes.Install(b);
            var ring = new Patterns.Audio.AudioRing(2, 48000);
            fakes.Taps = _ => ring;
            var graph = services.AudioGraph!;
            vm.IsSandboxActive = false;
            vm.State.AudioRouting.Enabled = true;
            AudioRouting.SetRoute(vm.State, "programme", "dev:(computer output)");
            vm.State.Transition.Enabled = true;
            vm.State.Transition.DurationMs = 500;
            Assert.Equal(500, AudioRouting.LeaveFadeMs(vm.State));

            // A clip on the programme: its tap is an input on the programme's lane, at its gain, not fading.
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Video;
            vm.State.Pattern.Media.VideoPath = AudioFakes.TempFile("leaving.mp4");
            Settle(window);
            services.ReconcileInputs();
            graph.Reconcile();
            var playing = Assert.Single(graph.InputRows(), r => r.Tag.StartsWith("clip:", StringComparison.Ordinal));
            Assert.Equal("dev:(computer output)", playing.Lane);
            Assert.Equal("programme", playing.Source);
            Assert.False(playing.Leaving);
            Assert.Equal(1.0, playing.Gain, 3);
            Assert.Contains("1 input playing", graph.Status);

            // The clip leaves the programme: the input stays, fading, and the status says so.
            vm.State.Pattern.Kind = PatternKind.Grid;
            Settle(window);
            services.ReconcileInputs();
            var at = ShowClock.UtcNow;
            graph.Reconcile();
            var leaving = Assert.Single(graph.InputRows(), r => r.Tag.StartsWith("clip:", StringComparison.Ordinal));
            Assert.True(leaving.Leaving);
            Assert.Contains("fading out", graph.Status);

            // The fade lands over the transition's length: the input closes by itself.
            for (var i = 1; i <= 4; i++)
            {
                graph.TickAt(at.AddMilliseconds(500 * i));
            }
            Assert.DoesNotContain(graph.InputRows(), r => r.Tag.StartsWith("clip:", StringComparison.Ordinal));
            Assert.DoesNotContain("fading out", graph.Status);
        }
        finally
        {
            b.Dispose();
        }
    }
}
