using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 43: a send to a box establishes one of four things — sent, delivered, accepted,
/// observed — and never more than its link and profile can. The profiles say which replies answer
/// a command, and a cue is the twin's fence only when the boxes behind it can answer.
/// </summary>
public class DeviceConfirmationTests
{
    [Fact]
    public void WhatALinkAndAProfileCanReach()
    {
        Assert.Equal(ConfirmLevel.Sent, DeviceConfirmation.Attainable(DeviceLink.Udp, DeviceProfile.Osc));
        Assert.Equal(ConfirmLevel.Sent, DeviceConfirmation.Attainable(DeviceLink.Udp, DeviceProfile.Disguise));
        Assert.Equal(ConfirmLevel.Sent, DeviceConfirmation.Attainable(DeviceLink.Midi, DeviceProfile.Lines));
        Assert.Equal(ConfirmLevel.Observed, DeviceConfirmation.Attainable(DeviceLink.Http, DeviceProfile.Lines));
        Assert.Equal(ConfirmLevel.Observed, DeviceConfirmation.Attainable(DeviceLink.Tcp, DeviceProfile.PjLink));
        Assert.Equal(ConfirmLevel.Observed, DeviceConfirmation.Attainable(DeviceLink.Tcp, DeviceProfile.Pixera));
        Assert.Equal(ConfirmLevel.Observed, DeviceConfirmation.Attainable(DeviceLink.Serial, DeviceProfile.Lines));
        Assert.Equal(ConfirmLevel.Delivered, DeviceConfirmation.Attainable(DeviceLink.Tcp, DeviceProfile.Osc));
        Assert.Contains("UDP datagram", DeviceConfirmation.Limit(DeviceLink.Udp, DeviceProfile.Osc));
        Assert.Contains("MIDI note", DeviceConfirmation.Limit(DeviceLink.Midi, DeviceProfile.Lines));
        Assert.Equal("", DeviceConfirmation.Limit(DeviceLink.Tcp, DeviceProfile.PjLink));
        Assert.Equal(new[] { "sent", "delivered", "accepted", "observed" }, Enum.GetValues<ConfirmLevel>().Select(DeviceConfirmation.Label));
        foreach (var level in Enum.GetValues<ConfirmLevel>()) Assert.NotEqual(level.ToString(), DeviceConfirmation.Describe(level));
    }

    [Fact]
    public void TheEffectiveLevelIsCappedAndObservedNeedsAQuery()
    {
        var udp = new DeviceConfig { Link = DeviceLink.Udp, Profile = DeviceProfile.Osc, Confirm = ConfirmLevel.Observed, ObserveQuery = "/state" };
        Assert.Equal(ConfirmLevel.Sent, DeviceConfirmation.Effective(udp));
        var projector = new DeviceConfig { Link = DeviceLink.Tcp, Profile = DeviceProfile.PjLink, Confirm = ConfirmLevel.Observed };
        Assert.Equal(ConfirmLevel.Accepted, DeviceConfirmation.Effective(projector));          // nothing to ask with
        projector.ObserveQuery = "INPUT ?";
        Assert.Equal(ConfirmLevel.Observed, DeviceConfirmation.Effective(projector));
        projector.Confirm = ConfirmLevel.Sent;
        Assert.Equal(ConfirmLevel.Sent, DeviceConfirmation.Effective(projector));
        Assert.Equal(ConfirmLevel.Accepted, new DeviceConfig().Confirm);                        // the default asks for a yes
        projector.ConfirmTimeoutMs = 50;
        Assert.Equal(200, projector.ConfirmTimeoutMs);
        projector.ConfirmTimeoutMs = 90_000;
        Assert.Equal(30_000, projector.ConfirmTimeoutMs);
        Assert.Equal(TimeSpan.FromSeconds(30), DeviceConfirmation.Timeout(projector));
    }

