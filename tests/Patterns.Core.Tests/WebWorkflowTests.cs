using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Web pages in the workflow: key chords as a browser reads them, the services' full-frame
/// addresses and their own actions, the WEB verbs on the wire and over OSC, and the cue action
/// through the spec, the checks, the summary and the sheet.
/// </summary>
public class WebWorkflowTests
{
    [Fact]
    public void ChordsBecomeKeyPressesAndBack()
    {
        Assert.True(WebKeys.TryParse("Ctrl+Shift+F5", out var f5));
        Assert.Equal(("F5", "F5", 116, "", WebKeyPress.Ctrl | WebKeyPress.Shift), (f5.Key, f5.Code, f5.VirtualKey, f5.Text, f5.Modifiers));
        Assert.Equal("Ctrl+Shift+F5", f5.Chord);
        Assert.True(WebKeys.TryParse("k", out var k));
        Assert.Equal(("k", "KeyK", 75, "k", 0), (k.Key, k.Code, k.VirtualKey, k.Text, k.Modifiers));
        Assert.True(WebKeys.TryParse("Shift+n", out var n));
        Assert.Equal(("N", "N", WebKeyPress.Shift), (n.Key, n.Text, n.Modifiers));
        Assert.Equal("Shift+N", WebKeys.Normalize("N"));                 // a capital is a shifted letter
        Assert.True(WebKeys.TryParse("Space", out var space));
        Assert.Equal((" ", "Space", 32, " "), (space.Key, space.Code, space.VirtualKey, space.Text));
        Assert.Equal("Space", space.Chord);
        Assert.True(WebKeys.TryParse("right", out var right));
        Assert.Equal("ArrowRight", right.Key);
        Assert.True(WebKeys.TryParse("Ctrl++", out var plus));
        // The plus key is Shift and the equals key on a US keyboard; a shortcut types nothing.
        Assert.Equal(("+", "Equal", WebKeyPress.Ctrl | WebKeyPress.Shift, ""), (plus.Key, plus.Code, plus.Modifiers, plus.Text));
        Assert.True(WebKeys.TryParse("!", out var bang));
        Assert.Equal(("!", "Digit1", WebKeyPress.Shift, "!"), (bang.Key, bang.Code, bang.Modifiers, bang.Text));
        Assert.True(WebKeys.TryParse("Shift+1", out var shifted));
        Assert.Equal("!", shifted.Key);
        Assert.True(WebKeys.TryParse("Enter", out var enter));
        Assert.Equal(("Enter", "\r", 13), (enter.Key, enter.Text, enter.VirtualKey));
        Assert.True(WebKeys.TryParse("esc", out var esc));
        Assert.Equal(("Escape", ""), (esc.Key, esc.Text));
        Assert.False(WebKeys.TryParse("next", out _));
        Assert.False(WebKeys.TryParse("Ctrl+", out _));
        Assert.False(WebKeys.TryParse("Bogus+k", out _));
        Assert.False(WebKeys.TryParse("", out _));
        Assert.Equal("", WebKeys.Normalize("dance"));
        Assert.Null(WebKeys.ForChar('é'));
        Assert.Equal("Shift+A", WebKeys.ForChar('A')!.Value.Chord);
        Assert.Equal(("?", "Slash", WebKeyPress.Shift), (WebKeys.ForChar('?')!.Value.Key, WebKeys.ForChar('?')!.Value.Code, WebKeys.ForChar('?')!.Value.Modifiers));
        Assert.Equal("Enter", WebKeys.ForChar('\n')!.Value.Key);
    }

