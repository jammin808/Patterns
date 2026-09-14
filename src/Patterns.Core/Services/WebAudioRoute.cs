namespace Patterns.Core.Services;

/// <summary>What became of a route asked of a web page.</summary>
public enum WebRouteOutcome
{
    /// <summary>No output was asked for: the machine's default, as the page would play anyway.</summary>
    NothingAsked,
    /// <summary>Asked, no answer yet.</summary>
    Pending,
    /// <summary>The page's players play on the output asked for.</summary>
    Routed,
    /// <summary>Asked to go back to the default, and it did.</summary>
    Default,
    /// <summary>The page could not route to the output asked for.</summary>
    Failed,
}

/// <summary>
/// The rule for a page's sound when an output was asked for: it fails closed. A page that cannot
/// put its players on the output the operator chose is muted until it can — its sound never falls
/// back to the machine's default output on its own, where a room may hear it from the wrong
/// speakers (the desk's own, a monitor) — and the words say the route failed and the sound is
/// held. The microphone permission that lets a page see the machine's outputs is granted for the
/// page's own origin only while a route is wanted, and taken back when it is not. Pure.
/// </summary>
public static class WebAudioRoute
{
    /// <summary>The outcome the page's own note reads as (<c>ReadSinkNote</c> writes it).</summary>
    public static WebRouteOutcome Classify(string? note, string wantedDevice)
    {
        note ??= "";
        if (wantedDevice.Length == 0) return note.Length == 0 || note.StartsWith("the machine's default", StringComparison.Ordinal) ? WebRouteOutcome.NothingAsked : WebRouteOutcome.Default;
        if (note.Length == 0 || note.StartsWith("nothing answered", StringComparison.Ordinal)) return WebRouteOutcome.Pending;
        if (note.StartsWith("routed to ", StringComparison.Ordinal)) return WebRouteOutcome.Routed;
        return WebRouteOutcome.Failed;
    }

    /// <summary>Whether the page's sound is held (muted by the desk) for this outcome: while a route is asked for and not in force.</summary>
    public static bool HoldSound(WebRouteOutcome outcome) => outcome is WebRouteOutcome.Pending or WebRouteOutcome.Failed;

    /// <summary>The note with the hold said: "not routed: no output called HDMI 3 — sound held, not on the default output".</summary>
    public static string HeldWords(string note) => note.Length == 0 ? "sound held until routed" : note + " — sound held, not on the default output";

    /// <summary>Whether the page needs the outputs' names — the microphone permission for its own origin — right now: only while a route is wanted.</summary>
    public static bool NeedsOutputNames(string wantedDevice) => wantedDevice.Length > 0;

    /// <summary>The origin a permission is granted for: the page's own scheme and host, "" for an address with none (a file, about:blank).</summary>
    public static string OriginOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        try
        {
            var uri = new Uri(url);
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";
            return uri.GetLeftPart(UriPartial.Authority);
        }
        catch (UriFormatException)
        {
            return "";
        }
    }
}
