using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The one gate every GO passes, in order; the sidecar's run place round-trips.</summary>
public class GoGateTests
{
    private static readonly DateTime T0 = new(2026, 9, 3, 19, 0, 0, DateTimeKind.Utc);

    private static GoGate.Inputs Ready() => new(
        Armed: true, Held: false, Blackout: false, Executing: false,
        StandbyCueId: "cue-b", SeenStandbyCueId: "cue-b", LastGoUtc: null, NowUtc: T0,
        RequireConfirm: false, ConfirmPendingCueId: null, ConfirmDeadlineUtc: null);

    [Fact]
    public void TheGateRefusesInOrderAndFiresWhenEverythingHolds()
    {
        Assert.Equal((GoDecision.Fire, ""), GoGate.Check(Ready()));
        Assert.Equal((GoDecision.Refuse, "not armed"), GoGate.Check(Ready() with { Armed = false, Held = true, Blackout = true }));
        Assert.Equal((GoDecision.Refuse, "held"), GoGate.Check(Ready() with { Held = true, Blackout = true }));
        Assert.Equal((GoDecision.Refuse, "blackout is on — lift it first"), GoGate.Check(Ready() with { Blackout = true, Executing = true }));
        Assert.Equal((GoDecision.Refuse, "a cue is still executing"), GoGate.Check(Ready() with { Executing = true, StandbyCueId = null }));
        Assert.Equal((GoDecision.Refuse, "no cue on standby"), GoGate.Check(Ready() with { StandbyCueId = null, SeenStandbyCueId = null }));
        Assert.Equal((GoDecision.Refuse, "standby moved"), GoGate.Check(Ready() with { SeenStandbyCueId = "cue-a" }));
        Assert.Equal(GoDecision.Fire, GoGate.Check(Ready() with { SeenStandbyCueId = null }).Decision); // a desk press skips the fence
        Assert.Equal((GoDecision.Refuse, "too soon after the last GO"), GoGate.Check(Ready() with { LastGoUtc = T0.AddMilliseconds(-100) }));
        Assert.Equal(GoDecision.Fire, GoGate.Check(Ready() with { LastGoUtc = T0.AddMilliseconds(-400) }).Decision);
    }

    [Fact]
    public void ConfirmIsAWindowNotADialog()
    {
        var wants = Ready() with { RequireConfirm = true };
        Assert.Equal(GoDecision.Confirm, GoGate.Check(wants).Decision);
        var armed = wants with { ConfirmPendingCueId = "cue-b", ConfirmDeadlineUtc = T0.AddSeconds(4) };
        Assert.Equal(GoDecision.Fire, GoGate.Check(armed with { NowUtc = T0.AddSeconds(3) }).Decision);
        Assert.Equal(GoDecision.Confirm, GoGate.Check(armed with { NowUtc = T0.AddSeconds(5) }).Decision);   // expired
        Assert.Equal(GoDecision.Confirm, GoGate.Check(armed with { ConfirmPendingCueId = "cue-a" }).Decision); // for another cue
    }

    [Fact]
    public void TheSidecarCarriesTheCallersPlace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-place-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new RecoveryStore(dir);
        var history = new List<CueExecutionRecord>
        {
            new(T0, "cue-a", "03.020", "Five-minute call", CueOutcome.Done, "desk", 2, 2, "ok"),
        };
        store.Write(true, false, null, new RunPlace("cue-b", "cue-a", T0, history));

        var back = store.Read();
        Assert.NotNull(back?.Run);
        Assert.Equal("cue-b", back!.Run!.StandbyCueId);
        Assert.Equal("cue-a", back.Run.LastCueId);
        Assert.Equal(T0, back.Run.LastGoUtc);
        var row = Assert.Single(back.Run.History);
        Assert.Equal("03.020 Five-minute call", row.Label);
        Assert.Equal(CueOutcome.Done, row.Outcome);

        store.Write(true, false); // a sidecar without a place still reads
        Assert.Null(store.Read()!.Run);
    }
}
