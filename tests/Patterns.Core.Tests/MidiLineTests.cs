using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 27: "Patterns needs the ability to allow external controllers like the AKAI Professional
/// APC 40 mkII controls to be mapped by midi."
///
/// A surface's messages become the same text lines an Arduino sends, which is what lets a control
/// surface ride the Interactive area rather than growing a second one beside it. These pin the
/// translation both ways, because everything above it — the learn rows, the action layer, the
/// journal, the fencing — is already written and already tested.
/// </summary>
public class MidiLineTests
{
    [Fact]
    public void EveryMessageTheWireCarriesIsReadAndWrittenAsALineAnOperatorCanSee()
    {
        // A pad down on channel 1, note 53, at full velocity. The channel is shown 1-based because
        // that is how every controller's own manual numbers it.
        var pad = MidiLines.Read(0x90, 53, 127);
        Assert.Equal(MidiKind.Note, pad.Kind);
        Assert.Equal(0, pad.Channel);
        Assert.Equal("NOTE 1 53 127", MidiLines.Format(pad));
        Assert.Equal("NOTE 1 53 *", MidiLines.Trigger(pad));
        Assert.False(pad.IsRelease);

        // The same pad let go — and the note-on at velocity nought most surfaces send instead.
        Assert.True(MidiLines.Read(0x80, 53, 0).IsRelease);
        Assert.True(MidiLines.Read(0x90, 53, 0).IsRelease);

        // A fader on channel 1, controller 7.
        var fader = MidiLines.Read(0xB0, 7, 64);
        Assert.Equal(MidiKind.Control, fader.Kind);
        Assert.True(fader.IsContinuous);
        // Read as the percentage the desk's level verbs take, not the raw 0–127: those verbs refuse
        // anything past 125, so a raw fader would silently stop responding in the top of its travel.
        Assert.Equal("CC 1 7 50", MidiLines.Format(fader));
        Assert.Equal("CC 1 7 *", MidiLines.Trigger(fader));

        // A bank button, and a wheel.
        Assert.Equal("PROGRAM 1 4", MidiLines.Format(MidiLines.Read(0xC0, 4, 0)));
        var bend = MidiLines.Read(0xE0, 0x00, 0x40);           // MSB 64, LSB 0 — dead centre
        Assert.Equal(MidiKind.Bend, bend.Kind);
        Assert.Equal(8192, bend.Data2);
        Assert.Equal(64, bend.Value);
        Assert.True(bend.IsContinuous);

        // The channel rides: a surface on channel 8 reads as 8, not as 7 or as 0.
        Assert.Equal("NOTE 8 36 100", MidiLines.Format(MidiLines.Read(0x97, 36, 100)));

        // A release is its own word, so a row learned from a press cannot fire again on the way up.
        Assert.Equal("NOTEOFF 1 53", MidiLines.Format(MidiLines.Read(0x80, 53, 0)));
        Assert.Equal("NOTEOFF 1 53", MidiLines.Format(MidiLines.Read(0x90, 53, 0)));

        // Anything the desk has no word for still arrives, so a row can be written for it rather
        // than the control simply doing nothing with no explanation.
        var unknown = MidiLines.Read(0xA0, 60, 90);
        Assert.Equal(MidiKind.Other, unknown.Kind);
        Assert.Equal("MIDI 1 60 90", MidiLines.Format(unknown));
    }

