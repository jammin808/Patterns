using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// What a page is treated as, and what CLEAN takes off it. The abstraction is the point: a
/// service is named once and FULL FRAME, the page's own actions and the furniture strip all
/// follow from it, so the next service is a row rather than a new code path.
/// </summary>
public class PageServiceTests
{
    [Fact]
    public void AutoReadsTheAddressAndAChoiceOverridesIt()
    {
        const string watch = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
        Assert.Equal(PageService.YouTube, WebPresets.Resolve(watch, PageServicePick.Auto));
        Assert.Equal(PageService.Page, WebPresets.Resolve("https://intranet.example.com/board", PageServicePick.Auto));

        // The address that does not say — a short link, a proxy, an embed on the client's own
        // hostname. Naming the service is the whole reason the choice exists.
        const string proxied = "https://video.acme-corp.example/embed/dQw4w9WgXcQ";
        Assert.Equal(PageService.Page, WebPresets.Resolve(proxied, PageServicePick.Auto));
        Assert.Equal(PageService.YouTube, WebPresets.Resolve(proxied, PageServicePick.YouTube));

        // And a YouTube link the operator wants as an ordinary page — the watch page, furniture
        // and all, when a video's owner blocks embedding.
        Assert.Equal(PageService.Page, WebPresets.Resolve(watch, PageServicePick.Page));
    }

    [Fact]
    public void TheChoiceReachesFullFrameAndTheActions()
    {
        const string watch = "https://www.youtube.com/watch?v=abc123";
        Assert.Contains("youtube-nocookie.com/embed/abc123", WebPresets.FullFrame(watch, PageServicePick.Auto));
        Assert.Equal(watch, WebPresets.FullFrame(watch, PageServicePick.Page));   // treated as a plain page: left alone
        Assert.False(WebPresets.CanFullFrame(watch, PageServicePick.Page));
        Assert.True(WebPresets.CanFullFrame(watch, PageServicePick.Auto));

        // The service's own actions come with the choice, so PLAY drives the player rather than
        // pressing the space bar and hoping.
        Assert.NotNull(WebPresets.For(watch, PageServicePick.YouTube).Find("restart"));
        Assert.Null(WebPresets.For(watch, PageServicePick.Page).Find("restart"));
    }

    [Fact]
    public void CleanTakesTheMediaBarOffAndIsOffUntilAsked()
    {
        const string url = "https://www.youtube-nocookie.com/embed/abc123?controls=0";

        // Opt-in: nothing changes for a show that never ticks it.
        Assert.Equal("", WebPresets.CleanCss(url, PageServicePick.Auto, clean: false));

        var css = WebPresets.CleanCss(url, PageServicePick.Auto, clean: true);
        Assert.Contains(".ytp-chrome-bottom", css);      // the media bar itself
        Assert.Contains(".ytp-watermark", css);          // the channel's mark
        Assert.Contains(".ytp-pause-overlay", css);      // what appears when it stops
        Assert.Contains(".ytp-ce-element", css);         // the end-screen cards
        Assert.Contains(".ytp-large-play-button", css);  // the big centre button
        Assert.Contains("overflow:hidden", css);         // and no scrollbar around any of it

        // Never the video: a strip that hid the picture would be worse than the furniture.
        Assert.DoesNotContain(".html5-main-video", css);
        Assert.DoesNotContain("video{display", css);
    }

    [Fact]
    public void EveryServiceHasAStripAndAPlainPageGetsTheResetOnly()
    {
        foreach (var pick in new[] { PageServicePick.YouTube, PageServicePick.Vimeo, PageServicePick.GoogleSlides, PageServicePick.PowerPoint, PageServicePick.Page })
        {
            var css = WebPresets.CleanCss("https://example.com", pick, clean: true);
            Assert.Contains("margin:0", css);
            Assert.NotEqual("", WebPresets.CleanNote("https://example.com", pick));
        }

        // A page nobody wrote for a wall gets the reset and nothing guessed at.
        var plain = WebPresets.CleanCss("https://intranet.example.com/board", PageServicePick.Auto, clean: true);
        Assert.Contains("cursor:none", plain);
        Assert.DoesNotContain("ytp", plain);
    }

    [Fact]
    public void ThePageTheEngineOpensCarriesItsStrip()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Web;
        state.Pattern.Media.WebUrl = "https://www.youtube.com/watch?v=abc123";
        state.Pattern.Media.WebClean = true;

        state.Pattern.Layer1.Enabled = true;
        state.Pattern.Layer1.Source = LayerSource.Web;
        state.Pattern.Layer1.WebUrl = "https://intranet.example.com/board";
        state.Pattern.Layer1.WebClean = false;

        var snap = new ShowSnapshot { State = state, Version = 1 };
        var wanted = MediaLocator.FindWantedInputs(snap).Where(w => w.Kind == MediaLocator.WantedKind.Web).ToList();
        Assert.Equal(2, wanted.Count);

        var video = wanted.First(w => w.Target.Contains("youtube"));
        Assert.Contains(".ytp-chrome-bottom", video.Clean);

        var board = wanted.First(w => w.Target.Contains("intranet"));
        Assert.Equal("", board.Clean);   // the layer never asked
    }
}
