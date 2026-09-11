using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 26: a picture or a short clip brought INTO the show. The fault is the one every desk
/// has — a headshot chosen off the Desktop the night before, a show file carried to the show
/// machine on a stick, and a blank rectangle in front of the room because the picture never
/// travelled and nothing had said so.
/// </summary>
public class ShowFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "patterns-showfiles-" + Guid.NewGuid().ToString("N"));
    private readonly string _media;
    private readonly string _wasMedia = ShowFiles.MediaDirectory;

    public ShowFilesTests()
    {
        Directory.CreateDirectory(_dir);
        _media = Path.Combine(_dir, "media");
        ShowFiles.MediaDirectory = _media;
    }

    public void Dispose()
    {
        ShowFiles.MediaDirectory = _wasMedia;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // A temp folder that will not go is the machine's problem, not the test's.
        }
    }

    private string Elsewhere(string name, int bytes = 32)
    {
        var outside = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(outside);
        var path = Path.Combine(outside, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void AChosenFileIsCopiedBesideTheShowAndTheShowPointsAtTheCopy()
    {
        var chosen = Elsewhere("headshot.jpg");
        var imported = ShowFiles.Import(chosen);

        Assert.True(imported.Copied);
        Assert.Equal(Path.Combine(_media, "headshot.jpg"), imported.Path);
        Assert.True(File.Exists(imported.Path));
        Assert.True(File.Exists(chosen), "the operator's own file is never moved out from under them");
        Assert.Contains("media folder", imported.Words);
        Assert.True(ShowFiles.IsInside(imported.Path));

        // Idempotent: the same file chosen again is the same copy, not headshot (2).jpg.
        var again = ShowFiles.Import(chosen);
        Assert.Equal(imported.Path, again.Path);
        Assert.Single(Directory.GetFiles(_media));

        // And a file already in the show is left exactly where it is.
        var inside = ShowFiles.Import(imported.Path);
        Assert.False(inside.Copied);
        Assert.Equal(imported.Path, inside.Path);
    }

    [Fact]
    public void ADifferentFileOfTheSameNameNeverOverwritesTheOneADesignIsUsing()
    {
        var first = Elsewhere("logo.png", 32);
        var kept = ShowFiles.Import(first).Path;

        var second = Path.Combine(_dir, "logo.png");
        File.WriteAllBytes(second, new byte[64]);                    // same name, different file
        var other = ShowFiles.Import(second);

        Assert.NotEqual(kept, other.Path);
        Assert.Equal(Path.Combine(_media, "logo (2).png"), other.Path);
        Assert.Equal(32, new FileInfo(kept).Length);                 // the design's picture is untouched
    }

    [Fact]
    public void AFileTooBigToCarryIsPointedAtWhereItIsAndSaidOutLoud()
    {
        var big = Elsewhere("master.mov");
        using (var f = new FileStream(big, FileMode.Open, FileAccess.Write))
        {
            f.SetLength(ShowFiles.ImportCeilingBytes + 1);           // sparse: no gigabyte is written
        }

        var imported = ShowFiles.Import(big);
        Assert.False(imported.Copied);
        Assert.Equal(big, imported.Path);
        Assert.Contains("too big to copy", imported.Words);
        Assert.Contains("Keep that drive with the show", imported.Words);
        Assert.False(Directory.Exists(_media) && Directory.GetFiles(_media).Length > 0);
    }

    [Fact]
    public void AShowOpenedFromSomewhereElseFindsItsOwnPicturesAgain()
    {
        Directory.CreateDirectory(_media);
        var carried = Path.Combine(_media, "headshot.jpg");
        File.WriteAllBytes(carried, new byte[8]);

        // The path the show was saved with, on a machine and a drive letter that are gone.
        var stale = Path.Combine(_dir, "gone", "D-drive", "headshot.jpg");
        Assert.Equal(carried, ShowFiles.Resolve(stale));
        Assert.True(ShowFiles.Exists(stale));

        // A name the show does not carry is given up on, and named as it was asked for.
        var missing = Path.Combine(_dir, "gone", "nobody.jpg");
        Assert.Equal(missing, ShowFiles.Resolve(missing));
        Assert.False(ShowFiles.Exists(missing));
        Assert.Equal("", ShowFiles.Resolve(""));
        Assert.False(ShowFiles.Exists(null));
    }

    [Fact]
    public void TheDrawPathReadsTheDiskATwentiethAsOftenAsItDrawsAndTheChecksReadItNow()
    {
        var chosen = Elsewhere("late.jpg");
        var stale = Path.Combine(_dir, "gone", "late.jpg");

        // Nothing there yet, and the draw path is now holding that answer.
        Assert.Equal(stale, ShowFiles.Resolve(stale));

        // The picture arrives beside the show. The checks read the disk as it is NOW — they run
        // once, before doors, and an answer half a second old is an answer about the wrong show.
        Directory.CreateDirectory(_media);
        File.Copy(chosen, Path.Combine(_media, "late.jpg"));
        Assert.Equal(Path.Combine(_media, "late.jpg"), ShowFiles.ResolveExact(stale));
        Assert.True(ShowFiles.Exists(stale));

        // An import changes the truth under every reading, so it forgets them all: the designer
        // must never be told the file it has just brought in is missing.
        var element = Elsewhere("brought-in.jpg");
        var imported = ShowFiles.Import(element);
        Assert.Equal(imported.Path, ShowFiles.Resolve(Path.Combine(_dir, "elsewhere-again", "brought-in.jpg")));
    }

    [Fact]
    public void TheCheckSaysWhichPictureDidNotTravelBeforeTheShowRatherThanDuringIt()
    {
        var state = new ShowState();
        var design = new LowerThirdDesign { Name = "Name strap" };
        design.Elements.Add(new LowerThirdElement
        {
            Kind = LowerThirdElementKind.Image, Name = "Headshot", Enabled = true, Path = Path.Combine(_dir, "gone", "speaker.jpg"),
        });
        design.Elements.Add(new LowerThirdElement
        {
            Kind = LowerThirdElementKind.Image, Name = "Off", Enabled = false, Path = Path.Combine(_dir, "gone", "unused.jpg"),
        });
        state.LowerThirds.Designs.Add(design);
        var stack = new CueStackConfig();
        var cue = new RunCueConfig { Number = "1", Name = "Speaker one" };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.LowerThirdShow, Target = "Name strap" });
        stack.Cues.Add(cue);

        var report = CueValidator.Validate(state, stack, CueValidationContext.Default);
        Assert.Contains(report.Issues, i => i.Text.Contains("speaker.jpg") && i.Text.Contains("draws a blank"));
        Assert.DoesNotContain(report.Issues, i => i.Text.Contains("unused.jpg"));   // an element that is off draws nothing
        Assert.False(report.IsBroken(cue.Id), "a picture that did not travel is worth a look, not a refusal");

        // The picture brought into the show clears it — found by name beside the show, which is
        // the whole point of importing rather than pointing.
        Directory.CreateDirectory(_media);
        File.WriteAllBytes(Path.Combine(_media, "speaker.jpg"), new byte[8]);
        Assert.DoesNotContain(CueValidator.Validate(state, stack, CueValidationContext.Default).Issues,
            i => i.Text.Contains("speaker.jpg"));
    }
}
