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
        Assert.False(LinkClock.Apart(TimeSpan.FromSeconds(1.9), TimeSpan.Zero));
        Assert.False(LinkClock.Apart(TimeSpan.FromSeconds(-2), TimeSpan.Zero));                 // 13.11.5: past two seconds, not at them
        Assert.True(LinkClock.Apart(TimeSpan.FromMilliseconds(-2001), TimeSpan.Zero));
        Assert.Equal("CLOCKS 3.2 s APART — Backup's clock is ahead; set both machines to one time server", LinkClock.ApartWords("Backup", TimeSpan.FromSeconds(3.2)));
        Assert.Equal("CLOCKS 2.0 s APART — timer STAGE-PC's clock is behind; set both machines to one time server", LinkClock.ApartWords("timer STAGE-PC", TimeSpan.FromSeconds(-2)));
    }

    /// <summary>
    /// Show Bus 1.3 §13.11.2: an exchange is a sample only when every stamp is above 0, t4 is not before t1, t3 not before t2
    /// and its round trip lies within 0 to <c>clock.rtt.max.ms</c> — what a clock that checks no more than its stamps takes (a
    /// reply before its ask, a peer's clock stepping between its two stamps, a negative round trip clamped to 0) is refused and
    /// counted.
    /// </summary>
    [Fact]
    public void AnExchangeIsASampleOnlyWhenSection13112AdmitsIt()
    {
        var clock = new LinkClock();
        Assert.False(clock.Sample(1000 * Ms, 1505 * Ms, 1504 * Ms, 1012 * Ms));                  // t3 before t2
        Assert.False(clock.Sample(1000 * Ms, 1505 * Ms, 1506 * Ms, 999 * Ms));                   // t4 before t1
        Assert.False(clock.Sample(1000 * Ms, 1505 * Ms, 1506 * Ms, 62000 * Ms));                 // a round trip past clock.rtt.max.ms
        Assert.False(clock.Sample(0, 1505 * Ms, 1506 * Ms, 1012 * Ms));                          // a stamp lost
        Assert.False(clock.Sample(1000 * Ms, 1000 * Ms + 31_000 * Ms, 1001 * Ms, 1002 * Ms));    // the peer stepped back 30 s between its stamps
        Assert.False(clock.Known);
        Assert.Equal((0, 5), (clock.Samples, clock.Refused));
        Assert.False(clock.Sample(1000 * Ms, 2000 * Ms, 2000 * Ms, 61_000 * Ms + 1));           // one tick past clock.rtt.max.ms
        Assert.True(clock.Sample(1000 * Ms, 2000 * Ms, 2000 * Ms, 61_000 * Ms));                // a round trip of the limit is within it
        Assert.Equal(TimeSpan.FromSeconds(60), clock.Delay);
        Assert.True(clock.Sample(1000 * Ms, 1505 * Ms, 1506 * Ms, 1012 * Ms));
        Assert.Equal((2, 6), (clock.Samples, clock.Refused));
    }

    /// <summary>
    /// 13.11.3–5: the believed sample's error is ⌊(rtt + 1) / 2⌋ + 1 ms, its age runs from its t4, it is fresh within
    /// <c>clock.fresh.ms</c> and fit for a tolerance while fresh with its error within it; CLOCKS APART takes the error off the
    /// offset first; and the window empties when the clock measures another process of the peer (13.11.7).
    /// </summary>
    [Fact]
    public void TheBelievedSampleSaysItsErrorAgeAndQualityAndApartTakesTheErrorOff()
    {
        var clock = new LinkClock();
        Assert.Null(clock.Age(5000 * Ms));
        Assert.False(clock.IsFresh(5000 * Ms));
        Assert.Equal(TimeSpan.Zero, clock.Error);
        clock.Sample(1000 * Ms, 1505 * Ms, 1506 * Ms, 1012 * Ms);                               // offset 499.5 ms, round trip 11
        Assert.Equal(TimeSpan.FromMilliseconds(7), clock.Error);
        Assert.Equal(TimeSpan.FromMilliseconds(988), clock.Age(2000 * Ms));
        Assert.True(clock.IsFresh(11_012 * Ms));
        Assert.False(clock.IsFresh(11_013 * Ms));
        Assert.True(clock.IsFitFor(TimeSpan.FromMilliseconds(7), 2000 * Ms));
        Assert.False(clock.IsFitFor(TimeSpan.FromMilliseconds(6), 2000 * Ms));
        Assert.False(clock.IsApart);
        // A round trip of part of a millisecond counts as the whole millisecond: an error is never said smaller than it is.
        Assert.Equal(1, LinkClock.WholeMs(1));
        Assert.Equal(2, LinkClock.ErrMs(LinkClock.WholeMs(Ms / 2)));

        // 13.11.5 both ways on the contract's own sample: 2010 ms with an error of 16 is not apart, 2020 is.
        Assert.False(LinkClock.Apart(TimeSpan.FromMilliseconds(2010), TimeSpan.FromMilliseconds(16)));
        Assert.True(LinkClock.Apart(TimeSpan.FromMilliseconds(2020), TimeSpan.FromMilliseconds(16)));
        Assert.True(LinkClock.Apart(TimeSpan.FromMilliseconds(-2017), TimeSpan.FromMilliseconds(16)));

        // 13.11.7: the first peer named keeps the window; another — a restarted process, another node — empties it.
        Assert.False(clock.Measuring("node:3f9c2a1b7d0e a41f09c2"));
        Assert.Equal(1, clock.Samples);
        Assert.False(clock.Measuring("node:3f9c2a1b7d0e a41f09c2"));
        Assert.True(clock.Measuring("node:3f9c2a1b7d0e b7c3d9e1"));
        Assert.Equal(0, clock.Samples);
        Assert.False(clock.Known);
        clock.Sample(1000 * Ms, 1505 * Ms, 1506 * Ms, 1012 * Ms);
        clock.Clear();
        Assert.False(clock.Known);
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
            TwinWatch.DescribeMain(9699, new[] { ("Backup", now) }, 3, now, clocks: new[] { ("Backup", TimeSpan.FromMilliseconds(-800), TimeSpan.FromMilliseconds(3)) }));
        Assert.Equal("MAIN — standby Backup in step (heard just now) · standby timer STAGE-PC in step (heard just now, its clock 3.2 s ahead) · 3 sections sent. CLOCKS 3.2 s APART — timer STAGE-PC's clock is ahead; set both machines to one time server",
            TwinWatch.DescribeMain(9699, new[] { ("Backup", now), ("timer STAGE-PC", now) }, 3, now, clocks: new[] { ("Backup", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(3)), ("timer STAGE-PC", TimeSpan.FromSeconds(3.2), TimeSpan.FromMilliseconds(3)) }));
        // Show Bus 1.3 §13.11.5: 2.01 s with a sample's error of 16 ms is within two seconds — no warning — and 2.02 s is apart.
        Assert.DoesNotContain("APART", TwinWatch.DescribeMain(9699, new[] { ("Backup", now) }, 3, now, clocks: new[] { ("Backup", TimeSpan.FromMilliseconds(2010), TimeSpan.FromMilliseconds(16)) }));
        Assert.Contains("CLOCKS 2.0 s APART — Backup's clock is ahead", TwinWatch.DescribeMain(9699, new[] { ("Backup", now) }, 3, now, clocks: new[] { ("Backup", TimeSpan.FromMilliseconds(2020), TimeSpan.FromMilliseconds(16)) }));
        Assert.Equal("MAIN — standby Backup in step (heard just now) · 3 sections sent.",
            TwinWatch.DescribeMain(9699, new[] { ("Backup", now) }, 3, now));                                  // before an exchange closed: no word
        Assert.EndsWith("· the desk's clock 0.8 s ahead", TwinWatch.DescribeCaller(TwinPhase.InStep, "FOH", now, 2, now, timer: true, clock: "the desk's clock 0.8 s ahead"));
        Assert.EndsWith("· TAKE OVER is yours. · the main's clock 0.8 s ahead", TwinWatch.DescribeStandby(TwinPhase.InStep, "FOH", now, 2, false, now, clock: "the main's clock 0.8 s ahead"));
        Assert.DoesNotContain("clock", TwinWatch.DescribeCaller(TwinPhase.InStep, "FOH", now, 2, now, timer: false));
    }
}
