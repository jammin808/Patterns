namespace Patterns.Core.Services;

/// <summary>
/// The one way a sidecar is written whole: the text to a temp file beside the target, then a move over the old
/// one, so a reader never sees half a record. ADR-008's lane covers the show's own files; every other sidecar —
/// the recovery record, the crash note, the quality profile, the playhead, the Spotify sign-in, an update request,
/// the known-good rig, the assistant's key, the journal's scrub — carried a hand-written copy of the same four lines
/// (rounds 14 to 76), and the copies drifted (one made the directory, one kept a .bak, one did neither). They are
/// one here; a source fence (AtomicFileTests) holds that no new copy appears outside the two named exemptions,
/// the output-ownership seam (it writes through its own file abstraction) and the management download (a stream).
/// </summary>
public static class AtomicFile
{
    /// <summary>The temp file beside the target — one per path, so two writers of one file collide on it and never on the target.</summary>
    public static string TempPath(string path) => path + ".tmp";

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> whole; the directory is made when it is missing.</summary>
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = TempPath(path);
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>As <see cref="WriteAllText"/>, the old file kept as <c>.bak</c> first — the settings' and the known-good rig's ladder.</summary>
    public static void WriteAllTextKeepingBackup(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = TempPath(path);
        File.WriteAllText(tmp, content);
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.Move(tmp, path, overwrite: true);
    }
}
