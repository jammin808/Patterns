namespace Patterns.Core.Services;

/// <summary>What an import did, and the sentence to put on the status line.</summary>
public readonly record struct ImportedFile(string Path, bool Copied, string Words);

/// <summary>
/// The show's own files: a picture or a short clip brought INTO the show rather than pointed at
/// where the operator happened to browse.
///
/// The fault this exists for is the one every desk has: a lower third is built the night before
/// with a speaker's headshot off the Desktop, the show file goes to the show machine on a stick,
/// and the picture is a blank rectangle in front of the room because the file never travelled.
/// Nothing warned anybody, because on the machine it was built on the path was perfectly good.
///
/// So a chosen file is copied into media/ beside the show and the show points at the copy: the
/// folder is the show, and copying the folder takes the pictures with it. Two rules keep that
/// honest. A file too big to copy is pointed at where it is and the desk SAYS so, rather than
/// quietly duplicating a gigabyte onto a stick. And a path that no longer resolves is looked for
/// by name in media/ before it is given up on, so a show folder opened from another drive letter
/// — or another machine entirely — finds its own pictures again.
/// </summary>
public static class ShowFiles
{
    /// <summary>
    /// Bigger than this and the file is pointed at rather than copied. A headshot and a short
    /// b-roll clip are what a lower third takes; a feature-length master is not, and doubling one
    /// onto the show drive during the last hour is not a kindness.
    /// </summary>
    public const long ImportCeilingBytes = 512L * 1024 * 1024;

    private static volatile string _media = "";

    /// <summary>media/ beside the show. Set once as the desk starts; empty in a build with no store.</summary>
    public static string MediaDirectory
    {
        get => _media;
        set => _media = value ?? "";
    }

    /// <summary>True when this path is already one of the show's own files.</summary>
    public static bool IsInside(string? path)
    {
        var media = _media;
        if (media.Length == 0 || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(media);
            return full.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The file as it is on THIS machine: the path itself when it is there, else the show's own
    /// copy of that name, else the path unchanged so the readouts still name what was asked for.
    /// </summary>
    public static string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            if (File.Exists(path)) return path;
            var media = _media;
            if (media.Length == 0) return path;
            var beside = Path.Combine(media, Path.GetFileName(path));
            return File.Exists(beside) ? beside : path;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>True when the desk can actually open this file right now.</summary>
    public static bool Exists(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(Resolve(path));

    /// <summary>
    /// A chosen file brought into the show. The returned path is what the show should hold: the
    /// copy when one was made, else the file where it is. The words are for the status line and
    /// are empty when there is nothing worth saying.
    /// </summary>
    public static ImportedFile Import(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new ImportedFile("", false, "");
        var media = _media;
        if (media.Length == 0 || IsInside(path)) return new ImportedFile(path, false, "");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new ImportedFile(path, false, $"'{Path.GetFileName(path)}' is not there.");
            if (info.Length > ImportCeilingBytes)
            {
                return new ImportedFile(path, false,
                    $"'{info.Name}' is {Megabytes(info.Length)} — too big to copy beside the show, so the show points at it where it is. Keep that drive with the show.");
            }

            Directory.CreateDirectory(media);
            var target = FreeName(media, info);
            if (!File.Exists(target)) File.Copy(info.FullName, target, overwrite: false);
            return new ImportedFile(target, true, $"'{Path.GetFileName(target)}' is in the show's media folder now — copy the folder and it travels.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Importing '{path}' failed; the show points at it where it is.", ex);
            return new ImportedFile(path, false, $"'{Path.GetFileName(path)}' could not be copied beside the show — the show points at it where it is.");
        }
    }

    /// <summary>
    /// Where this file goes in media/. The same file chosen twice is the same copy — matched on
    /// name and length, so an import is idempotent and a show does not grow a folder of
    /// headshot (2).jpg every time it is opened. A DIFFERENT file of the same name takes the
    /// next free name rather than overwriting the one a design is already pointing at.
    /// </summary>
    private static string FreeName(string media, FileInfo source)
    {
        var stem = Path.GetFileNameWithoutExtension(source.Name);
        var ext = Path.GetExtension(source.Name);
        for (var n = 1; n < 1000; n++)
        {
            var candidate = Path.Combine(media, n == 1 ? source.Name : $"{stem} ({n}){ext}");
            var existing = new FileInfo(candidate);
            if (!existing.Exists) return candidate;
            if (existing.Length == source.Length) return candidate;    // the same file, already here
        }
        return Path.Combine(media, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    private static string Megabytes(long bytes)
        => bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"
            : $"{bytes / (1024.0 * 1024):0} MB";
}