    [Fact]
    public void TheProfilesSayWhichRepliesAnswerACommand()
    {
        var pj = new PjLinkSession("");
        Assert.Equal("POWR", pj.SentKey("POWER ON"));
        Assert.Equal("INPT", pj.SentKey("INPUT HDMI 1"));
        Assert.Equal("", pj.SentKey("not a command"));
        var ok = pj.OnReceived("%1POWR=OK");
        Assert.True(ok.Ack);
        Assert.False(ok.IsError);
        Assert.Equal("POWR", ok.AckKey);
        var no = pj.OnReceived("%1INPT=ERR2");
        Assert.True(no.IsError);
        Assert.False(no.Ack);
        Assert.Equal("INPT", no.AckKey);
        Assert.Contains("out of parameter", no.Words);
        var value = pj.OnReceived("%1INPT=31");
        Assert.True(value.Ack);                                                                  // a question's answer answers the question
        Assert.Equal("INPT", value.AckKey);
        Assert.Equal("input 31", value.Words);
        Assert.False(pj.OnReceived("PJLINK 0").Ack);

        var lines = new LinesSession(LineEnding.Lf);
        Assert.Equal("", lines.SentKey("RELAY 1"));
        Assert.True(lines.OnReceived("OK").Ack);
        Assert.True(lines.OnReceived("ok done").Ack);
        Assert.True(lines.OnReceived("ERR no such relay").IsError);
        Assert.True(lines.OnReceived("ERROR").IsError);
        var own = lines.OnReceived("btn1");
        Assert.False(own.Ack);
        Assert.False(own.IsError);

        var pixera = new PixeraSession();
        var frames = pixera.Encode("TIMELINE Main PLAY", out var problem);
        Assert.Equal("", problem);
        Assert.Single(frames);
        var found = pixera.OnReceived("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":42}");
        Assert.False(found.Ack);                                                                 // a look-up on the way to the command
        Assert.Single(found.SendNext);
        var played = pixera.OnReceived("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":true}");
        Assert.True(played.Ack);                                                                 // the command's own result is the yes
        var error = pixera.OnReceived("{\"jsonrpc\":\"2.0\",\"id\":3,\"error\":{\"message\":\"no such timeline\"}}");
        Assert.True(error.IsError);
        Assert.False(error.Ack);
    }

    [Fact]
    public void ACueIsAFenceOnlyWhenItsBoxesCanAnswer()
    {
        var state = new ShowState();
        state.Interactive.Devices.Add(new DeviceConfig { Name = "Switcher", Link = DeviceLink.Http, Port = "http://10.0.0.9" });
        state.Interactive.Devices.Add(new DeviceConfig { Name = "Lights", Link = DeviceLink.Udp, Profile = DeviceProfile.Osc, Port = "10.0.0.8" });
        Cue(state, "Wall to main", "Switcher", "POST /route/main");
        Cue(state, "Wall by OSC", "Lights", "/wall/main 1");
        Cue(state, "Wall by words", "", "");
        Cue(state, "Wall via ghost", "Nobody", "x");

        Assert.Null(DeviceConfirmation.FenceProblem(state, "Wall to main"));
        var problem = DeviceConfirmation.FenceProblem(state, "Wall by OSC");
        Assert.NotNull(problem);
        Assert.Contains("'Lights'", problem);
        Assert.Contains("sent only", problem);
        Assert.Null(DeviceConfirmation.FenceProblem(state, "Wall by words"));                   // no box: the operator's own switch
        Assert.Null(DeviceConfirmation.FenceProblem(state, "Wall via ghost"));                  // fire time refuses a device that is not there
        Assert.Null(DeviceConfirmation.FenceProblem(state, "No such cue"));
        Assert.Null(DeviceConfirmation.FenceProblem(state, ""));

        Assert.Null(TwinWatch.AutoTakeOverBlocked(mainOnThisMachine: true, wallSwitchSet: true, problem));   // on one machine the kill is the fence
        Assert.Null(TwinWatch.AutoTakeOverBlocked(false, true, null));
        var blocked = TwinWatch.AutoTakeOverBlocked(false, true, problem);
        Assert.NotNull(blocked);
        Assert.EndsWith("so not by itself: TAKE OVER is yours", blocked);
        Assert.Contains("'Lights'", blocked);
        Assert.Contains("no wall-switch cue", TwinWatch.AutoTakeOverBlocked(false, false, null)!);
    }

    private static void Cue(ShowState state, string name, string device, string words)
    {
        var cue = new RunCueConfig { Name = name };
        if (device.Length > 0) cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.DeviceSend, Target = device, Value = words });
        else cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.MessageOn, Value = "Wall: main" });
        CueStacks.Caller(state).Cues.Add(cue);
    }
}
