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
}
