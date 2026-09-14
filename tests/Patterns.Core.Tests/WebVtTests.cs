using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The armed web VT's pure parts: the times, the scripts, the reading back, the rule, the words, the looks, the verbs.</summary>
public class WebVtTests
{
    [Theory]
    [InlineData("83", 83)]
    [InlineData("1:23", 83)]
    [InlineData("1:02:03", 3723)]
    [InlineData("1m23s", 83)]
    [InlineData("90s", 90)]
    [InlineData("2m", 120)]
    [InlineData("1h", 3600)]
    [InlineData("0", 0)]
    [InlineData("start", 0)]
    [InlineData(" 12.5 ", 12.5)]
    public void TimesAreReadInEveryWayAnOperatorWritesThem(string text, double seconds)
    {
        Assert.True(WebVt.TryParseTime(text, out var parsed));
        Assert.Equal(seconds, parsed, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("1:2:3:4")]
    [InlineData("1m30")]
    [InlineData("off")]
    public void WhatIsNotATimeIsRefused(string text)
    {
        Assert.False(WebVt.TryParseTime(text, out _));
    }

    [Fact]
    public void TimeTextWritesMinutesAndHoursTheWayTheDeskReadsThem()
    {
        Assert.Equal("0:00", WebVt.TimeText(0));
        Assert.Equal("1:23", WebVt.TimeText(83.9));
        Assert.Equal("1:02:03", WebVt.TimeText(3723));
        Assert.Equal("0:00", WebVt.TimeText(-4));
    }

    [Fact]
    public void AValueIsEmptyATimeOrOff()
    {
        Assert.True(WebVt.IsValidValue(""));
        Assert.True(WebVt.IsValidValue("1:23"));
        Assert.True(WebVt.IsValidValue("off"));
        Assert.True(WebVt.IsOff("DISARM"));
        Assert.False(WebVt.IsValidValue("later"));
    }

    [Fact]
    public void TheScriptsDriveYouTubeThroughItsPlayerAndAnyOtherPageThroughItsVideoElement()
    {
        var tube = WebVt.PrepareScript(PageService.YouTube, 83);
        Assert.Contains("movie_player", tube);
        Assert.Contains("seekTo(83,true)", tube);
        Assert.Contains("pauseVideo()", tube);
        var play = WebVt.PlayFromScript(PageService.YouTube, 83.5);
        Assert.Contains("seekTo(83.5,true)", play);
        Assert.Contains("playVideo()", play);
        Assert.Contains("ad-showing", WebVt.StateScript(PageService.YouTube));
        Assert.Contains("ytp-skip-ad-button", WebVt.SkipAdScript(PageService.YouTube));

        var page = WebVt.PlayFromScript(PageService.Page, 10);
        Assert.Contains("querySelector('video')", page);
        Assert.Contains("v.currentTime=10", page);
        Assert.Contains("v.play()", page);
        Assert.Contains("v.pause()", WebVt.PrepareScript(PageService.Vimeo, 0));
        Assert.Equal("0", WebVt.SkipAdScript(PageService.Page));
        // A decimal mark is written with a point whatever the desk's culture.
        Assert.DoesNotContain(",", WebVt.PrepareScript(PageService.Page, 1.5));
    }

    [Fact]
    public void TheReadingComesBackDoublyEncodedOrPlainAndNothingForNoPlayer()
    {
        var inner = "{\"ok\":true,\"t\":83.2,\"d\":300,\"paused\":true,\"ad\":false,\"muted\":true}";
        var doubly = System.Text.Json.JsonSerializer.Serialize(inner);
        var reading = WebVt.ParseReading(doubly);
        Assert.True(reading.Ok);
        Assert.Equal(83.2, reading.Position, 3);
        Assert.Equal(300, reading.Duration);
        Assert.True(reading.Paused);
        Assert.True(reading.Muted);
        Assert.Equal("1:23 / 5:00", reading.ClockText);

        Assert.True(WebVt.ParseReading(inner).Ok);
        Assert.False(WebVt.ParseReading("null").Ok);
        Assert.False(WebVt.ParseReading("\"null\"").Ok);
        Assert.False(WebVt.ParseReading("").Ok);
        Assert.False(WebVt.ParseReading("not json").Ok);
        Assert.False(WebVt.ParseReading("{\"ok\":false}").Ok);
        Assert.Equal("", WebPlayerReading.None.ClockText);
    }

    [Fact]
    public void TheArmFiresOnceOnTheWayToAirAndKeepsItsMark()
    {
        var now = new DateTime(2026, 9, 14, 19, 0, 0, DateTimeKind.Utc);
        var arm = WebArm.None.ArmedBy(false, 83, now);
        Assert.True(arm.Armed);
        Assert.False(WebVt.ShouldFire(arm, wasOnAir: false, isOnAir: false));
        Assert.False(WebVt.ShouldFire(arm, wasOnAir: true, isOnAir: true));
        Assert.True(WebVt.ShouldFire(arm, wasOnAir: false, isOnAir: true));
        var fired = arm.Fired(now.AddSeconds(30));
        Assert.False(fired.Armed);
        Assert.Equal(83, fired.PlayedFrom);
        Assert.NotNull(fired.PlayedUtc);
        Assert.False(WebVt.ShouldFire(fired, false, true));
        // Armed again with no time named: the mark it had.
        Assert.Equal(83, fired.ArmedBy(false, fired.StartSeconds, now).StartSeconds);
        Assert.False(fired.Cleared().Armed);
    }

    [Fact]
    public void TheWordsSayArmedPlayedTheClockAndAnAdvert()
    {
        var now = new DateTime(2026, 9, 14, 19, 0, 0, DateTimeKind.Utc);
        var arm = WebArm.None.ArmedBy(true, 83, now);
        var reading = new WebPlayerReading(true, 83, 300, true, false, false);
        var words = WebVt.Words(arm, reading, now);
        Assert.Contains("ARMED at 1:23 by the look", words);
        Assert.Contains("plays when it goes to air", words);
        Assert.Contains("1:23 / 5:00 · paused", words);
        Assert.Equal("VT armed at 1:23", WebVt.ShortWords(arm, reading));

        var played = arm.Fired(now.AddSeconds(-12));
        var playing = new WebPlayerReading(true, 95, 300, false, true, false);
        var later = WebVt.Words(played, playing, now);
        Assert.Contains("played from 1:23 12 s ago", later);
        Assert.Contains("ADVERT showing", later);
        Assert.Contains("1:35 / 5:00 · playing", later);
        Assert.Equal("VT playing 1:35 / 5:00", WebVt.ShortWords(played, playing));
        Assert.Equal("", WebVt.Words(WebArm.None, WebPlayerReading.None, now));
        Assert.Equal("", WebVt.ShortWords(WebArm.None, WebPlayerReading.None));
    }

    [Fact]
    public void AStartPointGoesIntoAYouTubeOrVimeoAddressAndNowhereElse()
    {
        Assert.Equal("https://www.youtube-nocookie.com/embed/abc?autoplay=1&start=83", WebVt.AddressWithStart("https://www.youtube-nocookie.com/embed/abc?autoplay=1", PageService.YouTube, 83.7));
        Assert.Equal("https://www.youtube-nocookie.com/embed/abc?start=5", WebVt.AddressWithStart("https://www.youtube-nocookie.com/embed/abc?start=5", PageService.YouTube, 83));
        Assert.Equal("https://player.vimeo.com/video/1?autoplay=1#t=83s", WebVt.AddressWithStart("https://player.vimeo.com/video/1?autoplay=1", PageService.Vimeo, 83));
        Assert.Equal("https://example.com/video", WebVt.AddressWithStart("https://example.com/video", PageService.Page, 83));
        Assert.Equal("https://www.youtube.com/embed/abc", WebVt.AddressWithStart("https://www.youtube.com/embed/abc", PageService.YouTube, 0));
    }

    [Fact]
    public void ALookCarriesItsArmedPagesAndThePreRollFindsThemOnTheCuesAhead()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Web;
        state.Pattern.Media.WebUrl = "https://www.youtube-nocookie.com/embed/abc?autoplay=1";
        state.Pattern.Media.WebAutoPlay = true;
        state.Pattern.Media.WebStartSeconds = 83;
        state.Pattern.Layer1.Enabled = true;
        state.Pattern.Layer1.Source = LayerSource.Web;
        state.Pattern.Layer1.WebUrl = "https://example.com/dashboard";   // no ask: a live page, never opened early
        var json = LookService.Capture(state);

        var pages = WebVt.AutoPlayPagesIn(json);
        var page = Assert.Single(pages);
        Assert.Equal("web:https://www.youtube-nocookie.com/embed/abc?autoplay=1", page.Key);
        Assert.Equal(PageService.YouTube, page.Service);
        Assert.Equal(83, page.StartSeconds);
        Assert.Equal("1920x1080", page.Format);
        Assert.Empty(WebVt.AutoPlayPagesIn(""));
        Assert.Empty(WebVt.AutoPlayPagesIn("not json"));

        state.LooksAndCues.Looks.Add(new LookConfig { Name = "Sponsor", Json = json });
        var cue = new RunCueConfig { Name = "Sponsor VT" };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ApplyLook, Target = "Sponsor" });
        var ahead = PreRoll.WebPagesFor(state, (RunCueConfig?)null, cue, cue);
        Assert.Single(ahead);   // the same page on two cues ahead is one page to open
        Assert.Empty(PreRoll.WebPagesFor(state, (RunCueConfig?)null));
    }

    [Fact]
    public void TheWireAndOscSpellTheVerbsAndTheChecksReadThem()
    {
        var arm = ControlProtocol.Parse("WEB ARM 1:23 ON sponsor");
        Assert.Equal(ShowActionKind.WebArm, arm.Action.Kind);
        Assert.Equal("1:23", arm.Action.Value);
        Assert.Equal("sponsor", arm.Action.Target);
        Assert.Equal(ShowActionKind.WebArm, ControlProtocol.Parse("WEB ARM").Action.Kind);
        Assert.Equal("", ControlProtocol.Parse("WEB ARM").Action.Value);
        Assert.Equal("off", ControlProtocol.Parse("WEB DISARM").Action.Value);
        Assert.Equal("youtube", ControlProtocol.Parse("WEB DISARM ON youtube").Action.Target);
        Assert.Equal(ShowActionKind.WebMark, ControlProtocol.Parse("PAGE MARK 90").Action.Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("WEB ARM later").Kind);

        Assert.Equal("WEB ARM 1:23 ON sponsor", OscMap.ToLine(OscMessage.Of("/patterns/web/arm/1:23", "sponsor")));
        Assert.Equal("WEB ARM", OscMap.ToLine(OscMessage.Of("/patterns/web/arm")));
        Assert.Equal("WEB MARK", OscMap.ToLine(OscMessage.Of("/patterns/web/mark")));
        Assert.Equal("WEB DISARM ON tube", OscMap.ToLine(OscMessage.Of("/patterns/web/disarm", "tube")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/web/arm/soon")));

        Assert.Equal("Page: arm the video at 1:23", CueSummary.DescribeAction(new ShowState(), new CueActionConfig { Kind = ShowActionKind.WebArm, Value = "83" }));
        Assert.Equal("Page: disarm the video", CueSummary.DescribeAction(new ShowState(), new CueActionConfig { Kind = ShowActionKind.WebArm, Value = "off" }));
        Assert.Equal("Page: mark where the player is", CueSummary.DescribeAction(new ShowState(), new CueActionConfig { Kind = ShowActionKind.WebMark }));
        Assert.Equal((TargetKind.Page, ValueKind.VideoTime), ActionSpec.For(ShowActionKind.WebArm));

        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Web;
        state.Pattern.Media.WebUrl = "https://www.youtube.com/watch?v=abc";
        var cue = new RunCueConfig { Name = "VT" };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.WebArm, Value = "later" });
        var check = CueValidator.ValidateOne(state, cue, null);
        Assert.Equal(1, check.BrokenCount);
        Assert.Contains("not a point in a video", check.ReasonFor(cue.Id));
        cue.Actions[0].Value = "1:23";
        Assert.Equal(0, CueValidator.ValidateOne(state, cue, null).BrokenCount);
    }
}
