using System.Text;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The Interactive area, pure: lines framed and split, a device's words onto the protocol, the
/// show's facts as lines a device parses, addresses read, and the cue action, the verb and the
/// OSC address that send a line.
/// </summary>
public class InteractiveTests
{
    [Fact]
    public void LinesAreFramedForTheDeviceAndSplitFromWhatItSends()
    {
        Assert.Equal("RELAY 1\n", DeviceLines.Frame("RELAY 1", LineEnding.Lf));
        Assert.Equal("RELAY 1\r\n", DeviceLines.Frame("RELAY 1\n", LineEnding.CrLf));
        Assert.Equal("RELAY 1\r", DeviceLines.Frame("RELAY 1", LineEnding.Cr));
        Assert.Equal("RELAY 1", DeviceLines.Frame("RELAY 1\r\n", LineEnding.None));

        var buffer = new StringBuilder("BTN1\r\nBTN2\n\n  SENSOR 42 \rBT");
        var lines = DeviceLines.Split(buffer);
        Assert.Equal(new[] { "BTN1", "BTN2", "SENSOR 42" }, lines);
        Assert.Equal("BT", buffer.ToString());          // the unfinished tail waits for its ending
        buffer.Append("N3\n");
        Assert.Equal(new[] { "BTN3" }, DeviceLines.Split(buffer));
        Assert.Equal("", buffer.ToString());

        var binary = new StringBuilder(new string('x', 5000));
        Assert.Empty(DeviceLines.Split(binary));
        Assert.Equal(0, binary.Length);                 // a device talking binary never fills the memory
    }

    [Fact]
    public void ADevicesWordsBecomeCommandsThroughItsTriggersOrAsTheyAre()
    {
        var device = new DeviceConfig { Name = "Arduino", SpeaksProtocol = true };
        device.Triggers.Add(new DeviceTriggerConfig { Match = "BTN1", Command = "CUE GO" });
        device.Triggers.Add(new DeviceTriggerConfig { Match = "sensor *", Command = "MESSAGE Room at *" });
        device.Triggers.Add(new DeviceTriggerConfig { Match = "BTN2", Command = "" });

        Assert.Equal("CUE GO", DeviceMap.Resolve(device, "btn1"));                    // case-blind, whole line
        Assert.Equal("MESSAGE Room at 42", DeviceMap.Resolve(device, "SENSOR 42"));   // a prefix, the rest rides into the command
        Assert.Equal("MESSAGE Room at", DeviceMap.Resolve(device, "SENSOR"));
        Assert.Equal("BLACKOUT ON", DeviceMap.Resolve(device, "BLACKOUT ON"));        // no trigger: the protocol as it is
        Assert.Null(DeviceMap.Resolve(device, "BTN2"));                               // a trigger with no command fires nothing … and is not a protocol line either
        Assert.Null(DeviceMap.Resolve(device, "GREETINGS FRIEND"));                    // not a verb, not a trigger
        Assert.Null(DeviceMap.Resolve(device, "   "));

        device.SpeaksProtocol = false;
        Assert.Null(DeviceMap.Resolve(device, "BLACKOUT ON"));                        // only the triggers now
        Assert.Equal("CUE GO", DeviceMap.Resolve(device, "BTN1"));
    }

    [Fact]
    public void TheShowsFactsGoToADeviceAsShortLinesAndOnlyWhenTheyChange()
    {
        const string json = "{\"blackout\":true,\"live\":false,\"duck\":false,\"frozen\":false,\"airLook\":\"Walk-in\",\"airLabel\":\"Walk-in\",\"stingerPlaying\":\"\"," +
                            "\"cuestack\":{\"armed\":true,\"hold\":false,\"standby\":{\"number\":\"01.020\",\"name\":\"Welcome\"}},\"deck\":{\"page\":3,\"count\":12},\"presenter\":{\"index\":1,\"count\":5}}";
        var facts = DeviceFeedback.Facts(json);
        Assert.Equal("1", facts["BLACKOUT"]);
        Assert.Equal("0", facts["LIVE"]);
        Assert.Equal("Walk-in", facts["LOOK"]);
        Assert.Equal("Walk-in", facts["PROGRAM"]);
        Assert.Equal("", facts["STINGER"]);
        Assert.Equal("1", facts["ARMED"]);
        Assert.Equal("0", facts["HOLD"]);
        Assert.Equal("01.020", facts["CUE"]);
        Assert.Equal("3 12", facts["DECK"]);
        Assert.Equal("2 5", facts["STEP"]);

        var first = DeviceFeedback.Changes(facts, null);
        Assert.Contains("BLACKOUT 1", first);
        Assert.Contains("LOOK Walk-in", first);
        Assert.Contains("CUE 01.020", first);
        Assert.Contains("STINGER", first);                                            // an empty value is the bare key
        Assert.Equal(first.OrderBy(l => l, StringComparer.Ordinal), first);           // a stable order, so a sketch can rely on it

        Assert.Empty(DeviceFeedback.Changes(facts, new Dictionary<string, string>(facts)));
        var later = DeviceFeedback.Facts(json.Replace("\"blackout\":true", "\"blackout\":false").Replace("01.020", "01.030"));
        Assert.Equal(new[] { "BLACKOUT 0", "CUE 01.030" }, DeviceFeedback.Changes(later, facts));

        Assert.Equal("0 0", DeviceFeedback.Facts("{\"deck\":null}")["DECK"]);
        Assert.Empty(DeviceFeedback.Facts("not json"));
    }

