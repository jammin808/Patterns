using Patterns.Core.Media;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The script that fades a page's sound as it leaves the programme (round 68).</summary>
public class WebLeavingTests
{
    [Fact]
    public void TheFadeRampsEveryMediaElementOverTheTransitionThenMutesIt()
    {
        var script = WebLeaving.FadeScript(0.8);
        Assert.Contains("var d=800;", script);
        Assert.Contains("querySelectorAll('video,audio')", script);
        Assert.Contains("els[i].volume=v0[i]*(1-k)", script);
        Assert.Contains("els[i].muted=true", script);
        Assert.StartsWith("(function(){try{", script);
        Assert.EndsWith("})()", script);
    }

    [Fact]
    public void TheFadeIsClampedToSomethingSensible()
    {
        Assert.Contains("var d=5000;", WebLeaving.FadeScript(60));
        Assert.Contains("var d=10;", WebLeaving.FadeScript(0));
        Assert.Contains("var d=10;", WebLeaving.FadeScript(double.NaN));
        Assert.Contains("var d=250;", WebLeaving.FadeScript(0.25));
    }

    [Fact]
    public void TheFadeKeepsEachElementsOriginalAndTheRestorePutsOnlyWhatTheFadeDidBack()
    {
        var fade = WebLeaving.FadeScript(1.5);
        Assert.Contains("window.__pvFade=g", fade);                              // a generation: the next fade or a restore cancels this one
        Assert.Contains("if(window.__pvFade!==g)return;", fade);
        Assert.Contains("if(e.__pv0===undefined)e.__pv0=e.volume;return e.__pv0;", fade);   // the original kept once, not the mid-ramp value
        Assert.Contains("els[i].muted=true;els[i].__pvm=true;", fade);           // what the fade muted is marked

        var restore = WebLeaving.RestoreScript;
        Assert.StartsWith("(function(){try{window.__pvFade=(window.__pvFade||0)+1;", restore);
        Assert.Contains("if(e.__pv0!==undefined){e.volume=e.__pv0;delete e.__pv0;n++;}", restore);
        Assert.Contains("if(e.__pvm){e.muted=false;e.__pvm=false;}", restore);   // an element the page itself muted stays muted
        Assert.EndsWith("})()", restore);
    }
}
