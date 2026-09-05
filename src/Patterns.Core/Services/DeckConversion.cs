using System.Security.Cryptography;
using System.Text;

namespace Patterns.Core.Services;

/// <summary>
/// PowerPoint, Keynote and Impress decks reach the deck pipeline as PDFs: LibreOffice Impress
/// converts a presentation headless — once — into a cache named for the file's identity, so an
/// edited deck converts again and an unchanged one never does. Pure: where LibreOffice may be,
/// the command line it is given, the cache's names. The App runs the process.
/// </summary>
public static class DeckConversion
{
    /// <summary>Presentations LibreOffice Impress opens: PowerPoint in every flavour, Keynote, and its own.</summary>
    public static readonly string[] PresentationExtensions = { ".pptx", ".ppt", ".pptm", ".ppsx", ".pps", ".ppsm", ".potx", ".odp", ".otp", ".key" };

    /// <summary>What the desk says when no LibreOffice is found — and what to do.</summary>
    public const string NotFoundNote = "LibreOffice not found — install it (free, libreoffice.org), put a portable copy in a LibreOfficePortable folder beside Patterns, or export the deck as PDF.";

    /// <summary>A file that must go through LibreOffice before the PDF renderer can show it.</summary>
    public static bool NeedsConversion(string path)
        => PresentationExtensions.Contains(System.IO.Path.GetExtension(path ?? "").ToLowerInvariant());

    /// <summary>"PowerPoint", "Keynote", "Impress", "PDF" — the kind of deck a file is, for the desk's words.</summary>
    public static string KindOf(string path)
    {
        var ext = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "PDF",
            ".odp" or ".otp" => "Impress",
            ".key" => "Keynote",
            _ when NeedsConversion(path ?? "") => "PowerPoint",
            _ => "deck",
        };
    }

    /// <summary>
    /// The cached PDF's file name for a presentation: the same while the file is unchanged, another
    /// once it is edited (its size or last write) or moved — so a fresh export converts afresh and
    /// the old PDF is never shown for it. The stem stays readable in the cache folder.
    /// </summary>
    public static string CacheName(string path, long length, DateTime lastWriteUtc)
    {
        var identity = $"{(path ?? "").Trim().ToLowerInvariant()}|{length}|{lastWriteUtc.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var stem = Stem(path ?? "").ToLowerInvariant();   // one cache file per file, however its path is spelt on a case-blind disk
        if (stem.Length > 40) stem = stem[..40];
        return $"{stem}-{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}.pdf";
    }

    /// <summary>The PDF LibreOffice writes for a source into an out directory: the source's stem with .pdf.</summary>
    public static string ProducedName(string source) => Stem(source) + ".pdf";

    /// <summary>The file's name without its extension, whichever separator the path uses (a Windows path read on a Linux build server is still a Windows path).</summary>
    private static string Stem(string path)
    {
        var cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        var name = cut >= 0 ? path[(cut + 1)..] : path;
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>
    /// LibreOffice's command line: headless, its own profile under Patterns (a conversion is never
    /// handed to a LibreOffice window the operator has open, and never waits on one), no splash, no
    /// recovery prompt, a PDF into the out directory. Impress renders the slides as exported.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string source, string outDir, string profileDir)
        => new[]
        {
            "-env:UserInstallation=" + ProfileUri(profileDir),
            "--headless",
            "--norestore",
            "--nologo",
            "--nolockcheck",
            "--convert-to",
            "pdf",
            "--outdir",
            outDir,
            source,
        };

    /// <summary>A file URI for the profile folder — file:///C:/… on Windows, file:///home/… elsewhere, spaces escaped.</summary>
    public static string ProfileUri(string profileDir)
    {
        var full = System.IO.Path.GetFullPath(profileDir);
        return new Uri(full).AbsoluteUri;
    }

    /// <summary>
    /// Where LibreOffice may be, in the order worth trying: a path the operator gave, a portable
    /// copy beside Patterns (a show drive carries its own), the usual install folders, then every
    /// folder on the PATH.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string? configuredPath, string appDirectory, string? programFiles, string? programFilesX86, string? pathEnvironment, bool windows)
    {
        var list = new List<string>();
        var separator = windows ? '\\' : '/';
        var exe = windows ? "soffice.exe" : "soffice";
        void Add(string candidate)
        {
            if (candidate.Length > 0 && !list.Contains(candidate, StringComparer.OrdinalIgnoreCase)) list.Add(candidate);
        }
        // Paths are joined for the platform asked about, whatever the machine building the list runs on.
        string Join(string root, params string[] parts) => root.TrimEnd('\\', '/') + separator + string.Join(separator, parts);

        var configured = (configuredPath ?? "").Trim();
        if (configured.Length > 0)
        {
            Add(configured);
            // A folder given instead of the executable: look inside it the way the install lays it out.
            Add(Join(configured, exe));
            Add(Join(configured, "program", exe));
        }
        if (windows)
        {
            Add(Join(appDirectory, "LibreOfficePortable", "App", "libreoffice", "program", exe));
            Add(Join(appDirectory, "libreoffice", "program", exe));
            foreach (var root in new[] { programFiles, programFilesX86 })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                Add(Join(root, "LibreOffice", "program", exe));
            }
            foreach (var dir in Split(pathEnvironment, ';')) Add(Join(dir, exe));
        }
        else
        {
            Add(Join(appDirectory, "libreoffice", "program", exe));
            Add("/Applications/LibreOffice.app/Contents/MacOS/soffice");
            Add("/usr/bin/soffice");
            Add("/usr/local/bin/soffice");
            Add("/usr/lib/libreoffice/program/soffice");
            Add("/opt/libreoffice/program/soffice");
            Add("/snap/bin/libreoffice");
            foreach (var dir in Split(pathEnvironment, ':')) Add(Join(dir, exe));
        }
        return list;
    }

    /// <summary>The first candidate that exists, or null: LibreOffice is nowhere Patterns looks.</summary>
    public static string? FindLibreOffice(Func<string, bool> exists, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                if (exists(candidate)) return candidate;
            }
            catch
            {
                // an unreadable folder on the PATH is not LibreOffice
            }
        }
        return null;
    }

    /// <summary>What the desk says about the converter: found where, or what to do.</summary>
    public static string Describe(string? exe) => exe is null ? NotFoundNote : $"LibreOffice found: {exe} — a PowerPoint deck converts to PDF here, once, and is kept.";

    private static IEnumerable<string> Split(string? pathEnvironment, char separator)
    {
        if (string.IsNullOrWhiteSpace(pathEnvironment)) yield break;
        foreach (var part in pathEnvironment.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = part.Trim('"');
            if (dir.Length > 0) yield return dir;
        }
    }
}
