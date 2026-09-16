using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// Which page of the rail edits a kind of picture (round 73). A fractal, a particle scene, a
/// reactive scene and a media picture each have a studio of their own; every other kind is edited
/// on the Pattern page, whose groups follow the kind. The Library page's OPEN button, the screen
/// menu's GO TO entry, STATE's editing row and the Eye read the one table, so a picture put up by
/// a click is one press from its own editors wherever the operator is.
/// </summary>
public static class PictureEditors
{
    /// <summary>The page header (as the rail's table has it) that edits this kind of picture.</summary>
    public static string PageFor(PatternKind kind) => kind switch
    {
        PatternKind.Fractal => "Fractals",
        PatternKind.Particles => "Particles",
        PatternKind.Reactive => "Reactive",
        PatternKind.Media => "Media",
        _ => "Pattern",
    };

    /// <summary>The same by the kind's word ("Fractal", "LED wall"); a word that is no kind is the Pattern page.</summary>
    public static string PageFor(string kindWord)
        => ActionSpec.ParsePatternKind(kindWord ?? "") is { } kind ? PageFor(kind) : "Pattern";

    /// <summary>The studio a kind has of its own — Fractals, Particles, Reactive, Media — or null when the Pattern page is its editor.</summary>
    public static string? StudioPage(string kindWord)
    {
        var page = PageFor(kindWord);
        return page == "Pattern" ? null : page;
    }

    /// <summary>The page for what a Library tile put up: a brand kit is the show's colours, edited on Branding; anything else is edited by the picture it made.</summary>
    public static string PageForTile(string section, PatternKind kind)
        => string.Equals(section, "Brand kits", StringComparison.OrdinalIgnoreCase) ? "Branding" : PageFor(kind);
}

/// <summary>
/// What the desk is editing (round 73): the target under the editors, whether it is its own
/// picture, the kind of picture, the page that edits it, and the Library tile last put there.
/// The desk hands it to the services; STATE's editing row, the Eye's desk node and the brief read
/// it, so a deck, a phone and the assistant know what a press on the Library page just changed.
/// </summary>
public sealed record EditingFacts(string TargetId, string Label, bool Own, string Kind, string Editor, string Library, string LibrarySection)
{
    /// <summary>"" is the programme's preview.</summary>
    public bool IsProgram => TargetId.Length == 0;

    /// <summary>"Right's preview (its own picture) · Fractal — Fractals page · library: Mandelbrot (Fractals)".</summary>
    public string Words
    {
        get
        {
            var where = IsProgram ? "the programme's preview" : $"{Label}'s preview{(Own ? " (its own picture)" : " — follows the programme")}";
            var tile = Library.Length > 0 ? $" · library: {Library}{(LibrarySection.Length > 0 ? $" ({LibrarySection})" : "")}" : "";
            return $"{where} · {Kind} — {Editor} page{tile}";
        }
    }
}
