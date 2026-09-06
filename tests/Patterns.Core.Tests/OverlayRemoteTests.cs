using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The overlays from a remote: the verbs on the wire, the OSC addresses in and the facts out,
/// and the words the phone reads — the clock, the message, the countdown, the logo, the PiP.
/// </summary>
public class OverlayRemoteTests
{
    [Fact]
    public void TheVerbsParse()
    {
        Assert.Equal(RemoteCommandKind.ClockToggle, ControlProtocol.Parse("CLOCK").Kind);
        Assert.Equal(RemoteCommandKind.ClockOn, ControlProtocol.Parse("clock on").Kind);
        Assert.Equal(RemoteCommandKind.ClockOff, ControlProtocol.Parse("CLOCK HIDE").Kind);
        Assert.Equal(new RemoteCommand(RemoteCommandKind.ClockFormat, 0, "24"), ControlProtocol.Parse("CLOCK 24H"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.ClockFormat, 0, "12"), ControlProtocol.Parse("CLOCK 12"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.ClockSeconds, 0, "off"), ControlProtocol.Parse("CLOCK SECONDS OFF"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.ClockSeconds, 0, "toggle"), ControlProtocol.Parse("CLOCK SECONDS"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.ClockDate, 0, "on"), ControlProtocol.Parse("CLOCK DATE 1"));
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("CLOCK sideways").Kind);

        Assert.Equal(RemoteCommandKind.MessageToggle, ControlProtocol.Parse("MESSAGE").Kind);
        Assert.Equal(new RemoteCommand(RemoteCommandKind.MessageOn, 0, ""), ControlProtocol.Parse("MESSAGE ON"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.MessageOn, 0, "Doors open at 7"), ControlProtocol.Parse("MESSAGE ON Doors open at 7"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.MessageOn, 0, "Doors open at 7"), ControlProtocol.Parse("MSG Doors open at 7"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.MessageOn, 0, "Welcome back"), ControlProtocol.Parse("MESSAGE TEXT Welcome back"));
        Assert.Equal(RemoteCommandKind.MessageOff, ControlProtocol.Parse("MESSAGE OFF").Kind);
        Assert.Equal(new RemoteCommand(RemoteCommandKind.MessageScroll, 0, "on"), ControlProtocol.Parse("MESSAGE SCROLL ON"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.MessageScroll, 0, "toggle"), ControlProtocol.Parse("TICKER"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.MessageScroll, 0, "off"), ControlProtocol.Parse("TICKER OFF"));

