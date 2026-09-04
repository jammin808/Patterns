using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The Library page: sections, search, tiles keyed by id, removal, brand kits, and the thumbnails that follow.</summary>
public class LibraryTests
{
    private static void Pump(Task task, int timeoutMs = 20000)
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
    public void SectionsSearchAndRemovalFileTheLibraryAndTilesAreKeyedById()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var dirA = Path.Combine(b.Dir, "a");
            var dirB = Path.Combine(b.Dir, "b");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);
            var logoA = Path.Combine(dirA, "logo.png");
            var logoB = Path.Combine(dirB, "logo.png");
            File.WriteAllBytes(logoA, new byte[] { 1 });
            File.WriteAllBytes(logoB, new byte[] { 2 });
            vm.State.MediaLibrary.Add(new MediaLibraryEntry { Path = logoA, Kind = LibraryMediaKind.Image });
            vm.State.MediaLibrary.Add(new MediaLibraryEntry { Path = logoB, Kind = LibraryMediaKind.Image });
            vm.State.MediaLibrary.Add(new MediaLibraryEntry { Path = Path.Combine(dirA, "opener.mp4"), IsVideo = true, Kind = LibraryMediaKind.Video });
            vm.State.MediaLibrary.Add(new MediaLibraryEntry { Path = Path.Combine(dirA, "bed.mp3"), IsVideo = true, Kind = LibraryMediaKind.Audio, Name = "Interval bed" });
            b.Services.Store.SaveBrandKit("Acme", new BrandKit { CompanyName = "Acme", PrimaryColor = "#112233", SecondaryColor = "#445566" });
            vm.RefreshLibrary();

            Assert.Equal(new[] { "All", "Patterns", "Images", "Videos", "Audio", "Particles", "Presets", "Brand kits" }, vm.LibrarySections);
            Assert.Equal("All", vm.SelectedLibrarySection);
            Assert.Equal(vm.LibraryAll.Count, vm.Library.Count);
            Assert.Equal(vm.LibraryAll.Count, vm.LibraryAll.Select(i => i.Id).Distinct().Count()); // every tile its own id
            Assert.Contains(vm.Library, i => i.Name == "SMPTE bars" && i.Section == "Patterns");
            Assert.Contains(vm.Library, i => i.Name == "Snow" && i.Section == "Particles");
            Assert.Contains(vm.Library, i => i.Name == "Acme" && i.Section == "Brand kits");
            Assert.Contains(vm.Library, i => i.Name == "Interval bed" && i.Section == "Audio");
            Assert.Equal(2, vm.Library.Count(i => i.Name == "logo.png"));

            // Sections file the tiles; the summary says what is shown.
            vm.SelectedLibrarySection = "Images";
            Assert.Equal(2, vm.Library.Count);
            Assert.All(vm.Library, i => Assert.Equal("Images", i.Section));
            Assert.Contains("2 of", vm.LibrarySummary);
            Assert.Contains("Images", vm.LibrarySummary);
            vm.SelectedLibrarySection = "Videos";
            Assert.Equal("opener.mp4", Assert.Single(vm.Library).Name);
            vm.SelectedLibrarySection = "Audio";
            Assert.Equal("Interval bed", Assert.Single(vm.Library).Name);
            vm.SelectedLibrarySection = "Particles";
            Assert.True(vm.Library.Count >= 7);
            Assert.All(vm.Library, i => Assert.Equal("Particles", i.Section));

            // Search matches every word against the name, the category and the section.
            vm.SelectedLibrarySection = "All";
            vm.LibrarySearch = "smpte";
            Assert.Equal("SMPTE bars", Assert.Single(vm.Library).Name);
            vm.LibrarySearch = "bars colour";
            Assert.Equal(2, vm.Library.Count);
            Assert.All(vm.Library, i => Assert.Contains("bars", i.Name, StringComparison.OrdinalIgnoreCase));
            vm.LibrarySearch = "logo";
            Assert.Equal(2, vm.Library.Count);
            vm.SelectedLibrarySection = "Videos";
            Assert.Empty(vm.Library);                    // a section and a search combine
            Assert.Contains("0 of", vm.LibrarySummary);
            vm.LibrarySearch = "";
            vm.SelectedLibrarySection = "All";

            // A brand kit applies to the branding; a media tile applies to the editing target.
            vm.Library.First(i => i.Name == "Acme").Apply();
            Assert.Equal("#112233", vm.State.Brand.PrimaryColor);
            Assert.Equal("Acme", vm.State.Brand.CompanyName);
            vm.Library.First(i => i.Name == "Interval bed").Apply();
            Assert.Equal(PatternKind.Media, vm.ActivePattern.Kind);
            Assert.Equal(MediaSource.Video, vm.ActivePattern.Media.Source);
            Assert.EndsWith("bed.mp3", vm.ActivePattern.Media.VideoPath);
            var second = vm.Library.Where(i => i.Name == "logo.png").ElementAt(1);
            second.Apply();
            Assert.Equal(MediaSource.Image, vm.ActivePattern.Media.Source);
            Assert.Equal(logoB, vm.ActivePattern.Media.ImagePath);

            // Thumbnails are rendered per tile, so both same-named files get one.
            Pump(vm.LibraryThumbnails);
            Assert.All(vm.LibraryAll.Where(i => i.Name == "logo.png"), i => Assert.NotNull(i.Thumbnail));
            Assert.NotNull(vm.LibraryAll.First(i => i.Name == "Acme").Thumbnail);
            Assert.NotNull(vm.LibraryAll.First(i => i.Name == "SMPTE bars").Thumbnail);

            // Only media tiles can be removed here; removing one takes it out of the show, not off the disk.
            Assert.False(vm.Library.First(i => i.Name == "SMPTE bars").CanRemove);
            Assert.False(vm.Library.First(i => i.Name == "Acme").CanRemove);
            Assert.True(second.CanRemove);
            vm.RemoveLibraryItemCommand.Execute(second);
            Assert.Equal(3, vm.State.MediaLibrary.Count);
            Assert.DoesNotContain(vm.State.MediaLibrary, m => m.Path == logoB);
            Assert.Single(vm.Library, i => i.Name == "logo.png");
            Assert.True(File.Exists(logoB));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheLibraryPageRendersItsChipsAndSearchBox()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var host = new Window { DataContext = vm, Width = 1000, Height = 700, Content = new LibrarySection() };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            using var frame = host.CaptureRenderedFrame();
            Assert.NotNull(frame);

            var chips = host.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "LibrarySections");
            Assert.Equal(8, chips.ItemCount);
            chips.SelectedItem = "Particles";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Particles", vm.SelectedLibrarySection);
            Assert.All(vm.Library, i => Assert.Equal("Particles", i.Section));

            var search = host.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "LibrarySearch");
            search.Text = "snow";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("snow", vm.LibrarySearch);
            Assert.Equal("Snow", Assert.Single(vm.Library).Name);
            host.Close();
        }
        finally
        {
            b.Dispose();
        }
    }
}
