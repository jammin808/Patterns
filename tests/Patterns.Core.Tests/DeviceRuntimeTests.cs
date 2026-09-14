using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>What a box did lately, as the card and STATE read it: each event in turn, the ages, a failure as the last word and one answered since.</summary>
public class DeviceRuntimeTests
{
    private static readonly DateTime T0 = new(2026, 9, 14, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EachEventMakesTheNextRecordAndTheWordsReadThemWithTheirAges()
    {
        var none = DeviceRuntime.None;
        Assert.False(none.Any);
        Assert.False(none.Failing);
        Assert.Equal("No line sent yet.", none.Words(T0));

        var sent = none.Sent("POWER ON", T0);
        Assert.Same(DeviceRuntime.None, none);                                                        // immutable: the first is untouched
        Assert.Equal("POWER ON", sent.LastCommand);
        Assert.Equal("Last sent POWER ON (just now)", sent.Words(T0));
        Assert.Equal("Last sent POWER ON (3 s ago)", sent.Words(T0.AddSeconds(3)));

        var replied = sent.Replied("POWR: OK", T0.AddSeconds(1)).Confirmed(ConfirmLevel.Accepted, "POWR: OK", T0.AddSeconds(1));
        Assert.Equal(ConfirmLevel.Accepted, replied.LastConfirmed);
        Assert.Equal("", replied.LastObserved);                                                       // accepted is not observed
        Assert.Equal("Last sent POWER ON (4 s ago) · reply POWR: OK — accepted (3 s ago)", replied.Words(T0.AddSeconds(4)));
        Assert.False(replied.Failing);

        var observed = replied.Sent("INPUT HDMI 1", T0.AddSeconds(10)).Replied("INPT: 31", T0.AddSeconds(11)).Confirmed(ConfirmLevel.Observed, "INPT: 31", T0.AddSeconds(11));
        Assert.Equal("INPT: 31", observed.LastObserved);
        Assert.Equal(T0.AddSeconds(11), observed.LastObservedUtc);
        Assert.Equal("Last sent INPUT HDMI 1 (2 min ago) · reply INPT: 31 — observed (2 min ago) · observed INPT: 31 (2 min ago)", observed.Words(T0.AddMinutes(2).AddSeconds(11)));

        // A silence: the failure is the box's last word — FAILED — until it answers again, then failed.
        var failed = observed.Sent("INPUT HDMI 2", T0.AddSeconds(20)).Failed("INPUT HDMI 2 — no answer in 2 s (delivered, not accepted)", T0.AddSeconds(22));
        Assert.True(failed.Failing);
        Assert.Equal("Last sent INPUT HDMI 2 (5 s ago) · reply INPT: 31 — observed (14 s ago) · observed INPT: 31 (14 s ago) · FAILED INPUT HDMI 2 — no answer in 2 s (delivered, not accepted) (3 s ago)", failed.Words(T0.AddSeconds(25)));
        var answered = failed.Replied("POWR: OK", T0.AddSeconds(30)).Confirmed(ConfirmLevel.Accepted, "POWR: OK", T0.AddSeconds(30));
        Assert.False(answered.Failing);
        Assert.Contains("· failed INPUT HDMI 2", answered.Words(T0.AddSeconds(31)));
        Assert.Equal("INPUT HDMI 2 — no answer in 2 s (delivered, not accepted)", answered.LastFailure);     // kept, for the record

        // A port that would not open is a failure with nothing sent: the words start with it.
        var closed = DeviceRuntime.None.Failed("could not open: COM7 is in use", T0);
        Assert.True(closed.Any);
        Assert.True(closed.Failing);
        Assert.Equal("FAILED could not open: COM7 is in use (1 h ago)", closed.Words(T0.AddHours(1).AddMinutes(5)));
        // A datagram's receipt is a level with no reply: the level stands alone.
        var datagram = DeviceRuntime.None.Sent("/wall/main 1", T0).Confirmed(ConfirmLevel.Sent, "", T0);
        Assert.Equal("Last sent /wall/main 1 (2 d ago) · sent (2 d ago)", datagram.Words(T0.AddDays(2).AddHours(3)));
    }

    [Fact]
    public void TheAgesAreShortWords()
    {
        Assert.Equal("just now", DeviceRuntime.Age(TimeSpan.Zero));
        Assert.Equal("just now", DeviceRuntime.Age(TimeSpan.FromMilliseconds(900)));
        Assert.Equal("1 s ago", DeviceRuntime.Age(TimeSpan.FromSeconds(1.4)));
        Assert.Equal("59 s ago", DeviceRuntime.Age(TimeSpan.FromSeconds(59.9)));
        Assert.Equal("1 min ago", DeviceRuntime.Age(TimeSpan.FromSeconds(60)));
        Assert.Equal("59 min ago", DeviceRuntime.Age(TimeSpan.FromMinutes(59.9)));
        Assert.Equal("1 h ago", DeviceRuntime.Age(TimeSpan.FromHours(1)));
        Assert.Equal("23 h ago", DeviceRuntime.Age(TimeSpan.FromHours(23.5)));
        Assert.Equal("1 d ago", DeviceRuntime.Age(TimeSpan.FromDays(1)));
        Assert.Equal("just now", DeviceRuntime.Age(TimeSpan.FromSeconds(-5)));                       // a clock that stepped back is no age
    }
}
