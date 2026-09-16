using System.Text.Json;
using Avalonia.Headless.XUnit;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.Core.Services;
using Patterns.Platform.Windows;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 69.3 on the desk: the metrics tick hands the GPU cache governor the limit for this card and
/// this rung (the worse of the media ladder's and the card's own), the sinks' draws apply it — none
/// here, headless, so the words say so rather than pretend — and STATE's machine row, the sample and
/// the Machine page carry the cache and the collector's facts.
/// </summary>
public class GpuGovernorAppTests
{
    private const long MB = 1024L * 1024;
    private static readonly DateTime Taken = new(2026, 9, 16, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>A small card beside the software adapter: an eighth of 256 MB is the floor, whatever this machine's class.</summary>
    private static MachineFacts SmallCard() => new(Taken, "1.69.3", "SHOW-PC-2", "Windows 11 Pro 24H2 (build 26100.4351)", "10.0.0", "Intel Core i5-1235U", 12, 15.8,
        new[]
        {
            new GpuFact("Intel Iris Xe Graphics", 0x8086, 0x46A8, 256, "31.0.101.5186", "2024-01-01", "Intel", false),
            new GpuFact("Microsoft Basic Render Driver", 0x1414, 0x8C, 65536, "", "", "", true),
        },
        Array.Empty<MachineDisplayFact>(), Array.Empty<AudioEndpointFact>(), "Balanced", false, "on", "off", "as Windows decides", Array.Empty<string>());

    [AvaloniaFact]
    public void TheTickFeedsTheGovernorAndTheMachineRowCarriesTheCacheAndTheCollector()
    {
        var b = TestApp.Boot();
        var wasExtra = MediaMemory.Extra;
        GpuCacheGovernor.ResetForTests();
        MachineProbe.Source = SmallCard;
        try
        {
            var (services, vm, _) = b;
            Assert.Equal(256, SystemMetricsService.DedicatedVramMB());                                   // the software adapter's number is never the card's

            services.Metrics.Poll();
            Assert.Equal(MemoryPressure.None, services.Metrics.GpuRung);
            var facts = GpuCacheGovernor.Facts;
            Assert.False(facts.HasContext);                                                              // headless: no draw ever held a GPU context
            Assert.Equal(GpuGovernor.FloorBytes, facts.LimitBytes);                                      // wanted now, applied by the next draw that has one
            Assert.Equal(0, facts.Applications);
            Assert.Equal("GPU cache: no GPU context (software rendering)", GpuCacheGovernor.Words);
            var s = services.Metrics.Current!;
            Assert.Equal(32, s.GpuCacheLimitMB);
            Assert.Equal(-1, s.GpuCacheUsedMB);
            Assert.True(s.LohMB >= 0 && s.GcLastPauseMs >= 0 && s.GcGen0 >= s.GcGen2);

            var machine = JsonDocument.Parse(services.Router.StateJson()).RootElement.GetProperty("machine");
            var cache = machine.GetProperty("gpuCache");
            Assert.False(cache.GetProperty("hasContext").GetBoolean());
            Assert.Equal(32, cache.GetProperty("limitMB").GetDouble());
            Assert.Equal(-1, cache.GetProperty("usedMB").GetDouble());
            Assert.Equal(0, cache.GetProperty("purges").GetInt32());
            Assert.Equal("none", cache.GetProperty("rung").GetString());
            Assert.Contains("no GPU context", cache.GetProperty("words").GetString());
            var gc = machine.GetProperty("gc");
            Assert.Equal(ShowGc.Describe(), gc.GetProperty("mode").GetString());
            Assert.StartsWith("collector ", gc.GetProperty("words").GetString());
            Assert.True(gc.GetProperty("gen0").GetInt32() >= gc.GetProperty("gen2").GetInt32());
            Assert.True(gc.GetProperty("lohMB").GetDouble() >= 0);

            // Critical pressure on the media ladder: the governor's rung follows, the limit quarters (the floor here) and a purge is asked of the next draw.
            var budget = MediaMemory.BudgetBytes(MemoryBudget.MachineMB);
            MediaMemory.Extra = () => budget + 1;
            services.Metrics.Poll();
            Assert.Equal(MemoryPressure.Critical, services.Metrics.Pressure.Level);
            Assert.Equal(MemoryPressure.Critical, services.Metrics.GpuRung);
            Assert.Equal(GpuGovernor.FloorBytes, GpuCacheGovernor.Facts.LimitBytes);
            machine = JsonDocument.Parse(services.Router.StateJson()).RootElement.GetProperty("machine");
            Assert.Equal("critical", machine.GetProperty("gpuCache").GetProperty("rung").GetString());
        }
        finally
        {
            MediaMemory.Extra = wasExtra;
            MachineProbe.Source = null;
            GpuCacheGovernor.ResetForTests();
            b.Dispose();
        }
    }

    [Fact]
    public void ADrawWithoutAContextSaysSoAndAppliesNothing()
    {
        GpuCacheGovernor.ResetForTests();
        try
        {
            GpuCacheGovernor.Want(64 * MB, purge: true);
            GpuCacheGovernor.Apply(null, nowTicks: 5000);
            GpuCacheGovernor.Apply(null, nowTicks: 7000);
            var f = GpuCacheGovernor.Facts;
            Assert.False(f.HasContext);
            Assert.Equal(64 * MB, f.LimitBytes);                                                        // the wanted limit stands until a context takes it
            Assert.Equal(0, f.Applications);
            Assert.Equal(0, f.Purges);
            Assert.Equal(0, f.Resources);
            Assert.Equal("GPU cache: no GPU context (software rendering)", GpuCacheGovernor.Words);
        }
        finally
        {
            GpuCacheGovernor.ResetForTests();
        }
    }
}
