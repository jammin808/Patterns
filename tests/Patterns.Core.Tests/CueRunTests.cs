using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>A cue's run named, and the history row's words for what it still waits for.</summary>
public class CueRunTests
{
    [Fact]
    public void AnExecutionIdIsShortUniqueAndNeverACuesOwn()
    {
        var a = CueExecution.NewId();
        var b = CueExecution.NewId();
        Assert.Equal(8, a.Length);
        Assert.NotEqual(a, b);
        Assert.Matches("^[0-9a-f]{8}$", a);
        var run = new CueExecution(a, 2, false);
        Assert.Equal(2, run.DevicePending);
        Assert.False(run.Settling);
        Assert.Null(ActionResult.Done("x").Execution);
        Assert.Same(run, (ActionResult.Requested("y") with { Execution = run }).Execution);
    }

    [Fact]
    public void TheRowsWordsSayWhatItWaitsFor()
    {
        var row = new CueExecutionRecord(DateTime.UtcNow, "c1", "01.010", "Doors", CueOutcome.Requested, "desk", 2, 2, "", "a1b2c3d4", 2);
        Assert.Equal("Awaiting 2 receipts", row.OutcomeWords);
        Assert.Equal("Awaiting 1 receipt", (row with { Pending = 1 }).OutcomeWords);
        Assert.Equal("Settling", (row with { Pending = 0 }).OutcomeWords);
        Assert.Equal("Done", (row with { Outcome = CueOutcome.Done, Pending = 0 }).OutcomeWords);
        Assert.Equal("Failed late", (row with { Outcome = CueOutcome.FailedLate }).OutcomeWords);
        Assert.Equal("Refused", (row with { Outcome = CueOutcome.Refused }).OutcomeWords);
        Assert.True((row with { Outcome = CueOutcome.FailedLate }).IsFailure);
        Assert.False(row.IsFailure);
        // An older sidecar's row, with no execution: the defaults.
        var old = new CueExecutionRecord(DateTime.UtcNow, "c1", "01.010", "Doors", CueOutcome.Done, "desk", 1, 1, "");
        Assert.Equal("", old.ExecutionId);
        Assert.Equal(0, old.Pending);
        Assert.False(old.Settling);
        var json = JsonUtil.SerializeCompact(row);
        var back = JsonUtil.Deserialize<CueExecutionRecord>(json)!;
        Assert.Equal("a1b2c3d4", back.ExecutionId);
        Assert.Equal(2, back.Pending);
    }
}
