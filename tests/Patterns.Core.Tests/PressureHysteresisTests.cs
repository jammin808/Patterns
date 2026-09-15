using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 64: the memory pressure ladder steps up the moment a reading passes a rung's entry and
/// steps down only once the share has sat below the rung's leaving line for the dwell — a
/// reading on a line no longer flaps the steps (pre-roll held, decks narrowed) on and off with
/// every poll. Never late upward.
/// </summary>
public class PressureHysteresisTests
{
    private const long Budget = 1000;

    private static long Bytes(double share) => (long)(share * Budget);

    [Fact]
    public void TheLadderStepsUpAtOnceAndDownOnlyAfterTheDwellBelowTheLeavingLine()
    {
        var since = -1.0;
        var level = MemoryPressure.None;
        level = MediaMemory.Step(level, Bytes(0.69), Budget, 0, ref since);
        Assert.Equal(MemoryPressure.None, level);
        level = MediaMemory.Step(level, Bytes(0.70), Budget, 1, ref since);                            // entry at 70 %: up at once
        Assert.Equal(MemoryPressure.Elevated, level);
        level = MediaMemory.Step(level, Bytes(0.67), Budget, 2, ref since);                            // 67 %: under the entry, over the leaving line — stays
        Assert.Equal(MemoryPressure.Elevated, level);
        level = MediaMemory.Step(level, Bytes(0.64), Budget, 3, ref since);                            // 64 %: below the leaving line, the dwell begins
        Assert.Equal(MemoryPressure.Elevated, level);
        level = MediaMemory.Step(level, Bytes(0.64), Budget, 7, ref since);                            // four seconds in: not yet
        Assert.Equal(MemoryPressure.Elevated, level);
        level = MediaMemory.Step(level, Bytes(0.66), Budget, 7.5, ref since);                          // back over the line: the dwell resets
        Assert.Equal(MemoryPressure.Elevated, level);
        level = MediaMemory.Step(level, Bytes(0.64), Budget, 8, ref since);
        level = MediaMemory.Step(level, Bytes(0.64), Budget, 13.5, ref since);                         // five seconds below: down one rung
        Assert.Equal(MemoryPressure.None, level);
        Assert.Equal(-1, since);
    }

    [Fact]
    public void CriticalComesAtOnceFromAnyRungAndLeavesOneRungAtATime()
    {
        var since = -1.0;
        var level = MediaMemory.Step(MemoryPressure.None, Bytes(1.05), Budget, 0, ref since);
        Assert.Equal(MemoryPressure.Critical, level);                                                  // straight to the top: never late upward
        level = MediaMemory.Step(level, Bytes(0.95), Budget, 1, ref since);                            // 95 %: over critical's leaving line (92) — stays
        Assert.Equal(MemoryPressure.Critical, level);
        level = MediaMemory.Step(level, Bytes(0.50), Budget, 2, ref since);                            // 50 %: far below, the dwell begins
        Assert.Equal(MemoryPressure.Critical, level);
        level = MediaMemory.Step(level, Bytes(0.50), Budget, 7.5, ref since);                          // one rung at a time
        Assert.Equal(MemoryPressure.High, level);
        level = MediaMemory.Step(level, Bytes(0.50), Budget, 8, ref since);
        level = MediaMemory.Step(level, Bytes(0.50), Budget, 13.5, ref since);
        Assert.Equal(MemoryPressure.Elevated, level);
        level = MediaMemory.Step(level, Bytes(0.50), Budget, 14, ref since);
        level = MediaMemory.Step(level, Bytes(0.50), Budget, 19.5, ref since);
        Assert.Equal(MemoryPressure.None, level);

        // No budget known: nothing to judge, and no rung to leave.
        since = -1;
        Assert.Equal(MemoryPressure.None, MediaMemory.Step(MemoryPressure.None, 500, 0, 0, ref since));
    }
}
