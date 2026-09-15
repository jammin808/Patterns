using Patterns.Core.LowerThirds;
using Patterns.Rendering.LowerThirds;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The lower-thirds designer's editing logic, with no desk in it.</summary>
public class LowerThirdDesignerTests
{
    private static (ShowState State, LowerThirdDesigner Designer) Fresh()
    {
        var state = new ShowState();
        return (state, new LowerThirdDesigner(state.LowerThirds));
    }

    [Fact]
    public void DesignsAreNamedSoTheyNeverCollideAndTheFirstIsTheDefault()
    {
        var (state, designer) = Fresh();
        var clean = designer.New("Clean");
        var again = designer.New("Clean");
        var blank = designer.New("Blank");
        Assert.NotEqual(clean.Name, again.Name);
        Assert.EndsWith(" 2", again.Name);
        Assert.Empty(blank.Elements);
        Assert.Equal(clean.Id, state.LowerThirds.DefaultDesignId);                // the first design is the default (★)

        var copy = designer.Duplicate(clean);
        Assert.NotEqual(clean.Id, copy.Id);
        Assert.Equal(clean.Elements.Count, copy.Elements.Count);
        Assert.Equal(4, state.LowerThirds.Designs.Count);

        var loaded = new LowerThirdDesign { Name = "" };
        var adopted = designer.Adopt(loaded, "from-file");
        Assert.Equal("from-file", adopted.Name);
        Assert.NotSame(loaded, adopted);
    }

    [Fact]
    public void RemovingADesignHidesItMovesTheDefaultAndNamesTheNeighbour()
    {
        var (state, designer) = Fresh();
        var a = designer.New("Clean");
        var b = designer.New("Clean");
        var c = designer.New("Clean");
        var now = new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);
        state.LowerThirds.Show(a, now);
        Assert.True(state.LowerThirds.IsShowing);

        var next = designer.Remove(a, now.AddSeconds(2));
        Assert.Same(b, next);                                                     // the neighbour that takes its place
        Assert.Equal(b.Id, state.LowerThirds.DefaultDesignId);                    // the default moved to the first that remains
        Assert.False(state.LowerThirds.IsShowing || state.LowerThirds.HiddenAtUtc is null);
        Assert.Same(b, designer.Remove(c, now));                                  // the last one: its neighbour before it
        Assert.Null(designer.Remove(b, now));
        Assert.Equal("", state.LowerThirds.DefaultDesignId);
    }

    [Fact]
    public void ElementsComeInAtTheDesignsSizeMoveAndTakeTheirKeysAndColours()
    {
        var (_, designer) = Fresh();
        var d = designer.New("Blank");
        d.Width = 1200;
        d.Height = 300;
        var bar = LowerThirdDesigner.AddElement(d, LowerThirdElementKind.Bar);
        var text = LowerThirdDesigner.AddElement(d, LowerThirdElementKind.Text);
        var image = LowerThirdDesigner.AddElement(d, LowerThirdElementKind.Image);
        Assert.Equal((1200.0, 300.0), (bar.W, bar.H));                             // a bar fills the design
        Assert.Equal(LowerThirdFill.Solid, bar.Fill);
        Assert.Equal((600.0, 80.0), (text.W, text.H));
        Assert.Equal("Text", text.Text);
        Assert.Equal((200.0, 200.0), (image.W, image.H));
        Assert.NotEmpty(text.In);                                                  // a plain fade both ways
        Assert.NotEmpty(text.Out);

        Assert.True(LowerThirdDesigner.MoveElement(d, image, -1));
        Assert.Equal(new[] { bar, image, text }, d.Elements);
        Assert.False(LowerThirdDesigner.MoveElement(d, bar, -1));                 // already first
        Assert.Same(text, LowerThirdDesigner.RemoveElement(d, image));            // the neighbour after it takes the selection

        var keys = text.In.Count;
        LowerThirdDesigner.AddKey(text, isIn: true);
        Assert.Equal(keys + 1, text.In.Count);
        Assert.True(LowerThirdDesigner.RemoveKey(text, text.In[^1], isIn: true));
        Assert.Equal(keys, text.In.Count);

        Assert.True(LowerThirdDesigner.SetColorWord(text, "TextColor:primary"));
        Assert.Equal("primary", text.TextColor);
        Assert.False(LowerThirdDesigner.SetColorWord(text, "NoSuchField:primary"));
        Assert.False(LowerThirdDesigner.SetColorWord(text, "primary"));

        d.InMs = 400;
        d.HoldMs = 0;
        d.OutMs = 600;
        Assert.Equal(400 + LowerThirdDesigner.WaitingHoldMs + 600, LowerThirdDesigner.PreviewLength(d));
        Assert.Equal(1000, LowerThirdDesigner.PreviewLength(null));
        var at = LowerThirdDesigner.ApplyMotion(d, text, LowerThirdMotion.Fade, isIn: false);
        Assert.Equal(400 + LowerThirdDesigner.WaitingHoldMs + 300, at);           // the preview scrubs to the middle of the way out
        Assert.Equal(200, LowerThirdDesigner.ApplyMotion(d, text, LowerThirdMotion.Fade, isIn: true));

        LowerThirdDesigner.PlaceFile(image, Path.Combine("shows", "logo.png"));
        Assert.Equal("logo", image.Name);                                          // named after the file while it had the kind's own name
        image.Name = "Sponsor";
        LowerThirdDesigner.PlaceFile(image, Path.Combine("shows", "other.png"));
        Assert.Equal("Sponsor", image.Name);                                       // a typed name is the operator's
        Assert.Contains("not there", LowerThirdDesigner.FileTrouble(image));
        Assert.Equal("", LowerThirdDesigner.FileTrouble(text));
        Assert.Equal("", LowerThirdDesigner.FileTrouble(null));
    }

    [Fact]
    public void PeopleAreKeptImportedAndExportedWithoutADesk()
    {
        var (state, designer) = Fresh();
        var ada = designer.NewEntry("Ada");
        var ada2 = designer.NewEntry("Ada");
        Assert.Equal("Ada 2", ada2.Name);
        Assert.True(designer.RemoveEntry(ada2, out var next));
        Assert.Same(ada, next);
        Assert.False(designer.RemoveEntry(ada2, out _));                          // already gone

        var dir = Path.Combine(Path.GetTempPath(), "patterns-designer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var csv = Path.Combine(dir, "people.csv");
            File.WriteAllText(csv, "Name,Role,Company\nGrace,Engineer,Acme\nAda,Mathematician,Analytical\n");
            var import = designer.ImportPeople(csv, append: false);
            Assert.True(import.Ok);
            Assert.True(import.Replaced);
            Assert.Contains("2 added", import.Words);
            Assert.Equal(2, state.LowerThirds.Entries.Count);
            Assert.Contains(state.LowerThirds.Entries, e => e.Name == "Grace" && e.Role == "Engineer");

            File.WriteAllText(csv, "Name,Role\nAda,Countess\n");
            var appended = designer.ImportPeople(csv, append: true);
            Assert.True(appended.Ok);
            Assert.False(appended.Replaced);
            Assert.Contains("1 updated", appended.Words);
            Assert.Equal(2, state.LowerThirds.Entries.Count);                      // a name already there is updated, never doubled

            var missing = designer.ImportPeople(Path.Combine(dir, "nope.csv"), append: false);
            Assert.False(missing.Ok);
            Assert.Contains("Could not read", missing.Words);

            var exported = designer.ExportPeopleCsv();
            Assert.Contains("Grace", exported);
            Assert.Contains("Countess", exported);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
