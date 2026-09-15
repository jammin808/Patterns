using Patterns.Core.LowerThirds;
using Patterns.Rendering.LowerThirds;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The overlays' verbs: the countdown, the message, the clock, the logo, the PiP, the weather chip — each on, off or toggled, and set.
/// </summary>
public sealed partial class ShowActions
{
    private ActionResult? RunOverlays(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.CountdownStart:
            {
                if (a.Value.Trim().Length == 0)
                {
                    // A bare START: the countdown as it is set up — the time of day it points at, else its duration from now.
                    return StartCountdownAsSetUp();
                }
                if (!double.TryParse(a.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
                {
                    return ActionResult.Refused("A countdown needs a number of minutes above zero.");
                }
                _s.EditAir(air =>
                {
                    air.Countdown.TargetKind = CountdownTargetKind.Duration;
                    air.Countdown.DurationMinutes = minutes;
                    air.Countdown.ArmedAtUtc = DateTime.UtcNow;
                    air.Countdown.Enabled = true;
                });
                return ActionResult.Done($"Countdown running: {minutes:0.#} min.");
            }
            case ShowActionKind.CountdownStop:
                _s.EditAir(air => air.Countdown.Enabled = false);
                return ActionResult.Done("Countdown off.");
            case ShowActionKind.CountdownFollow:
            {
                var follow = !a.Value.Equals("off", StringComparison.OrdinalIgnoreCase);
                _s.EditAir(air => air.Countdown.FollowPlan = follow);
                if (follow) _s.FollowPlanNow();   // the target is the standby cue's planned start from this moment
                return ActionResult.Done(follow ? "Countdown follows the running order — the standby cue's planned start is its target, and moves with the plan." : "Countdown no longer follows the running order.");
            }
            case ShowActionKind.CountdownToggle:
                // One key for the countdown, the way the clock, the message, the logo, the PiP and
                // the weather chip already have one: off when it is on air, else started as the
                // desk has it set up. A phone, a Stream Deck key, an OSC address and a cue all say
                // the same thing, and the key that turned it on turns it off again.
                if (_s.AirState.Countdown.Enabled)
                {
                    _s.EditAir(air => air.Countdown.Enabled = false);
                    return ActionResult.Done("Countdown off.");
                }
                return StartCountdownAsSetUp();
            case ShowActionKind.MessageOn:
                _s.EditAir(air =>
                {
                    if (a.Value.Length > 0) air.Overlays.Message.Text = a.Value;
                    air.Overlays.Message.Enabled = true;
                });
                return ActionResult.Done(a.Value.Length > 0 ? $"Message on: '{a.Value}'." : "Message on.");
            case ShowActionKind.MessageOff:
                _s.EditAir(air => air.Overlays.Message.Enabled = false);
                return ActionResult.Done("Message off.");
            case ShowActionKind.ClockOn:
                _s.EditAir(air => air.Overlays.Clock.Enabled = true);
                return ActionResult.Done("Clock on.");
            case ShowActionKind.ClockOff:
                _s.EditAir(air => air.Overlays.Clock.Enabled = false);
                return ActionResult.Done("Clock off.");
            case ShowActionKind.ClockToggle:
            {
                var on = !_s.AirState.Overlays.Clock.Enabled;
                _s.EditAir(air => air.Overlays.Clock.Enabled = on);
                return ActionResult.Done(on ? "Clock on." : "Clock off.");
            }
            case ShowActionKind.ClockFormat:
            {
                var hours = a.Value.Trim();
                if (hours is not ("12" or "24")) return ActionResult.Refused($"'{a.Value}' is not a clock format — 12 or 24.");
                _s.EditAir(air => air.Overlays.Clock.TwentyFourHour = hours == "24");
                return ActionResult.Done($"Clock: {hours}-hour.");
            }
            case ShowActionKind.ClockSeconds:
            {
                var on = OverlayControl.SwitchTo(a.Value, _s.AirState.Overlays.Clock.ShowSeconds);
                _s.EditAir(air => air.Overlays.Clock.ShowSeconds = on);
                return ActionResult.Done(on ? "Clock: seconds shown." : "Clock: seconds hidden.");
            }
            case ShowActionKind.ClockDate:
            {
                var on = OverlayControl.SwitchTo(a.Value, _s.AirState.Overlays.Clock.ShowDate);
                _s.EditAir(air => air.Overlays.Clock.ShowDate = on);
                return ActionResult.Done(on ? "Clock: date shown." : "Clock: date hidden.");
            }
            case ShowActionKind.MessageToggle:
            {
                var on = !_s.AirState.Overlays.Message.Enabled;
                _s.EditAir(air => air.Overlays.Message.Enabled = on);
                return ActionResult.Done(on ? "Message on." : "Message off.");
            }
            case ShowActionKind.MessageScroll:
            {
                var on = OverlayControl.SwitchTo(a.Value, _s.AirState.Overlays.Message.Scroll);
                _s.EditAir(air => air.Overlays.Message.Scroll = on);
                return ActionResult.Done(on ? "Message scrolls." : "Message stands still.");
            }
            case ShowActionKind.CountdownTo:
            {
                if (!CountdownService.TryParseTime(a.Value, out var timeOfDay)) return ActionResult.Refused($"'{a.Value}' is not a time of day — HH:mm, 24-hour.");
                var target = $"{(int)timeOfDay.TotalHours:00}:{timeOfDay.Minutes:00}";
                _s.EditAir(air =>
                {
                    air.Countdown.TargetKind = CountdownTargetKind.TimeOfDay;
                    air.Countdown.TargetTime = target;
                    air.Countdown.Enabled = true;
                });
                return ActionResult.Done($"Countdown to {target}.");
            }
            case ShowActionKind.CountdownLabel:
            {
                var label = a.Value.Trim();
                _s.EditAir(air => air.Countdown.Label = label);
                return ActionResult.Done(label.Length > 0 ? $"Countdown label: '{label}'." : "Countdown label cleared.");
            }
            case ShowActionKind.LogoOn:
            case ShowActionKind.LogoOff:
            case ShowActionKind.LogoToggle:
            {
                var on = a.Kind == ShowActionKind.LogoOn || (a.Kind == ShowActionKind.LogoToggle && !_s.AirState.Overlays.Logo.Enabled);
                _s.EditAir(air => air.Overlays.Logo.Enabled = on);
                return ActionResult.Done(!on ? "Logo off." : _s.State.Brand.LogoPath.Length > 0 ? "Logo on." : "Logo on — pick the logo file on the Branding page for it to show.");
            }
            case ShowActionKind.PipOn:
            case ShowActionKind.PipOff:
            case ShowActionKind.PipToggle:
            {
                var on = a.Kind == ShowActionKind.PipOn || (a.Kind == ShowActionKind.PipToggle && !_s.AirState.Overlays.Pip.Enabled);
                _s.EditAir(air => air.Overlays.Pip.Enabled = on);
                return ActionResult.Done(on ? "PiP on." : "PiP off.");
            }
            case ShowActionKind.OverlaysOff:
            {
                var was = new List<string>(6);
                var onAir = _s.AirState;
                if (onAir.Overlays.Clock.Enabled) was.Add("the clock");
                if (onAir.Overlays.Message.Enabled) was.Add("the message");
                if (onAir.Countdown.Enabled) was.Add("the countdown");
                if (onAir.Overlays.Logo.Enabled) was.Add("the logo");
                if (onAir.Overlays.Pip.Enabled) was.Add("the PiP");
                if (onAir.Overlays.Weather.Enabled) was.Add("the weather chip");
                if (onAir.Overlays.Badge.Enabled) was.Add("the Patterns badge");
                _s.EditAir(air =>
                {
                    air.Overlays.Clock.Enabled = false;
                    air.Overlays.Message.Enabled = false;
                    air.Countdown.Enabled = false;
                    air.Overlays.Logo.Enabled = false;
                    air.Overlays.Pip.Enabled = false;
                    air.Overlays.Weather.Enabled = false;
                    air.Overlays.Badge.Enabled = false; // a clean picture is clean of the maker's mark too
                });
                return ActionResult.Done(was.Count == 0 ? "No overlay was on." : "Overlays off: " + string.Join(", ", was) + ".");
            }
            case ShowActionKind.WeatherOn:
            {
                var view = WeatherWords.ParseView(a.Value);
                _s.EditAir(air =>
                {
                    air.Overlays.Weather.Enabled = true;
                    if (view is { } v) air.Overlays.Weather.View = v;
                });
                _s.Weather.RefreshNow();
                return ActionResult.Done(_s.State.Weather.HasLocation ? "Weather on." : "Weather on — set a place on the Overlays page for a forecast.");
            }
            case ShowActionKind.WeatherOff:
                _s.EditAir(air => air.Overlays.Weather.Enabled = false);
                return ActionResult.Done("Weather off.");
            case ShowActionKind.WeatherToggle:
            {
                var on = !_s.AirState.Overlays.Weather.Enabled;
                _s.EditAir(air => air.Overlays.Weather.Enabled = on);
                if (on) _s.Weather.RefreshNow();
                return ActionResult.Done(on ? "Weather on." : "Weather off.");
            }
            case ShowActionKind.WeatherView:
            {
                if (WeatherWords.ParseView(a.Value) is not { } view)
                {
                    return ActionResult.Refused($"'{a.Value}' is not a weather view — now, day or tomorrow.");
                }
                _s.EditAir(air => air.Overlays.Weather.View = view);
                return ActionResult.Done($"Weather: {WeatherWords.ViewName(view).ToLowerInvariant()}.");
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// The countdown on air as the desk has it set up: the time of day it points at when that
    /// reads, else its own duration armed from now. What a bare START means, and the "on" half of
    /// the toggle — the two must never drift apart.
    /// </summary>
    private ActionResult StartCountdownAsSetUp()
    {
        var setUp = _s.AirState.Countdown;
        if (setUp.TargetKind == CountdownTargetKind.TimeOfDay && CountdownService.TryParseTime(setUp.TargetTime, out _))
        {
            var to = setUp.TargetTime;
            _s.EditAir(air => air.Countdown.Enabled = true);
            return ActionResult.Done($"Countdown to {to}.");
        }
        var configured = setUp.DurationMinutes;
        _s.EditAir(air =>
        {
            air.Countdown.TargetKind = CountdownTargetKind.Duration;
            air.Countdown.ArmedAtUtc = DateTime.UtcNow;
            air.Countdown.Enabled = true;
        });
        return ActionResult.Done($"Countdown running: {configured:0.#} min.");
    }
}
