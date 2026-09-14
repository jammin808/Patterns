using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The clocks compared over the beats: NTP's arithmetic, the shortest round trip believed, the room clock that follows, the words.</summary>
public class LinkClockTests
{
    private const long Ms = TimeSpan.TicksPerMillisecond;

    [Fact]
    public void OneExchangeGivesTheOffsetAndTheRoundTripAsNtpDoes()
    {
        // The peer's clock is 30 s ahead; the path takes 5 ms each way; the peer held the beat 400 ms before answering.
        var t1 = 1000L * Ms;
        var t2 = t1 + 5 * Ms + 30_000 * Ms;
        var t3 = t2 + 400 * Ms;
        var t4 = t1 + 5 * Ms + 400 * Ms + 5 * Ms;
        var clock = new LinkClock();
        Assert.False(clock.Known);
        clock.Sample(t1, t2, t3, t4);
        Assert.True(clock.Known);
        Assert.Equal(30.0, clock.Offset.TotalSeconds, 3);
        Assert.Equal(10.0, clock.Delay.TotalMilliseconds, 3);
        Assert.Equal(1, clock.Samples);
        clock.Sample(0, t2, t3, t4);                                              // a stamp missing is no exchange
        Assert.Equal(1, clock.Samples);
    }

    [Fact]
    public void TheExchangeWithTheShortestRoundTripIsBelievedAndOldOnesSlideOut()
    {
        var clock = new LinkClock();
        clock.Sample(0 + 1000 * Ms, 1001 * Ms, 1101 * Ms, 1102 * Ms);           // clean: offset 0, round trip 2 ms
        Assert.Equal(0, clock.Offset.Ticks);
        clock.Sample(2000 * Ms, 2301 * Ms, 2401 * Ms, 2402 * Ms);               // queued 300 ms on the way out: an offset it never had, and a round trip that says so
        Assert.Equal(0, clock.Offset.Ticks);
        Assert.Equal(2.0, clock.Delay.TotalMilliseconds, 3);
        // Eight clean exchanges with a real 50 ms offset push the first ones out of the window.
        for (var i = 0; i < LinkClock.Kept; i++)
        {
            var b = (5000 + i * 1000) * Ms;
            clock.Sample(b, b + 51 * Ms, b + 151 * Ms, b + 102 * Ms);
        }
        Assert.Equal(LinkClock.Kept, clock.Samples);
        Assert.Equal(50.0, clock.Offset.TotalMilliseconds, 3);
        Assert.Equal(2.0, clock.Delay.TotalMilliseconds, 3);
    }

    [Fact]
    public void TheWordsSayWhoseClockIsAheadAndWhenTheyAreApart()
    {
        Assert.Equal("", LinkClock.Note(null, "its"));
        Assert.Equal("", LinkClock.Note(TimeSpan.FromMilliseconds(300), "its"));
        Assert.Equal("its clock 0.8 s ahead", LinkClock.Note(TimeSpan.FromMilliseconds(800), "its"));
        Assert.Equal("the desk's clock 1.5 s behind", LinkClock.Note(TimeSpan.FromMilliseconds(-1500), "the desk's"));
        Assert.False(LinkClock.Apart(TimeSpan.FromSeconds(1.9)));
        Assert.True(LinkClock.Apart(TimeSpan.FromSeconds(-2)));
        Assert.Equal("CLOCKS 3.2 s APART — Backup's clock is ahead; set both machines to one time server", LinkClock.ApartWords("Backup", TimeSpan.FromSeconds(3.2)));
        Assert.Equal("CLOCKS 2.0 s APART — timer STAGE-PC's clock is behind; set both machines to one time server", LinkClock.ApartWords("timer STAGE-PC", TimeSpan.FromSeconds(-2)));
    }

