using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>What tells a clip left on the screens from the show: the library's own files, by path.</summary>
public class StingerLifecycleTests
{
    [Fact]
    public void TheLibraryKnowsItsOwnClipOnAir()
    {
        var state = new ShowState();
        var clip = new StingerItemConfig { Path = @"C:\shows\stings\Opening.MP4", Name = "Opening" };
        var sound = new StingerItemConfig { Path = @"C:\shows\stings\ding.wav", Name = "Ding" };
        var pulse = new StingerItemConfig { Source = StingerSource.EffectPulse, Name = "Flash" };
        state.Stingers.Items.Add(clip);
        state.Stingers.Items.Add(sound);
        state.Stingers.Items.Add(pulse);

        // The show's own picture is never a clip on air.
        state.Pattern.Kind = PatternKind.Grid;
        Assert.Null(StingerLibrary.ClipOnAir(state));
        Assert.False(StingerLibrary.IsClipOnAir(state));

        // The library clip as the program, by path, case-blind.
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Video;
        state.Pattern.Media.VideoPath = @"c:\shows\stings\opening.mp4";
        Assert.Same(clip, StingerLibrary.ClipOnAir(state));

        // A video of the show's own, a still, a sound: not clips on air.
        state.Pattern.Media.VideoPath = @"C:\shows\walk-in.mp4";
        Assert.Null(StingerLibrary.ClipOnAir(state));
        state.Pattern.Media.VideoPath = clip.Path;
        state.Pattern.Media.Source = MediaSource.Image;
        Assert.Null(StingerLibrary.ClipOnAir(state));
        Assert.Null(StingerLibrary.ClipFor(state, sound.Path));
        Assert.Null(StingerLibrary.ClipFor(state, ""));
        Assert.Null(StingerLibrary.ClipFor(state, null));
    }

    [Fact]
    public void ACapturedLookIsReadForAClip()
    {
        var state = new ShowState();
        var clip = new StingerItemConfig { Path = @"C:\shows\stings\Opening.mp4", Name = "Opening" };
        state.Stingers.Items.Add(clip);

        var grid = new ShowState();
        grid.Pattern.Kind = PatternKind.Grid;
        Assert.False(StingerLibrary.IsClipLook(state, LookService.Capture(grid)));

        var onClip = new ShowState();
        onClip.Pattern.Kind = PatternKind.Media;
        onClip.Pattern.Media.Source = MediaSource.Video;
        onClip.Pattern.Media.VideoPath = clip.Path;
        Assert.True(StingerLibrary.IsClipLook(state, LookService.Capture(onClip)));

        // A clip the show does not have, junk, nothing: not a clip look.
        onClip.Pattern.Media.VideoPath = @"C:\elsewhere\other.mp4";
        Assert.False(StingerLibrary.IsClipLook(state, LookService.Capture(onClip)));
        Assert.False(StingerLibrary.IsClipLook(state, "{ not json"));
        Assert.False(StingerLibrary.IsClipLook(state, ""));
        Assert.False(StingerLibrary.IsClipLook(state, null));
    }
}
