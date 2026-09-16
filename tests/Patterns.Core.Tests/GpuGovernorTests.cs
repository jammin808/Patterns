using System.Runtime;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 69.3: Skia's GPU resource cache bounded from the card and the machine's class rather than
/// one number for every machine, shrunk as either memory presses (the media ladder's rung or the
/// card's own), purged at high and critical, never under the floor; the collector's facts read
/// honestly and the large-object heap asked to compact once each time the outputs go off air.
/// </summary>
public class GpuGovernorTests
{
    private const long MB = 1024L * 1024;

    [Fact]
    public void TheLimitIsTheClassNumberBoundedByTheCardAndShrunkByTheRung()
    {
        // No card known: the class's own number (round 57's table).
        Assert.Equal(64 * MB, GpuGovernor.LimitBytes(MachineClass.Small, 0, MemoryPressure.None));
        Assert.Equal(128 * MB, GpuGovernor.LimitBytes(MachineClass.Standard, 0, MemoryPressure.None));
        Assert.Equal(256 * MB, GpuGovernor.LimitBytes(MachineClass.Big, 0, MemoryPressure.None));

        // A card known: never more than an eighth of its dedicated memory.
        Assert.Equal(256 * MB, GpuGovernor.LimitBytes(MachineClass.Big, 16376, MemoryPressure.None));       // a 16 GB card leaves the class number alone
        Assert.Equal(64 * MB, GpuGovernor.LimitBytes(MachineClass.Standard, 512, MemoryPressure.None));     // a 512 MB card: 64 MB
        Assert.Equal(GpuGovernor.FloorBytes, GpuGovernor.LimitBytes(MachineClass.Big, 256, MemoryPressure.None));   // a 256 MB card: an eighth is the floor

        // Pressure: three quarters, a half, a quarter — and never under the floor.
        Assert.Equal(96 * MB, GpuGovernor.LimitBytes(MachineClass.Standard, 0, MemoryPressure.Elevated));
        Assert.Equal(64 * MB, GpuGovernor.LimitBytes(MachineClass.Standard, 0, MemoryPressure.High));
        Assert.Equal(32 * MB, GpuGovernor.LimitBytes(MachineClass.Standard, 0, MemoryPressure.Critical));
        Assert.Equal(GpuGovernor.FloorBytes, GpuGovernor.LimitBytes(MachineClass.Small, 0, MemoryPressure.Critical));   // 16 MB would thrash: the floor
        Assert.Equal(32 * MB, GpuGovernor.FloorBytes);
    }

    [Fact]
    public void TheCardsOwnRungComesFromWhatItUsesOfTheBudgetItIsGranted()
    {
        Assert.Equal(MemoryPressure.None, GpuGovernor.VramPressure(-1, 4096));                          // no reading: no rung
        Assert.Equal(MemoryPressure.None, GpuGovernor.VramPressure(100, 0));                            // no budget: no rung
        Assert.Equal(MemoryPressure.None, GpuGovernor.VramPressure(50, 100));
        Assert.Equal(MemoryPressure.Elevated, GpuGovernor.VramPressure(70, 100));
        Assert.Equal(MemoryPressure.High, GpuGovernor.VramPressure(85, 100));
        Assert.Equal(MemoryPressure.Critical, GpuGovernor.VramPressure(100, 100));
        Assert.Equal(MemoryPressure.Critical, GpuGovernor.VramPressure(120, 100));

        // The governor stands on the worse of the two rungs.
        Assert.Equal(MemoryPressure.High, GpuGovernor.Worse(MemoryPressure.Elevated, MemoryPressure.High));
        Assert.Equal(MemoryPressure.Critical, GpuGovernor.Worse(MemoryPressure.Critical, MemoryPressure.None));
        Assert.Equal(MemoryPressure.None, GpuGovernor.Worse(MemoryPressure.None, MemoryPressure.None));

        // The unlocked resources go at high and critical only.
        Assert.False(GpuGovernor.PurgeAt(MemoryPressure.None));
        Assert.False(GpuGovernor.PurgeAt(MemoryPressure.Elevated));
        Assert.True(GpuGovernor.PurgeAt(MemoryPressure.High));
        Assert.True(GpuGovernor.PurgeAt(MemoryPressure.Critical));
    }

    [Fact]
    public void TheWordsSayTheFillTheLimitAndThePurgesOrThatThereIsNoContext()
    {
        Assert.Equal("GPU cache: no GPU context (software rendering)", GpuGovernor.Words(false, 128 * MB, 48 * MB, 212, 3));
        Assert.Equal("GPU cache 48 MB of 128 MB (212 resources) · purged 3×", GpuGovernor.Words(true, 128 * MB, 48 * MB, 212, 3));
        Assert.Equal("GPU cache 0 MB of 64 MB (1 resource)", GpuGovernor.Words(true, 64 * MB, 0, 1, 0));
    }

    [Fact]
    public void TheCollectorsFactsAreReadAndTheLargeObjectHeapIsAskedToCompactOnceOffAir()
    {
        var before = ShowGc.CompactionsRequested;
        ShowGc.Apply(false);                                                                             // resting, whatever came before
        Assert.Equal(before, ShowGc.CompactionsRequested);                                               // never live: nothing to give back

        ShowGc.Apply(true);                                                                              // the outputs go live
        ShowGc.Apply(false);                                                                             // and off air: one compaction asked
        Assert.Equal(before + 1, ShowGc.CompactionsRequested);
        ShowGc.Apply(false);                                                                             // still off air: asked no more
        Assert.Equal(before + 1, ShowGc.CompactionsRequested);
        Assert.NotEqual(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);

        var f = ShowGc.Facts();
        Assert.Equal(ShowGc.Describe(), f.Mode);
        Assert.True(f.Gen0 >= f.Gen1 && f.Gen1 >= f.Gen2, "a collection of a generation counts for every generation under it");
        Assert.True(f.LohMB >= 0 && f.PohMB >= 0 && f.LastPauseMs >= 0 && f.PausePct >= 0);
        Assert.Equal(ShowGc.CompactionsRequested, f.Compactions);
        Assert.StartsWith("collector ", f.Words);
        Assert.Contains("large objects", f.Words);
        Assert.Contains("LOH compaction asked", f.Words);

        // The line, exactly, from fixed facts.
        Assert.Equal("collector interactive (off air) · gen 0/1/2 × 10/4/1 · large objects 84 MB · last pause 4.2 ms · 0.3 % paused",
            new GcFacts("interactive (off air)", 10, 4, 1, 84.0, 0, 4.2, 0.3, 0).Words);
        Assert.Equal("collector sustained low latency (outputs live) · gen 0/1/2 × 10/4/1 · large objects 84 MB · pinned 2 MB · last pause 4.2 ms · 0.3 % paused · LOH compaction asked 2×",
            new GcFacts("sustained low latency (outputs live)", 10, 4, 1, 84.0, 2.0, 4.2, 0.3, 2).Words);
    }
}
