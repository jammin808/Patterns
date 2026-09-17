using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 73: MIDI learn from any button, pure. The Interactive area's trigger table is the map;
/// these pin what a learned line and a wire line become as a row (a pad at any velocity, a
/// fader anywhere with a level verb's number turned into *), that the last mapping of a
/// control wins and says what it replaced, which rows are bindings and which are lamps, what a
/// wire line has bound to it, and what FORGET takes away.
/// </summary>
public class MidiBindingsTests
{
    private static DeviceConfig Surface(string name = "APC40") => new() { Name = name, Link = DeviceLink.Midi, Port = "APC40 mkII" };

    [Fact]
    public void APadKeepsTheLineAndAFaderTakesALevelVerbsNumberAsAStar()
    {
        Assert.Equal(("NOTE 1 53 *", "LOOK Walk-in"), MidiBindings.RowFor("NOTE 1 53 127", "LOOK Walk-in"));
        Assert.Equal(("NOTE 1 53 *", "AUDIO LEVEL 50"), MidiBindings.RowFor("NOTE 1 53 100", "AUDIO LEVEL 50"));   // the pad sets it to fifty
        Assert.Equal(("CC 1 7 *", "AUDIO LEVEL *"), MidiBindings.RowFor("CC 1 7 64", "AUDIO LEVEL 50"));           // the fader sets it
        Assert.Equal(("CC 1 7 *", "music vol *"), MidiBindings.RowFor("CC 1 7 0", "music  vol 40"));   // the spaces tidied, the case the operator's (a look's name keeps its case)
        Assert.Equal(("BEND 1 *", "AUDIO LEVEL *"), MidiBindings.RowFor("BEND 1 40", "AUDIO LEVEL 75%"));
        Assert.Equal(("CC 1 7 *", "LOOK Walk-in"), MidiBindings.RowFor("CC 1 7 64", "LOOK Walk-in"));               // no number to take
        Assert.Equal(("CC 1 7 *", "LOOK #3"), MidiBindings.RowFor("CC 1 7 64", "LOOK #3"));                         // #3 is a name, not a level
        Assert.Equal(("PROGRAM 1 5", "CUE GO"), MidiBindings.RowFor("PROGRAM 1 5", "CUE GO"));
    }

    [Fact]
    public void TheLastMappingOfAControlWinsAndSaysWhatItReplaced()
    {
        var d = Surface();
        Assert.Equal("NOTE 1 53 on APC40 → LOOK Walk-in", MidiBindings.Bind(d, "NOTE 1 53 127", "LOOK Walk-in"));
        Assert.Single(d.Triggers);
        Assert.Equal("NOTE 1 53 on APC40 → LOOK Keynote (was LOOK Walk-in)", MidiBindings.Bind(d, "NOTE 1 53 90", "LOOK Keynote"));
        Assert.Single(d.Triggers);                                                        // retimed, not doubled
        Assert.Equal("LOOK Keynote", d.Triggers[0].Command);
        Assert.Equal("NOTE 1 53 on APC40 → LOOK Keynote (as it was)", MidiBindings.Bind(d, "NOTE 1 53 1", "look keynote"));   // the same line, case-blind: the row keeps its spelling
        Assert.Equal("LOOK Keynote", d.Triggers[0].Command);
        Assert.Equal("CC 1 7 on APC40 → AUDIO LEVEL *", MidiBindings.Bind(d, "CC 1 7 64", "AUDIO LEVEL 50"));
        Assert.Equal(2, d.Triggers.Count);
        Assert.Equal("", MidiBindings.Bind(d, "", "LOOK Walk-in"));
        Assert.Equal("", MidiBindings.Bind(d, "NOTE 1 54 127", "  "));
        Assert.Equal(2, d.Triggers.Count);
    }

