namespace Patterns.Core.Media;

/// <summary>Canonical mount keys — the same "ndi:"/"cap:"/"web:" scheme the operator input labels use.</summary>
public static class InputKeys
{
    public static string Video(string path) => path.Length == 0 ? "" : "vid:" + path;
    public static string Capture(string device) => device.Length == 0 ? "" : "cap:" + device;
    public static string Ndi(string source) => source.Length == 0 ? "" : "ndi:" + source;

    /// <summary>
    /// A page's key is its normalised address, so "example.com" and "https://example.com" share one browser. Round 89:
    /// the key never carries a pass — every reader of it (the Eye, the context, STATE's residency, the input nicknames)
    /// publishes what it holds — so a private address that reaches it raw is keyed by its unkept form, which opens
    /// nothing; a kept address is its own key.
    /// </summary>
    public static string Web(string url)
    {
        var normalized = Services.WindowAddresses.Held(Services.WebAddress.Normalize(url));
        return normalized.Length == 0 ? "" : "web:" + normalized;
    }

    /// <summary>True for a page's key whose address is held — kept on a machine or written without its pass: black until it resolves.</summary>
    public static bool IsHeldWeb(string? key)
        => key is not null && key.StartsWith("web:", StringComparison.Ordinal) && Services.WindowAddresses.IsHeld(key[4..]);

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
