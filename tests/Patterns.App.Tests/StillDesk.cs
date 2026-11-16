using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>What a still desk stood at when its count began: the Eye's builds and picture, the process's contained errors, and the sample the desk's sampler last took.</summary>
internal sealed record StillBaseline(long Builds, EyeGraph Graph, long Faults, string? LastFault, DateTime? LastFaultLocal, long SampleFaults, DateTime SampleUtc, long DeskTicks, DateTime TakenUtc);

/// <summary>
/// A still desk's baseline, taken only once the desk has taken its own first readings: its status tick has ticked (the Desk tick row
/// exists) and its sampler has sampled the count of contained errors the process holds at that moment (the advisor's suggestions are
/// built). A desk younger than one tick of its own 1 s clocks would otherwise take a first reading inside the counted window and
/// rebuild the Eye for a row that was only late. The waits are on those facts, never on a time; nothing here runs inside the window.
/// </summary>
internal static class StillDesk
{
    public static StillBaseline Baseline(AppServices services, int timeoutMs = 10000)
    {
        var watch = Stopwatch.StartNew();
        while (services.DeskTick.Ticks < 1)
        {
            Assert.True(watch.ElapsedMilliseconds < timeoutMs,
                $"the desk's status tick never ticked in {timeoutMs} ms — ticks {services.DeskTick.Ticks}, the sampler {(services.Metrics.Current is null ? "not sampled" : "sampled")}, timers {string.Join(", ", DeskTimers.RunningOwners.Take(6))}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        // The sampler's sample carries the count the process holds now: sampled on the test's own clock, again if an error landed
        // between the sample and the read (each pass a fact: the sample's count against the counter).
        var faults = -1L;
        for (var pass = 0; pass < 10 && services.Metrics.Current?.Faults != faults; pass++)
        {
            services.Metrics.Poll();
            faults = HealthMonitor.Faults;
        }
        var sample = services.Metrics.Current;
        Assert.True(sample is not null && sample.Faults == faults,
            $"the sampler's sample never caught up with the process's contained errors: sample {(sample is null ? "none" : sample.Faults.ToString(CultureInfo.InvariantCulture))}, the process {HealthMonitor.Faults} (last {HealthMonitor.LastFault})");
        services.Eye.Refresh();
        return new StillBaseline(services.Eye.Cost.Total, services.Eye.Graph, faults, HealthMonitor.LastFault, HealthMonitor.LastFaultLocal,
            sample!.Faults, sample.Utc, services.DeskTick.Ticks, DateTime.UtcNow);
    }

    /// <summary>
    /// After the counted window: the Eye rebuilt nothing. A rebuild's words name the first node that moved with its old and new
    /// words, the sample's count at the baseline and when the first sample after it was taken, and the process's contained errors at
    /// both ends with the last one's words and time. A contained error inside the window is that diagnosis and never a failure of its
    /// own: an earlier test's late error, or an ambient port's, may land in any window, and what a still desk owes is a still Eye.
    /// </summary>
    public static void AssertStill(AppServices services, StillBaseline at, int ticks, string what)
    {
        var builds = services.Eye.Cost.Total;
        if (builds == at.Builds) return;
        var faults = HealthMonitor.Faults;
        Assert.Fail(
            $"{builds - at.Builds} rebuilds in {ticks} ticks of {what} — {Moved(at.Graph, services.Eye.Graph)} · at the baseline the sample counted {at.SampleFaults} contained errors " +
            $"(taken {(at.TakenUtc - at.SampleUtc).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms before it), the desk had ticked {at.DeskTicks} times; {FirstSampleAfter(services, at)} · " +
            $"the process counted {at.Faults} contained errors at the baseline (last: {at.LastFault ?? "none"} at {Clock(at.LastFaultLocal)}) " +
            $"and {faults} after (last: {HealthMonitor.LastFault ?? "none"} at {Clock(HealthMonitor.LastFaultLocal)})");
    }

    /// <summary>
    /// The desk's first sample after the baseline: pumps the desk's clocks until its sampler has sampled again, so a window counted
    /// up to it holds the reading it is about. A wait on that fact, bounded; its words carry the newest sample's time and the timers.
    /// </summary>
    public static MetricSample NextSample(AppServices services, StillBaseline at, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var next = services.Metrics.History.Recent.FirstOrDefault(s => s.Utc > at.SampleUtc);
            if (next is not null) return next;
            Assert.True(watch.ElapsedMilliseconds < timeoutMs,
                $"the desk's sampler took no sample in {timeoutMs} ms after the baseline's (taken {at.SampleUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}; newest {services.Metrics.Current?.Utc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "none"}, live {services.Metrics.Live}), timers {string.Join(", ", DeskTimers.RunningOwners.Take(6))}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    /// <summary>The first node whose words, line or light moved between two pictures, with its old and new words; or that nothing drawn moved.</summary>
    public static string Moved(EyeGraph before, EyeGraph after)
    {
        foreach (var n in after.Nodes)
        {
            var was = before.Find(n.Id);
            if (was is null) return $"{n.Id} appeared: {n.Sub} · {n.Light} · {string.Join(" | ", n.Words)}";
            if (was.Sub == n.Sub && was.Light == n.Light && was.Words.SequenceEqual(n.Words)) continue;
            return $"{n.Id}: {was.Sub} · {was.Light} → {n.Sub} · {n.Light} · words gone [{string.Join(" | ", was.Words.Except(n.Words))}] new [{string.Join(" | ", n.Words.Except(was.Words))}]";
        }
        var gone = before.Nodes.FirstOrDefault(n => after.Find(n.Id) is null);
        return gone is not null ? $"{gone.Id} went: {gone.Sub} · {string.Join(" | ", gone.Words)}" : "nothing drawn moved (a hashed fact no node draws)";
    }

    private static string FirstSampleAfter(AppServices services, StillBaseline at)
    {
        var next = services.Metrics.History.Recent.FirstOrDefault(s => s.Utc > at.SampleUtc);
        return next is null
            ? "no sample after it"
            : $"the first sample after it at +{(next.Utc - at.TakenUtc).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms counting {next.Faults}";
    }

    private static string Clock(DateTime? local) => local?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "—";
}
