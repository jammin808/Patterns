using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 27: the one part of a MIDI surface that had no precedent in this desk. The Interactive
/// area was built for Arduinos — a few lines a second, one closure posted to the UI thread each,
/// and a journal row written to disk synchronously on that thread. A fader sweep is hundreds of
/// messages a second and sixteen encoders can move at once, so copying that path literally would
/// stall the desk for the length of a gesture, which is the opposite of what a surface is for.
/// </summary>
public class MidiCoalescerTests
{
    private static MidiMessage Pad(int note, int velocity = 127) => MidiLines.Read(0x90, note, velocity);
    private static MidiMessage Fader(int cc, int value) => MidiLines.Read(0xB0, cc, value);

    [Fact]
    public void APressGoesThroughAtOnceBecauseThatIsWhatAGoButtonIsFor()
    {
        var c = new MidiCoalescer();
        Assert.Equal("NOTE 1 53 127", c.Offer(Pad(53)));
        Assert.Equal("NOTEOFF 1 53", c.Offer(Pad(53, 0)));                // the release, as its own word
        Assert.Equal("PROGRAM 1 4", c.Offer(MidiLines.Read(0xC0, 4, 0))); // and a bank button

        // None of that waited for a sample, so nothing is held.
        var lines = new List<string>();
        c.Drain(lines);
        Assert.Empty(lines);
        Assert.Equal(0, c.Coalesced);
    }

    [Fact]
    public void AFaderSweepCostsTheDeskWhereItEndedUpAndNotWhereItWent()
    {
        var c = new MidiCoalescer();

        // A hand moving one fader through its whole travel: what a surface actually sends.
        for (var v = 0; v <= 127; v++) Assert.Null(c.Offer(Fader(7, v)));

        var lines = new List<string>();
        c.Drain(lines);
        Assert.Single(lines);
        // 128 messages, one action — and read as the percentage the desk's level verbs take, because
        // a raw 127 would be refused and the top of the fader's travel would silently do nothing.
        Assert.Equal("CC 1 7 100", lines[0]);
        Assert.Equal(127, c.Coalesced);

        // And nothing at all while nobody is touching it, which is most of a show.
        lines.Clear();
        c.Drain(lines);
        Assert.Empty(lines);
    }

    [Fact]
    public void EveryControlThatMovedIsReadAndOnlyTheOnesThatMoved()
    {
        var c = new MidiCoalescer();
        c.Offer(Fader(7, 10));
        c.Offer(Fader(8, 20));
        c.Offer(MidiLines.Read(0xE0, 0, 0x40));                           // a wheel, on its own slot
        c.Offer(MidiLines.Read(0xB1, 7, 30));                             // controller 7 on channel 2 is a different control

        var lines = new List<string>();
        c.Drain(lines);
        Assert.Equal(4, lines.Count);
        Assert.Contains("CC 1 7 8", lines);                               // raw 10 of 127 is 8 %
        Assert.Contains("CC 1 8 16", lines);
        Assert.Contains("CC 2 7 24", lines);
        Assert.Contains(lines, l => l.StartsWith("BEND 1 "));

        // A surface that reports its faders continuously — several do — costs nothing while they
        // are all standing still.
        for (var i = 0; i < 50; i++)
        {
            c.Offer(Fader(7, 10));
            c.Offer(Fader(8, 20));
        }
        lines.Clear();
        c.Drain(lines);
        Assert.Empty(lines);

        // Move one of them and only that one is read.
        c.Offer(Fader(8, 22));                                            // far enough to be a different percent
        c.Drain(lines);
        Assert.Single(lines);
        Assert.Equal("CC 1 8 17", lines[0]);
    }

    [Fact]
    public void AFaderThatCameBackToWhereItStartedHasNotMoved()
    {
        var c = new MidiCoalescer();
        c.Offer(Fader(7, 64));
        var lines = new List<string>();
        c.Drain(lines);
        Assert.Single(lines);

        // Knocked and put back between two ticks: as far as the show is concerned it never moved,
        // so the desk does not run an action that changes nothing.
        lines.Clear();
        c.Offer(Fader(7, 90));
        c.Offer(Fader(7, 64));
        c.Drain(lines);
        Assert.Empty(lines);
    }

    [Fact]
    public void UnpluggingAndBackForgetsWhereEveryControlWas()
    {
        var c = new MidiCoalescer();
        c.Offer(Fader(7, 64));
        var lines = new List<string>();
        c.Drain(lines);
        lines.Clear();

        // The operator moves the fader while the surface is unplugged and puts it back. A desk that
        // remembered the old reading would ignore the first move home and sit at the wrong level.
        c.Forget();
        c.Offer(Fader(7, 64));
        c.Drain(lines);
        Assert.Single(lines);
        Assert.Equal("CC 1 7 50", lines[0]);
    }

    [Fact]
    public void TheSampleRateIsFinerThanAHandAndCoarserThanAFlood()
    {
        // Fifty times a second: finer than a hand moves a fader, far finer than an eye follows, and
        // it turns a sweep from several hundred actions into about a dozen.
        Assert.Equal(20, MidiCoalescer.SampleMs);
        Assert.Equal(50, 1000 / MidiCoalescer.SampleMs);
    }
}
