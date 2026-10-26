using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Services;
using Patterns.Rendering.Media;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 92: the lifecycle census judges only what its cycles left (<see cref="DeskLifecycle"/>,
/// the judgement <see cref="LifecycleTests"/> runs). It stays as strict about a cycle's own leaks —
/// a desk, a picture or a running timer left by every cycle is caught in the first cycle, and so
/// is a timer made before the mark and started again by a cycle, which a count by birth would
/// miss, and a cycle's leak that another test's leaving hides in the whole process's count — and it
/// no longer reads another test's desk, timer or picture leaving on its own clock during the
/// cycles as a change, which the whole-process census did (L64's census half). Its mark is read
/// after the dispatcher's queue has run and a full collection, so a release still queued when the
/// cycles are asked for is no fall in the rows that stay the whole process's. The class's name
/// is not matched by the lifecycle job's or windows-smoke's `~LifecycleTests` filter, so neither
/// job's command changes.
/// </summary>
public class LifecycleCensusTests
{
    private const int Cycles = 3;

    [AvaloniaFact]
    public void ADeskEveryCycleLeavesIsStillCaught()
    {
        var kept = new List<AppServices>();
        try
        {
            DeskLifecycle.Warm();
            var change = DeskLifecycle.FirstChange(Cycles, inCycle: (_, b) => kept.Add(b.Services));   // each cycle's closed desk held past its close

            Assert.NotNull(change);
            Assert.Equal(1, change.Cycle);
            AssertCounts(change, desks: 1);
            Assert.Equal(new[] { "desks: was 0, now 1" }, change.Differences);
            Assert.StartsWith("cycle 1 of 3: desks: was 0, now 1", change.Words, StringComparison.Ordinal);
        }
        finally
        {
            kept.Clear();
        }
    }

    [AvaloniaFact]
    public void APictureEveryCycleLeavesIsStillCaught()
    {
        var drawn = new List<string>();
        try
        {
            DeskLifecycle.Warm();
            var change = DeskLifecycle.FirstChange(Cycles, inCycle: (i, b) =>
            {
                var path = WritePicture(Path.Combine(b.Dir, $"left by cycle {i}.png"));
                Assert.NotNull(ImageCache.Get(path));                                                 // decoded and held in the cache past the desk's close
                drawn.Add(path);
            });

            Assert.NotNull(change);
            Assert.Equal(1, change.Cycle);
            AssertCounts(change, pictures: 1);
            Assert.Equal(new[] { "pictures: was 0, now 1" }, change.Differences);
            Assert.Contains($"pictures held since the mark: [{drawn[0]}]", change.Words, StringComparison.Ordinal);
        }
        finally
        {
            LetGo(drawn);
        }
    }

    [AvaloniaFact]
    public void ATimerEveryCycleLeavesRunningIsStillCaught()
    {
        var running = new List<DispatcherTimer>();
        try
        {
            DeskLifecycle.Warm();
            var change = DeskLifecycle.FirstChange(Cycles, inCycle: (_, _) =>
            {
                var timer = DeskTimers.Make(TimeSpan.FromHours(1));
                timer.Start();
                running.Add(timer);
            });

            Assert.NotNull(change);
            Assert.Equal(1, change.Cycle);
            AssertCounts(change, timers: 1);
            Assert.Equal(new[] { "timers: was 0, now 1" }, change.Differences);
            Assert.Contains($"timers running since the mark: [{nameof(LifecycleCensusTests)}.{nameof(ATimerEveryCycleLeavesRunningIsStillCaught)} (3600000 ms)]", change.Words, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var timer in running) timer.Stop();
        }
    }

    /// <summary>
    /// A desk's timer is made once, as its owner is built, and started again each time it is
    /// armed: a timer there before the mark, stopped at it, and started by a cycle is running now
    /// and was not then, so it is the cycle's — a census that judged by birth would not see it.
    /// </summary>
    [AvaloniaFact]
    public void ATimerMadeBeforeTheMarkAndStartedAgainDuringTheCyclesIsStillCaught()
    {
        DeskLifecycle.Warm();
        var timer = DeskTimers.Make(TimeSpan.FromHours(1));
        timer.Start();                                                                                // armed once and stopped again before the mark, as a debounce between two arms
        timer.Stop();
        try
        {
            var change = DeskLifecycle.FirstChange(Cycles, inCycle: (i, _) =>
            {
                if (i == 1) timer.Start();
            });

            Assert.NotNull(change);
            Assert.Equal(1, change.Cycle);
            AssertCounts(change, timers: 1);
            Assert.Equal(new[] { "timers: was 0, now 1" }, change.Differences);
        }
        finally
        {
            timer.Stop();
        }
    }

    /// <summary>
    /// The mark and the baseline are read once the dispatcher's queue has run and a full
    /// collection has taken what it can, as every cycle's census is, whatever the caller did just
    /// before: a release still queued when the cycles are asked for (a closed owner's last
    /// continuation taking its row off a registry the census counts whole) lands before the mark,
    /// so the first cycle never reads its landing as a fall.
    /// </summary>
    [AvaloniaFact]
    public void AReleaseStillQueuedWhenTheCyclesAreAskedForLandsBeforeTheMark()
    {
        const string owner = "a closed owner's ledger row, its release still queued";
        DeskLifecycle.Warm();
        MemoryLedger.Register(owner, () => (0, "held until its release runs"));
        Dispatcher.UIThread.Post(() => MemoryLedger.Unregister(owner));
        try
        {
            var change = DeskLifecycle.FirstChange(1);

            Assert.True(change is null, change?.Words);
            Assert.DoesNotContain(MemoryLedger.Read(), line => line.Owner == owner);
        }
        finally
        {
            MemoryLedger.Unregister(owner);
        }
    }

    /// <summary>
    /// L64's census half, staged: an earlier test's closed desk whose shutdown's tail still holds
    /// it, a timer of its still running and a picture it drew still held are there at the mark,
    /// and all three leave during the first cycle — the desk reclaimed, the timer stopped, the
    /// picture swept as the residency sweep lets an idle one go. The whole process's census falls
    /// in each of the three rows, which the whole-process judgement read as a change; judged
    /// against the mark no cycle changed anything.
    /// </summary>
    [AvaloniaFact]
    public void AnEarlierTestsDeskTimerAndPictureLeavingDuringTheCyclesIsNoCycleChange()
    {
        DeskLifecycle.Warm();
        var leftover = Leftover.Stage();
        try
        {
            var before = DeskLifecycle.Take();                                                        // the whole process with the three in it: the baseline a whole-process judgement took
            var after = new List<LifetimeCensus>();
            var change = DeskLifecycle.FirstChange(Cycles,
                inCycle: (i, _) =>
                {
                    if (i == 1) leftover.Leave();
                },
                afterCycle: _ => after.Add(DeskCensus.Take()));

            Assert.True(change is null, change?.Words);
            Assert.False(leftover.Desk.TryGetTarget(out _), "the earlier test's desk was not reclaimed during the first cycle");
            Assert.False(leftover.Timer.IsEnabled);
            Assert.DoesNotContain(leftover.Picture, ImageCache.PathsNotIn(null));
            // The whole process fell in each row by one at least (in a whole run another test's
            // leftover may leave beside these): the change the whole-process judgement reported.
            foreach (var row in Judged)
            {
                Assert.True(after[0][row] <= before[row] - 1, $"{row}: was {before[row]} before the cycles, {after[0][row]} after the first");
            }
        }
        finally
        {
            leftover.Leave();                                                                         // a failure before the first cycle leaves nothing staged behind it
        }
    }

    /// <summary>
    /// A cycle that leaves a desk, a running timer and a picture while an earlier test's desk,
    /// timer and picture leave in the same cycle: each of the cycle's three takes the place of
    /// one that left, so the whole process's count does not rise in any of the three rows and a
    /// whole-process judgement never sees the cycle's leak as a rise; judged against the mark
    /// the cycle's three are caught. The whole-process count is held to "no rise" rather than
    /// "no change" because in a whole run another test's leftover may leave in the same cycle.
    /// </summary>
    [AvaloniaFact]
    public void ACyclesLeakThatAnEarlierTestsLeavingNetsToNoughtIsStillCaught()
    {
        DeskLifecycle.Warm();
        var leftover = Leftover.Stage();
        var kept = new List<AppServices>();
        var drawn = new List<string>();
        var timer = DeskTimers.Make(TimeSpan.FromHours(1));
        try
        {
            var before = DeskLifecycle.Take();
            var change = DeskLifecycle.FirstChange(Cycles, inCycle: (i, b) =>
            {
                if (i != 1) return;
                leftover.Leave();
                kept.Add(b.Services);
                timer.Start();
                var path = WritePicture(Path.Combine(b.Dir, "left by the first cycle.png"));
                Assert.NotNull(ImageCache.Get(path));
                drawn.Add(path);
            });
            var now = DeskLifecycle.Take();

            Assert.NotNull(change);
            Assert.Equal(1, change.Cycle);
            AssertCounts(change, desks: 1, timers: 1, pictures: 1);
            Assert.Equal(new[] { "desks: was 0, now 1", "timers: was 0, now 1", "pictures: was 0, now 1" }, change.Differences);
            Assert.False(leftover.Desk.TryGetTarget(out _), "the earlier test's desk was not reclaimed during the first cycle");
            foreach (var row in Judged)
            {
                Assert.True(now[row] <= before[row], $"{row}: was {before[row]} before the cycles, {now[row]} after the first — the whole process rose");
            }
        }
        finally
        {
            leftover.Leave();                                                                         // a failure before the first cycle leaves nothing staged behind it
            kept.Clear();
            timer.Stop();
            LetGo(drawn);
        }
    }

    /// <summary>An earlier test's leavings, held until <see cref="Leave"/> lets them go: a closed desk, a desk timer running, a picture in the cache.</summary>
    private sealed class Leftover
    {
        private readonly List<AppServices> _held = new();

        private Leftover(AppServices desk, DispatcherTimer timer, string picture)
        {
            _held.Add(desk);
            Desk = new WeakReference<AppServices>(desk);
            Timer = timer;
            Picture = picture;
        }

        public WeakReference<AppServices> Desk { get; }

        public DispatcherTimer Timer { get; }

        public string Picture { get; }

        /// <summary>Not inlined, so no local of the test's frame keeps the desk once it is let go.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Leftover Stage()
        {
            var b = TestApp.Boot();
            string picture;
            try
            {
                picture = WritePicture(Path.Combine(b.Dir, "an earlier test's picture.png"));
                Assert.NotNull(ImageCache.Get(picture));
            }
            finally
            {
                b.Dispose();
            }
            var timer = DeskTimers.Make(TimeSpan.FromHours(1));
            timer.Start();
            return new Leftover(b.Services, timer, picture);
        }

        /// <summary>All three leave: the desk's last holder lets it go, the timer stops, the picture is swept. A second call finds nothing left to let go and changes nothing.</summary>
        public void Leave()
        {
            _held.Clear();
            Timer.Stop();
            LetGo(new[] { Picture });
        }
    }

    /// <summary>The rows the census judges against its mark.</summary>
    private static readonly string[] Judged = { "desks", "timers", "pictures" };

    /// <summary>
    /// The change's fields: the baseline reads nought in the judged rows, the census at the change
    /// reads the given counts in them, and every other row reads what the baseline read.
    /// </summary>
    private static void AssertCounts(DeskLifecycle.Change change, long desks = 0, long timers = 0, long pictures = 0)
    {
        var expected = new Dictionary<string, long> { ["desks"] = desks, ["timers"] = timers, ["pictures"] = pictures };
        foreach (var (name, count) in change.Census.Rows)
        {
            if (expected.TryGetValue(name, out var want))
            {
                Assert.True(change.Baseline[name] == 0, $"{name}: the baseline read {change.Baseline[name]} against the mark");
                Assert.True(count == want, $"{name}: the census read {count}, {want} expected");
            }
            else
            {
                Assert.True(count == change.Baseline[name], $"{name}: was {change.Baseline[name]}, now {count}");
            }
        }
    }

    private static string WritePicture(string path)
    {
        using var bitmap = new SKBitmap(8, 8);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>Lets the named pictures go from the cache, and no other: the residency sweep's own step, read as if they had been idle for ever.</summary>
    private static void LetGo(IReadOnlyCollection<string> paths)
    {
        var mine = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        ImageCache.SweepIdle(0, keep: p => !mine.Contains(p), nowTicks: long.MaxValue / 2);
    }
}
