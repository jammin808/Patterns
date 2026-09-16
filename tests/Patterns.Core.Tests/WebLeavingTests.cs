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
}
