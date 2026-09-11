using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.ViewModels;
using Patterns.Core.LowerThirds;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 26: "Lower thirds can have a feature to import a user image or short video."
///
/// Every piece of it was already in the model — a Picture element, a Clip element, a path, a fit,
/// a mute and a level. What was missing was the import: the row that chooses a file sat two
/// navigations away behind the settings pop-out, the filter offered audio and decks the renderer
/// draws as a dark rectangle, and the file the operator chose stayed wherever they had browsed,
/// so the show travelled to the show machine and the picture did not.
/// </summary>
public class LowerThirdImportTests
{
    private static string Elsewhere(TestApp.Booted b, string name, int bytes = 32)
    {
        var dir = Path.Combine(b.Dir, "elsewhere");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static LowerThirdDesign FreshDesign(MainViewModel vm)
    {
        var design = new LowerThirdDesign { Name = "Name strap" };
        vm.State.LowerThirds.Designs.Add(design);
        vm.SelectedLowerThird = design;
        return design;
    }

    [AvaloniaFact]
    public void AChosenPictureComesIntoTheShowAndNamesItsOwnElement()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            FreshDesign(vm);
            vm.SelectPage(Shell.IndexOf("Lower thirds"));
            Dispatcher.UIThread.RunJobs();

            var element = vm.AddElement(LowerThirdElementKind.Image);
            Assert.NotNull(element);
            Assert.Equal("Image", element!.Name);                     // the kind, until it holds something

            var chosen = Elsewhere(b, "jane-doe.jpg");
            vm.AdoptElementFile(element, chosen);

            // It is the show's own file now: media/ beside the show, not the folder it was browsed from.
            Assert.Equal(Path.Combine(services.Store.MediaDirectory, "jane-doe.jpg"), element.Path);
            Assert.True(File.Exists(element.Path));
            Assert.True(ShowFiles.IsInside(element.Path));
            Assert.Equal("jane-doe", element.Name);                   // the list reads as what it holds
            Assert.Contains("media folder", vm.StatusMessage);

            // And it is in the media library, so the next design is one click away from it.
            Assert.Contains(vm.State.MediaLibrary, m => m.Path == element.Path);

            // A name the operator typed is theirs: a second file does not take it away.
            element.Name = "Headshot";
            vm.AdoptElementFile(element, Elsewhere(b, "other.jpg"));
            Assert.Equal("Headshot", element.Name);
            Assert.EndsWith("other.jpg", element.Path);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void APictureThatDidNotTravelSaysSoInTheDesignerRatherThanInFrontOfTheRoom()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, _) = b;
            var design = FreshDesign(vm);
            var element = vm.AddElement(LowerThirdElementKind.Image)!;
            vm.SelectedElement = element;
            Assert.Equal("", vm.ElementFileTrouble);                  // nothing chosen yet is not a fault

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
            element.Path = Path.Combine(b.Dir, "gone", "speaker.jpg");
            Assert.Contains(nameof(vm.ElementFileTrouble), raised);   // on the keystroke, not the next poll
            Assert.Contains("speaker.jpg", vm.ElementFileTrouble);
            Assert.Contains("draws a blank", vm.ElementFileTrouble);

            // Brought into the show, it is found again and the line clears.
            var chosen = Elsewhere(b, "speaker.jpg");
            vm.AdoptElementFile(element, chosen);
            Assert.Equal("", vm.ElementFileTrouble);

            // The show file remembers a path from the machine it was built on; the picture beside
            // the show is found by name, which is what makes an import worth more than a browse.
            element.Path = Path.Combine("D:", "Desktop", "speaker.jpg");
            Assert.Equal("", vm.ElementFileTrouble);
            Assert.True(ShowFiles.Exists(element.Path));
            Assert.Equal(design.Elements[0], element);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATooBigClipIsPointedAtWhereItIsAndTheDeskSaysWhy()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, _) = b;
            FreshDesign(vm);
            var element = vm.AddElement(LowerThirdElementKind.Media)!;

            var big = Elsewhere(b, "master.mov");
            using (var f = new FileStream(big, FileMode.Open, FileAccess.Write))
            {
                f.SetLength(ShowFiles.ImportCeilingBytes + 1);        // sparse: no gigabyte is written
            }

            vm.AdoptElementFile(element, big);
            Assert.Equal(big, element.Path);                          // not doubled onto the show drive
            Assert.Contains("too big to copy", vm.StatusMessage);
            Assert.Contains("Keep that drive with the show", vm.StatusMessage);
            Assert.Equal("", vm.ElementFileTrouble);                  // it is there; it is just not carried
        }
        finally
        {
            b.Dispose();
        }
    }
}
