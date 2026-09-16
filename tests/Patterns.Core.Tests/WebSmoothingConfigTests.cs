using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The smoothing choice rides the look to the page's mount (round 68).</summary>
public class WebSmoothingConfigTests
{
    [Fact]
    public void TheChoiceReachesTheWantedInputFromTheMediaPatternAndFromALayer()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Web;
        state.Pattern.Media.WebUrl = "https://www.youtube.com/embed/abc";
        Assert.Equal(WebSmoothing.Auto, state.Pattern.Media.WebSmoothing);
        state.Pattern.Media.WebSmoothing = WebSmoothing.Smooth;
        state.Pattern.Layer1.Enabled = true;
        state.Pattern.Layer1.Source = LayerSource.Web;
        state.Pattern.Layer1.WebUrl = "https://intranet/scoreboard";
        state.Pattern.Layer1.WebSmoothing = WebSmoothing.LowLatency;

        var wanted = MediaLocator.FindWantedInputs(new ShowSnapshot { State = state, Version = 1 }).Where(w => w.Kind == MediaLocator.WantedKind.Web).ToList();
        Assert.Equal(2, wanted.Count);
        Assert.Equal(WebSmoothing.Smooth, wanted.Single(w => w.Target.Contains("youtube")).Smoothing);
        Assert.Equal(WebSmoothing.LowLatency, wanted.Single(w => w.Target.Contains("scoreboard")).Smoothing);
    }
}
