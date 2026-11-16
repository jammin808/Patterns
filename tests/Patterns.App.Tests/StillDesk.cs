using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>What a still desk stood at when its count began: the Eye's builds and picture, the process's contained errors, the sample the desk's sampler last took, and the desk's facts no node draws (<see cref="StillDesk.DeskWords"/>).</summary>
internal sealed record StillBaseline(long Builds, EyeGraph Graph, long Faults, string? LastFault, DateTime? LastFaultLocal, long SampleFaults, DateTime SampleUtc, long DeskTicks, DateTime TakenUtc, string Desk);

/// <summary>What the Eye stood at when a count began — its revision, its builds, its picture — with the process's contained errors and the desk's facts no node draws, for the words at both ends.</summary>
internal sealed record EyeAt(long Rev, long Builds, EyeGraph Graph, long Faults, string? LastFault, DateTime? LastFaultLocal, string Desk);

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
            sample!.Faults, sample.Utc, services.DeskTick.Ticks, DateTime.UtcNow, DeskWords(services));
    }

    /// <summary>
    /// The baseline of a test that drives the desk's ticks by hand (<see cref="MainViewModel.PollNow"/>), taken right after its last
    /// tick and before the tick it counts. A tick's Eye reads the desk inside its health area, before the tick records its own time and
    /// its areas' faults, so what a tick leaves is first read by the next tick's Eye: here the Eye reads it now, through the desk's own
    /// read (the tick's PollEye: the facts read, the picture rebuilt when they moved, the view model's words raised and its seen
    /// revision brought up with it), so the counted tick reads only what moved inside its own window. Nothing here ticks: no area runs
    /// and no time is recorded between this read and the counted tick's own Eye read.
    /// </summary>
    public static EyeAt AfterTicks(AppServices services, MainViewModel vm)
    {
        // Resolved here, by its exact shape, so a rename or an overload fails this helper with its own words and no other caller.
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        var eyeRead = typeof(MainViewModel).GetMethod("PollEye", Private, null, Type.EmptyTypes, null);
        var eyeSeen = typeof(MainViewModel).GetField("_eyeSeen", Private);
        Assert.True(eyeRead is not null && eyeSeen is not null,
            "the desk's tick no longer reads the Eye through MainViewModel.PollEye() into its seen revision _eyeSeen — the helper reads it the way the tick does");
        Assert.True(services.IsDesk, "the desk's Eye read runs only on a desk; this desk's profile is a node's");
        eyeRead!.Invoke(vm, null);
        var seen = (long)eyeSeen!.GetValue(vm)!;
        Assert.True(seen == services.Eye.Rev, string.Create(CultureInfo.InvariantCulture,
            $"the desk's Eye read left the picture at revision {services.Eye.Rev} and the view model's seen revision at {seen}"));
        return At(services);
    }

    /// <summary>The Eye as it stands now, read and nothing more: for a count whose baseline the test's own refresh just took, with no tick before the next.</summary>
    public static EyeAt At(AppServices services) =>
        new(services.Eye.Rev, services.Eye.Cost.Total, services.Eye.Graph, HealthMonitor.Faults, HealthMonitor.LastFault, HealthMonitor.LastFaultLocal, DeskWords(services));

    /// <summary>
    /// After the counted window: neither the picture's revision nor its builds moved. A move's words name the first node that moved with
    /// its old and new words, the desk's ticks and the counts no node draws at both ends, and the process's contained errors at both ends
    /// with the last one's words and time — so a sighting names its own route.
    /// </summary>
    public static void AssertUnmoved(AppServices services, EyeAt at, string what)
    {
        var (rev, builds) = (services.Eye.Rev, services.Eye.Cost.Total);
        if (rev == at.Rev && builds == at.Builds) return;
        Assert.Fail(string.Create(CultureInfo.InvariantCulture,
            $"the picture moved on {what}: revision {at.Rev} → {rev}, builds {at.Builds} → {builds} — {Moved(at.Graph, services.Eye.Graph)} · " +
            $"at the baseline {at.Desk}; after {DeskWords(services)} · " +
            $"the process counted {at.Faults} contained errors at the baseline (last: {at.LastFault ?? "none"} at {Clock(at.LastFaultLocal)}) " +
            $"and {HealthMonitor.Faults} after (last: {HealthMonitor.LastFault ?? "none"} at {Clock(HealthMonitor.LastFaultLocal)})"));
    }

    /// <summary>
    /// The desk's facts an Eye rebuild may follow that <see cref="Moved"/> cannot name: its ticks (the last and the window's worst, with
    /// the worst's slowest area, and the areas that threw), and the counts the Eye hashes but no node draws — wire and web clients,
    /// decks, app links, heard nodes and Companions. Culture-invariant.
    /// </summary>
    public static string DeskWords(AppServices services)
    {
        var tick = services.DeskTick;
        var nodes = services.Kernel.Nodes.Nodes;
        return string.Create(CultureInfo.InvariantCulture,
            $"desk ticks {tick.Ticks} (last {tick.LastMs:0.0} ms, worst {tick.WorstMs:0.0} ms in {(tick.WorstArea.Length > 0 ? tick.WorstArea : "no area named")}, {tick.Faults} area faults), " +
            $"wire {services.Control.WireConnections}, web {services.Control.HttpConnections}, decks {services.Control.Decks.Count}, app links {services.Control.AppLinkList.Count}, " +
            $"heard nodes {nodes.Count} ({nodes.Count(n => n.Fresh)} fresh), Companions {services.Kernel.Mdns.Companions.Count}");
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
            $"{builds - at.Builds} rebuilds in {ticks} ticks of {what} — {Moved(at.Graph, services.Eye.Graph)} · at the baseline {at.Desk}; after {DeskWords(services)} · at the baseline the sample counted {at.SampleFaults} contained errors " +
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
