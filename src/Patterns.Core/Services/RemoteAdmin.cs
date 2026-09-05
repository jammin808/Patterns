using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Patterns.Core.Services;

/// <summary>
/// The passcode gate in front of remote administration — the web remote's ADMIN page, RESTART and
/// UPDATE APPLY on the wire. A compare that takes the same time whatever is typed, and a lock
/// after too many wrong tries, so a passcode on a LAN is a fence and not a puzzle. One per app.
/// </summary>
public sealed class AdminGate
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private int _failures;
    private DateTime _lockedUntil;

    /// <summary>Why the last check failed, in words for the caller.</summary>
    public string Reason { get; private set; } = "";

    public bool IsLocked(DateTime utcNow) => utcNow < _lockedUntil;

    /// <summary>True when <paramref name="offered"/> is the configured passcode; false with <see cref="Reason"/> set otherwise.</summary>
    public bool Check(string configured, string offered, DateTime utcNow)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                Reason = "no admin passcode is set — type one on the Install page first";
                return false;
            }
            if (utcNow < _lockedUntil)
            {
                Reason = $"too many wrong passcodes — try again in {Math.Ceiling((_lockedUntil - utcNow).TotalSeconds):0} s";
                return false;
            }
            var a = Encoding.UTF8.GetBytes(configured.Trim());
            var b = Encoding.UTF8.GetBytes((offered ?? "").Trim());
            var ok = a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
            if (ok)
            {
                _failures = 0;
                Reason = "";
                return true;
            }
            _failures++;
            if (_failures >= MaxFailures)
            {
                _failures = 0;
                _lockedUntil = utcNow + Lockout;
                Reason = $"wrong passcode — locked for {Lockout.TotalSeconds:0} s";
            }
            else
            {
                Reason = "wrong passcode";
            }
            return false;
        }
    }
}

/// <summary>
/// Everything a support engineer asks for, in one zip beside the settings: the logs, the journal,
/// the settings with every secret blanked, the last super-check, the metrics, what the watchdog
/// left behind, and a note of who, what and when. Nothing leaves the machine by itself.
/// </summary>
public static class SupportBundle
{
    public static readonly string[] Files =
    {
        "patterns.log", "patterns.log.old", "patterns.watchdog.log", ShowLog.FileName, "patterns.settings.json",
        SuperCheck.FileName, "patterns.metrics.csv", "patterns.recovery.json", WatchdogMarker.FileName,
    };

    /// <summary>The bundle's file name for a moment: patterns-support-20260905-1130.zip.</summary>
    public static string FileNameFor(DateTime localNow) => $"patterns-support-{localNow:yyyyMMdd-HHmm}.zip";

    /// <summary>Builds the zip; returns the entries it holds. A file that cannot be read is noted, never fatal.</summary>
    public static IReadOnlyList<string> Build(string baseDirectory, string zipPath, string info)
    {
        var included = new List<string>();
        var notes = new StringBuilder(info).AppendLine().AppendLine();
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in Files)
            {
                var path = Path.Combine(baseDirectory, name);
                if (!File.Exists(path)) continue;
                try
                {
                    if (name == "patterns.settings.json")
                    {
                        var entry = zip.CreateEntry(name);
                        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                        writer.Write(Redact(File.ReadAllText(path)));
                    }
                    else
                    {
                        // The app appends to its logs between opens: a shared read is enough.
                        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var entry = zip.CreateEntry(name);
                        using var target = entry.Open();
                        source.CopyTo(target);
                    }
                    included.Add(name);
                }
                catch (Exception ex)
                {
                    notes.AppendLine($"{name}: could not be read — {ex.Message}");
                }
            }
            var last = Path.Combine(UpdatePackage.Folder(baseDirectory), UpdateApply.NoteName);
            if (File.Exists(last))
            {
                try
                {
                    zip.CreateEntryFromFile(last, "updates/" + UpdateApply.NoteName);
                    included.Add("updates/" + UpdateApply.NoteName);
                }
                catch (Exception ex)
                {
                    notes.AppendLine($"{UpdateApply.NoteName}: could not be read — {ex.Message}");
                }
            }
            var infoEntry = zip.CreateEntry("bundle-info.txt");
            using (var writer = new StreamWriter(infoEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(notes.ToString());
            }
            included.Add("bundle-info.txt");
        }
        return included;
    }

    /// <summary>The passcode, the management token and any stored credential become ••• in the settings text; an empty value stays empty.</summary>
    public static string Redact(string settingsJson)
        => Regex.Replace(settingsJson,
            "(\"(AdminPasscode|ManagementToken|ClientSecret|ClientId|RefreshToken|AccessToken|Password|Passcode|Token|Key)\"\\s*:\\s*\")([^\"]*)(\")",
            m => m.Groups[1].Value + (m.Groups[3].Value.Length == 0 ? "" : "•••") + m.Groups[4].Value);
}

