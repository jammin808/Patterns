using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 62: "show the pointer and clicks on the page" is the desk's own switch — off by default,
/// and once set it stays set. It used to be a setting of each picture (the media pattern's and each
/// layer's, on by default), so every fresh target, preset, look and TAKE brought it back on; now it
/// lives on the show's Web section, where no look, preset or picture copy can reach it.
/// </summary>
public class WebPointerSettingTests
{
    [Fact]
    public void ThePointerIsOffByDefaultAndIsTheDesksNotThePictures()
    {
        var state = new ShowState();
        Assert.False(state.Web.ShowPointer);

        // Neither the media pattern nor a layer carries a pointer switch of its own any more.
        Assert.Null(typeof(MediaOptions).GetProperty("WebShowPointer"));
        Assert.Null(typeof(LayerConfig).GetProperty("WebShowPointer"));
    }

    [Fact]
    public void OnceSetNoLookPresetOrPictureCopyReArmsOrResetsIt()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Web;
        state.Pattern.Media.WebUrl = "https://example.com/schedule";

        // A look captured while the pointer was on, applied to a desk that turned it off: it stays off.
        state.Web.ShowPointer = true;
        var lookOn = LookService.Capture(state);
        state.Web.ShowPointer = false;
        Assert.True(LookService.Apply(lookOn, state));
        Assert.False(state.Web.ShowPointer);

        // A look captured while it was off, applied to a desk that turned it on: it stays on.
        var lookOff = LookService.Capture(state);
        state.Web.ShowPointer = true;
        Assert.True(LookService.Apply(lookOff, state));
        Assert.True(state.Web.ShowPointer);

        // A copy of the picture (a preset, a TAKE, a target switch all copy pictures) carries no pointer.
        var copy = JsonUtil.ClonePattern(state.Pattern);
        Assert.DoesNotContain("ShowPointer", JsonUtil.Serialize(copy));

        // And the show file keeps the desk's choice.
        Assert.True(JsonUtil.Clone(state).Web.ShowPointer);
    }
}