    [Fact]
    public void TheServicesGetFullFrameAddressesAndTheirOwnActions()
    {
        Assert.Equal(PageService.YouTube, WebPresets.Detect("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=90"));
        Assert.Equal(PageService.YouTube, WebPresets.Detect("youtu.be/dQw4w9WgXcQ"));
        Assert.Equal(PageService.GoogleSlides, WebPresets.Detect("https://docs.google.com/presentation/d/1abc/edit#slide=id.p"));
        Assert.Equal(PageService.PowerPoint, WebPresets.Detect("https://contoso.sharepoint.com/sites/x/_layouts/15/Doc.aspx?sourcedoc=%7B1%7D&action=edit"));
        Assert.Equal(PageService.PowerPoint, WebPresets.Detect("https://onedrive.live.com/embed?resid=1&em=2"));
        Assert.Equal(PageService.Vimeo, WebPresets.Detect("https://vimeo.com/123456"));
        Assert.Equal(PageService.Page, WebPresets.Detect("https://example.com/schedule"));
        Assert.Equal(PageService.Page, WebPresets.Detect("C:\\show\\schedule.html"));
        Assert.Equal(PageService.Page, WebPresets.Detect(""));

        var tube = WebPresets.FullFrame("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1m30s&list=PL123");
        Assert.StartsWith("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?autoplay=1&controls=0", tube);
        Assert.Contains("&list=PL123", tube);
        Assert.Contains("&start=90", tube);
        Assert.Equal(tube, WebPresets.FullFrame(tube));                                   // idempotent
        Assert.False(WebPresets.CanFullFrame(tube));
        Assert.StartsWith("https://www.youtube-nocookie.com/embed/abc123XYZ_-?", WebPresets.FullFrame("https://youtu.be/abc123XYZ_-"));
        Assert.StartsWith("https://www.youtube-nocookie.com/embed/shorty?", WebPresets.FullFrame("https://www.youtube.com/shorts/shorty"));
        Assert.StartsWith("https://www.youtube-nocookie.com/embed/videoseries?autoplay=1", WebPresets.FullFrame("https://www.youtube.com/playlist?list=PL9"));
        Assert.Equal("https://www.youtube.com/", WebPresets.FullFrame("https://www.youtube.com/"));   // nothing to embed
        Assert.Equal("https://player.vimeo.com/video/123456?autoplay=1&controls=0&title=0&byline=0&portrait=0&h=abcdef12", WebPresets.FullFrame("https://vimeo.com/123456/abcdef12"));
        Assert.Equal("https://docs.google.com/presentation/d/e/2PACX-1/embed?start=false&loop=false&delayms=60000&rm=minimal",
            WebPresets.FullFrame("https://docs.google.com/presentation/d/e/2PACX-1/pub?start=false&loop=false&delayms=3000"));
        Assert.Equal("https://docs.google.com/presentation/d/1abc/present?slide=id.g5", WebPresets.FullFrame("https://docs.google.com/presentation/d/1abc/edit#slide=id.g5"));
        Assert.Equal("https://contoso.sharepoint.com/sites/x/_layouts/15/Doc.aspx?sourcedoc=%7B1%7D&action=embedview",
            WebPresets.FullFrame("https://contoso.sharepoint.com/sites/x/_layouts/15/Doc.aspx?sourcedoc=%7B1%7D&action=edit"));
        Assert.Equal("https://example.com/schedule", WebPresets.FullFrame("example.com/schedule"));
        Assert.True(WebPresets.CanFullFrame("https://www.youtube.com/watch?v=x"));
        Assert.False(WebPresets.CanFullFrame("example.com"));
        Assert.Contains("FULL FRAME", WebPresets.Note("https://www.youtube.com/watch?v=x"));
        Assert.Contains("player alone", WebPresets.Note(tube));
        Assert.Equal("", WebPresets.Note("example.com"));

        // Each service answers "next" and "play" its own way; an unknown page takes the generic keys.
        var slides = WebPresets.For(PageService.GoogleSlides);
        Assert.Equal("ArrowRight", slides.Find("next")!.Chord);
        Assert.Equal("Ctrl+Shift+F5", slides.Find("present")!.Chord);
        Assert.Equal("b", slides.Find("Black")!.Chord);
        Assert.Null(slides.Find("play"));
        var youtube = WebPresets.For("https://youtu.be/x");
        Assert.True(youtube.Find("play")!.IsScript);
        Assert.Contains("playVideo", youtube.Find("play")!.Script);
        Assert.Equal("c", youtube.Find("captions")!.Chord);
        Assert.Equal("F5", WebPresets.For(PageService.PowerPoint).Find("present")!.Chord);
        Assert.Equal("Space", WebPresets.For(PageService.Page).Find("play")!.Chord);
        Assert.Contains("next", WebPresets.AllActionIds);
        Assert.True(WebPresets.IsActionOrKey("next"));
        Assert.True(WebPresets.IsActionOrKey("Ctrl+Shift+F5"));
        Assert.True(WebPresets.IsActionOrKey("reload"));
        Assert.False(WebPresets.IsActionOrKey("dance"));
        Assert.Equal("next slide", WebPresets.LabelFor("next"));
        Assert.Equal("key Ctrl+Shift+F5", WebPresets.LabelFor("ctrl+shift+f5"));
        Assert.Equal("dance", WebPresets.LabelFor("dance"));

        Assert.True(WebPresets.TryParsePoint("50 50", out var x, out var y));
        Assert.Equal((50d, 50d), (x, y));
        Assert.True(WebPresets.TryParsePoint("0.25, 0.5", out x, out y));
        Assert.Equal((25d, 50d), (x, y));
        Assert.True(WebPresets.TryParsePoint("10%,90%", out x, out y));
        Assert.Equal((10d, 90d), (x, y));
        Assert.False(WebPresets.TryParsePoint("150 50", out _, out _));
        Assert.False(WebPresets.TryParsePoint("middle", out _, out _));

        // A page is named by its key, its address, its nickname, its host or a word of it.
        Assert.True(WebPresets.Matches("web:https://docs.google.com/presentation/d/1/present", "https://docs.google.com/presentation/d/1/present", "Deck", "deck"));
        Assert.True(WebPresets.Matches("web:https://docs.google.com/x", "https://docs.google.com/x", "", "docs.google.com"));
        Assert.True(WebPresets.Matches("web:https://docs.google.com/x", "https://docs.google.com/x", "", "google"));
        Assert.True(WebPresets.Matches("web:https://example.com", "https://example.com", "", "example.com"));
        Assert.False(WebPresets.Matches("web:https://example.com", "https://example.com", "", "youtube"));
        Assert.False(WebPresets.Matches("web:https://example.com", "https://example.com", "", ""));
    }