    [Fact]
    public void ABindingIsAControlDoingSomethingAndALampRowIsNot()
    {
        var apc = Surface();
        apc.Triggers.Add(new DeviceTriggerConfig { Match = "NOTE 1 53 *", Command = "LOOK Walk-in" });
        apc.Triggers.Add(new DeviceTriggerConfig { Match = "LOOK *", Command = "LAMP 1 53 21" });           // the show lighting the pad: not a control
        apc.Triggers.Add(new DeviceTriggerConfig { Match = "CC 1 7 *", Command = "AUDIO LEVEL *" });
        apc.Triggers.Add(new DeviceTriggerConfig { Match = "NOTE 1 54 *", Command = "" });                  // learned on the page, not yet told what to do
        var arduino = new DeviceConfig { Name = "Arduino", Link = DeviceLink.Serial, Port = "COM3" };
        arduino.Triggers.Add(new DeviceTriggerConfig { Match = "BTN1", Command = "CUE GO" });               // a board's row is not a MIDI binding
        var all = MidiBindings.All(new[] { apc, arduino });
        Assert.Equal(2, all.Count);
        Assert.Equal(2, MidiBindings.Count(apc));
        Assert.Equal(0, MidiBindings.Count(arduino));
        Assert.Equal(new MidiBinding("APC40", "NOTE 1 53 *", "LOOK Walk-in"), all[0]);
        Assert.Equal("NOTE 1 53", all[0].Control);
        Assert.Equal("NOTE 1 53 (APC40)", all[0].Words);

        // What a wire line has bound to it: the line itself, and the level row for the verb with a number.
        Assert.Single(MidiBindings.For(all, "look walk-in"));
        Assert.Empty(MidiBindings.For(all, "LOOK Keynote"));
        Assert.Single(MidiBindings.For(all, "AUDIO LEVEL 50"));
        Assert.Single(MidiBindings.For(all, "AUDIO LEVEL *"));
        Assert.Empty(MidiBindings.For(all, "AUDIO LEVEL"));
        Assert.Empty(MidiBindings.For(all, ""));
        Assert.True(MidiBindings.SameLine(" LOOK  walk-in", "look Walk-in "));
        Assert.False(MidiBindings.SameLine("LOOK Walk-in", "LOOK Walkin"));
    }

    [Fact]
    public void ForgetTakesEveryControlBoundToALineOnEverySurfaceAndOneRowByItself()
    {
        var apc = Surface();
        var pad = Surface("Launchpad");
        apc.Triggers.Add(new DeviceTriggerConfig { Match = "NOTE 1 53 *", Command = "LOOK Walk-in" });
        apc.Triggers.Add(new DeviceTriggerConfig { Match = "NOTE 1 54 *", Command = "LOOK Keynote" });
        apc.Triggers.Add(new DeviceTriggerConfig { Match = "LOOK *", Command = "LAMP 1 53 21" });
        pad.Triggers.Add(new DeviceTriggerConfig { Match = "NOTE 1 81 *", Command = "look walk-in" });
        pad.Triggers.Add(new DeviceTriggerConfig { Match = "CC 1 7 *", Command = "AUDIO LEVEL *" });
        var devices = new[] { apc, pad };
        Assert.Equal(2, MidiBindings.Forget(devices, "LOOK Walk-in"));
        Assert.Equal(2, apc.Triggers.Count);                                                  // Keynote and the lamp row stay
        Assert.Single(pad.Triggers);
        Assert.Equal(0, MidiBindings.Forget(devices, "LOOK Walk-in"));
        Assert.Equal(1, MidiBindings.Forget(devices, "AUDIO LEVEL 30"));                       // the fader that sets the level goes with the verb
        Assert.Empty(pad.Triggers);
        Assert.Equal(0, MidiBindings.Forget(devices, ""));
        Assert.True(MidiBindings.ForgetRow(devices, new MidiBinding("APC40", "NOTE 1 54 *", "LOOK Keynote")));
        Assert.False(MidiBindings.ForgetRow(devices, new MidiBinding("APC40", "NOTE 1 54 *", "LOOK Keynote")));
        Assert.Single(apc.Triggers);
        Assert.Equal("LAMP 1 53 21", apc.Triggers[0].Command);
    }
}