        Assert.Equal(new RemoteCommand(RemoteCommandKind.CountdownStart, 0, "5"), ControlProtocol.Parse("COUNTDOWN 5"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.CountdownStart, 0, "2.5"), ControlProtocol.Parse("COUNTDOWN START 2:30"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.CountdownStart, 0, "1.5"), ControlProtocol.Parse("TIMER START 90s"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.CountdownStart, 0, ""), ControlProtocol.Parse("COUNTDOWN START"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.CountdownTo, 0, "19:30"), ControlProtocol.Parse("COUNTDOWN TO 19:30"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.CountdownTo, 0, "14:00"), ControlProtocol.Parse("COUNTDOWN AT 14:00"));
        Assert.Equal(RemoteCommandKind.CountdownStop, ControlProtocol.Parse("COUNTDOWN STOP").Kind);
        Assert.Equal(RemoteCommandKind.CountdownStop, ControlProtocol.Parse("COUNTDOWN OFF").Kind);
        Assert.Equal(new RemoteCommand(RemoteCommandKind.CountdownLabel, 0, "BACK FROM LUNCH IN"), ControlProtocol.Parse("COUNTDOWN LABEL BACK FROM LUNCH IN"));
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("COUNTDOWN").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("COUNTDOWN START soon").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("COUNTDOWN TO").Kind);

        Assert.Equal(RemoteCommandKind.LogoToggle, ControlProtocol.Parse("LOGO").Kind);
        Assert.Equal(RemoteCommandKind.LogoOn, ControlProtocol.Parse("LOGO ON").Kind);
        Assert.Equal(RemoteCommandKind.LogoOff, ControlProtocol.Parse("LOGO OFF").Kind);
        Assert.Equal(RemoteCommandKind.PipToggle, ControlProtocol.Parse("PIP TOGGLE").Kind);
        Assert.Equal(RemoteCommandKind.PipOn, ControlProtocol.Parse("PIP SHOW").Kind);
        Assert.Equal(RemoteCommandKind.PipOff, ControlProtocol.Parse("PIP OFF").Kind);
        Assert.Equal(RemoteCommandKind.OverlaysOff, ControlProtocol.Parse("OVERLAYS OFF").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("OVERLAYS ON").Kind);

        // The minutes a countdown takes: plain, decimal, minutes:seconds, with a unit; never words, zero or more than a day.
        Assert.True(ControlProtocol.TryParseMinutes("10", out var m) && m == 10);
        Assert.True(ControlProtocol.TryParseMinutes("0.5", out m) && m == 0.5);
        Assert.True(ControlProtocol.TryParseMinutes("1:15", out m) && Math.Abs(m - 1.25) < 1e-9);
        Assert.True(ControlProtocol.TryParseMinutes("5 min", out m) && m == 5);
        Assert.True(ControlProtocol.TryParseMinutes("30s", out m) && m == 0.5);
        Assert.False(ControlProtocol.TryParseMinutes("soon", out _));
        Assert.False(ControlProtocol.TryParseMinutes("0", out _));
        Assert.False(ControlProtocol.TryParseMinutes("1:75", out _));
        Assert.False(ControlProtocol.TryParseMinutes("2000", out _));
        Assert.Equal("on", ControlProtocol.SwitchWord("SHOW"));
        Assert.Equal("off", ControlProtocol.SwitchWord("0"));
        Assert.Equal("toggle", ControlProtocol.SwitchWord(""));
    }

    [Fact]
    public void TheAddressesMapAndTheFactsComeBack()
    {
        Assert.Equal("CLOCK TOGGLE", OscMap.ToLine(OscMessage.Of("/patterns/clock")));
        Assert.Equal("CLOCK ON", OscMap.ToLine(OscMessage.Of("/patterns/clock", 1)));
        Assert.Equal("CLOCK OFF", OscMap.ToLine(OscMessage.Of("/patterns/clock/off")));
        Assert.Equal("CLOCK 24", OscMap.ToLine(OscMessage.Of("/patterns/clock/24")));
        Assert.Equal("CLOCK 12", OscMap.ToLine(OscMessage.Of("/patterns/clock", 12)));
        Assert.Equal("CLOCK 24", OscMap.ToLine(OscMessage.Of("/patterns/clock/format", "24")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/clock/format", "13")));
        Assert.Equal("CLOCK SECONDS OFF", OscMap.ToLine(OscMessage.Of("/patterns/clock/seconds", 0)));
        Assert.Equal("CLOCK DATE TOGGLE", OscMap.ToLine(OscMessage.Of("/patterns/clock/date")));
        Assert.Equal("MESSAGE TOGGLE", OscMap.ToLine(OscMessage.Of("/patterns/message")));
        Assert.Equal("MESSAGE ON", OscMap.ToLine(OscMessage.Of("/patterns/message", 1)));
        Assert.Equal("MESSAGE OFF", OscMap.ToLine(OscMessage.Of("/patterns/message", "off")));
        Assert.Equal("MESSAGE Doors open", OscMap.ToLine(OscMessage.Of("/patterns/message", "Doors open")));
        Assert.Equal("MESSAGE Doors open", OscMap.ToLine(OscMessage.Of("/patterns/message/text", "Doors open")));
        Assert.Equal("MESSAGE Doors open", OscMap.ToLine(OscMessage.Of("/patterns/msg/Doors/open")));
        Assert.Equal("MESSAGE SCROLL ON", OscMap.ToLine(OscMessage.Of("/patterns/message/scroll", 1)));
        Assert.Equal("MESSAGE SCROLL TOGGLE", OscMap.ToLine(OscMessage.Of("/patterns/ticker")));
        Assert.Equal("COUNTDOWN START 5", OscMap.ToLine(OscMessage.Of("/patterns/countdown", 5)));
        Assert.Equal("COUNTDOWN START 2:30", OscMap.ToLine(OscMessage.Of("/patterns/countdown/start/2:30")));
        Assert.Equal("COUNTDOWN START", OscMap.ToLine(OscMessage.Of("/patterns/countdown/start")));
        Assert.Equal("COUNTDOWN START 10", OscMap.ToLine(OscMessage.Of("/patterns/timer/10")));
        Assert.Equal("COUNTDOWN TO 19:30", OscMap.ToLine(OscMessage.Of("/patterns/countdown/to", "19:30")));
        Assert.Equal("COUNTDOWN TO 19:30", OscMap.ToLine(OscMessage.Of("/patterns/countdown/to/19:30")));
        Assert.Equal("COUNTDOWN STOP", OscMap.ToLine(OscMessage.Of("/patterns/countdown/stop")));
        Assert.Equal("COUNTDOWN STOP", OscMap.ToLine(OscMessage.Of("/patterns/countdown", "off")));
        Assert.Equal("COUNTDOWN LABEL DOORS IN", OscMap.ToLine(OscMessage.Of("/patterns/countdown/label", "DOORS IN")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/countdown")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/countdown/to")));
        Assert.Equal("LOGO TOGGLE", OscMap.ToLine(OscMessage.Of("/patterns/logo")));
        Assert.Equal("LOGO ON", OscMap.ToLine(OscMessage.Of("/patterns/logo/on")));
        Assert.Equal("PIP OFF", OscMap.ToLine(OscMessage.Of("/patterns/pip", 0)));
        Assert.Equal("OVERLAYS OFF", OscMap.ToLine(OscMessage.Of("/patterns/overlays/off")));
        Assert.Equal("OVERLAYS OFF", OscMap.ToLine(OscMessage.Of("/patterns/overlays")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/overlays/on")));
        Assert.Contains(OscMap.Reference, a => a.Address.StartsWith("/patterns/clock"));
        Assert.Contains(OscMap.Reference, a => a.Address.StartsWith("/patterns/countdown"));

        var fed = OscFeedback.FromState("{\"overlays\":{\"clock\":{\"on\":true,\"hours\":24,\"seconds\":false,\"date\":true},\"message\":{\"on\":false,\"text\":\"WELCOME\",\"scroll\":true},\"countdown\":{\"on\":true,\"phase\":\"running\",\"label\":\"DOORS IN\",\"target\":\"19:30\",\"remaining\":754,\"text\":\"12:34 · DOORS IN\"},\"logo\":{\"on\":true},\"pip\":{\"on\":false},\"text\":\"Clock 24 h and the date · Countdown 12:34 to 19:30 · Logo\"}}");
        object? One(string address) => Assert.Single(fed, m => m.Address == OscFeedback.Prefix + address).Args[0];
        Assert.Equal(1, One("clock"));
        Assert.Equal(24, One("clock/hours"));
        Assert.Equal(0, One("clock/seconds"));
        Assert.Equal(1, One("clock/date"));
        Assert.Equal(0, One("message"));
        Assert.Equal("WELCOME", One("message/text"));
        Assert.Equal(1, One("message/scroll"));
        Assert.Equal(1, One("countdown"));
        Assert.Equal("running", One("countdown/phase"));
        Assert.Equal("DOORS IN", One("countdown/label"));
        Assert.Equal("19:30", One("countdown/target"));
        Assert.Equal(754, One("countdown/remaining"));
        Assert.Equal("12:34 · DOORS IN", One("countdown/text"));
        Assert.Equal(1, One("logo"));
        Assert.Equal(0, One("pip"));
        Assert.Equal("Clock 24 h and the date · Countdown 12:34 to 19:30 · Logo", One("overlays/text"));
    }

    [Fact]
    public void TheWordsThePhoneReads()
    {
        Assert.True(OverlayControl.SwitchTo("on", false));
        Assert.False(OverlayControl.SwitchTo("hide", true));
        Assert.True(OverlayControl.SwitchTo("toggle", false));
        Assert.False(OverlayControl.SwitchTo("", true));

        var clock = new ClockOverlay { TwentyFourHour = true, ShowSeconds = true };
        var at = new DateTime(2026, 9, 6, 14, 32, 7);
        Assert.Equal("14:32:07", OverlayControl.ClockText(clock, at));
        clock.ShowSeconds = false;
        Assert.Equal("14:32", OverlayControl.ClockText(clock, at));
        clock.TwentyFourHour = false;
        Assert.Equal("2:32 PM", OverlayControl.ClockText(clock, at));
        clock.ShowSeconds = true;
        Assert.Equal("2:32:07 PM", OverlayControl.ClockText(clock, at));

        var utc = new DateTime(2026, 9, 6, 13, 32, 7, DateTimeKind.Utc);
        var cd = new CountdownConfig { Enabled = true, TargetKind = CountdownTargetKind.Duration, DurationMinutes = 15, ArmedAtUtc = utc.AddMinutes(-2).AddSeconds(-26), Label = "DOORS IN" };
        Assert.Equal(("running", 754, "12:34 · DOORS IN"), OverlayControl.CountdownWords(cd, at, utc));
        Assert.True(OverlayControl.CountsEverySecond(cd, at, utc));
        Assert.Equal("15 min", OverlayControl.CountdownTarget(cd));
        cd.ArmedAtUtc = utc.AddMinutes(-20);
        Assert.Equal(("over", 0, "OVER · STARTING NOW"), OverlayControl.CountdownWords(cd, at, utc));
        Assert.False(OverlayControl.CountsEverySecond(cd, at, utc));
        cd.Enabled = false;
        Assert.Equal(("off", 0, "off"), OverlayControl.CountdownWords(cd, at, utc));
        cd.Enabled = true;
        cd.TargetKind = CountdownTargetKind.TimeOfDay;
        cd.TargetTime = "19:30";
        Assert.Equal("19:30", OverlayControl.CountdownTarget(cd));
        Assert.Equal("running", OverlayControl.CountdownWords(cd, at, utc).Phase);
        cd.TargetTime = "junk";
        Assert.Equal(("off", 0, "set up, not counting"), OverlayControl.CountdownWords(cd, at, utc));

        var overlays = new OverlaySet();
        var countdown = new CountdownConfig();
        Assert.Equal("No overlays on.", OverlayControl.Line(overlays, countdown, at, utc));
        overlays.Clock.Enabled = true;
        overlays.Message.Enabled = true;
        overlays.Message.Text = "WELCOME";
        overlays.Message.Scroll = true;
        overlays.Logo.Enabled = true;
        countdown.Enabled = true;
        countdown.TargetKind = CountdownTargetKind.Duration;
        countdown.DurationMinutes = 15;
        countdown.ArmedAtUtc = utc.AddMinutes(-2).AddSeconds(-26);
        Assert.Equal("Clock 24 h with seconds and the date · Message: WELCOME (scrolling) · Countdown 12:34 to 15 min · Logo", OverlayControl.Line(overlays, countdown, at, utc));
        overlays.Pip.Enabled = true;
        overlays.Weather.Enabled = true;
        countdown.ArmedAtUtc = utc.AddMinutes(-20);
        Assert.EndsWith("Countdown over · Logo · PiP · Weather", OverlayControl.Line(overlays, countdown, at, utc));
    }
}
