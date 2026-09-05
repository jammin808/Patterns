using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// PowerPoint, Keynote and Impress decks on their way to the PDF renderer: which files convert,
/// the cache's names, LibreOffice's command line, and where it is looked for.
/// </summary>
public class DeckConversionTests
{
    [Fact]
    public void PresentationsAreDecksThatConvertAndPdfsAreDecksAsTheyAre()
    {
        foreach (var ext in new[] { ".pptx", ".PPTX", ".ppt", ".pptm", ".ppsx", ".pps", ".odp", ".key" })
        {
            Assert.True(DeckConversion.NeedsConversion("C:\\show\\talk" + ext), ext);
            Assert.True(PlaylistSequencer.IsDeckPath("C:\\show\\talk" + ext), ext);
            Assert.Equal(LibraryMediaKind.Deck, MediaLibraryEntry.KindOf("C:\\show\\talk" + ext, false));
        }
        Assert.False(DeckConversion.NeedsConversion("C:\\show\\talk.pdf"));
        Assert.True(PlaylistSequencer.IsDeckPath("C:\\show\\talk.pdf"));
        Assert.False(DeckConversion.NeedsConversion("C:\\show\\talk.mp4"));
        Assert.False(PlaylistSequencer.IsDeckPath("C:\\show\\talk.mp4"));
        Assert.Contains(".pptx", PlaylistSequencer.DeckExtensions);
        Assert.Contains(".pdf", PlaylistSequencer.DeckExtensions);

        Assert.Equal("PowerPoint", DeckConversion.KindOf("talk.pptx"));
        Assert.Equal("PowerPoint", DeckConversion.KindOf("talk.ppsx"));
        Assert.Equal("Keynote", DeckConversion.KindOf("talk.key"));
        Assert.Equal("Impress", DeckConversion.KindOf("talk.odp"));
        Assert.Equal("PDF", DeckConversion.KindOf("talk.pdf"));
        Assert.Equal("deck", DeckConversion.KindOf("talk.mp4"));
    }

    [Fact]
    public void TheCacheNameFollowsTheFileAndChangesWhenTheFileDoes()
    {
        var when = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);
        var a = DeckConversion.CacheName("C:\\show\\Keynote Talk.pptx", 120_000, when);
        Assert.EndsWith(".pdf", a);
        Assert.StartsWith("keynote talk-", a);
        Assert.Equal(a, DeckConversion.CacheName("C:\\show\\Keynote Talk.pptx", 120_000, when));        // stable
        Assert.Equal(a, DeckConversion.CacheName("c:\\SHOW\\keynote talk.pptx", 120_000, when));        // the path's case is not a change
        Assert.NotEqual(a, DeckConversion.CacheName("C:\\show\\Keynote Talk.pptx", 120_001, when));     // edited: another size
        Assert.NotEqual(a, DeckConversion.CacheName("C:\\show\\Keynote Talk.pptx", 120_000, when.AddSeconds(1))); // edited: written again
        Assert.NotEqual(a, DeckConversion.CacheName("D:\\show\\Keynote Talk.pptx", 120_000, when));     // moved
        Assert.Equal("Keynote Talk.pdf", DeckConversion.ProducedName("C:\\show\\Keynote Talk.pptx"));

        var longStem = new string('x', 80) + ".pptx";
        Assert.True(DeckConversion.CacheName(longStem, 1, when).Length < 80);
    }

    [Fact]
    public void LibreOfficeIsRunHeadlessWithItsOwnProfileIntoTheOutFolder()
    {
        var profile = Path.Combine(Path.GetTempPath(), "patterns decks", "lo-profile");
        var args = DeckConversion.Arguments("/shows/my talk.pptx", "/tmp/out dir", profile);
        Assert.StartsWith("-env:UserInstallation=file:///", args[0]);
        Assert.Contains("%20", args[0]);                          // a space in the profile path survives as a URI
        Assert.DoesNotContain(" ", args[0]);
        Assert.Contains("--headless", args);
        Assert.Contains("--norestore", args);
        Assert.Contains("--nolockcheck", args);
        var convert = args.ToList().IndexOf("--convert-to");
        Assert.Equal("pdf", args[convert + 1]);
        var outDir = args.ToList().IndexOf("--outdir");
        Assert.Equal("/tmp/out dir", args[outDir + 1]);
        Assert.Equal("/shows/my talk.pptx", args[^1]);           // the source last, as soffice reads it
    }

    [Fact]
    public void LibreOfficeIsLookedForBesideTheAppInTheInstallFoldersAndOnThePath()
    {
        var windows = DeckConversion.Candidates(
            configuredPath: "",
            appDirectory: "D:\\Patterns",
            programFiles: "C:\\Program Files",
            programFilesX86: "C:\\Program Files (x86)",
            pathEnvironment: "C:\\Windows;C:\\Tools\\LibreOffice\\program",
            windows: true);
        Assert.Equal("D:\\Patterns\\LibreOfficePortable\\App\\libreoffice\\program\\soffice.exe", windows[0]);
        Assert.Contains("C:\\Program Files\\LibreOffice\\program\\soffice.exe", windows);
        Assert.Contains("C:\\Program Files (x86)\\LibreOffice\\program\\soffice.exe", windows);
        Assert.Contains("C:\\Tools\\LibreOffice\\program\\soffice.exe", windows);
        var order = windows.ToList();
        Assert.True(order.IndexOf("C:\\Program Files\\LibreOffice\\program\\soffice.exe") < order.IndexOf("C:\\Tools\\LibreOffice\\program\\soffice.exe"));

        // The operator's own path comes first — the executable, or its folder.
        var told = DeckConversion.Candidates("E:\\LO", "D:\\Patterns", null, null, null, windows: true);
        Assert.Equal("E:\\LO", told[0]);
        Assert.Equal("E:\\LO\\soffice.exe", told[1]);
        Assert.Equal("E:\\LO\\program\\soffice.exe", told[2]);

        var unix = DeckConversion.Candidates("", "/opt/patterns", null, null, "/usr/local/bin:/usr/bin", windows: false);
        Assert.Equal("/opt/patterns/libreoffice/program/soffice", unix[0]);
        Assert.Contains("/Applications/LibreOffice.app/Contents/MacOS/soffice", unix);
        Assert.Contains("/usr/bin/soffice", unix);
        Assert.Equal(unix.Count, unix.Distinct(StringComparer.OrdinalIgnoreCase).Count());   // /usr/bin from the PATH is not listed twice

        var found = DeckConversion.FindLibreOffice(p => p == "C:\\Program Files\\LibreOffice\\program\\soffice.exe", windows);
        Assert.Equal("C:\\Program Files\\LibreOffice\\program\\soffice.exe", found);
        Assert.Null(DeckConversion.FindLibreOffice(_ => false, windows));
        Assert.Null(DeckConversion.FindLibreOffice(_ => throw new UnauthorizedAccessException(), windows));

        Assert.Contains("libreoffice.org", DeckConversion.Describe(null));
        Assert.Contains("found", DeckConversion.Describe("/usr/bin/soffice"));
    }
}