    [Fact]
    public void ALampLineGoesBackOutAsTheBytesTheSurfaceExpects()
    {
        Assert.True(MidiLines.TryLamp("LAMP 1 53 21", out var status, out var d1, out var d2));
        Assert.Equal(0x90, status);
        Assert.Equal(53, d1);
        Assert.Equal(21, d2);                                  // the colour index this surface's palette calls 21

        Assert.True(MidiLines.TryLamp("LAMP 1 53 0", out _, out _, out var off));
        Assert.Equal(0, off);                                  // nought is out, on every surface there is

        // A controller's LED ring is a CC, not a note.
        Assert.True(MidiLines.TryLamp("CC 2 48 90", out status, out d1, out d2));
        Assert.Equal(0xB1, status);
        Assert.Equal(48, d1);
        Assert.Equal(90, d2);

        // A motorised fader is set with fourteen bits. Seven would step visibly on its way there,
        // which is the whole reason somebody bought a motorised fader.
        Assert.True(MidiLines.TryLamp("BEND 1 8192", out status, out d1, out d2));
        Assert.Equal(0xE0, status);
        Assert.Equal(0, d1);
        Assert.Equal(64, d2);
        Assert.True(MidiLines.TryLamp("BEND 1 16383", out _, out d1, out d2));
        Assert.Equal(0x7F, d1);
        Assert.Equal(0x7F, d2);

        // A channel outside the wire's sixteen is brought back rather than wrapping into somebody
        // else's, and a value past the top is held at the top.
        Assert.True(MidiLines.TryLamp("LAMP 99 200 200", out status, out d1, out d2));
        Assert.Equal(0x9F, status);
        Assert.Equal(127, d1);
        Assert.Equal(127, d2);

        // And what is not ours to send stays unsent: the same wire carries the plain-text feedback
        // an Arduino hears, and a desk that turned "LOOK Walk-in" into note-ons would be a fault
        // nobody could diagnose from the surface.
        Assert.False(MidiLines.TryLamp("LOOK Walk-in", out _, out _, out _));
        Assert.False(MidiLines.TryLamp("BLACKOUT 1", out _, out _, out _));
        Assert.False(MidiLines.TryLamp("", out _, out _, out _));
        Assert.False(MidiLines.TryLamp("LAMP", out _, out _, out _));
        Assert.False(MidiLines.TryLamp("LAMP one two", out _, out _, out _));
    }

    [Fact]
    public void AFaderReadsZeroAtTheBottomAndAHundredAtTheTop()
    {
        // An operator who pushes a fader all the way up and reads 99 % has found a bug, whatever
        // the arithmetic says.
        Assert.Equal(0, MidiLines.Percent(0));
        Assert.Equal(100, MidiLines.Percent(127));
        Assert.Equal(50, MidiLines.Percent(64));
        Assert.Equal(0, MidiLines.Percent(-5));
        Assert.Equal(100, MidiLines.Percent(999));

        // Monotonic all the way up: no step ever reads lower than the one below it.
        var last = -1;
        for (var v = 0; v <= MidiLines.Max; v++)
        {
            var pct = MidiLines.Percent(v);
            Assert.InRange(pct, 0, 100);
            Assert.True(pct >= last, $"{v} read {pct} after {last}");
            last = pct;
        }
    }

    [Fact]
    public void TheLineAndTheRowRoundTripThroughTheMapTheArduinosAlreadyUse()
    {
        // The point of the whole translation: a learn row written from a control matches that
        // control's own lines and no others, through the trigger table that has been in the desk
        // since Arduinos — so the mapping, the action layer, the journal and the fencing are all
        // code that already exists and is already tested.
        var pad = MidiLines.Read(0x90, 53, 127);
        var device = new DeviceConfig { SpeaksProtocol = false };
        device.Triggers.Add(new DeviceTriggerConfig { Match = MidiLines.Trigger(pad), Command = "LOOK 3" });

        Assert.Equal("LOOK 3", DeviceMap.Resolve(device, MidiLines.Format(pad)));
        Assert.Equal("LOOK 3", DeviceMap.Resolve(device, MidiLines.Format(MidiLines.Read(0x90, 53, 42))));   // any velocity
        Assert.Null(DeviceMap.Resolve(device, MidiLines.Format(MidiLines.Read(0x90, 54, 127))));             // the pad beside it
        Assert.Null(DeviceMap.Resolve(device, MidiLines.Format(MidiLines.Read(0x91, 53, 127))));             // another channel

        // And a fader carries its value through the * into the command's tail.
        var fader = MidiLines.Read(0xB0, 7, 64);
        device.Triggers.Add(new DeviceTriggerConfig { Match = MidiLines.Trigger(fader), Command = "AUDIO LEVEL *" });
        Assert.Equal("AUDIO LEVEL 50", DeviceMap.Resolve(device, MidiLines.Format(fader)));
        Assert.Equal("AUDIO LEVEL 100", DeviceMap.Resolve(device, MidiLines.Format(MidiLines.Read(0xB0, 7, 127))));
        Assert.Equal("AUDIO LEVEL 0", DeviceMap.Resolve(device, MidiLines.Format(MidiLines.Read(0xB0, 7, 0))));
    }
}
