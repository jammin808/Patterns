using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The Library's pipeline: one file picked draws one thumbnail and every other tile keeps its
/// instance and its picture; two builds in a row are one pass over the tiles; a thumbnail draws
/// over the published show and shares what it does not write.
/// </summary>
public class LibraryPipelineTests
{
    private static void Pump(Task task, int timeoutMs = 30000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("pumped task timed out");
        }
        task.GetAwaiter().GetResult();
    }

    [AvaloniaFact]
    public void OneFilePickedDrawsOneThumbnailAndKeepsEveryOtherTile()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var queue = b.Services.Thumbnails;
            Pump(vm.LibraryThumbnails);                                   // the boot's own pass
            var before = vm.LibraryAll.ToArray();
            Assert.NotEmpty(before);
            Assert.All(before, t => Assert.NotNull(t.Thumbnail));
            var drawn = queue.Rendered;
            var pictures = before.ToDictionary(t => t.Id, t => t.Thumbnail);
            var shown = vm.Library.ToArray();

            var logo = Path.Combine(b.Dir, "logo.png");
            File.WriteAllBytes(logo, new byte[] { 1 });
            vm.State.MediaLibrary.Add(new MediaLibraryEntry { Path = logo, Kind = LibraryMediaKind.Image });
            vm.RefreshLibrary();
            Pump(vm.LibraryThumbnails);

            Assert.Equal(drawn + 1, queue.Rendered);                      // the new tile and nothing else
            Assert.Equal(before.Length + 1, vm.LibraryAll.Count);
            foreach (var old in before)
            {
                var now = vm.LibraryAll.Single(t => t.Id == old.Id);
                Assert.Same(old, now);                                    // the instance stays on the page
                Assert.Same(pictures[old.Id], now.Thumbnail);             // and so does its picture
            }
            Assert.NotNull(vm.LibraryAll.Single(t => t.Name == "logo.png").Thumbnail);
            foreach (var old in shown) Assert.Contains(old, vm.Library);  // the ItemsControl keeps its containers

            // A chip or a search is a filter, not a build: nothing is drawn.
            var afterAdd = queue.Rendered;
            vm.SelectedLibrarySection = "Images";
            vm.LibrarySearch = "logo";
            Assert.Equal("logo.png", Assert.Single(vm.Library).Name);
            Assert.Equal(afterAdd, queue.Rendered);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TwoBuildsInARowAreOnePassOverTheTiles()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var queue = b.Services.Thumbnails;
            Pump(vm.LibraryThumbnails);
            var drawn = queue.Rendered;
            var dropped = queue.Dropped;

            vm.State.Brand.PrimaryColor = "#101010";                      // the brand colours every tile: all are due again
            vm.RefreshLibrary();
            vm.RefreshLibrary();                                          // supersedes the pass in hand
            Pump(vm.LibraryThumbnails);

            Assert.Equal(drawn + vm.LibraryAll.Count, queue.Rendered);    // each tile once, whichever pass drew it
            Assert.True(queue.Dropped > dropped);                         // the first pass gave way
            Assert.All(vm.LibraryAll, t => Assert.NotNull(t.Thumbnail));
        }
        finally
        {
            b.Dispose();
        }
    }

    [Fact]
    public void AThumbnailDrawsOverThePublishedShowAndSharesWhatItDoesNotWrite()
    {
        var show = new ShowState();
        show.Brand.PrimaryColor = "#123456";
        var published = SnapshotClone.Clone(show);

        var state = ThumbnailRenderer.ThumbnailState(published, new PatternConfig { Kind = PatternKind.Focus });

        var shared = SnapshotClone.SharedSections(published, state);
        Assert.Contains(nameof(ShowState.Brand), shared);
        Assert.Contains(nameof(ShowState.LooksAndCues), shared);
        Assert.Contains(nameof(ShowState.Stacks), shared);
        Assert.Contains(nameof(ShowState.Output), shared);
        Assert.DoesNotContain(nameof(ShowState.Pattern), shared);
        Assert.DoesNotContain(nameof(ShowState.Overlays), shared);
        Assert.DoesNotContain(nameof(ShowState.Countdown), shared);
        Assert.DoesNotContain(nameof(ShowState.LowerThirds), shared);
        Assert.False(state.Pattern.IsPublished);
        Assert.True(state.Brand.IsPublished);
        Assert.Equal(PatternKind.Focus, state.Pattern.Kind);
    }

    [Fact]
    public void TheCatalogueReconcilesInPlaceAndTheFilterSyncsWithoutRemounting()
    {
        var a = Tile("a", "Patterns");
        var b = Tile("b", "Images");
        var all = new List<PresetItem> { a, b };
        var fresh = new List<PresetItem> { Tile("b", "Images"), Tile("c", "Images"), Tile("a", "Patterns") };
        LibraryCatalogue.Reconcile(all, fresh);
        Assert.Equal(new[] { "b", "c", "a" }, all.Select(t => t.Id));
        Assert.Same(a, all[2]);                                           // kept instances
        Assert.Same(b, all[0]);
        Assert.Same(fresh[0].Apply, b.Apply);                             // with the fresh tile's action

        var shown = new System.Collections.ObjectModel.ObservableCollection<PresetItem> { a, b };
        var moves = 0;
        shown.CollectionChanged += (_, e) => moves++;
        LibraryCatalogue.Sync(shown, LibraryCatalogue.Filter(all, "Images", ""));
        Assert.Equal(new[] { "b", "c" }, shown.Select(t => t.Id));
        Assert.Same(b, shown[0]);
        Assert.Equal(2, moves);                                           // a removed, c inserted; b untouched

        Assert.Equal("3 tiles", LibraryCatalogue.Summary(3, 3, "All", ""));
        Assert.Equal("2 of 3 · Images · 'logo'", LibraryCatalogue.Summary(2, 3, "Images", " logo "));
    }

    private static PresetItem Tile(string id, string section)
        => new() { Id = id, Section = section, Category = "c", Name = id, Apply = () => { } };
}
