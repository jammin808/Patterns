namespace Patterns.Core.Media;

/// <summary>Canonical mount keys — the same "ndi:"/"cap:"/"web:" scheme the operator input labels use.</summary>
public static class InputKeys
{
    public static string Video(string path) => path.Length == 0 ? "" : "vid:" + path;
    public static string Capture(string device) => device.Length == 0 ? "" : "cap:" + device;
    public static string Ndi(string source) => source.Length == 0 ? "" : "ndi:" + source;

    /// <summary>A page's key is its normalised address, so "example.com" and "https://example.com" share one browser.</summary>
    public static string Web(string url)
    {
        var normalized = Services.WebAddress.Normalize(url);
        return normalized.Length == 0 ? "" : "web:" + normalized;
    }

    /// <summary>A deck's key is its file: the same deck on two screens is one deck, on one page.</summary>
    public static string Deck(string path) => string.IsNullOrWhiteSpace(path) ? "" : "deck:" + path.Trim();

    /// <summary>
    /// This machine's own arcade — the game its loop draws — as an input: one picture whatever
    /// asks for it, so a pattern, a layer, the PiP and a wall tile share the frames the loop
    /// already drew, with no NDI, no network and no decode between the game and the wall.
    /// </summary>
    public const string ArcadeKey = "arcade:local";

    public static string Arcade() => ArcadeKey;
}