    [Fact]
    public void TheWireAndOscSpeakWeb()
    {
        var key = ControlProtocol.Parse("WEB KEY ArrowRight");
        Assert.Equal((RemoteCommandKind.WebKey, "ArrowRight", ""), (key.Kind, key.TextArg, key.Extra));
        var on = ControlProtocol.Parse("PAGE KEY Ctrl+Shift+F5 ON slides");
        Assert.Equal((RemoteCommandKind.WebKey, "Ctrl+Shift+F5", "slides"), (on.Kind, on.TextArg, on.Extra));
        var next = ControlProtocol.Parse("WEB NEXT");
        Assert.Equal((RemoteCommandKind.WebKey, "NEXT", ""), (next.Kind, next.TextArg, next.Extra));
        var play = ControlProtocol.Parse("web play on youtube");
        Assert.Equal((RemoteCommandKind.WebKey, "play", "youtube"), (play.Kind, play.TextArg, play.Extra));
        var click = ControlProtocol.Parse("WEB CLICK 50 50 ON schedule");
        Assert.Equal((RemoteCommandKind.WebClick, "50 50", "schedule"), (click.Kind, click.TextArg, click.Extra));
        var type = ControlProtocol.Parse("WEB TYPE hello on stage");
        Assert.Equal((RemoteCommandKind.WebType, "hello on stage", ""), (type.Kind, type.TextArg, type.Extra));
        Assert.Equal((RemoteCommandKind.WebReload, ""), (ControlProtocol.Parse("WEB RELOAD").Kind, ControlProtocol.Parse("WEB RELOAD").Extra));
        Assert.Equal("slides", ControlProtocol.Parse("WEB RELOAD ON slides").Extra);
        Assert.Equal("slides", ControlProtocol.Parse("WEB RELOAD slides").Extra);
        var open = ControlProtocol.Parse("WEB OPEN https://example.com/deck?x=1 ON schedule");
        Assert.Equal((RemoteCommandKind.WebOpen, "https://example.com/deck?x=1", "schedule"), (open.Kind, open.TextArg, open.Extra));
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("WEB").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("WEB KEY").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("WEB TYPE").Kind);