    [Fact]
    public void TheBeatCarriesItsStampsAndAnOldBeatStillReads()
    {
        var beat = new TwinBeat(12, 1000, 900, 950);
        Assert.Equal("12 1000 900 950", beat.Format());
        Assert.Equal(beat, TwinBeat.Parse("12 1000 900 950"));
        Assert.True(beat.HasStamps);
        Assert.True(beat.HasEcho);
        var first = new TwinBeat(1, 1000, 0, 0);
        Assert.Equal("1 1000", first.Format());
        Assert.True(TwinBeat.Parse("1 1000").HasStamps);
        Assert.False(TwinBeat.Parse("1 1000").HasEcho);
        var old = TwinBeat.Parse("7");
        Assert.Equal(7, old.Seq);
        Assert.False(old.HasStamps);
        Assert.Equal("7", old.Format());
        Assert.Equal(0, TwinBeat.Parse("x y").Seq);
        Assert.Equal(0, TwinBeat.Parse("").SentTicks);
        Assert.Equal(new TwinBeat(3, 0, 0, 0), TwinBeat.Parse("3 -5 -1 -1"));  // a negative stamp is no stamp
    }

    [Fact]
    public void TheRoomClockFollowsPastTheDeadbandAndReadsTheMachineMovedByTheOffset()
    {
        var machine = new DateTime(2026, 9, 14, 19, 0, 0, DateTimeKind.Utc);
        var room = new RoomClock { Machine = () => machine };
        Assert.Equal(machine, room.UtcNow);
        Assert.Equal("", room.Words);
        Assert.False(room.Follow(TimeSpan.FromMilliseconds(30)));               // within the deadband: nothing moves
        Assert.Equal(TimeSpan.Zero, room.Offset);
        Assert.True(room.Follow(TimeSpan.FromSeconds(30)));
        Assert.Equal(machine.AddSeconds(30), room.UtcNow);
        Assert.Equal(machine.AddSeconds(30).ToLocalTime(), room.Now);
        Assert.False(room.Follow(TimeSpan.FromSeconds(30.02)));
        Assert.True(room.Follow(TimeSpan.FromSeconds(29.9)));
        Assert.Equal("the show clock runs 29.9 s ahead of this machine's, on the desk's", room.Words);
        Assert.True(room.Follow(TimeSpan.FromSeconds(-1.25)));
        Assert.Equal("the show clock runs 1.3 s behind this machine's, on the desk's", room.Words);
        room.Reset();
        Assert.Equal(machine, room.UtcNow);
        Assert.Equal("", room.Words);
    }

    [Fact]
    public void TheMainsLineCarriesEachPeersClockAndTheWarningAndTheFollowersCarryTheirs()
    {
        var now = new DateTime(2026, 9, 14, 19, 0, 0, DateTimeKind.Utc);
        Assert.Equal("MAIN — standby Backup in step (heard just now, its clock 0.8 s behind) · 3 sections sent.",
            TwinWatch.DescribeMain(9699, new[] { ("Backup", now) }, 3, now, clocks: new[] { ("Backup", TimeSpan.FromMilliseconds(-800)) }));
        Assert.Equal("MAIN — standby Backup in step (heard just now) · standby timer STAGE-PC in step (heard just now, its clock 3.2 s ahead) · 3 sections sent. CLOCKS 3.2 s APART — timer STAGE-PC's clock is ahead; set both machines to one time server",
            TwinWatch.DescribeMain(9699, new[] { ("Backup", now), ("timer STAGE-PC", now) }, 3, now, clocks: new[] { ("Backup", TimeSpan.FromMilliseconds(100)), ("timer STAGE-PC", TimeSpan.FromSeconds(3.2)) }));
        Assert.Equal("MAIN — standby Backup in step (heard just now) · 3 sections sent.",
            TwinWatch.DescribeMain(9699, new[] { ("Backup", now) }, 3, now));                                  // before an exchange closed: no word
        Assert.EndsWith("· the desk's clock 0.8 s ahead", TwinWatch.DescribeCaller(TwinPhase.InStep, "FOH", now, 2, now, timer: true, clock: "the desk's clock 0.8 s ahead"));
        Assert.EndsWith("· TAKE OVER is yours. · the main's clock 0.8 s ahead", TwinWatch.DescribeStandby(TwinPhase.InStep, "FOH", now, 2, false, now, clock: "the main's clock 0.8 s ahead"));
        Assert.DoesNotContain("clock", TwinWatch.DescribeCaller(TwinPhase.InStep, "FOH", now, 2, now, timer: false));
    }
}