    [Fact]
    public void AddressesAreReadAsTyped()
    {
        Assert.Equal("COM3", DeviceAddress.SerialPort("com3"));
        Assert.Equal("COM12", DeviceAddress.SerialPort(" COM12 "));
        Assert.Equal("/dev/ttyUSB0", DeviceAddress.SerialPort("/dev/ttyUSB0"));
        Assert.Equal("", DeviceAddress.SerialPort("COM0"));
        Assert.Equal("", DeviceAddress.SerialPort("192.168.1.50"));
        Assert.Equal("", DeviceAddress.SerialPort(""));

        Assert.True(DeviceAddress.TryParseHost("192.168.1.50", 7000, out var host, out var port));
        Assert.Equal(("192.168.1.50", 7000), (host, port));
        Assert.True(DeviceAddress.TryParseHost("pi.local:8000", 7000, out host, out port));
        Assert.Equal(("pi.local", 8000), (host, port));
        Assert.False(DeviceAddress.TryParseHost("", 7000, out _, out _));
        Assert.False(DeviceAddress.TryParseHost("two words", 7000, out _, out _));
        Assert.False(DeviceAddress.TryParseHost("host:99999", 7000, out _, out _));

        Assert.Equal("COM3 at 115200", DeviceAddress.Describe(new DeviceConfig { Link = DeviceLink.Serial, Port = "COM3", Baud = 115200 }));
        Assert.Contains("no serial port", DeviceAddress.Describe(new DeviceConfig { Link = DeviceLink.Serial, Port = "" }));
        Assert.Equal("192.168.1.50:7000 (TCP)", DeviceAddress.Describe(new DeviceConfig { Link = DeviceLink.Tcp, Port = "192.168.1.50", NetPort = 7000 }));
        Assert.Equal("pi.local:8000 (UDP)", DeviceAddress.Describe(new DeviceConfig { Link = DeviceLink.Udp, Port = "pi.local:8000" }));
    }

    [Fact]
    public void ADeviceIsFoundByNameByPlaceOrAsTheFirst()
    {
        var config = new InteractiveConfig();
        var off = new DeviceConfig { Name = "Lectern", Enabled = false };
        var pi = new DeviceConfig { Name = "Pi" };
        config.Devices.Add(off);
        config.Devices.Add(pi);
        Assert.Same(pi, Interactive.Find(config, "pi"));
        Assert.Same(off, Interactive.Find(config, "1"));
        Assert.Same(pi, Interactive.Find(config, ""));      // the first enabled one
        Assert.Same(pi, Interactive.Find(config, "*"));
        Assert.Same(off, Interactive.Find(config, off.Id));
        Assert.Null(Interactive.Find(config, "nobody"));
        Assert.Null(Interactive.Find(new InteractiveConfig(), ""));
    }

    [Fact]
    public void TheVerbTheAddressAndTheCueActionSendALine()
    {
        var cmd = ControlProtocol.Parse("DEVICE Arduino RELAY 1");
        Assert.Equal(RemoteCommandKind.DeviceSend, cmd.Kind);
        Assert.Equal("Arduino", cmd.Extra);
        Assert.Equal("RELAY 1", cmd.TextArg);
        Assert.Equal(RemoteCommandKind.DeviceSend, ControlProtocol.Parse("send * PING").Kind);
        Assert.Equal("*", ControlProtocol.Parse("send * PING").Extra);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("DEVICE Arduino").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("DEVICE").Kind);

        Assert.Equal("DEVICE Arduino RELAY 1", OscMap.ToLine(OscMessage.Of("/patterns/device/Arduino", "RELAY 1")));
        Assert.Equal("DEVICE Arduino RELAY 1", OscMap.ToLine(OscMessage.Of("/patterns/device/Arduino/RELAY", 1)));
        Assert.Equal("DEVICE pi SHOW 3", OscMap.ToLine(OscMessage.Of("/patterns/device", "pi", "SHOW", 3)));
        Assert.Equal("DEVICE * PING", OscMap.ToLine(OscMessage.Of("/patterns/send/*", "PING")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/device/Arduino")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/device")));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/device"));

        Assert.Equal((TargetKind.Device, ValueKind.Text), CueActionSpec.For(CueActionKind.DeviceSend));
        Assert.Equal("Device — send a line", CueActionSpec.Label(CueActionKind.DeviceSend));
        Assert.Equal(CueActionKind.DeviceSend, CueSheet.ParseKind("arduino"));
        Assert.Equal(CueActionKind.DeviceSend, CueSheet.ParseKind("device"));

        var state = new ShowState();
        state.Interactive.Devices.Add(new DeviceConfig { Name = "Arduino" });
        var cue = new RunCueConfig { Number = "01.010", Name = "Relay" };
        cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.DeviceSend, Target = "Arduino", Value = "RELAY 1" });
        Assert.Equal("Device Arduino: RELAY 1", CueSummary.DescribeAction(state, cue.Actions[0]));
        var stack = new CueStackConfig();
        stack.Cues.Add(cue);
        var report = CueValidator.Validate(state, stack);
        Assert.False(report.IsBroken(cue.Id));
        Assert.True(report.Warnings.ContainsKey(cue.Id));                                   // the area is off: a soft note
        state.Interactive.Enabled = true;
        Assert.False(CueValidator.Validate(state, stack).Warnings.ContainsKey(cue.Id));

        cue.Actions[0].Target = "Nobody";
        Assert.True(CueValidator.Validate(state, stack).IsBroken(cue.Id));
        cue.Actions[0].Target = "";
        cue.Actions[0].Value = "";
        Assert.True(CueValidator.Validate(state, stack).IsBroken(cue.Id));      // nothing to send
    }
}
