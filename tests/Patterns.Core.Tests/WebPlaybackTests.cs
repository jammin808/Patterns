using Patterns.Core.Media;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 76: a YouTube page's player is steered to H.264, the codec every card decodes in hardware,
/// by telling the page the browser cannot play VP9 or AV1 before its scripts run; and a page's sound
/// route is asked for its answer with patience rather than read once at a timer — a busy page that
/// had not answered in a second and a half used to keep its sound held for good.
/// </summary>
public class WebPlaybackTests
{
    [Fact]
    public void ThePlayerIsToldTheBrowserCannotPlayVp9OrAv1BeforeItsScriptsRun()
    {
        var script = WebPlayback.PreferH264Script;
        Assert.Contains("vp9", WebPlayback.BlockedCodecs, StringComparison.Ordinal);
        Assert.Contains("av01", WebPlayback.BlockedCodecs, StringComparison.Ordinal);
        Assert.DoesNotContain("avc1", WebPlayback.BlockedCodecs, StringComparison.Ordinal);       // H.264 is what it picks
        Assert.Contains("ms.isTypeSupported=function(t){return block.test(String(t))?false:o(t);}", script, StringComparison.Ordinal);
        Assert.Contains("p.canPlayType=function(t){return block.test(String(t))?'':c.call(this,t);}", script, StringComparison.Ordinal);
        Assert.Contains("window.__patternsPreferH264=true", script, StringComparison.Ordinal);
        Assert.StartsWith("(function(){try{", script, StringComparison.Ordinal);                       // a page without either object is left alone
        Assert.EndsWith("catch(x){}})()", script, StringComparison.Ordinal);
        Assert.Equal("H.264 preferred", WebPlayback.PreferH264Words);
    }

    [Fact]
    public void ARoutesAnswerIsAwaitedWithPatienceAndAnEarlyFailureIsAskedAgain()
    {
        var soon = TimeSpan.FromSeconds(1);
        var late = WebAudioRoute.AnswerPatience;
        Assert.True(WebAudioRoute.KeepAsking(WebRouteOutcome.Pending, "nothing answered yet", soon));
        Assert.False(WebAudioRoute.KeepAsking(WebRouteOutcome.Pending, "nothing answered yet", late));    // the patience ran out: held, and said
        Assert.True(WebAudioRoute.KeepAsking(WebRouteOutcome.Failed, "not routed: the page cannot see any output (no permission)", soon));
        Assert.False(WebAudioRoute.KeepAsking(WebRouteOutcome.Failed, "not routed: no output called HDMI 3 among 2", soon));   // time will not mend it
        Assert.False(WebAudioRoute.KeepAsking(WebRouteOutcome.Failed, "not routed: this page cannot pick outputs", soon));
        Assert.False(WebAudioRoute.KeepAsking(WebRouteOutcome.Routed, "routed to HDMI 3 (1 player)", soon));
        Assert.False(WebAudioRoute.KeepAsking(WebRouteOutcome.Default, "the machine's default output", soon));
        Assert.False(WebAudioRoute.KeepAsking(WebRouteOutcome.NothingAsked, "", soon));
        Assert.True(WebAudioRoute.IsEarlyFailure("not routed: the page cannot see any output (no permission)"));
        Assert.False(WebAudioRoute.IsEarlyFailure("not routed: setSinkId failed"));
        Assert.True(WebAudioRoute.AskEvery < TimeSpan.FromSeconds(1));
        Assert.True(WebAudioRoute.AnswerPatience >= TimeSpan.FromSeconds(5));
    }
}
