using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 27: the two directions of one table, and the starter rows for the surfaces most likely to
/// be in the flight case. The desk ships no driver per controller on purpose — a note map changes
/// with firmware and nobody can state one with confidence without the hardware — so what it ships
/// is rows the operator can read, edit and re-learn by pressing the pad.
/// </summary>
public class MidiSurfaceTests
{
    private static DeviceConfig Surface()
    {
        var d = new DeviceConfig { Link = DeviceLink.Midi, SpeaksProtocol = false };
        return d;
    }

    private static void Row(DeviceConfig d, string match, string command)
        => d.Triggers.Add(new DeviceTriggerConfig { Match = match, Command = command });

    [Fact]
    public void OneTableCarriesBothDirectionsAndNeitherReadsTheOther()
    {
        var d = Surface();
        Row(d, "NOTE 1 53 *", "LOOK 3");                       // the pad fires the look
        Row(d, "LOOK Walk-in", "LAMP 1 53 21");                // the look lights the pad

        // Inbound: the control row fires, and the lamp row is not a control so it never matches.
        Assert.Equal("LOOK 3", DeviceMap.Resolve(d, "NOTE 1 53 127"));
        Assert.Null(DeviceMap.Resolve(d, "LOOK Walk-in"));

        // Outbound: the lamp row lights, and the control row is not a lamp so it is left alone.
        var lamps = new List<string>();
        DeviceMap.Lamps(d, "LOOK Walk-in", lamps);
        Assert.Equal(new[] { "LAMP 1 53 21" }, lamps);

        lamps.Clear();
        DeviceMap.Lamps(d, "NOTE 1 53 127", lamps);
        Assert.Empty(lamps);
    }

    [Fact]
    public void OneFactCanLightMoreThanOneLampAndAnythingElsePutsThemOut()
    {
        var d = Surface();
        Row(d, "BLACKOUT 1", "LAMP 1 82 5");
        Row(d, "BLACKOUT 1", "LAMP 1 83 5");
        Row(d, "BLACKOUT 0", "LAMP 1 82 0");
        Row(d, "BLACKOUT 0", "LAMP 1 83 0");

        var lamps = new List<string>();
        DeviceMap.Lamps(d, "BLACKOUT 1", lamps);
        Assert.Equal(new[] { "LAMP 1 82 5", "LAMP 1 83 5" }, lamps);

        lamps.Clear();
        DeviceMap.Lamps(d, "BLACKOUT 0", lamps);
        Assert.Equal(new[] { "LAMP 1 82 0", "LAMP 1 83 0" }, lamps);
    }

    [Fact]
    public void TheShowsOwnReadingDrivesAnLedRingWithoutTheOperatorDoingSums()
    {
        // % is the fact read as 0–100 and stretched onto the wire's 0–127 — which is what turns the
        // audio level into the ring of light round a knob.
        var d = Surface();
        Row(d, "VOL *", "CC 1 48 %");

        var lamps = new List<string>();
        DeviceMap.Lamps(d, "VOL 100", lamps);
        Assert.Equal(new[] { "CC 1 48 127" }, lamps);

        lamps.Clear();
        DeviceMap.Lamps(d, "VOL 0", lamps);
        Assert.Equal(new[] { "CC 1 48 0" }, lamps);

        lamps.Clear();
        DeviceMap.Lamps(d, "VOL 50", lamps);
        Assert.Equal(new[] { "CC 1 48 64" }, lamps);

        // And * still carries the words through for a lamp whose value IS the fact.
        Row(d, "CUE *", "LAMP 1 84 *");
        lamps.Clear();
        DeviceMap.Lamps(d, "CUE 7", lamps);
        Assert.Contains("LAMP 1 84 7", lamps);
    }

    [Fact]
    public void APadFiresOnceWhenItGoesDownAndNotAgainWhenItComesUp()
    {
        // The fault the word "once" exists for: a release rendered as another NOTE would match the
        // same row as the press, so a GO button would GO twice and nobody would find out until the
        // show ran two cues on one press.
        var d = Surface();
        Row(d, "NOTE 1 53 *", "CUE GO");

        var down = MidiLines.Read(0x90, 53, 127);
        var up = MidiLines.Read(0x80, 53, 0);
        var upAsZero = MidiLines.Read(0x90, 53, 0);

        Assert.Equal("CUE GO", DeviceMap.Resolve(d, MidiLines.Format(down)));
        Assert.Null(DeviceMap.Resolve(d, MidiLines.Format(up)));
        Assert.Null(DeviceMap.Resolve(d, MidiLines.Format(upAsZero)));

        // A row learned FROM a release is a release row, so a surface whose pads only send note-off
        // is still usable — and the desk writes that row for you when you let go.
        Row(d, MidiLines.Trigger(up), "STOPALL");
        Assert.Equal("STOPALL", DeviceMap.Resolve(d, MidiLines.Format(up)));
        Assert.Equal("CUE GO", DeviceMap.Resolve(d, MidiLines.Format(down)));
    }