/// <summary>What a staged update package holds, and whether it can be used.</summary>
public sealed record UpdateInfo(string Path, string Version, string Notes, IReadOnlyList<string> Files, IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>"1.2.0 (patterns-update-1.2.0.zip, 3 files)" or the first problem.</summary>
    public string Summary => Ok
        ? $"{(Version.Length > 0 ? Version : "unversioned")} ({FileName}, {Files.Count} file{(Files.Count == 1 ? "" : "s")})"
        : $"{FileName}: {Problems[0]}";
}

/// <summary>
/// An update is a zip dropped into the updates folder beside the settings: Patterns.exe at its
/// root, anything else the build ships beside it (a libvlc folder), and a small manifest with the
/// version. Read here, never trusted: a path that climbs out of the folder or a package with no
/// exe is refused before anything is touched.
/// </summary>
public static class UpdatePackage
{
    public const string ManifestName = "patterns.update.json";
    public const string DefaultExe = "Patterns.exe";

    public static string Folder(string baseDirectory) => Path.Combine(baseDirectory, "updates");

    /// <summary>The newest zip in the updates folder, or null.</summary>
    public static string? Staged(string baseDirectory)
    {
        var folder = Folder(baseDirectory);
        if (!Directory.Exists(folder)) return null;
        return Directory.EnumerateFiles(folder, "*.zip").Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    public static UpdateInfo Inspect(string zipPath, string exeName = DefaultExe)
    {
        var problems = new List<string>();
        var files = new List<string>();
        var version = "";
        var notes = "";
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var hasExe = false;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) continue;                       // a folder
                if (!IsSafePath(name))
                {
                    problems.Add($"unsafe path in the package: {name}");
                    continue;
                }
                if (string.Equals(name, ManifestName, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var stream = entry.Open();
                        using var doc = JsonDocument.Parse(stream);
                        if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String) version = (v.GetString() ?? "").Trim();
                        if (doc.RootElement.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String) notes = (n.GetString() ?? "").Trim();
                    }
                    catch (JsonException ex)
                    {
                        problems.Add($"the manifest does not read: {ex.Message}");
                    }
                    continue;
                }
                if (string.Equals(name, exeName, StringComparison.OrdinalIgnoreCase)) hasExe = true;
                files.Add(name);
            }
            if (!hasExe) problems.Add($"no {exeName} at the root of the package");
            if (version.Length == 0) problems.Add($"no version — the package needs a {ManifestName} with {{ \"version\": \"1.2.0\" }}");
        }
        catch (Exception ex)
        {
            problems.Add($"not a package that opens: {ex.Message}");
        }
        return new UpdateInfo(zipPath, version, notes, files, problems);
    }

    /// <summary>A relative path that stays inside the app folder: no drive, no root, no "..".</summary>
    public static bool IsSafePath(string name)
    {
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Contains('\0')) return false;
        return name.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
    }

    public static bool IsSameVersion(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>The request the app leaves for the watchdog when it exits to be updated.</summary>
public sealed record UpdateRequest(string Package, string Version, DateTime RequestedUtc);

/// <summary>What an apply or a roll-back did.</summary>
public sealed record UpdateReport(bool Ok, string Message, IReadOnlyList<string> Replaced, IReadOnlyList<string> Added);

/// <summary>
/// The file side of an update, run by the watchdog between two starts of the app: every file
/// the package carries replaces its namesake — the old one moved into a backup folder first (a
/// running exe can be renamed on Windows where it cannot be overwritten), the new one written —
/// and, when the new app does not stay up through its proving period, every backup moved back.
/// </summary>
public static class UpdateApply
{
    public const string RequestName = "apply.json";
    public const string NoteName = "last-update.txt";

    /// <summary>How long the updated app must stay up before the update counts as good.</summary>
    public static readonly TimeSpan ProvingPeriod = TimeSpan.FromMinutes(2);

    public static void WriteRequest(string updatesDir, UpdateRequest request)
    {
        Directory.CreateDirectory(updatesDir);
        var path = Path.Combine(updatesDir, RequestName);
        File.WriteAllText(path + ".tmp", JsonUtil.Serialize(request));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static UpdateRequest? ReadRequest(string updatesDir)
    {
        var path = Path.Combine(updatesDir, RequestName);
        try
        {
            return File.Exists(path) ? JsonUtil.Deserialize<UpdateRequest>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Update request unreadable.", ex);
            return null;
        }
    }

    public static void ClearRequest(string updatesDir)
    {
        try
        {
            File.Delete(Path.Combine(updatesDir, RequestName));
        }
        catch
        {
            // Nothing to clear.
        }
    }

    /// <summary>A backup folder for a moment: updates/backup-20260905-0300.</summary>
    public static string BackupFolderFor(string updatesDir, DateTime localNow) => Path.Combine(updatesDir, $"backup-{localNow:yyyyMMdd-HHmm}");

    /// <summary>
    /// Puts the package's files in place, the old ones into <paramref name="backupDir"/> (its layout
    /// mirrors the app folder). A failure half-way rolls back what was done and reports it.
    /// </summary>
    public static UpdateReport Run(string zipPath, string appDir, string backupDir, string exeName = UpdatePackage.DefaultExe)
    {
        var info = UpdatePackage.Inspect(zipPath, exeName);
        if (!info.Ok) return new UpdateReport(false, string.Join("; ", info.Problems), Array.Empty<string>(), Array.Empty<string>());
        var replaced = new List<string>();
        var added = new List<string>();
        var root = Path.GetFullPath(appDir);
        try
        {
            Directory.CreateDirectory(backupDir);
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/') || string.Equals(name, UpdatePackage.ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.GetFullPath(Path.Combine(root, name));
                if (!target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidOperationException($"{name} lands outside the app folder");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var keep = Path.Combine(backupDir, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                    File.Move(target, keep, overwrite: true);       // a rename: allowed for a running exe on Windows
                    replaced.Add(name);
                }
                else
                {
                    added.Add(name);
                }
                entry.ExtractToFile(target, overwrite: false);
            }
            return new UpdateReport(true, $"{replaced.Count} file{(replaced.Count == 1 ? "" : "s")} replaced, {added.Count} added; the old files are in {backupDir}", replaced, added);
        }
        catch (Exception ex)
        {
            var back = RollBack(backupDir, appDir, added);
            return new UpdateReport(false, $"{ex.Message} — rolled back ({back.Message})", replaced, added);
        }
    }

    /// <summary>Every backed-up file back where it was; the files the package added are removed. Never throws: a file it cannot move is named in the message.</summary>
    public static UpdateReport RollBack(string backupDir, string appDir, IEnumerable<string> addedFiles)
    {
        var restored = new List<string>();
        var problems = new List<string>();
        var root = Path.GetFullPath(appDir);
        foreach (var name in addedFiles)
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(root, name));
                if (path.StartsWith(root, StringComparison.Ordinal) && File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                problems.Add($"{name}: {ex.Message}");
            }
        }
        if (Directory.Exists(backupDir))
        {
            foreach (var keep in Directory.EnumerateFiles(backupDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(backupDir, keep).Replace('\\', '/');
                try
                {
                    var target = Path.GetFullPath(Path.Combine(root, name));
                    if (!target.StartsWith(root, StringComparison.Ordinal)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target))
                    {
                        try
                        {
                            File.Delete(target);
                        }
                        catch
                        {
                            // A file still in use (the failed exe's image) steps aside instead.
                            File.Move(target, target + ".failed-" + DateTime.UtcNow.Ticks, overwrite: true);
                        }
                    }
                    File.Move(keep, target, overwrite: true);
                    restored.Add(name);
                }
                catch (Exception ex)
                {
                    problems.Add($"{name}: {ex.Message}");
                }
            }
        }
        var message = $"{restored.Count} file{(restored.Count == 1 ? "" : "s")} put back" + (problems.Count > 0 ? "; could not: " + string.Join(", ", problems) : "");
        return new UpdateReport(problems.Count == 0, message, restored, Array.Empty<string>());
    }

    /// <summary>After the updated app's first run: it stays ("commit") when it ran through the proving period or was closed cleanly; otherwise it goes ("rollback").</summary>
    public static string Verdict(int exitCode, bool killedForHang, TimeSpan ranFor)
    {
        if (ranFor >= ProvingPeriod) return "commit";
        return exitCode == 0 && !killedForHang ? "commit" : "rollback";
    }

    public static void WriteNote(string updatesDir, string note)
    {
        try
        {
            Directory.CreateDirectory(updatesDir);
            File.WriteAllText(Path.Combine(updatesDir, NoteName), note);
        }
        catch
        {
            // Best-effort, like the watchdog log.
        }
    }

    public static string ReadNote(string updatesDir)
    {
        try
        {
            var path = Path.Combine(updatesDir, NoteName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>An update a management server offers: where it is, what it is, and its SHA-256 so a wrong download never lands.</summary>
public sealed record ManagementUpdate(string Url, string Version, string Sha256);

/// <summary>What a check-in reply asks for.</summary>
public sealed record ManagementReply(IReadOnlyList<string> Commands, ManagementUpdate? Update, bool ApplyUpdate, bool Restart, string Note, string Problem)
{
    public static readonly ManagementReply Empty = new(Array.Empty<string>(), null, false, false, "", "");
}

/// <summary>
/// The check-in a site makes with its management server — one POST every few minutes with who
/// it is, how it is and what is on, and a reply that may carry protocol lines to run, an update to
/// stage, or a restart. The reply is trusted only with the shared token echoed back, and only over
/// HTTPS or a private network: a site behind a shop's router never needs an inbound port.
/// </summary>
public static class CheckIn
{
    public const int MaxCommands = 20;
    public const int MaxCommandLength = 200;

    /// <summary>The JSON a check-in posts: the site, the build, the machine, the health line and the same STATE every remote reads.</summary>
    public static string Payload(string site, string version, string machine, string health, string stateJson, DateTime utcNow)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("site", site);
            writer.WriteString("version", version);
            writer.WriteString("machine", machine);
            writer.WriteString("health", health);
            writer.WriteString("utc", utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            writer.WritePropertyName("state");
            try
            {
                using var doc = JsonDocument.Parse(stateJson);
                doc.RootElement.WriteTo(writer);
            }
            catch (JsonException)
            {
                writer.WriteNullValue();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Why a management URL cannot be used: not https, or http to somewhere that is not this machine or a private network. Null when it can.</summary>
    public static string? ProblemWithUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "not an address (https://server/patterns/checkin)";
        if (uri.Scheme == Uri.UriSchemeHttps) return null;
        if (uri.Scheme != Uri.UriSchemeHttp) return "only https:// (or http:// on this machine or a private network)";
        var host = uri.Host.ToLowerInvariant();
        if (host is "localhost" or "127.0.0.1" or "::1" or "[::1]") return null;
        if (host.StartsWith("10.") || host.StartsWith("192.168.") || host.EndsWith(".local")) return null;
        if (host.StartsWith("172.") && host.Split('.') is { Length: 4 } parts && int.TryParse(parts[1], out var second) && second is >= 16 and <= 31) return null;
        return "http:// is only for this machine or a private network — use https:// across the internet";
    }

    /// <summary>
    /// Reads a reply. With a token configured, a reply that does not echo it is ignored with a
    /// problem noted; commands are capped and trimmed; an update needs a URL, a version and a SHA-256.
    /// </summary>
    public static ManagementReply Parse(string json, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(json)) return ManagementReply.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ManagementReply.Empty with { Problem = "the reply is not an object" };
            if (expectedToken.Length > 0)
            {
                var token = root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                if (!string.Equals(token, expectedToken, StringComparison.Ordinal)) return ManagementReply.Empty with { Problem = "the reply did not carry the shared token — ignored" };
            }
            var commands = new List<string>();
            if (root.TryGetProperty("commands", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var line = (item.GetString() ?? "").Trim();
                    if (line.Length == 0 || line.Length > MaxCommandLength) continue;
                    commands.Add(line);
                    if (commands.Count >= MaxCommands) break;
                }
            }
            ManagementUpdate? update = null;
            if (root.TryGetProperty("update", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                var url = Str(u, "url");
                var version = Str(u, "version");
                var sha = Str(u, "sha256").ToLowerInvariant();
                if (url.Length > 0 && version.Length > 0 && sha.Length == 64) update = new ManagementUpdate(url, version, sha);
            }
            var apply = root.TryGetProperty("applyUpdate", out var a) && a.ValueKind == JsonValueKind.True;
            var restart = root.TryGetProperty("restart", out var r) && r.ValueKind == JsonValueKind.True;
            return new ManagementReply(commands, update, apply, restart, Str(root, "note"), "");
        }
        catch (JsonException ex)
        {
            return ManagementReply.Empty with { Problem = "the reply is not JSON: " + ex.Message };
        }
    }

    /// <summary>The SHA-256 of a file, lower-case hex.</summary>
    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
}
