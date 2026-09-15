using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Round 63: how overlays and layers arrive and leave is a setting of the show — a fade by default, kept in the file, never a crossfade of the whole picture.</summary>
public class AppearanceConfigTests
{
    [Fact]
    public void TheDefaultIsAFadeOverTheShowsTransitionTimeAndTheFileKeepsAChoice()
    {
        var state = new ShowState();
        Assert.Equal(AppearKind.Fade, state.Overlays.Appear.Kind);
        Assert.Equal(0, state.Overlays.Appear.DurationMs);          // the show's transition time
        Assert.Equal(AppearKind.Fade, state.Pattern.Layer1.Appear.Kind);
        Assert.Equal(AppearKind.Fade, state.Pattern.Layer2.Appear.Kind);

        state.Overlays.Appear.Kind = AppearKind.Slide;
        state.Overlays.Appear.DurationMs = 9000;
        Assert.Equal(3000, state.Overlays.Appear.DurationMs);       // clamped
        state.Overlays.Appear.DurationMs = double.NaN;
        Assert.Equal(0, state.Overlays.Appear.DurationMs);
        state.Overlays.Appear.DurationMs = 250;
        state.Pattern.Layer2.Appear.Kind = AppearKind.Cut;

        var back = JsonUtil.Deserialize<ShowState>(JsonUtil.Serialize(state))!;
        Assert.Equal(AppearKind.Slide, back.Overlays.Appear.Kind);
        Assert.Equal(250, back.Overlays.Appear.DurationMs);
        Assert.Equal(AppearKind.Cut, back.Pattern.Layer2.Appear.Kind);

        // An older show file has no such member: the fade, as the default says.
        Assert.Equal(AppearKind.Fade, JsonUtil.Deserialize<ShowState>("{}")!.Overlays.Appear.Kind);
    }
}
