using Patterns.Audience;
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
public sealed class CommandRouter : IRouter
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
            // The arcade's status from a desk is the arcade nodes' — asked on their wires, off the UI thread.
            if (cmd.Kind is RemoteCommandKind.ArcadeStatus or RemoteCommandKind.PlayStatus && _services.Profile != NodeKind.Arcade
                && await UiThread.InvokeAsync(() => _services.Nodes.Arcades().Count) > 0)
            {
                var head = cmd.Kind == RemoteCommandKind.ArcadeStatus ? "ARCADE " : "PLAY ";
                return ControlProtocol.Ok(await _services.Nodes.AskArcadesAsync(head + (cmd.Text.Length == 0 ? "STATUS" : cmd.Text)));
            }
            if (cmd.Kind == RemoteCommandKind.AssistantAsk)
            {
                // The key is the desk's alone unless the desk said a node may ask — Remote page, or the setting.
                if (!_services.State.Control.AssistantOnWire) return ControlProtocol.Err("the assistant is not on the wire — Remote page, 'Nodes may ask the assistant'");
                // The desk's assistant for a node (a hub's queue, a caller's brief): one ask, the reply as JSON, the network off the UI thread.
                var question = cmd.Text.StartsWith("moderate ", StringComparison.OrdinalIgnoreCase) ? PlayService.ModerationQuestion(cmd.Text[9..].Trim()) : cmd.Text;
                var answer = await UiThread.InvokeAsync(() => _services.Assistant.AskAsync(question));
                return ControlProtocol.Ok(JsonUtil.SerializeCompact(new { sent = answer.Sent, status = answer.Status, inScope = answer.Reply?.InScope ?? false, reply = answer.Reply?.Reply ?? "" }));
            }
            return await UiThread.InvokeAsync(() => Execute(cmd, origin ?? new ActionOrigin(OriginKind.Tcp)));
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
            case RemoteCommandKind.TwinStatus:
                return ControlProtocol.Ok(_services.Twin.StatusJson());
            case RemoteCommandKind.ShowLockStatus:
                return ControlProtocol.Ok(_services.ShowLock.StatusJson());
            case RemoteCommandKind.CalibrationStatus:
                return ControlProtocol.Ok(_services.Calibration.StatusJson());
            case RemoteCommandKind.NodesStatus:
                return ControlProtocol.Ok(_services.Nodes.StatusJson());
            case RemoteCommandKind.StageStatus:
                return ControlProtocol.Ok(_services.Stage.StatusJson());
            case RemoteCommandKind.ArcadeStatus:
                return ControlProtocol.Ok(_services.Arcade.StatusJson(cmd.Text));
            case RemoteCommandKind.PlayStatus:
                return ControlProtocol.Ok(_services.Play.StatusJson(cmd.Text));
            case RemoteCommandKind.RigDayStatus:
                return ControlProtocol.Ok(_services.RigDay.StatusJson(cmd.Text));
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
        var deck = Patterns.Rendering.Media.InputBus.For(wanted.Key) as Patterns.Rendering.Media.IDeckSource;
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
        var page = Patterns.Rendering.Media.InputBus.For(wanted.Key) as Patterns.Rendering.Media.IWebSource;
        var url = page?.CurrentUrl is { Length: > 0 } current ? current : wanted.Target;
        var preset = WebPresets.For(url);
        return new
        {
            page = _services.State.InputLabel(wanted.Key, WebAddress.ShortName(url)),
            url,
            title = page?.Title ?? "",
            service = preset.Service == PageService.Page ? "" : preset.Name,
            fps = (int)Math.Round(page?.FrameRate ?? 0),                    // frames the page delivered in the last second: a video's rate, 0 for a still page
            actions = preset.Actions.Select(a => new { id = a.Id, label = a.Label }).ToArray(),
            player = PlayerRow(wanted.Key),                                   // the page's video: where it is, paused, an advert; null with no player
            arm = ArmRow(wanted.Key),                                         // the armed VT on this page; null with none
        };
    }

    /// <summary>
    /// The routing matrix as the remotes read it: whether it is in charge, the sources the show has,
    /// and each destination with its rows — the sources on it at their levels, the live gain the
    /// lane runs at (the VOG's duck folded in), its delay, its mute, its VOG mode and its meter.
    /// </summary>
    private object AudioRoutingRow()
    {
        var s = _services.State;
        var graph = _services.AudioGraph;
        var vog = _services.AudioPlayer.VogSoundPlaying;
        var plan = AudioRouting.Resolve(s, vog);
        return new
        {
            on = s.AudioRouting.Enabled,
            words = AudioRouting.Words(s),
            status = graph?.Status ?? "",
            vog,
            sources = AudioRouting.Sources(s).Select(src => new { id = src.Id, label = src.Label, kind = src.Kind.ToString().ToLowerInvariant() }).ToArray(),
            destinations = s.AudioRouting.Destinations.Where(d => d.Key.Length > 0).Select(d =>
            {
                var p = plan.FirstOrDefault(x => string.Equals(x.Key, d.Key, StringComparison.OrdinalIgnoreCase));
                return new
                {
                    key = d.Key,
                    label = AudioRouting.DestinationLabel(s, d.Key),
                    kind = AudioRouting.IsNdi(d.Key) ? "ndi" : "device",
                    delayMs = d.DelayMs,
                    trimDb = d.TrimDb,
                    mute = d.Mute,
                    vogMode = d.VogMode.ToString().ToLowerInvariant(),
                    peakDb = Math.Round(graph?.PeakDb(d.Key) ?? Db.Floor, 1),
                    error = graph?.LaneError(d.Key) ?? "",
                    lanes = s.AudioRouting.Routes.Where(r => r.Enabled && string.Equals(r.Destination, d.Key, StringComparison.OrdinalIgnoreCase)).Select(r => new
                    {
                        source = r.Source,
                        db = r.LevelDb,
                        gain = Math.Round(p?.GainFor(r.Source) ?? 0, 4),   // the plan's gain now: the level, the trim, the mute and the VOG mode
                        liveDb = Math.Round(graph?.LiveDb(d.Key, r.Source) ?? Db.Floor, 1),
                    }).ToArray(),
                };
            }).ToArray(),
            pages = _services.WebIn.AudioRouteNotes().Select(n => new { page = _services.State.InputLabel(n.Key, WebAddress.ShortName(n.Key[4..])), device = n.Device, note = n.Note }).ToArray(),
        };
    }

    /// <summary>What a page's player last said — its clock, paused, an advert over it — or null with no player answering.</summary>
    private object? PlayerRow(string key)
    {
        var r = _services.WebIn.ReadingOf(key);
        if (!r.Ok) return null;
        return new
        {
            pos = Math.Round(r.Position, 1),
            dur = Math.Round(r.Duration, 1),
            text = r.ClockText,
            paused = r.Paused,
            ad = r.AdShowing,
            muted = r.Muted,
        };
    }

    /// <summary>The arm on a page: where it plays from and whether the look put it there; null with nothing armed and nothing played.</summary>
    private object? ArmRow(string key)
    {
        var arm = _services.WebIn.ArmOf(key);
        if (!arm.Armed && arm.PlayedUtc is null) return null;
        return new
        {
            armed = arm.Armed,
            at = Math.Round(arm.StartSeconds, 1),
            atText = WebVt.TimeText(arm.StartSeconds),
            byLook = arm.ByLook,
            played = arm.PlayedUtc is not null,
            phase = _services.WebIn.PhaseOf(key).ToString(),                                        // observed: PreparedObserved, PlayingObserved, Failed — never what was sent
            words = WebVt.Words(arm, _services.WebIn.ReadingOf(key), ShowClock.UtcNow, _services.WebIn.PhaseOf(key)),
        };
    }

    /// <summary>
    /// The armed web VT anywhere on the desk — a page in the preview, one opened for a cue ahead,
    /// the page on air — as the phone, the deck and the Show page read it: its name, its mark,
    /// its words. Null with nothing armed.
    /// </summary>
    private object? WebArmedRow()
    {
        foreach (var (key, arm, reading) in _services.WebIn.Armed())
        {
            return new
            {
                page = _services.State.InputLabel(key, WebAddress.ShortName(key[4..])),
                key,
                at = Math.Round(arm.StartSeconds, 1),
                atText = WebVt.TimeText(arm.StartSeconds),
                byLook = arm.ByLook,
                preRolled = _services.WebIn.IsPreRolled(key),
                phase = _services.WebIn.PhaseOf(key).ToString(),
                words = WebVt.Words(arm, reading, ShowClock.UtcNow, _services.WebIn.PhaseOf(key)),
                @short = WebVt.ShortWords(arm, reading, _services.WebIn.PhaseOf(key)),
            };
        }
        return null;
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
            version = AppVersion.Current,                                  // this build, so a deck can say which desk it is on
            decks = _services.Control.Decks.Select(d => new { name = d.Name, module = d.Module, address = d.Address }).ToArray(),   // every deck that said HELLO
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
            webArmed = WebArmedRow(),                                      // the armed web VT anywhere on the desk (its page, its mark, its words), or null
            audioRouting = AudioRoutingRow(),                              // the routing matrix: on/off, the sources, each destination with its lanes and live gains
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
                ceilingMB = Math.Round(MemoryBudget.For(_services.Metrics.Current?.RamTotalMB ?? -1, Patterns.Rendering.Media.ImageCache.Capacity, VideoEngine.MaxMounts).AppCeilingMB),
                privateMB = Math.Round(_services.Metrics.Current?.PrivateMB ?? -1),
                managedMB = Math.Round(_services.Metrics.Current?.ManagedMB ?? -1),
                pictureMB = Math.Round(Patterns.Rendering.Media.ImageCache.Bytes / (1024.0 * 1024.0)),
                framePoolMB = Math.Round(Patterns.Rendering.Media.FramePools.Bytes / (1024.0 * 1024.0)),
                retiringMB = Math.Round((Patterns.Rendering.Media.RetiredFrames.Bytes + Patterns.Rendering.Media.FramePools.RetiringBytes) / (1024.0 * 1024.0)),   // behind the render fence: frames, pictures and pools let go, waiting for the sinks that drew them
                starved = Patterns.Rendering.Media.FramePools.Starved,
                pendingFree = Patterns.Rendering.Media.FramePools.PendingFree,
                fenceOldestMs = Math.Round(Math.Max(Patterns.Rendering.Media.FramePools.OldestRetiredMs, Patterns.Rendering.Media.RetiredFrames.OldestMs)),
                liveSinks = Patterns.Rendering.Media.RenderFence.LiveSinks,
                forcedFrees = Patterns.Rendering.Media.RenderFence.ForcedFrees,
                mediaMB = Math.Round((_services.Metrics.Pressure.Reading?.Total ?? MediaMemory.Read(MemoryBudget.MachineMB).Total) / (1024.0 * 1024.0)),
                mediaBudgetMB = Math.Round(MediaMemory.BudgetBytes(MemoryBudget.MachineMB) / (1024.0 * 1024.0)),
                pressure = MediaMemory.Word(_services.Metrics.Pressure.Level),                            // none / elevated / high / critical: the ladder's rung
                pressureSteps = MediaMemory.Steps(_services.Metrics.Pressure.Level),
                placed = MemoryLedger.Describe(),
                text = _services.Metrics.MemoryCeilingLine(),
            },
            inputs = new                                                     // the decoders: mounted, fading, the limit, and the reopens staged under a source on air
            {
                mounted = _services.Video.MountCount,
                retiring = _services.Video.RetiredCount,
                limitNote = _services.Video.LimitNote,
                pendingNote = _services.Video.PendingNote,
                pending = _services.Video.PendingChanges.Select(p => new { key = p.Key, target = p.Target, what = TopologyPolicy.Name(p.Edit), words = p.Words }).ToArray(),
            },
            machine = MachineRow(),
            modules = Modules.Rows().Select(r => new { name = r.Name, version = r.Version, native = r.Native, loaded = r.Loaded }).ToArray(),   // the build's assemblies, and which this process loaded
            beacon = new { sending = _services.Beacon.Sending, listening = _services.Beacon.Listening, main = _services.Beacon.WatchText },
            // The room around the desk, for a deck's keys: every node heard, the callers linked, the twin, the stage.
            linked = _services.Nodes.Linked,
            nodes = _services.Nodes.Rows(),
            twin = _services.Twin.DeckBlock(),
            stage = _services.Stage.Block(),
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

    /// <summary>
    /// Machine health for remotes: rounded numbers plus how many advisor lines want attention —
    /// and the render faults: frames whose draw threw in the last minute across the sinks, and
    /// whether a sink is faulting right now (its last frames threw in a row), so a deck can show
    /// a red key while the room is looking at the last good picture.
    /// </summary>
    private object MachineRow()
    {
        var m = _services.Metrics.Current;
        var advice = _services.Metrics.Suggestions.Count(x => x.Severity >= HealthSeverity.Advice);
        var sinks = FrameBudgets.Readings(ShowClock.Seconds);
        var renderFaults = sinks.Sum(r => r.Faults);
        var faulting = sinks.Any(r => r.ConsecutiveFaults > 0);
        var liveAgeMs = Math.Round(sinks.Where(r => r.Kind == SinkKind.Output).Select(r => r.LiveAgeMs).DefaultIfEmpty(-1).Max(), 0);   // the oldest camera or feed picture an output drew in the last minute, decoder to frame; -1 none
        return m is null
            ? new { cpu = -1.0, ram = -1.0, fps = 0.0, battery = false, advice, renderFaults, faulting, liveAgeMs }
            : new
            {
                cpu = Math.Round(m.CpuSystemPct, 0),
                ram = Math.Round(m.RamSystemPct, 0),
                fps = Math.Round(m.OutputWindows > 0 ? m.OutputFps : m.PreviewFps, 0),
                battery = m.OnBattery,
                advice,
                renderFaults,
                faulting,
                liveAgeMs,
            };
    }

    /// <summary>Builds StateJson from any thread.</summary>
    public Task<string> StateJsonAsync() => UiThread.InvokeAsync(StateJson).GetTask();

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

    public Task<string> CueListJsonAsync() => UiThread.InvokeAsync(CueListJson).GetTask();

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
        execution = r.ExecutionId,            // this run of the cue: what a later receipt settled, or will
        pending = r.Pending,                  // device receipts the row still waits for
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
