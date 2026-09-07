using System.Diagnostics;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 18: the start-up budget with Main's own phases — the runtime before Main, the one
/// settings read, the graphics choices — folded in before the desk's, in order, and the new
/// phase names in the line the Machine page reads.
/// </summary>
public class StartupBudgetTests
{
    [Fact]
    public void MainsMarksComeFirstInOrderAndTheLineNamesEveryPhase()
    {
        StartupBudget.ResetEarly();
        try
        {
            StartupBudget.MarkProcessStart();
            var origin = StartupBudget.ProcessStartedAt;
            Assert.NotEqual(0, origin);
            Assert.True(StartupBudget.RuntimeMsBeforeMain >= 0);
            StartupBudget.MarkEarly(StartupBudget.Settings);
            StartupBudget.MarkEarly(StartupBudget.Graphics);

            var b = new StartupBudget();
            b.Begin(origin);
            Assert.True(b.Mark(StartupBudget.Avalonia));
            Assert.False(b.Mark(StartupBudget.Settings));          // Main's mark stands
            Assert.True(b.Mark(StartupBudget.Services));
            Assert.True(b.Mark(StartupBudget.ViewModel));
            Assert.True(b.Mark(StartupBudget.Pages));
            Assert.True(b.Mark(StartupBudget.Window));
            Assert.True(b.Mark(StartupBudget.FirstFrame));

            var phases = b.Phases.Select(p => p.Phase).ToList();
            var expected = new List<string>();
            if (StartupBudget.RuntimeMsBeforeMain > 0) expected.Add(StartupBudget.Runtime);
            expected.AddRange(new[] { StartupBudget.Settings, StartupBudget.Graphics, StartupBudget.Avalonia, StartupBudget.Services, StartupBudget.ViewModel, StartupBudget.Pages, StartupBudget.Window, StartupBudget.FirstFrame });
            Assert.Equal(expected, phases);
            Assert.All(b.Phases, p => Assert.True(p.Ms >= 0, $"{p.Phase} {p.Ms}"));
            Assert.True(b.Complete);
            Assert.True(b.TotalMs >= StartupBudget.RuntimeMsBeforeMain);
            var line = b.Describe();
            Assert.StartsWith("Start-up ", line);
            Assert.Contains("graphics", line);
            Assert.Contains("view model", line);
            Assert.Contains("pages", line);
            Assert.DoesNotContain("still starting", line);

            // Without Main there are no early phases: the desk's own marks only, from the first one.
            var alone = new StartupBudget();
            Assert.True(alone.Mark(StartupBudget.Settings));
            Assert.Equal(new[] { StartupBudget.Settings }, alone.Phases.Select(p => p.Phase));
            Assert.Contains("still starting", alone.Describe());
        }
        finally
        {
            StartupBudget.ResetEarly();
        }
    }

    [Fact]
    public void ABeginWithoutMainIgnoresMarksMadeForAnotherStart()
    {
        StartupBudget.ResetEarly();
        try
        {
            StartupBudget.MarkEarly(StartupBudget.Settings);     // a stray early mark with no process start
            var b = new StartupBudget();
            b.Begin();                                            // no origin: the marks before it are not this budget's
            Assert.True(b.Mark(StartupBudget.Services));
            Assert.Equal(new[] { StartupBudget.Services }, b.Phases.Select(p => p.Phase));
            // An early mark older than the origin it is folded into is left out rather than counted negative.
            StartupBudget.ResetEarly();
            StartupBudget.MarkEarly(StartupBudget.Graphics);
            var later = Stopwatch.GetTimestamp();
            var c = new StartupBudget();
            c.Begin(later);
            Assert.True(c.Mark(StartupBudget.Services));
            Assert.Equal(new[] { StartupBudget.Services }, c.Phases.Select(p => p.Phase));
        }
        finally
        {
            StartupBudget.ResetEarly();
        }
    }
}
