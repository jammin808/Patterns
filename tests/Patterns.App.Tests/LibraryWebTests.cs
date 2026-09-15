using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 62: the Library holds everything the show can put up. The saved web pages — a YouTube
/// link, a schedule — were a combo box on the Media page and nowhere else, and the decks (PDFs)
/// had tiles but no chip of their own; now a saved address is a tile in the Web section, grouped
/// by what it is, with the service's colours, APPLY putting the page on the picture and ✕
/// forgetting the address, and Decks is a chip beside Images and Videos.
/// </summary>
public class LibraryWebTests
{
    [AvaloniaFact]
    public void SavedPagesAreTilesOfTheWebSectionAndDecksHaveTheirChip()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            Assert.Contains("Web", vm.LibrarySections);
            Assert.Contains("Decks", vm.LibrarySections);

            vm.State.Web.SavedUrls.Add("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
            vm.State.Web.SavedUrls.Add("https://example.com/schedule/today");
            vm.State.MediaLibrary.Add(new MediaLibraryEntry { Id = "d1", Path = "C:/shows/keynote.pdf", Kind = LibraryMediaKind.Deck });
            vm.RefreshLibrary();
            Dispatcher.UIThread.RunJobs();

            var tube = Assert.Single(vm.LibraryAll, t => t.Id == "web:https://www.youtube.com/watch?v=dQw4w9WgXcQ");
            Assert.Equal("Web", tube.Section);
            Assert.Equal("YouTube", tube.Category);
            Assert.Equal("youtube.com · watch?v=dQw4w9WgXcQ", tube.Name);
            Assert.NotNull(tube.Swatch);
            Assert.True(tube.CanRemove);
            var page = Assert.Single(vm.LibraryAll, t => t.Id == "web:https://example.com/schedule/today");
            Assert.Equal("Saved pages", page.Category);
            Assert.Equal("example.com · schedule/today", page.Name);
            var deck = Assert.Single(vm.LibraryAll, t => t.Id == "media:d1");
            Assert.Equal("Decks", deck.Section);

            // The Web chip shows the pages alone; the Decks chip the deck alone.
            vm.SelectedLibrarySection = "Web";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, vm.Library.Count);
            Assert.All(vm.Library, t => Assert.Equal("Web", t.Section));
            vm.SelectedLibrarySection = "Decks";
            Dispatcher.UIThread.RunJobs();
            Assert.Single(vm.Library, t => t.Id == "media:d1");

            // APPLY: the page on the picture being edited, treated as its address says.
            tube.Apply();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Media, vm.ActivePattern.Kind);
            Assert.Equal(MediaSource.Web, vm.ActivePattern.Media.Source);
            Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", vm.ActivePattern.Media.WebUrl);
            Assert.Equal(PageServicePick.Auto, vm.ActivePattern.Media.WebService);

            // ✕ forgets the address, and the tile goes with it.
            vm.RemoveLibraryItemCommand.Execute(page);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("https://example.com/schedule/today", vm.State.Web.SavedUrls);
            Assert.DoesNotContain(vm.LibraryAll, t => t.Id == "web:https://example.com/schedule/today");
        }
        finally
        {
            b.Dispose();
        }
    }

    [Fact]
    public void AWebTileIsNamedGroupedAndColouredByWhatTheAddressIs()
    {
        Assert.Equal("YouTube", LibraryCatalogue.WebCategory(WebPresets.Detect("https://youtu.be/abc123")));
        Assert.Equal("Vimeo", LibraryCatalogue.WebCategory(WebPresets.Detect("https://vimeo.com/12345")));
        Assert.Equal("Saved pages", LibraryCatalogue.WebCategory(WebPresets.Detect("https://example.com")));
        Assert.Equal("example.com", LibraryCatalogue.WebTitle("https://example.com/"));
        Assert.Equal("example.com · a/very/long/path/that/goes/…", LibraryCatalogue.WebTitle("https://www.example.com/a/very/long/path/that/goes/on/and/on"));
        Assert.NotEqual(LibraryCatalogue.WebSwatch(PageService.YouTube), LibraryCatalogue.WebSwatch(PageService.Page));

        var target = new PatternConfig();
        LibraryCatalogue.ApplyWeb(target, "example.com/live");
        Assert.Equal(PatternKind.Media, target.Kind);
        Assert.Equal(MediaSource.Web, target.Media.Source);
        Assert.Equal(WebAddress.Normalize("example.com/live"), target.Media.WebUrl);
    }
}
