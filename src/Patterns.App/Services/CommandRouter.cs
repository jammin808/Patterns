using System.Text.Json;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Turns remote commands (TCP, web remote, Companion) into show actions on the UI thread —
/// the same typed verbs the operator's own clicks use — and builds the state JSON remotes
/// display. Needs no window: everything goes through <see cref="ShowActions"/>.
/// </summary>
public sealed class CommandRouter
{
    private readonly AppServices _services;

    public CommandRouter(AppServices services)
    {
        _services = services;
    }

    /// <summary>Runs one command on the UI thread; returns the protocol response line.</summary>
    public async Task<string> ExecuteAsync(RemoteCommand cmd, ActionOrigin? origin = null)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => Execute(cmd, origin ?? new ActionOrigin(OriginKind.Tcp)));
        }
        catch (Exception ex)
        {
            Log.Error("Remote command failed.", ex);
            return ControlProtocol.Err(ex.Message);
        }
    }

    /// <summary>A revision the tablet long-polls on: bumped by the control service on every push-worthy change.</summary>
    public Func<long>? Rev { get; set; }

    private string Execute(RemoteCommand cmd, ActionOrigin origin)
    {
        switch (cmd.Kind)
        {
            case RemoteCommandKind.Ping:
                return ControlProtocol.Ok("PONG");
            case RemoteCommandKind.Status:
                return ControlProtocol.Ok(StateJson());
            case RemoteCommandKind.Unknown:
                return ControlProtocol.Err($"unknown command '{cmd.TextArg}'");
            case RemoteCommandKind.Hello:
                return ControlProtocol.Ok(); // the connection renamed its origin; nothing to run
            case RemoteCommandKind.CueList:
                return ControlProtocol.Ok(CueListJson());
        }

        var stack = _services.CueStack;
        switch (cmd.Kind)
        {
            case RemoteCommandKind.CueGo:
            {
                // The OK payload carries the record, so a controller knows what happened, not just that it was heard.
                var go = _services.Actions.Execute(new ShowAction(ShowActionKind.CueGo, cmd.TextArg), origin);
                if (go.Status == ActionStatus.Requested && go.Message.StartsWith("CONFIRM", StringComparison.Ordinal))
                {
                    return ControlProtocol.Ok(JsonSerializer.Serialize(new { outcome = "Confirm", confirm = stack.ConfirmText, standby = StandbyRow(stack.StandbyCue) }));
                }
                return go.Ok
                    ? ControlProtocol.Ok(JsonSerializer.Serialize(new { outcome = go.Status.ToString(), last = LastRow(stack), standby = StandbyRow(stack.StandbyCue) }))
                    : ControlProtocol.Err(go.Message);
            }
            case RemoteCommandKind.CueStandbyNext:
            case RemoteCommandKind.CueStandbyPrev:
                return stack.StandbyMove(cmd.Kind == RemoteCommandKind.CueStandbyNext ? +1 : -1)
                    ? ControlProtocol.Ok(JsonSerializer.Serialize(new { standby = StandbyRow(stack.StandbyCue) }))
                    : ControlProtocol.Err("no cue that way");
            case RemoteCommandKind.CueStandby:
            {
                var cue = stack.Stack.Cues.FirstOrDefault(c => CueNumber.Compare(c.Number, cmd.TextArg) == 0 && CueNumber.Parse(cmd.TextArg) is not null)
                          ?? stack.Stack.Cues.FirstOrDefault(c => string.Equals(c.Name, cmd.TextArg, StringComparison.OrdinalIgnoreCase));
                if (cue is null) return ControlProtocol.Err($"no cue '{cmd.TextArg}'");
                stack.Standby(cue.Id);
                return ControlProtocol.Ok(JsonSerializer.Serialize(new { standby = StandbyRow(cue) }));
            }
            case RemoteCommandKind.CueHoldOn:
            case RemoteCommandKind.CueHoldOff:
                stack.SetHold(cmd.Kind == RemoteCommandKind.CueHoldOn, origin);
                return ControlProtocol.Ok();
            case RemoteCommandKind.CueArmOn:
            case RemoteCommandKind.CueArmOff:
                if (!_services.State.Control.RemotesMayArm) return ControlProtocol.Err("remotes may not arm — allow it on the Remote page");
                stack.SetArmed(cmd.Kind == RemoteCommandKind.CueArmOn, origin);
                return ControlProtocol.Ok();
            case RemoteCommandKind.StopAll:
            {
                var stop = _services.Actions.Execute(ShowActionKind.StopAll, origin);
                return stop.Ok ? ControlProtocol.Ok() : ControlProtocol.Err(stop.Message);
            }
        }

        if (ToAction(cmd) is not { } action) return ControlProtocol.Err($"unknown command '{cmd.Kind}'");
        var result = _services.Actions.Execute(action, origin);
        return result.Ok ? ControlProtocol.Ok() : ControlProtocol.Err(result.Message);
    }

    /// <summary>The wire vocabulary → the show's vocabulary. Pure; unit tested.</summary>
    public static ShowAction? ToAction(RemoteCommand cmd)
    {
        var byNumberOrName = cmd.IntArg > 0 ? cmd.IntArg.ToString() : cmd.TextArg;
        return cmd.Kind switch
        {
            RemoteCommandKind.OutputsOn => new ShowAction(ShowActionKind.OutputsOn),
            RemoteCommandKind.OutputsOff => new ShowAction(ShowActionKind.OutputsOff),
            RemoteCommandKind.BlackoutOn => new ShowAction(ShowActionKind.BlackoutOn),
            RemoteCommandKind.BlackoutOff => new ShowAction(ShowActionKind.BlackoutOff),
            RemoteCommandKind.BlackoutToggle => new ShowAction(ShowActionKind.BlackoutToggle),
            RemoteCommandKind.Identify => new ShowAction(ShowActionKind.Identify),
            RemoteCommandKind.Look => cmd.IntArg > 0
                ? new ShowAction(ShowActionKind.ApplyLookHotkey, cmd.IntArg.ToString())
                : new ShowAction(ShowActionKind.ApplyLook, cmd.TextArg),
            RemoteCommandKind.Next => new ShowAction(ShowActionKind.PresenterNext),
            RemoteCommandKind.Prev => new ShowAction(ShowActionKind.PresenterPrev),
            RemoteCommandKind.ScreenOn => new ShowAction(ShowActionKind.ScreenOn, cmd.IntArg.ToString()),
            RemoteCommandKind.ScreenOff => new ShowAction(ShowActionKind.ScreenOff, cmd.IntArg.ToString()),
            RemoteCommandKind.ScreenToggle => new ShowAction(ShowActionKind.ScreenToggle, cmd.IntArg.ToString()),
            RemoteCommandKind.GroupOn => new ShowAction(ShowActionKind.CanvasOn, cmd.TextArg),
            RemoteCommandKind.GroupOff => new ShowAction(ShowActionKind.CanvasOff, cmd.TextArg),
            RemoteCommandKind.AudioPlay => new ShowAction(ShowActionKind.AudioPlay),
            RemoteCommandKind.AudioStop => new ShowAction(ShowActionKind.AudioStop),
            RemoteCommandKind.MusicPlay => new ShowAction(ShowActionKind.SpotifyPlay, byNumberOrName),
            RemoteCommandKind.MusicPause => new ShowAction(ShowActionKind.SpotifyPause),
            RemoteCommandKind.MusicNext => new ShowAction(ShowActionKind.SpotifyNext),
            RemoteCommandKind.MusicVolume => new ShowAction(ShowActionKind.SpotifyVolume, "", cmd.TextArg),
            RemoteCommandKind.ToneOn => new ShowAction(ShowActionKind.ToneOn),
            RemoteCommandKind.ToneOff => new ShowAction(ShowActionKind.ToneOff),
            RemoteCommandKind.DuckOn => new ShowAction(ShowActionKind.DuckOn),
            RemoteCommandKind.DuckOff => new ShowAction(ShowActionKind.DuckOff),
            RemoteCommandKind.DuckToggle => new ShowAction(ShowActionKind.DuckToggle),
            RemoteCommandKind.Stinger => new ShowAction(ShowActionKind.StingerFire, byNumberOrName),
            RemoteCommandKind.Vog => new ShowAction(ShowActionKind.StingerFire, byNumberOrName, "vog"),
            RemoteCommandKind.Sting => new ShowAction(ShowActionKind.StingerFire, byNumberOrName, "sting"),
            RemoteCommandKind.StingerStop => new ShowAction(ShowActionKind.StingerStop),
            RemoteCommandKind.PlaylistSection => new ShowAction(ShowActionKind.PlaylistPart, byNumberOrName),
            RemoteCommandKind.StreamOn => new ShowAction(ShowActionKind.StreamStart),
            RemoteCommandKind.StreamOff => new ShowAction(ShowActionKind.StreamStop),
            RemoteCommandKind.LowerThirdShow => new ShowAction(ShowActionKind.LowerThirdShow, byNumberOrName),
            RemoteCommandKind.LowerThirdHide => new ShowAction(ShowActionKind.LowerThirdHide),
            _ => null,
        };
    }

    /// <summary>The lower third on screen (arriving, holding or leaving), by name; "" when none.</summary>
    private string LowerThirdOnAir()
    {
        var air = _services.AirState.LowerThirds;
        return Patterns.Core.LowerThirds.LowerThirdClock.IsLive(air, ShowClock.UtcNow) ? air.Active?.Name ?? "" : "";
    }

    /// <summary>State summary for remotes. UI thread only.</summary>
    public string StateJson()
    {
        var s = _services.State;
        var payload = new
        {
            show = s.Name,
            rev = Rev?.Invoke() ?? 0,
            airLabel = _services.AirLabel,
            cuestack = CueStackJson(),
            blackout = s.Blackout,
            live = _services.Outputs.IsLive,
            looks = s.LooksAndCues.Looks.Select(l => new { name = l.Name, slot = l.Hotkey }).ToArray(),
            presenter = PresenterState(s),
            screens = _services.Actions.RemoteScreens(),
            audio = new
            {
                playing = s.AudioPlayer.Playing,
                track = System.IO.Path.GetFileName(s.AudioPlayer.Path),
            },
            music = new
            {
                on = s.Spotify.Enabled,
                playing = s.Spotify.Playing,
                level = (int)Math.Round(s.Spotify.LevelPct),
                now = _services.Spotify.NowPlaying,
                device = _services.Spotify.DeviceLabel,
                status = _services.Spotify.Status,
                items = s.Spotify.Items.Select((i, n) => new { n = n + 1, name = i.DisplayName }).ToArray(),
            },
            tone = s.Tone.Enabled,
            lowerThirds = s.LowerThirds.Designs.Select((d, n) => new { n = n + 1, name = d.Name }).ToArray(),
            lowerThird = LowerThirdOnAir(),                                // the design on screen right now, or ""
            stingers = s.Stingers.Items.Select((i, n) => new
            {
                n = n + 1,
                name = i.DisplayName,
                kind = i.Kind == StingerKind.Sting ? "sting" : "vog",
                source = i.Source == StingerSource.EffectPulse ? "pulse" : "file",
            }).ToArray(),
            stingerPlaying = s.Stingers.PlayingName,                       // whatever owns the show right now, either kind
            stingerKind = _services.Stingers.StingOnAir.Length > 0 ? "sting"
                        : _services.Stingers.VogOnAir.Length > 0 ? "vog" : "",
            vogSound = _services.Stingers.VogSoundOnAir,                   // a VOG sound over the show — over a stinger too
            stingHold = _services.Stingers.HoldName,                       // "" = not holding
            duck = s.Stingers.DuckActive,                                  // the live duck for an announcement from the room
            sections = SectionRows(s),
            playlist = _services.Playlist.Status,
            nextCue = ShowActions.NextScheduledText(s, DateTime.Now),
            stream = new { active = s.Stream.Active, status = _services.Stream.Status },
            health = HealthMonitor.Summary(DateTime.UtcNow),
            machine = MachineRow(),
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Playlist parts for remotes; empty when the playlist has a single unnamed flow.</summary>
    private static object[] SectionRows(ShowState s)
    {
        var options = MediaLocator.FindActivePlaylist(s)?.Playlist ?? s.Pattern.Media.Playlist;
        if (options.Sections.Count <= 1) return Array.Empty<object>();
        var active = Math.Clamp(options.ActiveSection, 0, options.Sections.Count - 1);
        return options.Sections
            .Select((x, i) => (object)new { n = i + 1, name = x.Name, active = i == active })
            .ToArray();
    }

    /// <summary>Machine health for remotes: rounded numbers plus how many advisor lines want attention.</summary>
    private object MachineRow()
    {
        var m = _services.Metrics.Current;
        var advice = _services.Metrics.Suggestions.Count(x => x.Severity >= HealthSeverity.Advice);
        return m is null
            ? new { cpu = -1.0, ram = -1.0, fps = 0.0, battery = false, advice }
            : new
            {
                cpu = Math.Round(m.CpuSystemPct, 0),
                ram = Math.Round(m.RamSystemPct, 0),
                fps = Math.Round(m.OutputWindows > 0 ? m.OutputFps : m.PreviewFps, 0),
                battery = m.OnBattery,
                advice,
            };
    }

    /// <summary>Builds StateJson from any thread.</summary>
    public Task<string> StateJsonAsync() => Dispatcher.UIThread.InvokeAsync(StateJson).GetTask();

    /// <summary>The caller's whole list with notes — GET /api/cues and CUE LIST, refetched when listRev changes.</summary>
    public string CueListJson()
    {
        var stack = _services.CueStack;
        var report = CueValidator.Validate(_services.State, stack.Stack, _services.ValidationContext);
        var rows = stack.Stack.Cues.Select(c => new
        {
            id = c.Id,
            number = c.Number,
            name = c.Name,
            enabled = c.Enabled,
            requireConfirm = c.RequireConfirm,
            ready = c.Ready,
            track = c.Track,
            notes = c.Notes,
            summary = CueSummary.Describe(_services.State, c),
            broken = report.ReasonFor(c.Id),
        }).ToArray();
        return JsonSerializer.Serialize(new { name = stack.Stack.Name, listRev = ListRev(), cues = rows });
    }

    public Task<string> CueListJsonAsync() => Dispatcher.UIThread.InvokeAsync(CueListJson).GetTask();

    /// <summary>The compact block every STATE push carries; the full list rides /api/cues.</summary>
    private object CueStackJson()
    {
        var stack = _services.CueStack;
        var rt = stack.Runtime;
        var cues = stack.Stack.Cues;
        var standby = stack.StandbyCue;
        var standbyIndex = standby is null ? -1 : cues.IndexOf(standby);
        var next = new List<object>();
        for (var i = standbyIndex + 1; i < cues.Count && next.Count < 6; i++)
        {
            if (cues[i].Enabled) next.Add(new { id = cues[i].Id, number = cues[i].Number, name = cues[i].Name });
        }
        var previous = stack.LastCue;
        return new
        {
            armed = rt.Armed,
            hold = rt.Hold,
            seq = rt.Seq,
            listRev = ListRev(),
            confirm = stack.ConfirmText,
            program = new { label = _services.AirLabel },
            previous = previous is null ? null : new { id = previous.Id, number = previous.Number, name = previous.Name },
            standby = StandbyRow(standby),
            next = next.ToArray(),
            last = LastRow(stack),
            history = stack.History.Take(8).Select(RowJson).ToArray(),
        };
    }

    private static object? StandbyRow(RunCueConfig? cue)
        => cue is null ? null : new { id = cue.Id, number = cue.Number, name = cue.Name, requireConfirm = cue.RequireConfirm, notes = cue.Notes };

    private static object? LastRow(CueStackService stack) => stack.History.Count == 0 ? null : RowJson(stack.History[0]);

    private static object RowJson(CueExecutionRecord r) => new
    {
        id = r.CueId,
        number = r.Number,
        name = r.Name,
        outcome = r.Outcome.ToString(),
        error = r.IsFailure ? r.Detail : "",
        at = r.AtUtc,
        origin = r.Origin,
        actionsDone = r.ActionsDone,
        actionsTotal = r.ActionsTotal,
    };

    /// <summary>Changes when the list's shape does (ids, numbers, names, flags) — remotes refetch /api/cues on it.</summary>
    private long ListRev()
    {
        unchecked
        {
            long h = 1469598103934665603;
            foreach (var c in _services.CueStack.Stack.Cues)
            {
                foreach (var ch in $"{c.Id}|{c.Number}|{c.Name}|{c.Enabled}|{c.RequireConfirm}|{c.Ready}|")
                {
                    h = (h ^ ch) * 1099511628211;
                }
            }
            return h & 0x7FFFFFFFFFFF;
        }
    }

    /// <summary>The clicker list as the remotes have always seen the presenter: armed, index, count, step names.</summary>
    private object PresenterState(ShowState s)
    {
        var clicker = CueStacks.Clicker(s);
        var rt = _services.Cues.For(clicker);
        return new
        {
            armed = rt.Armed,
            index = rt.CurrentIndex,
            count = clicker.Cues.Count,
            steps = clicker.Cues.Select(c => c.Name).ToArray(),
        };
    }
}
