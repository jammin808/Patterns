namespace Patterns.Core.Services;

/// <summary>
/// The one way a sidecar is written whole: the text to a temp file beside the target, then a move over the old
/// one, so a reader never sees half a record. ADR-008's lane covers the show's own files; every other sidecar —
/// the recovery record, the crash note, the quality profile, the playhead, the Spotify sign-in, an update request,
/// the known-good rig, the assistant's key, the journal's scrub — carried a hand-written copy of the same four lines
/// (rounds 14 to 76), and the copies drifted (one made the directory, one kept a .bak, one did neither). They are
/// one here; a source fence (AtomicFileTests) holds that no new copy appears outside the two named exemptions,
/// the output-ownership seam (it writes through its own file abstraction) and the management download (a stream).
/// Round 83: the temp file is flushed to the disk before the move, so a power cut after the move finds the record
/// whole rather than an empty file with a new name; a writer that comes round again within a second may skip it.
/// </summary>
public static class AtomicFile
{
    /// <summary>The temp file beside the target — one per path, so two writers of one file collide on it and never on the target.</summary>
    public static string TempPath(string path) => path + ".tmp";

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> whole; the directory is made when it is missing.
    /// With <paramref name="durable"/> the bytes are flushed to the disk before the move — an fsync, milliseconds on
    /// a disk and more on a USB stick — which every record a restart reads is worth; a record rewritten every second
    /// (the playhead) says false, since its next write is a second away and a power cut loses that second at most.
    /// </summary>
    public static void WriteAllText(string path, string content, bool durable = true)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = TempPath(path);
        WriteTemp(tmp, content, durable);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// As <see cref="WriteAllText"/>, the old file kept as <c>.bak</c> first — the settings', the known-good rig's and
    /// (round 83) the recovery record's ladder: on a FAT32 or exFAT stick the replacing move is not atomic, and the
    /// copy kept before it is a whole record whatever the move left behind. With <paramref name="worthKeeping"/> the
    /// old file is read and kept only when the predicate says it is whole: a torn record — the very thing the backup
    /// is for — must never go over the whole one before it, or the ladder's last rung is the torn file twice.
    /// </summary>
    public static void WriteAllTextKeepingBackup(string path, string content, bool durable = true, Func<string, bool>? worthKeeping = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = TempPath(path);
        WriteTemp(tmp, content, durable);
        if (File.Exists(path) && Whole(path, worthKeeping)) File.Copy(path, path + ".bak", overwrite: true);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The old file is worth keeping when no predicate was given, or when it reads and the predicate says so; a file that cannot be read is not.</summary>
    private static bool Whole(string path, Func<string, bool>? worthKeeping)
    {
        if (worthKeeping is null) return true;
        try
        {
            return worthKeeping(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteTemp(string tmp, string content, bool durable)
    {
        using var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);                     // UTF-8 with no byte-order mark, as File.WriteAllText writes
        stream.Write(bytes, 0, bytes.Length);
        if (durable) stream.Flush(flushToDisk: true);
    }
}
