using Patterns.Core.LowerThirds;
using Patterns.Rendering.LowerThirds;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The sound's verbs: the audio track and its volume, the break music, the tone, the duck, the stingers, the stream, and STOP ALL.
/// </summary>
public sealed partial class ShowActions
{
    private ActionResult? RunAudio(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.AudioVolume:
            {
                // The track is not in the snapshot: the player reads the live model every poll,
                // sandbox or not, so this is the audio's air seam.
                if (!ActionSpec.TryParsePercent(a.Value, out var percent))
                {
                    return ActionResult.Refused("Audio volume needs a number from 0 to 125.");
                }
                State.AudioPlayer.VolumePct = percent;
                return ActionResult.Done($"Audio volume {percent:0}%.");
            }

            case ShowActionKind.AudioPlay:
            {
                // The list is not in the snapshot: the player reads the live model every poll, sandbox or not.
                if (!AudioPlaylist.HasTracks(State.AudioPlayer)) return ActionResult.Refused("The audio playlist is empty — add tracks or a folder on the Audio page.");
                if (a.Target.Length > 0)
                {
                    var index = _s.AudioPlayer.Resolve(a.Target);
                    if (index < 0) return ActionResult.Refused($"No audio track '{a.Target}' in the list.");
                    _s.AudioPlayer.PlayAt(index);
                    return ActionResult.Requested($"Audio: {_s.AudioPlayer.CurrentName} playing.");
                }
                State.AudioPlayer.Playing = true;
                _s.AudioPlayer.Poll();
                return ActionResult.Requested($"Audio playlist playing{(_s.AudioPlayer.CurrentName.Length > 0 ? ": " + _s.AudioPlayer.CurrentName : "")}.");
            }
            case ShowActionKind.AudioNext:
            case ShowActionKind.AudioPrev:
            {
                var moved = a.Kind == ShowActionKind.AudioNext ? _s.AudioPlayer.Next() : _s.AudioPlayer.Previous();
                return moved
                    ? ActionResult.Done($"Audio: {_s.AudioPlayer.CurrentName} ({_s.AudioPlayer.NowIndex + 1}/{_s.AudioPlayer.Count}).")
                    : ActionResult.Refused("The audio playlist is empty — add tracks or a folder on the Audio page.");
            }
            case ShowActionKind.AudioStop:
                State.AudioPlayer.Playing = false;
                return ActionResult.Done("Audio track stopped.");

            // ---- the routing matrix: not in the snapshot — every player reads the live model on its poll, so this is the audio's air seam ----
            case ShowActionKind.AudioRouting:
            {
                if (!ActionSpec.IsSwitchWord(a.Value)) return ActionResult.Refused("AUDIO ROUTING takes on, off or toggle.");
                var on = OverlayControl.SwitchTo(a.Value, State.AudioRouting.Enabled);
                var seeded = 0;
                if (on && !State.AudioRouting.Enabled) seeded = AudioRouting.SeedDefaults(State);
                State.AudioRouting.Enabled = on;
                _s.AudioGraph?.Reconcile();
                return ActionResult.Done(on
                    ? seeded > 0 ? $"Audio routing on — audio follows video to begin with ({seeded} routes seeded)." : "Audio routing on."
                    : "Audio routing off — the programme's outputs and the monitor, as before.");
            }
            case ShowActionKind.AudioRoute:
            case ShowActionKind.AudioUnroute:
            {
                var source = AudioRouting.FindSource(State, a.Target);
                if (source is null) return ActionResult.Refused($"'{a.Target}' is not a sound the show has — programme, a screen with its own picture, preview, music, vog, sting or tone.");
                var (toWord, db) = AudioRouting.ParseRouteValue(a.Value);
                if (double.IsNaN(db)) return ActionResult.Refused("The level after AT is not a number in dB.");
                var devices = _s.AudioEndpoints.RenderNames;
                var destination = AudioRouting.FindDestination(State, devices, toWord);
                if (destination is null) return ActionResult.Refused($"'{toWord}' is not a destination — an output this machine has (by its name), NDI <send>, or computer.");
                var label = AudioRouting.DestinationLabel(State, destination);
                if (a.Kind == ShowActionKind.AudioRoute)
                {
                    AudioRouting.SetRoute(State, source, destination, db);
                    _s.AudioGraph?.Reconcile();
                    return ActionResult.Done($"{AudioRouting.SourceLabel(State, source)} → {label} at {Db.Text(db)}{(State.AudioRouting.Enabled ? "" : " (routing is off — AUDIO ROUTING ON puts the matrix in charge)")}.");
                }
                var cleared = AudioRouting.ClearRoute(State, source, destination);
                _s.AudioGraph?.Reconcile();
                return cleared ? ActionResult.Done($"{AudioRouting.SourceLabel(State, source)} off {label}.") : ActionResult.Done($"{AudioRouting.SourceLabel(State, source)} was not on {label}.");
            }
            case ShowActionKind.AudioVogMode:
            {
                if (!AudioRouting.TryParseVogMode(a.Value, out var mode)) return ActionResult.Refused("AUDIO VOG takes duck, replace or leave.");
                var devices = _s.AudioEndpoints.RenderNames;
                var destination = AudioRouting.FindDestination(State, devices, a.Target);
                if (destination is null) return ActionResult.Refused($"'{a.Target}' is not a destination — an output this machine has, NDI <send>, or computer.");
                AudioRouting.EnsureRow(State, destination).VogMode = mode;
                _s.AudioGraph?.Reconcile();
                return ActionResult.Done($"A VOG on {AudioRouting.DestinationLabel(State, destination)}: {mode.ToString().ToLowerInvariant()}.");
            }
            case ShowActionKind.AudioFollow:
            {
                // Round 69: the sound follows the picture — the matrix derives each screen's route from what it shows now.
                if (!ActionSpec.IsSwitchWord(a.Value)) return ActionResult.Refused("AUDIO FOLLOW takes on, off or toggle.");
                var on = OverlayControl.SwitchTo(a.Value, State.AudioRouting.FollowPicture);
                State.AudioRouting.FollowPicture = on;
                _s.AudioGraph?.Reconcile();
                return ActionResult.Done(AudioRouting.FollowWords(State, _s.AirState) + (on && !State.AudioRouting.Enabled ? " Routing is off — AUDIO ROUTING ON puts the matrix in charge." : ""));
            }

            case ShowActionKind.SpotifyPlay:
            {
                // Break music is not in the snapshot: the service reads the live model every poll,
                // sandbox or not — the same air seam as the audio track. Never Failed and never
                // Refused for a network or setup fact: RunCue aborts a cue at the first non-Ok
                // result and the GO gate skips a cue the validator calls Broken, so a cue must not
                // stop, or be skipped, because Spotify is unhappy.
                // A name that resolves to nothing is a programming error, on or off: refused either
                // way, so the validator's Broken set and the executor's Refused set are one set.
                SpotifyItemConfig? item = null;
                if (a.Target.Length > 0)
                {
                    item = SpotifyLibrary.Find(State, a.Target);
                    if (item is null) return ActionResult.Refused($"No break music '{a.Target}'.");
                    if (!SpotifyUri.IsValid(item.Uri)) return ActionResult.Refused($"'{item.DisplayName}' has no valid Spotify link.");
                }
                if (!State.Spotify.Enabled) return ActionResult.Done("Break music is off — nothing played.");
                if (item is not null)
                {
                    State.Spotify.PlayingId = item.Id;
                    State.Spotify.Playing = true;
                    return ActionResult.Requested($"Break music: {item.DisplayName}.");
                }
                State.Spotify.Playing = true;
                return ActionResult.Requested("Break music playing.");
            }
            case ShowActionKind.SpotifyPause:
                if (!State.Spotify.Enabled) return ActionResult.Done("Break music is off.");
                State.Spotify.Playing = false;
                _s.Spotify.PokeNow();
                return ActionResult.Requested("Break music pausing.");
            case ShowActionKind.SpotifyNext:
                if (!State.Spotify.Enabled) return ActionResult.Done("Break music is off.");
                _s.Spotify.SkipRequested = true;   // consumed by the next poll; never a synchronous socket
                return ActionResult.Requested("Break music: next track.");
            case ShowActionKind.SpotifyVolume:
            {
                if (!ActionSpec.TryParseLevel(a.Value, out var level))
                {
                    return ActionResult.Refused("Break music level needs a number from 0 to 100.");
                }
                if (!State.Spotify.Enabled) return ActionResult.Done("Break music is off.");
                State.Spotify.LevelPct = level;
                return ActionResult.Done($"Break music level {level:0}%.");
            }

            case ShowActionKind.ToneOn:
                State.Tone.Enabled = true;
                return ActionResult.Done("Tone on.");
            case ShowActionKind.ToneOff:
                State.Tone.Enabled = false;
                return ActionResult.Done("Tone off.");

            // The live duck: sound only, never the picture — so never the sandbox, never the label.
            case ShowActionKind.DuckOn:
                _s.Stingers.SetDuck(true);
                return ActionResult.Done($"Ducked to {State.Stingers.DuckToPct:0}% for a live announcement.");
            case ShowActionKind.DuckOff:
                _s.Stingers.SetDuck(false);
                return ActionResult.Done("Duck lifted.");
            case ShowActionKind.DuckToggle:
                _s.Stingers.SetDuck(!State.Stingers.DuckActive);
                return ActionResult.Done(State.Stingers.DuckActive
                    ? $"Ducked to {State.Stingers.DuckToPct:0}% for a live announcement."
                    : "Duck lifted.");

            case ShowActionKind.StingerFire:
            {
                var item = StingerLibrary.Find(State, a.Target);
                if (item is null) return ActionResult.Refused($"No VOG or stinger '{a.Target}'.");
                if (!StingerLibrary.KindMatches(item, a.Value, out var wanted))
                {
                    // A button that says VOG must never fire a stinger: refused, and the item named.
                    return ActionResult.Refused($"'{item.DisplayName}' is a {StingerLibrary.KindWord(item.Kind)}, not a {wanted}.");
                }
                if (!_s.Stingers.Fire(item)) return ActionResult.Failed(_s.Stingers.Status);
                return ActionResult.Requested(_s.Stingers.Status); // the service owns the strip's label while it plays
            }
            case ShowActionKind.StingerStop:
                _s.Stingers.Stop();
                return ActionResult.Done(_s.Stingers.Status);

            case ShowActionKind.StreamStart:
                State.Stream.Active = true;
                return ActionResult.Requested("Stream starting…");
            case ShowActionKind.StreamStop:
                State.Stream.Active = false;
                return ActionResult.Done("Stream stopped.");

            case ShowActionKind.StopAll:
            {
                _s.Stingers.Stop();              // both kinds; a clip or a held frame reverts; an after is cancelled, never fired
                State.AudioPlayer.Playing = false;
                State.Spotify.Playing = false;   // the service issues the pause and retries until it lands…
                _s.Spotify.PokeNow();            // …starting on this turn, not up to 400 ms later
                State.Tone.Enabled = false;
                // A cue's waiting steps go with it: STOP ALL means nothing more is coming.
                var dropped = _s.Tail.DropAll();
                var also = dropped == 0 ? "" : $" {dropped} waiting cue step{(dropped == 1 ? "" : "s")} dropped.";
                return ActionResult.Done("Stopped: audio track, break music, VOGs and stingers (previous content back), tone. Outputs, blackout and the stream are untouched." + also);
            }

            default:
                return null;
        }
    }
}
