using Patterns.Core.LowerThirds;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The installation's verbs: the announcement and the advert, the daily schedule, a device's line, the update and the restart.
/// </summary>
public sealed partial class ShowActions
{
    private ActionResult? RunInstall(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.DeviceSend:
                return _s.Devices.Send(a.Target, a.Value);

            // The Install page: announcements and adverts by hand, the schedule's switch.
            case ShowActionKind.Announce:
                return _s.Install.Announce(a.Target, a.Value, origin);
            case ShowActionKind.AnnounceOff:
                return _s.Install.EndOverride(SlotKind.Announcement, origin);
            case ShowActionKind.AdvertPlay:
                return _s.Install.PlayAdvert(a.Target, origin);
            case ShowActionKind.AdvertOff:
                return _s.Install.EndOverride(SlotKind.Advert, origin);
            case ShowActionKind.ScheduleOn:
                return _s.Install.SetSchedule(true, origin);
            case ShowActionKind.ScheduleOff:
                return _s.Install.SetSchedule(false, origin);

            // Remote administration: the passcode rides the target, the gate decides.
            case ShowActionKind.UpdateApply:
                return _s.Updates.Apply(a.Target, origin);
            case ShowActionKind.Restart:
            {
                if (!_s.Gate.Check(State.Install.AdminPasscode, a.Target, DateTime.UtcNow)) return ActionResult.Refused($"Restart refused — {_s.Gate.Reason}.");
                if (!_s.Updates.Supervised) return ActionResult.Refused("A restart in place needs the watchdog — start Patterns normally, with the watchdog on under Machine → Stability.");
                if (_s.ExitRequest is null) return ActionResult.Refused("No way to restart in this session.");
                var code = _s.PrepareRestart();
                Log.Info($"Restart requested from {origin.Label}.");
                return _s.ExitRequest(code) ? ActionResult.Requested("Restarting — the show comes straight back.") : ActionResult.Failed("The app did not accept the exit request.");
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// The daily schedule: fires every cue whose minute has come (once per day) to air, with
    /// origin "schedule", whether or not the sandbox is open. Called from the 1 s poll.
    /// </summary>
    public void RunSchedule(DateTime localNow)
    {
        if (_s.CueStack.SuspendsAutomation) return; // the caller is armed: only GO moves the picture
        foreach (var cue in State.LooksAndCues.Cues)
        {
            if (!LookService.ShouldFire(cue, localNow)) continue;
            cue.LastFiredDate = localNow.Date;
            var look = LookService.Find(State, cue.LookName);
            if (look is null)
            {
                _s.Journal.Record(ActionOrigin.Schedule.Label, ShowActionKind.ApplyLook.ToString(), cue.LookName,
                    ActionStatus.Refused.ToString(), $"Scheduled cue {cue.Time}: look '{cue.LookName}' not found.");
                continue;
            }
            var result = ApplyLook(look, ActionOrigin.Schedule, note: $"cue {cue.Time}");
            if (result.Ok) Log.Info($"Cue {cue.Time}: look '{look.Name}' applied.");
        }
    }

    /// <summary>"Next cue: 'Walk-in' at 18:00 tomorrow" — the same line on the Show page and in STATE.</summary>
    public static string NextScheduledText(ShowState state, DateTime localNow)
    {
        var next = LookService.NextCue(state.LooksAndCues.Cues, localNow);
        return next is { } n
            ? $"Next cue: '{n.Cue.LookName}' at {n.At:HH:mm}{(n.At.Date != localNow.Date ? " tomorrow" : "")}"
            : "No cues scheduled.";
    }
}
