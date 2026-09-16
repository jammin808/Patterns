using Patterns.Core.Services;

namespace Patterns.Core.Media;

/// <summary>How a page's video is played: in the browser (the page as the site drew it), or its stream handed to the native player (round 68.6).</summary>
public enum WebPlayVia
{
    Browser,
    NativePlayer,
}

/// <summary>A page's video resolved to a stream the native player opens: the picture's address, a separate sound stream when the site serves them apart, and when the addresses stop working.</summary>
public readonly record struct WebStream(string VideoUrl, string AudioUrl, DateTime ExpiresUtc)
{
    public bool HasSeparateAudio => AudioUrl.Length > 0;

    public bool Expired(DateTime nowUtc) => nowUtc >= ExpiresUtc;
}

/// <summary>
/// The native-player path for a page's video (round 68.6): a YouTube or Vimeo page whose look asks
/// for it has its stream address found by yt-dlp — a tool the operator brings, never bundled — and
/// the clip player (libVLC, decoding on the GPU) plays that address under the page's own mount key,
/// so every engine and renderer sees one input and the browser, its screencast, its JPEGs and its
/// decode are not in the chain at all. Pure: the words, the arguments, the parse and the search for
/// the tool; the desk runs the process and keeps the answers.
/// </summary>
public static class WebVideoResolver
{
    public const string ToolName = "yt-dlp";
    public const string ToolFile = "yt-dlp.exe";

    /// <summary>
    /// The stream asked for: one file with sound at 1080p or under first (one address, the simplest
    /// play), then any with sound, then the best picture and the best sound apart (the player takes
    /// the sound as its slave), then whatever there is.
    /// </summary>
    public const string Format = "best[height<=1080][ext=mp4][acodec!=none]/best[acodec!=none]/bestvideo[height<=1080]+bestaudio/best";

    /// <summary>How long an answer stands when the address does not say: the sites' addresses last hours, a fresh one costs a second.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);

    /// <summary>The tool's time to answer before the page stays in the browser.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    /// <summary>The native player is for a page that is a video: YouTube and Vimeo. A dashboard, a deck or a plain page stays in the browser whatever the look asks.</summary>
    public static bool Applies(PageService service, WebPlayVia via) => via == WebPlayVia.NativePlayer && service is PageService.YouTube or PageService.Vimeo;

    /// <summary>The arguments for the tool, one per entry (never a shell line): print the addresses, the format above, no playlists, no chatter.</summary>
    public static string[] ArgumentList(string url) => new[] { "-g", "-f", Format, "--no-playlist", "--no-warnings", "--no-progress", "--", url };

    /// <summary>
    /// The tool's answer: one address per line — the picture, then the sound when they come apart.
    /// The expiry is read from the address (`expire=` seconds since the epoch, the way the sites
    /// stamp them) or assumed. False when no address came back.
    /// </summary>
    public static bool TryParse(string? stdout, DateTime nowUtc, out WebStream stream)
    {
        stream = default;
        if (string.IsNullOrWhiteSpace(stdout)) return false;
        var urls = new List<string>();
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) urls.Add(line);
        }
        if (urls.Count == 0) return false;
        var video = urls[0];
        var audio = urls.Count > 1 ? urls[1] : "";
        stream = new WebStream(video, audio, ExpiryOf(video, nowUtc) ?? nowUtc + DefaultTtl);
        return true;
    }

    /// <summary>The `expire=` stamp on an address, as UTC; null without one.</summary>
    public static DateTime? ExpiryOf(string url, DateTime nowUtc)
    {
        var at = url.IndexOf("expire=", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var start = at + "expire=".Length;
        var end = start;
        while (end < url.Length && char.IsDigit(url[end])) end++;
        if (end == start || !long.TryParse(url.AsSpan(start, end - start), out var seconds)) return null;
        var when = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(seconds, 0, 253402300799)).UtcDateTime;
        return when > nowUtc ? when : null;                                                 // a stamp already passed is no expiry to plan on
    }

    /// <summary>The tool's complaint in one line: its ERROR line, else its first line, else that it said nothing.</summary>
    public static string FailureWords(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return $"{ToolName} gave nothing back";
        string? first = null;
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            first ??= line;
            if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) return Shorten(line);
        }
        return Shorten(first ?? $"{ToolName} gave nothing back");
    }

    private static string Shorten(string line) => line.Length > 160 ? line[..157] + "…" : line;

    /// <summary>
    /// Where the tool is: the path the operator named (the file, or a folder holding it), then beside
    /// the desk's own executable, then each folder on PATH. Null when nowhere — the page plays in the
    /// browser and the words say why. Pure over <paramref name="exists"/>.
    /// </summary>
    public static string? Find(string? configured, string baseDirectory, string? pathEnvironment, Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var c = configured.Trim();
            if (exists(c) && !c.EndsWith(Path.DirectorySeparatorChar) && !c.EndsWith(Path.AltDirectorySeparatorChar) && Path.HasExtension(c)) return c;
            var inFolder = Path.Combine(c, ToolFile);
            if (exists(inFolder)) return inFolder;
            if (exists(c) && Path.HasExtension(c)) return c;
        }
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            var beside = Path.Combine(baseDirectory, ToolFile);
            if (exists(beside)) return beside;
        }
        if (!string.IsNullOrWhiteSpace(pathEnvironment))
        {
            foreach (var folder in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = Path.Combine(folder, ToolFile);
                if (exists(candidate)) return candidate;
                var bare = Path.Combine(folder, ToolName);
                if (exists(bare)) return bare;
            }
        }
        return null;
    }

    /// <summary>The words when the look asks for the native player and the tool is not there.</summary>
    public static string ToolMissingNote =>
        $"Native player: {ToolName} is not on this machine, so the page plays in the browser. Put {ToolFile} beside Patterns.exe, on PATH, or name it under Play via on the Media page.";

    /// <summary>The words the desk says once, plainly, about what this path does — beside the picker and in the help.</summary>
    public const string TermsNote =
        "The native player fetches the video's stream outside the site's own player, through yt-dlp. That is the operator's call to make against the site's terms and the content owner's permission for the show; the browser is the default and stays the path for anything that is not a YouTube or Vimeo video.";
}
