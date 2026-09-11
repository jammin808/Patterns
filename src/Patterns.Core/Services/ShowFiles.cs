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

    /// <summary>
    /// How long a reading of the disk is trusted on the draw path. A picture element is resolved
    /// on every frame of every sink, and a file system call per element per frame per sink is a
    /// cost the frame budget should never carry for an answer that changes once a night. Half a
    /// second is far below anything an operator can see and far above the frame rate.
    /// </summary>
    private const long FreshMs = 500;

    private readonly record struct Reading(string Resolved, bool Found, long Stamp);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Reading> Readings = new(StringComparer.OrdinalIgnoreCase);

    private static volatile string _media = "";

    /// <summary>media/ beside the show. Set once as the desk starts; empty in a build with no store.</summary>
    public static string MediaDirectory
    {
        get => _media;
        set
        {
            _media = value ?? "";
            Readings.Clear();                                          // another show's folder: nothing read before it holds
        }
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
    /// For the draw path — a reading of the disk no older than half a second.
    /// </summary>
    public static string Resolve(string? path) => Read(path, fresh: false).Resolved;

    /// <summary>The same answer, read from the disk now. For the checks, which run once and must be exact.</summary>
    public static string ResolveExact(string? path) => Read(path, fresh: true).Resolved;

    /// <summary>
    /// True when the desk can actually open this file. Read from the disk now by default, for the
    /// checks and the designer's own line; pass <c>fresh: false</c> on a draw path, where a reading
    /// half a second old is worth far more than a file-system call every frame.
    /// </summary>
    public static bool Exists(string? path, bool fresh = true) => Read(path, fresh).Found;

    private static Reading Read(string? path, bool fresh)
    {
        if (string.IsNullOrWhiteSpace(path)) return new Reading("", false, 0);
        var now = Environment.TickCount64;
        if (!fresh && Readings.TryGetValue(path, out var seen) && now - seen.Stamp < FreshMs) return seen;

        var reading = Probe(path) with { Stamp = now };
        // Bounded: a show has a handful of pictures, but a folder browsed over a long night must
        // never grow this without end.
        if (Readings.Count > 512) Readings.Clear();
        Readings[path] = reading;
        return reading;
    }

    private static Reading Probe(string path)
    {
        try
        {
            if (File.Exists(path)) return new Reading(path, true, 0);
            var media = _media;
            if (media.Length == 0) return new Reading(path, false, 0);
            var beside = Path.Combine(media, Path.GetFileName(path));
            return File.Exists(beside) ? new Reading(beside, true, 0) : new Reading(path, false, 0);
        }
        catch
        {
            return new Reading(path, false, 0);
        }
    }

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
            Readings.Clear();                                          // the disk just changed under every reading
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
