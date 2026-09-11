using System.Text.Json;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Answers the wire (TCP, the web remote, OSC, Companion, the devices) on the UI thread. A parsed
/// line is a show action already — the same typed verb the operator's own keys use — so the router
/// has nothing to translate: it runs the action through <see cref="ShowActions"/>, shapes the
/// reply, answers the handshakes and queries itself, and builds the state JSON remotes display.
/// Needs no window.
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

    /// <summary>
    /// A handshake or a query is answered here; an action — every verb of the wire is one, parsed
    /// straight into the show's vocabulary — goes through the one executor, and the reply carries
    /// what a controller wants to know about it.
    /// </summary>
    private string Execute(RemoteCommand cmd, ActionOrigin origin)
    {
        switch (cmd.Kind)
        {
            case RemoteCommandKind.Ping:
                return ControlProtocol.Ok("PONG");
            case RemoteCommandKind.Status:
                return ControlProtocol.Ok(StateJson());
            case RemoteCommandKind.Unknown:
                return ControlProtocol.Err($"unknown command '{cmd.Text}'");
            case RemoteCommandKind.Hello:
                return ControlProtocol.Ok(); // the connection renamed its origin; nothing to run
            case RemoteCommandKind.CueList:
                return ControlProtocol.Ok(CueListJson());
        }

        var action = cmd.Action;
        var result = _services.Actions.Execute(action, origin);
        var stack = _services.CueStack;
        switch (action.Kind)
        {
            case ShowActionKind.CueGo:
            {
                // The OK payload carries the record, so a controller knows what happened, not just that it was heard.
                if (result.Status == ActionStatus.Requested && result.Message.StartsWith("CONFIRM", StringComparison.Ordinal))
                {
                    return ControlProtocol.Ok(JsonSerializer.Serialize(new { outcome = "Confirm", confirm = stack.ConfirmText, standby = StandbyRow(stack.StandbyCue) }));
                }
                return result.Ok
                    ? ControlProtocol.Ok(JsonSerializer.Serialize(new { outcome = result.Status.ToString(), last = LastRow(stack), standby = StandbyRow(stack.StandbyCue) }))
                    : ControlProtocol.Err(result.Message);
            }
            case ShowActionKind.CueStandby:
                return result.Ok
                    ? ControlProtocol.Ok(JsonSerializer.Serialize(new { standby = StandbyRow(stack.StandbyCue) }))
                    : ControlProtocol.Err(result.Message);
            default:
                return result.Ok ? ControlProtocol.Ok() : ControlProtocol.Err(result.Message);
        }
    }

    /// <summary>The deck the program shows — the click-through's pages — or null when none is on air.</summary>
    private object? DeckRow()
    {
        var wanted = MediaLocator.FindWantedInputs(_services.Bus.Current).FirstOrDefault(w => w.Kind == MediaLocator.WantedKind.Deck);
        if (wanted is null) return null;
        var deck = Patterns.Core.Media.InputBus.For(wanted.Key) as Patterns.Core.Media.IDeckSource;
        var ends = MediaLocator.FindActiveMedia(_services.AirState, MediaSource.Deck)?.DeckEndsWithGo ?? true;
        return new
        {
            file = System.IO.Path.GetFileName(wanted.Target),
            kind = DeckConversion.KindOf(wanted.Target),                    // PDF, PowerPoint, Keynote, Impress
            page = deck?.Page ?? 0,
            count = deck?.PageCount ?? 0,
            ended = deck is { PageCount: > 0 } d && d.AtEnd,
            endsWithGo = ends,
            converting = deck is PendingDeckSource { Failed: false },        // LibreOffice is still making the PDF
            status = deck?.StatusText ?? "Opening the deck…",
        };
    }

    /// <summary>The audio playlist for remotes: playing, the track on (or up next when stopped), its place and the count, the next, the clock, the rows by place.</summary>
    private object AudioRow(ShowState s)
    {
        var p = _services.AudioPlayer;
        var length = p.LengthSeconds;
        var position = p.PositionSeconds;
        return new
        {
            playing = s.AudioPlayer.Playing,
            track = p.CurrentName,
            n = p.NowIndex + 1,
            count = p.Count,
            next = p.NextName,
            position = (int)Math.Round(position),
            length = (int)Math.Round(length),
            remaining = length > 0 ? (int)Math.Round(Math.Max(0, length - position)) : 0,
            positionText = VideoClock.Format(position),
            lengthText = length > 0 ? VideoClock.Format(length) : "",
            remainingText = length > 0 ? VideoClock.Format(Math.Max(0, length - position)) : "",
            shuffle = s.AudioPlayer.Shuffle,
            loop = s.AudioPlayer.Loop,
            level = (int)Math.Round(s.AudioPlayer.VolumePct),
            status = p.Status,
            items = p.Names().Select((name, i) => new { n = i + 1, name }).ToArray(),
        };
    }

    /// <summary>The clip on air — the caller's VT clock: the file, where it is, what is left, the ten-second word — or null when none.</summary>
    private object? VideoRow()
    {
        var r = _services.VideoOnAir();
        if (r is null) return null;
        return new
        {
            file = r.Name,
            role = r.Role.ToString().ToLowerInvariant(),                   // program, playlist, stinger, layer
            tag = VideoClock.Tag(r),                                       // VT, AUDIO, STINGER CLIP, PLAYLIST
            position = (int)Math.Round(r.PositionSeconds),
            length = (int)Math.Round(r.LengthSeconds),                     // 0 while the decoder has not said
            remaining = (int)Math.Round(r.RemainingSeconds),
            positionText = VideoClock.Format(r.PositionSeconds),
            lengthText = r.HasLength ? VideoClock.Format(r.LengthSeconds) : "",
            remainingText = r.HasLength && !r.Loops ? VideoClock.Format(r.RemainingSeconds) : "",
            text = VideoClock.Describe(r),                                 // "VT sponsor.mp4 · 1:02 / 3:30 · 2:28 left"
            chip = VideoClock.Chip(r),                                     // "VT 2:28"
            playing = r.Playing,
            ended = r.Ended,
            loops = r.Loops,                                               // a loop never comes out
            @out = r.InLast(VideoClock.OutWarningSeconds),                 // the last ten seconds
            call = VideoClock.Call(r),                                     // "OUT IN 7"
        };
    }

    /// <summary>The web page the program shows — what WEB KEY / CLICK / TYPE reach with no page named — with its service's actions; null when none.</summary>
    private object? WebRow()
    {
        var wanted = MediaLocator.FindWantedInputs(_services.Bus.Current).FirstOrDefault(w => w.Kind == MediaLocator.WantedKind.Web);
        if (wanted is null) return null;
        var page = Patterns.Core.Media.InputBus.For(wanted.Key) as Patterns.Core.Media.IWebSource;
        var url = page?.CurrentUrl is { Length: > 0 } current ? current : wanted.Target;
        var preset = WebPresets.For(url);
        return new
        {
            page = _services.State.InputLabel(wanted.Key, WebAddress.ShortName(url)),
            url,
            title = page?.Title ?? "",
            service = preset.Service == PageService.Page ? "" : preset.Name,
            actions = preset.Actions.Select(a => new { id = a.Id, label = a.Label }).ToArray(),
        };
    }

    /// <summary>The lower third on screen (arriving, holding or leaving), by name; "" when none.</summary>
    private string LowerThirdOnAir()
    {
        var air = _services.AirState.LowerThirds;
        return Patterns.Core.LowerThirds.LowerThirdClock.IsLive(air, ShowClock.UtcNow) ? air.Active?.Name ?? "" : "";
    }

    /// <summary>The name the lower third on screen carries (a library entry's, or the design's own); "" when none is on.</summary>
    private string LowerThirdPersonOnAir()
    {
        var air = _services.AirState.LowerThirds;
        return Patterns.Core.LowerThirds.LowerThirdClock.IsLive(air, ShowClock.UtcNow) ? air.Active?.PersonName ?? "" : "";
    }

    /// <summary>The lower third in the preview (EDIT SAFE open, a design showing in the edited state), by name; "" when none.</summary>
    private string LowerThirdInPreview()
    {
        if (!_services.LowerThirdInPreview()) return "";
        var preview = _services.State.LowerThirds;
        return Patterns.Core.LowerThirds.LowerThirdClock.IsLive(preview, ShowClock.UtcNow) ? preview.Active?.Name ?? "" : "";
    }

    private string LowerThirdPersonInPreview()
    {
        if (!_services.LowerThirdInPreview()) return "";
        var preview = _services.State.LowerThirds;
        return Patterns.Core.LowerThirds.LowerThirdClock.IsLive(preview, ShowClock.UtcNow) ? preview.Active?.PersonName ?? "" : "";
    }

    /// <summary>The targets faded to black on their own (FADE with a scope): the count, the wall's names for them, and whether the sound is down with the picture.</summary>
    private object BlackRow()
    {
        var targets = _services.Bus.BlackTargets;
        var geometry = Rig.Geometry(_services.State, _services.Screens.All);
        var names = targets.Select(t => geometry.LabelFor(_services.State, t)).ToArray();
        return new
        {
            count = names.Length,
            text = string.Join(" · ", names),
            audio = _services.Stingers.BlackAudioActive,
            targets = names,
        };
    }

    /// <summary>State summary for remotes. UI thread only.</summary>
    public string StateJson()
    {
        var s = _services.State;
        var airLook = LookService.Find(s, _services.AirLookId)?.Name ?? "";
        var previewLook = _services.Sandbox.Active ? LookService.Find(s, _services.PreviewLookId)?.Name ?? "" : "";
        // One reading of "is the look on air still what is on the screens", shared with the Looks
        // page and the Show panel — the page said PROGRAM · EDITED for rounds while every remote
        // saw a plain green, and two surfaces disagreeing about a fact is worse than neither having it.
        var lookEdited = _services.LookTally.AirEdited();
        var offLook = _services.LookTally.TargetsOffLook();
        var payload = new
        {
            show = s.Name,
            rev = Rev?.Invoke() ?? 0,
            airLabel = _services.AirLabel,
            cuestack = CueStackJson(),
            blackout = s.Blackout,
            black = BlackRow(),                                            // the screens faded to black on their own: how many, their names, whether the sound went with the picture
            live = _services.Outputs.IsLive,
            review = _services.Bus.ReviewOnMultiview,                      // the preview fills every multiview
            frozen = _services.Bus.Frozen,                                 // every output holds its frame
            editSafe = _services.Sandbox.Active,                           // EDIT SAFE open: there is a preview, and a TAKE to come
            previousLook = LookService.Find(s, _services.PreviousAirLookId)?.Name ?? "",   // what LOOKBACK returns to
            airLook = airLook,                                             // the look on air, by name ("" = none recorded, or the picture moved on)
            previewLook = previewLook,                                     // the look loaded into the preview, by name
            pattern = _services.AirState.Pattern.Kind.ToString(),          // what kind of picture is on air: Media, LedWall, ProjectionBlend…
            patternKinds = Enum.GetNames<PatternKind>(),                    // every kind a PATTERN key can ask for, in the desk's order

            // The show's looks in order — a bank of keys labels itself from these: n, the name, the F-key, on air, in the preview.
            // The show's looks in order — a bank of keys labels itself from these: n, the name, the
            // F-key, on air, in the preview. Whether that look has been changed since it was
            // recalled is lookEdited below, once, rather than repeated on every row: on a
            // sixteen-look show a per-row copy is thirty-two redundant fields on the hottest JSON
            // on the wire, pushed four times a second to every controller and to the phone, and a
            // row can only be edited if it is the row that is on air.
            looks = s.LooksAndCues.Looks.Select((l, i) => new
            {
                n = i + 1,
                name = l.Name,
                slot = l.Hotkey,
                air = l.Name == airLook && airLook.Length > 0,
                preview = l.Name == previewLook && previewLook.Length > 0,
            }).ToArray(),
            lookEdited = lookEdited,                                       // the picture has moved since the look was recalled
            lookScreensOff = offLook.Count,                                // and on how many screens
            presenter = PresenterState(s),
            screens = _services.Actions.RemoteScreens(),
            audio = AudioRow(s),                                           // the audio playlist: the track on, its place, what is left, the rows
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
            people = s.LowerThirds.Entries.Select((e, n) => new { n = n + 1, name = e.Name, role = e.Role }).ToArray(),
            lowerThirdPerson = LowerThirdPersonOnAir(),                    // the name on screen right now, or ""
            lowerThirdPreview = LowerThirdInPreview(),                     // the design in the preview for a sign-off, or ""
            lowerThirdPreviewPerson = LowerThirdPersonInPreview(),
            lowerThirdDefault = s.LowerThirds.DefaultDesign?.Name ?? "",   // the show's ★ design — where a person goes with none on air
            lowerThirdEdited = _services.LowerThirdAirEdited(),            // the design on air differs from the edited one: LT UPDATE
            web = WebRow(),                                                // the web page on air and its service's actions, or null
            deck = DeckRow(),                                              // the deck on air: file, page, count, ended — or null
            video = VideoRow(),                                            // the clip on air: file, where it is, what is left, the ten-second word — or null
            weather = WeatherRow(),                                        // the weather chip: on air, its view, the place, the line and the figure
            overlays = OverlaysRow(),                                      // the clock, the message, the countdown, the logo and the PiP: on air, their settings, what is left, and the line
            interactive = s.Interactive.Enabled,                           // the Interactive area is on: devices open
            devices = _services.Devices.Rows(),                            // every device: name, link, address, open, status, the last lines
            install = _services.Install.StateRow(DateTime.Now),            // the install: the schedule's switch, the programme on, the override on, the next change, the rows, the update

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
            // The stream's health rides with its state everywhere: the phone's SHOW tab, an OSC
            // feedback bundle, a Stream Deck's colour. One reading, so nothing can disagree.
            stream = new
            {
                active = s.Stream.Active,
                status = _services.Stream.Status,
                word = _services.Stream.Health.Word,
                health = _services.Stream.Health.Line,
                light = _services.Stream.Health.Light.ToString(),
                hue = _services.Stream.Health.Hue,
                up = _services.Stream.Health.Uptime,
                fps = Math.Round(_services.Stream.Health.Facts.Fps, 1),
                trouble = _services.Stream.Health.IsTrouble,
                destinations = _services.Stream.Health.Facts.Destinations,
            },
            health = HealthMonitor.Summary(DateTime.UtcNow),
            quality = new                                                    // the effects' ladder: the mode, the level (0 full … 3), its factor, the Machine page's words
            {
                mode = _services.Quality.Ladder.Mode.ToString(),
                level = _services.Quality.Ladder.Level,
                factor = Math.Round(_services.Quality.Ladder.Factor, 2),
                text = _services.Quality.Describe(),
            },
            memory = new                                                     // the memory ceilings: the app's working set against its ceiling, and the line
            {
                appMB = Math.Round(_services.Metrics.Current?.RamAppMB ?? -1),
                ceilingMB = Math.Round(MemoryBudget.For(_services.Metrics.Current?.RamTotalMB ?? -1, Patterns.Core.Media.ImageCache.Capacity, VideoEngine.MaxMounts).AppCeilingMB),
                text = _services.Metrics.MemoryCeilingLine(),
            },
            machine = MachineRow(),
            beacon = new { sending = _services.Beacon.Sending, listening = _services.Beacon.Listening, main = _services.Beacon.WatchText },
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>The weather chip for remotes: on air, the view, the place, the line the desk reads, the figure alone, the source and the status.</summary>
    private object WeatherRow()
    {
        var overlay = _services.AirState.Overlays.Weather;
        var settings = _services.State.Weather;
        var report = _services.Bus.Weather;
        var now = DateTime.Now;
        var card = report is null ? null : WeatherWords.Card(report, overlay.View, now, settings.Units);
        return new
        {
            on = overlay.Enabled,
            view = overlay.View switch { WeatherView.RestOfDay => "day", WeatherView.Tomorrow => "tomorrow", _ => "now" },
            place = settings.Place,
            text = WeatherWords.Line(report, overlay.View, now, settings.Units, settings.Place),
            figure = card?.Figure ?? "",
            sky = card?.Sky.ToString() ?? "",
            source = report?.Source ?? "",
            status = _services.Weather.Status,
        };
    }

    /// <summary>
    /// The overlays for remotes: the clock (on, its hours, seconds, date, what it reads now), the
    /// message (on, the words, scrolling), the countdown (on, its phase, label, target, what is
    /// left), the logo (on, and whether a file is set), the PiP, and the line the phone shows.
    /// </summary>
    private object OverlaysRow()
    {
        var air = _services.AirState;
        var now = DateTime.Now;
        var utc = DateTime.UtcNow;
        var (phase, remaining, text) = OverlayControl.CountdownWords(air.Countdown, now, utc);
        return new
        {
            clock = new
            {
                on = air.Overlays.Clock.Enabled,
                hours = air.Overlays.Clock.TwentyFourHour ? 24 : 12,
                seconds = air.Overlays.Clock.ShowSeconds,
                date = air.Overlays.Clock.ShowDate,
                text = OverlayControl.ClockText(air.Overlays.Clock, now),
            },
            message = new { on = air.Overlays.Message.Enabled, text = air.Overlays.Message.Text, scroll = air.Overlays.Message.Scroll },
            countdown = new
            {
                on = air.Countdown.Enabled,
                phase,
                label = air.Countdown.Label,
                target = OverlayControl.CountdownTarget(air.Countdown),
                remaining,
                text,
            },
            logo = new { on = air.Overlays.Logo.Enabled, file = _services.State.Brand.LogoPath.Length > 0 },
            pip = new { on = air.Overlays.Pip.Enabled },
            text = OverlayControl.Line(air.Overlays, air.Countdown, now, utc),
        };
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
            plannedStart = c.PlannedStart,
            plannedSeconds = c.PlannedSeconds,
            followSeconds = c.FollowSeconds,
            mark = c.Mark == CueMark.None ? "" : c.Mark.ToString().ToLowerInvariant(),
            summary = CueSummary.Describe(_services.State, c),
            broken = report.ReasonFor(c.Id),
        }).ToArray();
        return JsonSerializer.Serialize(new { name = stack.Stack.Name, listRev = ListRev(), cues = rows });
    }

    /// <summary>Where the day stands — the block a remote shows beside the standby card.</summary>
    private object TimingJson()
    {
        var stack = _services.CueStack;
        var t = stack.Timing();
        return new
        {
            offsetSeconds = t.Offset is { } o ? (int?)Math.Round(o.TotalSeconds) : null,
            offset = t.OffsetText,
            nextBreak = MarkJson(t.NextBreak),
            lunch = MarkJson(t.Lunch),
            end = MarkJson(t.End),
            follow = stack.FollowText(),
        };
    }

    private static object? MarkJson(MarkEstimate? m) => m is null ? null : new
    {
        number = m.Cue.Number,
        name = m.Cue.Name,
        expected = CueTiming.FormatClock(m.EstimatedAt),
        planned = m.PlannedAt is { } p ? CueTiming.FormatClock(p) : null,
        deltaSeconds = m.Delta is { } d ? (int?)Math.Round(d.TotalSeconds) : null,
        atLeast = m.Uncertain,
        text = m.Text,
    };

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
            timing = TimingJson(),
        };
    }

    private static object? StandbyRow(RunCueConfig? cue)
        => cue is null ? null : new { id = cue.Id, number = cue.Number, name = cue.Name, requireConfirm = cue.RequireConfirm, notes = cue.Notes, plannedStart = cue.PlannedStart, followSeconds = cue.FollowSeconds };

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
                foreach (var ch in $"{c.Id}|{c.Number}|{c.Name}|{c.Enabled}|{c.RequireConfirm}|{c.Ready}|{c.PlannedStart}|{c.PlannedSeconds}|{c.FollowSeconds}|{c.Mark}|")
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
