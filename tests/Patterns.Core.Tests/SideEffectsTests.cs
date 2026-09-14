using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The change mask: which systems a change reaches, and the budget that says what the reconciles cost.</summary>
public class SideEffectsTests
{
    [Fact]
    public void AChangeReachesTheSystemsThatReadItsSectionsAndNoOther()
    {
        var countdown = new HashSet<string> { nameof(ShowState.Countdown) };
        Assert.False(SideEffectDomains.Touches(countdown, SideEffectDomains.Inputs));
        Assert.False(SideEffectDomains.Touches(countdown, SideEffectDomains.Devices));
        Assert.False(SideEffectDomains.Touches(countdown, SideEffectDomains.Wire));
        Assert.False(SideEffectDomains.Touches(countdown, SideEffectDomains.Twin));

        var boxes = new HashSet<string> { nameof(ShowState.Interactive) };
        Assert.True(SideEffectDomains.Touches(boxes, SideEffectDomains.Devices));
        Assert.False(SideEffectDomains.Touches(boxes, SideEffectDomains.Inputs));

        var picture = new HashSet<string> { nameof(ShowState.Pattern) };
        Assert.True(SideEffectDomains.Touches(picture, SideEffectDomains.Inputs));
        Assert.False(SideEffectDomains.Touches(picture, SideEffectDomains.Devices));
        Assert.False(SideEffectDomains.Touches(picture, SideEffectDomains.Beacon));

        var name = new HashSet<string> { nameof(ShowState.Name) };
        Assert.True(SideEffectDomains.Touches(name, SideEffectDomains.Twin));
        Assert.True(SideEffectDomains.Touches(name, SideEffectDomains.Beacon));
        Assert.True(SideEffectDomains.Touches(name, SideEffectDomains.Mdns));
        Assert.False(SideEffectDomains.Touches(name, SideEffectDomains.Inputs));

        // Everything moved, or nothing could be named: every system runs. Nothing moved: none.
        Assert.True(SideEffectDomains.Touches(null, SideEffectDomains.Osc));
        Assert.False(SideEffectDomains.Touches(new HashSet<string>(), SideEffectDomains.Osc));
        Assert.Equal("everything", SideEffectDomains.Words(null));
        Assert.Equal("nothing", SideEffectDomains.Words(new HashSet<string>()));
        Assert.Equal("Interactive, Pattern", SideEffectDomains.Words(new HashSet<string> { "Pattern", "Interactive" }));
    }

    [Fact]
    public void EverySectionASystemReadsIsARealSectionOfTheShow()
    {
        var sections = TwinSync.SectionNames.ToHashSet(StringComparer.Ordinal);
        foreach (var reads in new[] { SideEffectDomains.Rig, SideEffectDomains.NdiOut, SideEffectDomains.Inputs, SideEffectDomains.Osc, SideEffectDomains.Devices, SideEffectDomains.Wire, SideEffectDomains.Beacon, SideEffectDomains.Mdns, SideEffectDomains.Twin })
        {
            foreach (var section in reads) Assert.Contains(section, sections);
        }
    }

    [Fact]
    public void TheBudgetCountsRunsSkipsAndPassesAndNamesTheCostliest()
    {
        var b = new ReconcileBudget();
        Assert.Equal("Side effects: no pass yet — the first edit runs them.", b.Describe());
        b.Ran("inputs", 3.9);
        b.Ran("devices", 0.2);
        b.Skipped("twin");
        b.Pass(4.2, 2, 1, "Interactive, Pattern", full: false);
        b.Skipped("inputs");
        b.Skipped("devices");
        b.Skipped("twin");
        b.Pass(0.3, 0, 3, "Countdown", full: false);
        b.Ran("inputs", 1.0);
        b.Ran("devices", 0.1);
        b.Ran("twin", 0.5);
        b.Pass(1.7, 3, 0, "everything", full: true);

        Assert.Equal(3, b.Passes);
        Assert.Equal(1, b.FullPasses);
        Assert.Equal(0, b.SlowPasses);
        Assert.Equal(4.2, b.WorstPassMs);
        Assert.Equal("Interactive, Pattern", b.WorstPassSections);
        Assert.Equal(1.7, b.LastPassMs);
        Assert.Equal(3, b.LastRan);
        var inputs = b.Of("inputs")!;
        Assert.Equal(2, inputs.Runs);
        Assert.Equal(1, inputs.Skipped);
        Assert.Equal(3.9, inputs.WorstMs);
        Assert.Equal(1.0, inputs.LastMs);
        Assert.Equal(4.9, inputs.TotalMs, 6);
        Assert.Equal("inputs 2 runs, 1 skipped, worst 3.9 ms", inputs.Words);
        Assert.Equal("twin 1 run, 2 skipped, worst 0.5 ms", b.Of("twin")!.Words);
        Assert.Null(b.Of("osc"));
        Assert.Equal(new[] { "inputs", "devices", "twin" }, b.Lines().Select(l => l.Name));

        var words = b.Describe();
        Assert.StartsWith("Side effects: 3 passes (1 ran everything) · worst 4.2 ms after Interactive, Pattern · last 1.7 ms, 3 of 3 ran after everything", words);
        Assert.Contains("inputs 2 runs, 1 skipped, worst 3.9 ms", words);
        Assert.DoesNotContain("past", words);

        b.Pass(ReconcileBudget.SlowMs + 1, 1, 0, "Output", full: false);
        Assert.Equal(1, b.SlowPasses);
        Assert.Contains($"1 past {ReconcileBudget.SlowMs:0} ms", b.Describe());
        b.Reset();
        Assert.Equal(0, b.Passes);
        Assert.Empty(b.Lines());
    }
}
