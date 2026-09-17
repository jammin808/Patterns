using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 73: a lower third comes on, holds and leaves by itself — the chip counts it down, STATE and
/// the Eye say when it goes, and FOR / STAY / HOLD on the wire override one run or set the design's own.
/// </summary>
public class LowerThirdTimedAppTests
{
    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static JsonElement State(CommandRouter router) => JsonDocument.Parse(router.StateJson()).RootElement;

    [AvaloniaFact]
    public void ATimedDesignCountsDownOnItsChipInStateAndInTheEyeAndTheWireOverridesARun()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            vm.State.Transition.Enabled = false;
            var neon = LowerThirdPresets.Create("Neon");
            neon.Name = "Neon";
            neon.InMs = 0;                                                       // straight into the hold, so the chip counts from the first tick
            vm.State.LowerThirds.Designs.Add(neon);
            Dispatcher.UIThread.RunJobs();
            var router = new CommandRouter(services);
            Assert.True(neon.Timed);
            Assert.Equal(5000, neon.HoldMs);

            // On air: five seconds, counted down on the chip, in STATE and on the Eye's desk node.
            vm.ShowLowerThird(neon);
            Dispatcher.UIThread.RunJobs();
            vm.RefreshTallies();
            Assert.True(neon.IsOnAir);
            Assert.StartsWith("ON AIR · ", neon.OnAirText);
            Assert.EndsWith(" s", neon.OnAirText);
            var state = State(router);
            Assert.True(state.GetProperty("lowerThirdTimed").GetBoolean());
            Assert.InRange(state.GetProperty("lowerThirdLeavesIn").GetDouble(), 3.5, 5.0);
            Assert.Equal(5000, state.GetProperty("lowerThirdHoldMs").GetInt32());
            services.Eye.Refresh();
            Assert.Contains(services.Eye.Graph.Find("desk")!.Words, w => w.StartsWith("Lower third 'Neon'", StringComparison.Ordinal) && w.Contains("leaves in", StringComparison.Ordinal));

            // This run stays: no countdown, the design's own hold untouched.
            Assert.StartsWith("OK", Send(router, "LT Neon STAY"));
            Dispatcher.UIThread.RunJobs();
            vm.RefreshTallies();
            Assert.Equal("ON AIR", neon.OnAirText);
            state = State(router);
            Assert.False(state.GetProperty("lowerThirdTimed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, state.GetProperty("lowerThirdLeavesIn").ValueKind);
            Assert.Equal(0, state.GetProperty("lowerThirdHoldMs").GetInt32());
            Assert.Equal(5000, neon.HoldMs);
            Assert.True(neon.Timed);
            services.Eye.Refresh();
            Assert.Contains(services.Eye.Graph.Find("desk")!.Words, w => w.StartsWith("Lower third 'Neon'", StringComparison.Ordinal) && w.EndsWith("until hidden", StringComparison.Ordinal));

            // This run for two seconds: the hold is the run's.
            Assert.StartsWith("OK", Send(router, "LT Neon FOR 2"));
            Dispatcher.UIThread.RunJobs();
            state = State(router);
            Assert.True(state.GetProperty("lowerThirdTimed").GetBoolean());
            Assert.Equal(2000, state.GetProperty("lowerThirdHoldMs").GetInt32());
            Assert.InRange(state.GetProperty("lowerThirdLeavesIn").GetDouble(), 1.0, 2.0);

            // The design's own hold, saved with the show — and the copy on air retimes with it.
            Assert.StartsWith("OK", Send(router, "LT Neon HOLD 7"));
            Assert.Equal(7000, neon.HoldMs);
            Assert.True(neon.Timed);
            Assert.StartsWith("OK", Send(router, "LT Neon HOLD STAY"));
            Assert.False(neon.Timed);
            Assert.Equal(7000, neon.HoldMs);                                     // the number is kept for later
            Assert.StartsWith("ERR", Send(router, "LT Neon FOR soon"));
            Assert.StartsWith("ERR", Send(router, "LT Nobody FOR 8"));

            // A plain show of a design that stays: no countdown; the run's override is gone.
            vm.ShowLowerThird(neon);
            Dispatcher.UIThread.RunJobs();
            vm.RefreshTallies();
            Assert.Equal("ON AIR", neon.OnAirText);
            Assert.Equal(0, State(router).GetProperty("lowerThirdHoldMs").GetInt32());
        }
        finally
        {
            b.Dispose();
        }
    }
}
