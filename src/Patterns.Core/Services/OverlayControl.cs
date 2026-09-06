using System.Globalization;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The overlays as a remote drives and reads them — the clock, the message, the countdown, the
/// logo, the PiP: the switch words a verb carries, what the clock reads now, the countdown's
/// phase and what is left, and the one line every remote shows. Pure; unit tested.
/// </summary>
public static class OverlayControl
{
    /// <summary>on / off as asked ("on", "1", "show" / "off", "0", "hide"), else the opposite of what is (toggle, or no word).</summary>
    public static bool SwitchTo(string value, bool current) => value.Trim().ToLowerInvariant() switch
    {
        "on" or "1" or "true" or "show" or "yes" => true,
        "off" or "0" or "false" or "hide" or "no" => false,
        _ => !current,
    };

    /// <summary>"14:32:07" / "2:32 PM" — the time as the clock overlay draws it now (its hours, its seconds).</summary>
    public static string ClockText(ClockOverlay clock, DateTime localNow)
    {
        var format = clock.TwentyFourHour
            ? (clock.ShowSeconds ? "HH:mm:ss" : "HH:mm")
            : (clock.ShowSeconds ? "h:mm:ss tt" : "h:mm tt");
        return localNow.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>What the countdown points at: "19:30" for a time of day, "15 min" for a duration.</summary>
    public static string CountdownTarget(CountdownConfig cfg)
        => cfg.TargetKind == CountdownTargetKind.TimeOfDay
            ? cfg.TargetTime
            : cfg.DurationMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " min";

    /// <summary>
    /// The countdown for a remote: the phase ("running", "over", "off"), what is left in whole
    /// seconds, and the words — "12:34 · SHOW STARTS IN", "OVER · STARTING NOW", or "off".
    /// </summary>
    public static (string Phase, int RemainingSeconds, string Text) CountdownWords(CountdownConfig cfg, DateTime localNow, DateTime utcNow)
    {
        var status = CountdownService.Evaluate(cfg, localNow, utcNow);
        switch (status.Phase)
        {
            case CountdownPhase.Running:
            {
                var seconds = (int)Math.Ceiling(status.Remaining.TotalSeconds);
                var label = cfg.Label.Trim();
                return ("running", seconds, CountdownService.Format(status.Remaining) + (label.Length > 0 ? " · " + label : ""));
            }
            case CountdownPhase.Over:
            {
                var end = cfg.EndMessage.Trim();
                return ("over", 0, "OVER" + (end.Length > 0 ? " · " + end : ""));
            }
            default:
                return ("off", 0, cfg.Enabled ? "set up, not counting" : "off");
        }
    }

    /// <summary>A countdown that is counting: the remotes want a push every second while it runs, like the VT clock.</summary>
    public static bool CountsEverySecond(CountdownConfig cfg, DateTime localNow, DateTime utcNow)
        => cfg.Enabled && CountdownService.Evaluate(cfg, localNow, utcNow).Phase == CountdownPhase.Running;

    /// <summary>
    /// One line for the phone's OVERLAYS tab and the feedback: "Clock 24 h with seconds · Message:
    /// WELCOME (scrolling) · Countdown 12:34 to 19:30 · Logo · PiP · Weather" — or "No overlays on."
    /// </summary>
    public static string Line(OverlaySet overlays, CountdownConfig countdown, DateTime localNow, DateTime utcNow)
    {
        var parts = new List<string>(6);
        if (overlays.Clock.Enabled)
        {
            parts.Add($"Clock {(overlays.Clock.TwentyFourHour ? "24" : "12")} h" + (overlays.Clock.ShowSeconds ? " with seconds" : "") + (overlays.Clock.ShowDate ? " and the date" : ""));
        }
        if (overlays.Message.Enabled)
        {
            var text = overlays.Message.Text.Trim();
            parts.Add("Message: " + (text.Length > 0 ? text : "(blank)") + (overlays.Message.Scroll ? " (scrolling)" : ""));
        }
        if (countdown.Enabled)
        {
            var words = CountdownWords(countdown, localNow, utcNow);
            parts.Add(words.Phase == "running"
                ? $"Countdown {CountdownService.Format(TimeSpan.FromSeconds(words.RemainingSeconds))} to {CountdownTarget(countdown)}"
                : words.Phase == "over" ? "Countdown over" : "Countdown set up, not counting");
        }
        if (overlays.Logo.Enabled) parts.Add("Logo");
        if (overlays.Pip.Enabled) parts.Add("PiP");
        if (overlays.Weather.Enabled) parts.Add("Weather");
        return parts.Count == 0 ? "No overlays on." : string.Join(" · ", parts);
    }
}
