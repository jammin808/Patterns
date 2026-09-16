using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 73: one table says which page edits a kind of picture, and the desk's editing facts read
/// as one line — the Library page's OPEN key, the screen menu's GO TO entry, STATE's editing row
/// and the Eye's desk node all stand on these.
/// </summary>
public class PictureEditorsTests
{
    [Fact]
    public void AKindOfPictureNamesItsEditorAndTheStudiosAreTheFour()
    {
        Assert.Equal("Fractals", PictureEditors.PageFor(PatternKind.Fractal));
        Assert.Equal("Particles", PictureEditors.PageFor(PatternKind.Particles));
        Assert.Equal("Reactive", PictureEditors.PageFor(PatternKind.Reactive));
        Assert.Equal("Media", PictureEditors.PageFor(PatternKind.Media));
        foreach (var kind in new[] { PatternKind.Grid, PatternKind.ColorBars, PatternKind.LedWall, PatternKind.Multiview, PatternKind.TestCard, PatternKind.ProjectionBlend })
        {
            Assert.Equal("Pattern", PictureEditors.PageFor(kind));
        }

        // By the kind's word, as the menus and STATE carry it — spaces ignored like the wire; a stranger is the Pattern page.
        Assert.Equal("Fractals", PictureEditors.PageFor("Fractal"));
        Assert.Equal("Pattern", PictureEditors.PageFor("LED wall"));
        Assert.Equal("Pattern", PictureEditors.PageFor("nothing like it"));
        Assert.Equal("Pattern", PictureEditors.PageFor(""));

        Assert.Equal("Fractals", PictureEditors.StudioPage("Fractal"));
        Assert.Equal("Media", PictureEditors.StudioPage("Media"));
        Assert.Null(PictureEditors.StudioPage("Grid"));
        Assert.Null(PictureEditors.StudioPage(""));

        // A brand kit is the show's colours: Branding, whatever picture is under the editors.
        Assert.Equal("Branding", PictureEditors.PageForTile("Brand kits", PatternKind.Grid));
        Assert.Equal("Fractals", PictureEditors.PageForTile("Fractals", PatternKind.Fractal));
        Assert.Equal("Media", PictureEditors.PageForTile("Web", PatternKind.Media));
    }

    [Fact]
    public void TheEditingFactsReadAsOneLine()
    {
        var programme = new EditingFacts("", "the programme", false, "Grid", "Pattern", "", "");
        Assert.True(programme.IsProgram);
        Assert.Equal("the programme's preview · Grid — Pattern page", programme.Words);

        var own = new EditingFacts("b", "Right", true, "Fractal", "Fractals", "Mandelbrot", "Fractals");
        Assert.False(own.IsProgram);
        Assert.Equal("Right's preview (its own picture) · Fractal — Fractals page · library: Mandelbrot (Fractals)", own.Words);

        var following = new EditingFacts("a", "Left", false, "ColorBars", "Pattern", "Bars", "");
        Assert.Equal("Left's preview — follows the programme · ColorBars — Pattern page · library: Bars", following.Words);
    }
}
