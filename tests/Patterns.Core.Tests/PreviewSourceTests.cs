using Patterns.Core.Menus;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 63: "right-click context menus should also be on the Preview. It should offer options to
/// change source." The preview menu's SOURCE group — what a media picture shows, and a still or
/// clip of the library straight in — as edits on the picture being built.
/// </summary>
public class PreviewSourceTests
{
    [Fact]
    public void TheSourceEditsMakeThePreviewAMediaPictureOfThatSourceOrThatLibraryEntry()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Grid;
        var facts = new DeskFacts();
        var words = PreviewEdits.Apply(state, state.Pattern, "media.source:Web", DateTime.UtcNow, facts);
        Assert.Equal(PatternKind.Media, state.Pattern.Kind);
        Assert.Equal(MediaSource.Web, state.Pattern.Media.Source);
        Assert.Contains("a web page", words);

        state.MediaLibrary.Add(new MediaLibraryEntry { Id = "m1", Name = "Sponsor reel", Path = "/shows/sponsor.mp4", IsVideo = true });
        state.MediaLibrary.Add(new MediaLibraryEntry { Id = "m2", Path = "/shows/hold.png" });
        Assert.Contains("Sponsor reel", PreviewEdits.Apply(state, state.Pattern, "media.pick:m1", DateTime.UtcNow, facts));
        Assert.Equal(MediaSource.Video, state.Pattern.Media.Source);
        Assert.Equal("/shows/sponsor.mp4", state.Pattern.Media.VideoPath);
        Assert.Contains("hold.png", PreviewEdits.Apply(state, state.Pattern, "media.pick:m2", DateTime.UtcNow, facts));
        Assert.Equal(MediaSource.Image, state.Pattern.Media.Source);
        Assert.Equal("/shows/hold.png", state.Pattern.Media.ImagePath);
        Assert.Contains("no longer", PreviewEdits.Apply(state, state.Pattern, "media.pick:gone", DateTime.UtcNow, facts));
        Assert.Null(PreviewEdits.Apply(state, state.Pattern, "media.source:Hologram", DateTime.UtcNow, facts));
    }

    [Fact]
    public void ThePreviewMenuLeadsWithSourceAndMarksTheOneItShows()
    {
        var facts = new DeskFacts
        {
            SandboxOpen = true,
            PreviewSource = "Web",
            Media = new[] { new MenuMedia("m1", "Sponsor reel", true) },
        };
        var menu = DeskMenus.Preview(facts);
        Assert.Equal("SOURCE", menu.Groups[0].Heading);
        var source = Assert.Single(menu.Groups[0].Entries, e => e.Id == "media.source");
        Assert.Contains("a web page", source.Text);
        Assert.Equal(Enum.GetValues<MediaSource>().Length, source.Children.Count);
        Assert.True(source.Children.Single(c => c.Id == "media.source:Web").IsOn);
        Assert.False(source.Children.Single(c => c.Id == "media.source:Video").IsOn);
        Assert.All(source.Children, c => Assert.Equal(MenuScope.Preview, c.Scope));
        var pick = Assert.Single(menu.Groups[0].Entries, e => e.Id == "media.pick");
        Assert.Equal("media.pick:m1", Assert.Single(pick.Children).Edit);

        // Not a media picture: the drawer is plain, nothing marked; an empty library says why.
        var plain = DeskMenus.Preview(new DeskFacts { SandboxOpen = true });
        var drawer = plain.Groups[0].Entries.Single(e => e.Id == "media.source");
        Assert.Equal("Media source", drawer.Text);
        Assert.DoesNotContain(drawer.Children, c => c.IsOn);
        Assert.Contains("library is empty", plain.Groups[0].Entries.Single(e => e.Id == "media.pick").Because);
    }
}