    [Fact]
    public void AFaderReachesBothEndsOfEveryLevelVerbTheDeskHas()
    {
        // A raw 0–127 fader would be REFUSED past 125 by the audio verb and past 100 by break
        // music, so the top of its travel would silently do nothing — a control that dies halfway
        // is worse than one that never worked.
        var d = Surface();
        Row(d, "CC 1 7 *", "AUDIO LEVEL *");

        Assert.Equal("AUDIO LEVEL 100", DeviceMap.Resolve(d, MidiLines.Format(MidiLines.Read(0xB0, 7, 127))));
        Assert.Equal("AUDIO LEVEL 0", DeviceMap.Resolve(d, MidiLines.Format(MidiLines.Read(0xB0, 7, 0))));
        Assert.Equal("AUDIO LEVEL 50", DeviceMap.Resolve(d, MidiLines.Format(MidiLines.Read(0xB0, 7, 64))));

        // And an exact row still works, so a fader with nothing to mix is an honest two-position
        // lever with nothing firing in between.
        Row(d, "CC 1 20 100", "BLACKOUT ON");
        Row(d, "CC 1 20 0", "BLACKOUT OFF");
        Assert.Equal("BLACKOUT ON", DeviceMap.Resolve(d, MidiLines.Format(MidiLines.Read(0xB0, 20, 127))));
        Assert.Equal("BLACKOUT OFF", DeviceMap.Resolve(d, MidiLines.Format(MidiLines.Read(0xB0, 20, 0))));
        Assert.Null(DeviceMap.Resolve(d, MidiLines.Format(MidiLines.Read(0xB0, 20, 64))));
    }

    [Fact]
    public void EveryStarterSetIsRowsTheOperatorCanReadEditAndReLearn()
    {
        Assert.NotEmpty(MidiSurfaces.All);
        foreach (var surface in MidiSurfaces.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(surface.Name));
            // Every set says out loud that it has not been run against the hardware here, because a
            // desk that claims to know a controller it has never met costs somebody a show.
            Assert.True(surface.Note.Length > 60, $"{surface.Name} says what it does not know");
            Assert.NotEmpty(surface.Rows);

            var device = new DeviceConfig { Link = DeviceLink.Midi };
            var added = MidiSurfaces.Seed(device, surface);
            Assert.Equal(surface.Rows.Count, added);

            // Seeding twice adds nothing: an operator who presses it again does not get a second
            // copy of every row, and a row they edited is theirs.
            Assert.Equal(0, MidiSurfaces.Seed(device, surface));
            Assert.Equal(surface.Rows.Count, device.Triggers.Count);

            // Every row does something: the control rows resolve, the lamp rows light.
            foreach (var trigger in device.Triggers)
            {
                var isControl = MidiLines.IsSurfaceLine(trigger.Match);
                var isLamp = MidiLines.IsSurfaceLine(trigger.Command);
                Assert.True(isControl ^ isLamp, $"{surface.Name}: '{trigger.Match}' → '{trigger.Command}' reads both ways or neither");
            }
        }

        // At least one set must be honest about having no feedback at all, because one of these
        // surfaces genuinely has none until its own editor is used.
        Assert.Contains(MidiSurfaces.All, s => s.Note.Contains("nothing on the surface lights"));
    }

    [Fact]
    public void APortNameIsMatchedThroughWindowsThirtyOneCharacterClip()
    {
        // MIDIINCAPS truncates a port's name, and vendors are not shy with theirs, so an exact
        // match would never fire.
        Assert.Equal("AKAI APC40 mkII", MidiSurfaces.For("MIDIIN2 (Akai APC40 mkII MIDI)")?.Name);
        Assert.Equal("Korg nanoKONTROL2", MidiSurfaces.For("nanoKONTROL2 SLIDER/KNOB")?.Name);
        Assert.Null(MidiSurfaces.For(""));
        Assert.Null(MidiSurfaces.For("Microsoft GS Wavetable Synth"));
    }

    /// <summary>
    /// The two things in the show that carry a level reach a device as facts, so a fader has
    /// something to follow home and a ring of light round a knob can show where the audio is.
    /// Without them the starter rows that drive an LED ring would never fire once, which is half
    /// of "and feedback" quietly missing.
    /// </summary>
    [Fact]
    public void ASurfaceHearsTheTwoLevelsTheShowActuallyHas()
    {
        const string state = """
            {"blackout":false,"live":true,"audio":{"level":63},"music":{"level":40},"lookEdited":true}
            """;
        var facts = DeviceFeedback.Facts(state);
        Assert.Equal("63", facts["VOL"]);
        Assert.Equal("40", facts["MUSICVOL"]);
        Assert.Equal("1", facts["LOOKEDITED"]);

        // And they drive a ring through the same table as everything else.
        var d = Surface();
        Row(d, "VOL *", "CC 1 48 %");
        var lamps = new List<string>();
        DeviceMap.Lamps(d, $"VOL {facts["VOL"]}", lamps);
        Assert.Equal(new[] { "CC 1 48 80" }, lamps);            // 63 % of 127

        // A state with no audio block at all says nothing rather than inventing a nought.
        Assert.DoesNotContain("VOL", DeviceFeedback.Facts("""{"blackout":false}""").Keys);
    }

    /// <summary>
    /// A surface that has just arrived is told so, as a fact like any other — so a row can light a
    /// lamp on a port coming back without the desk growing a second way of saying things.
    /// </summary>
    [Fact]
    public void ASurfaceComingBackCanLightItselfFromARowLikeAnyOther()
    {
        var d = Surface();
        Row(d, "OPEN 1", "LAMP 1 98 3");
        var lamps = new List<string>();
        DeviceMap.Lamps(d, "OPEN 1", lamps);
        Assert.Equal(new[] { "LAMP 1 98 3" }, lamps);

        // And it is not a command: a surface being plugged in must not run anything.
        Assert.Null(DeviceMap.Resolve(d, "OPEN 1"));
    }
}