        Assert.Equal("WEB KEY next", OscMap.ToLine(OscMessage.Of("/patterns/web/key", "next")));
        Assert.Equal("WEB KEY ArrowRight ON slides", OscMap.ToLine(OscMessage.Of("/patterns/web/key/ArrowRight", "slides")));
        Assert.Equal("WEB KEY NEXT", OscMap.ToLine(OscMessage.Of("/patterns/web/next")));
        Assert.Equal("WEB KEY PRESENT ON deck", OscMap.ToLine(OscMessage.Of("/patterns/page/present", "deck")));
        Assert.Equal("WEB CLICK 50 50", OscMap.ToLine(OscMessage.Of("/patterns/web/click", 50, 50)));
        Assert.Equal("WEB CLICK 25 75", OscMap.ToLine(OscMessage.Of("/patterns/web/click", 0.25f, 0.75f)));
        Assert.Equal("WEB CLICK 10 20", OscMap.ToLine(OscMessage.Of("/patterns/web/click/10/20")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/web/click", 50)));
        Assert.Equal("WEB TYPE hello there", OscMap.ToLine(OscMessage.Of("/patterns/web/type", "hello there")));
        Assert.Equal("WEB RELOAD", OscMap.ToLine(OscMessage.Of("/patterns/web/reload")));
        Assert.Equal("WEB OPEN https://x.y/z ON deck", OscMap.ToLine(OscMessage.Of("/patterns/web/open", "https://x.y/z", "deck")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/web")));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/web/key"));
    }

    [Fact]
    public void ACueDrivesAPageThroughTheSpecTheChecksTheSummaryAndTheSheet()
    {
        Assert.Equal((TargetKind.Page, ValueKind.WebKey), CueActionSpec.For(CueActionKind.WebKey));
        Assert.Equal((TargetKind.Page, ValueKind.Point), CueActionSpec.For(CueActionKind.WebClick));
        Assert.Equal((TargetKind.Page, ValueKind.Text), CueActionSpec.For(CueActionKind.WebType));
        Assert.Equal((TargetKind.Page, ValueKind.None), CueActionSpec.For(CueActionKind.WebReload));
        Assert.Contains(CueActionKind.WebKey, CueActionSpec.Editable);
        Assert.Equal(CueActionKind.WebKey, CueSheet.ParseKind("page key"));
        Assert.Equal(CueActionKind.WebKey, CueSheet.ParseKind("Web page — key or action"));
        Assert.Equal(CueActionKind.WebClick, CueSheet.ParseKind("click"));
        Assert.Equal(CueActionKind.WebReload, CueSheet.ParseKind("reload"));

        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Web;
        state.Pattern.Media.WebUrl = "https://docs.google.com/presentation/d/e/2PACX/embed?rm=minimal";
        var stack = new CueStackConfig();
        var next = new RunCueConfig { Number = "01.010", Name = "Next slide" };
        next.Actions.Add(new CueActionConfig { Kind = CueActionKind.WebKey, Value = "next" });
        var wrongPage = new RunCueConfig { Number = "01.020", Name = "Wrong page" };
        wrongPage.Actions.Add(new CueActionConfig { Kind = CueActionKind.WebKey, Target = "youtube", Value = "play" });
        var badKey = new RunCueConfig { Number = "01.030", Name = "Bad key" };
        badKey.Actions.Add(new CueActionConfig { Kind = CueActionKind.WebKey, Value = "dance" });
        var badClick = new RunCueConfig { Number = "01.040", Name = "Bad click" };
        badClick.Actions.Add(new CueActionConfig { Kind = CueActionKind.WebClick, Value = "middle" });
        var byHost = new RunCueConfig { Number = "01.050", Name = "By host" };
        byHost.Actions.Add(new CueActionConfig { Kind = CueActionKind.WebKey, Target = "docs.google.com", Value = "ArrowRight" });
        var typed = new RunCueConfig { Number = "01.060", Name = "Type" };
        typed.Actions.Add(new CueActionConfig { Kind = CueActionKind.WebType, Value = "" });
        foreach (var cue in new[] { next, wrongPage, badKey, badClick, byHost, typed }) stack.Cues.Add(cue);

        var report = CueValidator.Validate(state, stack);
        Assert.False(report.IsBroken(next.Id));
        Assert.Contains("not in what will be on air", report.ReasonFor(wrongPage.Id));
        Assert.Contains("neither a key", report.ReasonFor(badKey.Id));
        Assert.Contains("x y", report.ReasonFor(badClick.Id));
        Assert.False(report.IsBroken(byHost.Id));
        Assert.False(report.IsBroken(typed.Id));
        Assert.Contains("nothing to type", report.Warnings[typed.Id]);

        // No page in the show: a soft note, never a broken cue — the page may be put on by hand before the cue.
        var bare = new ShowState();
        var bareStack = new CueStackConfig();
        var bareCue = new RunCueConfig { Number = "01.010", Name = "Next" };
        bareCue.Actions.Add(new CueActionConfig { Kind = CueActionKind.WebKey, Value = "next" });
        bareStack.Cues.Add(bareCue);
        var bareReport = CueValidator.Validate(bare, bareStack);
        Assert.False(bareReport.IsBroken(bareCue.Id));
        Assert.Contains("no web page is on air", bareReport.Warnings[bareCue.Id]);

        Assert.Equal("Page: next slide", CueSummary.DescribeAction(state, next.Actions[0]));
        Assert.Equal("Page: key ArrowRight → docs.google.com", CueSummary.DescribeAction(state, byHost.Actions[0]));
        Assert.Equal("Page: click at 50 50 → deck", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.WebClick, Target = "deck", Value = "50 50" }));
        Assert.Equal("Page: reload", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.WebReload }));
        Assert.Equal("Page: type 'hello'", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.WebType, Value = "hello" }));
    }
}
