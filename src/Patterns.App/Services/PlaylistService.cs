using Patterns.Core.Model;
using Avalonia.Threading;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Drives the media playlist: scans folders, keeps the sequencer's order current, advances
/// items on their timers / video ends / daily schedules, and publishes the current item on
/// the snapshot bus so every sink (outputs, span slices, NDI, tiles) shows the same thing.
/// </summary>
public sealed class PlaylistService : IDisposable
{
    private const int MaxFolderFiles = 1000;

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly PlaylistSequencer _sequencer = new();
    private List<string> _folderFiles = new();
    private string _folderKey = "";
    private DateTime? _unmountedSince; // when the current video item was first found unmounted
    private string _orderKey = "";
    private DateTime _lastScanUtc = DateTime.MinValue;
    private IVideoFrameSource? _heldSource;   // the decoder a hold was taken with: the hold is that clip's and ends when it is gone

    /// <summary>The playlist's clock: the wall's, or a test's. The local time of day the schedules read is this instant's.</summary>
    public Func<DateTime> NowUtc { get; set; } = () => DateTime.UtcNow;

    /// <summary>The 250 ms tick, driven directly — a test's clock does not wait on the timer.</summary>
    public void Poll() => Tick();

    /// <summary>
    /// Whether the playlist's clips can play: the decoder's own answer, or — set — a host's that
    /// gives the clips itself (a headless test's fakes). Null leaves the decoder's.
    /// </summary>
    public Func<bool>? VideoAvailableOverride { get; set; }

    public PlaylistService(AppServices services)
    {
        _services = services;
        _timer = global::Patterns.App.Services.DeskTimers.Make(TimeSpan.FromMilliseconds(250));
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        try
        {
            // The playlist that is on air keeps running while the operator programs in the
            // sandbox — the audience's walk-in loop never pauses for a rehearsal, and never
            // goes blank because the preview is showing something that is not a playlist.
            var options = MediaLocator.FindActivePlaylist(_services.AirState)?.Playlist;
            if (options is null)
            {
                // A playlist that left the air took its held clip with it: it does not come back held.
                // What waited under a hold was its own, whether the hold ends here or VIDEO PLAY ended
                // it a moment before this tick — the next playlist on air starts clean.
                if (_sequencer.Held) EndHold(NowUtc(), "the playlist left the air", onAir: false);
                _sequencer.ForgetWaiting();
                if (_services.Bus.PlaylistNow is not null)
                {
                    _services.Bus.PlaylistNow = null;
                    _services.PublishRuntime();
                }
                return;
            }

            var utcNow = NowUtc();
            var localNow = utcNow.ToLocalTime();

            // A section with a start time takes over daily at its minute — unless the caller's
            // stack is armed, when only GO moves the picture.
            if (!_services.CueStack.SuspendsAutomation &&
                _sequencer.SectionDue(options, localNow) is { } dueSection && options.ActiveSection != dueSection)
            {
                options.ActiveSection = dueSection;
                var name = options.Sections[dueSection].Name;
                Log.Info($"Playlist section '{name}' took over ({options.Sections[dueSection].StartTime}).");
                _services.Journal.Record(ActionOrigin.Playlist.Label, ShowActionKind.PlaylistPart.ToString(), name,
                    ActionStatus.Done.ToString(), $"Show part '{name}' took over at {options.Sections[dueSection].StartTime}.");
            }

            var section = PlaylistSequencer.ActiveSectionOf(options);

            // Rescan folders when the active set changes or every 30 s (files may be dropped in live).
            var folderKey = $"{options.ActiveSection}|{string.Join('|', section.Folders)}";
            if (folderKey != _folderKey || (utcNow - _lastScanUtc).TotalSeconds > 30)
            {
                _folderKey = folderKey;
                _lastScanUtc = utcNow;
                _folderFiles = ScanFolders(section.Folders);
            }

            var videoAvailable = !options.IncludeVideos || (VideoAvailableOverride is { } clips ? clips() : _services.Video.EnsureAvailable());
            var orderKey = $"{folderKey}#{string.Join('|', section.Items.Select(i => i.Path))}#{options.Shuffle}#{options.ShuffleSeed}#{options.IncludeImages}#{options.IncludeVideos}#{videoAvailable}#{_folderFiles.Count}";
            if (orderKey != _orderKey)
            {
                _orderKey = orderKey;
                var wasHeld = _sequencer.Held;
                _sequencer.SetOrder(PlaylistSequencer.BuildOrder(options, section.Items, _folderFiles, videoAvailable), utcNow);
                // A held item the new order no longer has took its hold with it: said like every hold that ends on its own.
                if (wasHeld && !_sequencer.Held) SayHoldEnded("its item left the playlist");
            }

            var currentItem = _sequencer.Current;
            var currentIsVideo = currentItem?.IsVideo == true;
            var video = currentIsVideo ? InputBus.For(InputKeys.Video(currentItem!.Path)) : null;

            // A video item whose decoder never mounted (past the input limit, missing libVLC,
            // an unreadable file) must not park the playlist forever — treat it as ended so
            // the running order moves on.
            if (currentIsVideo && video is null)
            {
                _unmountedSince ??= utcNow;
            }
            else
            {
                _unmountedSince = null;
            }
            var stalled = _unmountedSince is { } since && (utcNow - since).TotalSeconds > 8;
            if (stalled)
            {
                Log.Warn($"Playlist item '{currentItem!.Path}' never started — advancing.");
                _unmountedSince = null;
            }

            var videoEnded = currentIsVideo && (video is { IsEnded: true } || stalled);
            var videoLength = currentIsVideo && video is not null ? video.DurationSeconds : 0;

            // The hold is the held clip's: a decoder that is gone, replaced or at its end is not
            // held, and a playlist parked under a picture that moved on would never move again.
            if (_sequencer.Held && (video is null || !ReferenceEquals(video, _heldSource) || video.IsEnded))
            {
                EndHold(utcNow, video is null ? "its clip is no longer open" : video.IsEnded ? "its clip reached its end" : "its clip was opened again", onAir: true);
            }

            _sequencer.Tick(options, localNow, utcNow, videoEnded, videoLength);
            foreach (var missed in _sequencer.TakeMissed()) ReportMissed(missed);

            var current = _sequencer.Current;
            var now = current is null
                ? null
                : new PlaylistNow(current.Path, current.IsVideo, _sequencer.CurrentIndex, _sequencer.Count,
                    _sequencer.ItemStartedUtc, _sequencer.ItemDurationSeconds);

            var previous = _services.Bus.PlaylistNow;
            if (!Equals(previous?.Path, now?.Path) || previous?.StartedUtc != now?.StartedUtc)
            {
                _services.Bus.PlaylistNow = now;
                _services.PublishRuntime();
                // Runtime publishes skip side effects — mount the new item's decoder now,
                // not on the next model edit.
                _services.ReconcileInputs();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Playlist tick failed.", ex);
        }
    }

    private static List<string> ScanFolders(IEnumerable<string> folders)
    {
        var files = new List<string>();
        foreach (var folder in folders)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (PlaylistSequencer.IsMediaPath(file))
                    {
                        files.Add(file);
                        if (files.Count >= MaxFolderFiles) return files;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Playlist folder '{folder}' could not be scanned.", ex);
            }
        }
        return files;
    }

    /// <summary>
    /// The item on screen ends in <paramref name="seconds"/>: a timed item's clock is wound forward
    /// so the running order moves when the clip does (VIDEO END on a playlist video).
    /// </summary>
    public void EndCurrentIn(double seconds)
    {
        if (_sequencer.EndItemIn(seconds, NowUtc())) Republish();
    }

    /// <summary>The item on screen starts its clock again (VIDEO RESTART on a playlist video).</summary>
    public void RestartCurrent()
    {
        if (_sequencer.RestartItem(NowUtc())) Republish();
    }

    /// <summary>The item on screen is held with its clip: nothing advances and no scheduled entry fires until the release.</summary>
    public bool Held => _sequencer.Held;

    /// <summary>
    /// Holds the item on screen with its clip (VIDEO PAUSE / VIDEO STOP on a playlist video): its
    /// clock stands still from now. False with no video item on screen whose decoder is open.
    /// </summary>
    public bool HoldCurrent()
    {
        var current = _sequencer.Current;
        var source = current is { IsVideo: true } ? InputBus.For(InputKeys.Video(current.Path)) : null;
        if (source is null) return false;
        if (!_sequencer.Held && !_sequencer.Hold(NowUtc())) return false;
        _heldSource = source;
        return true;
    }

    /// <summary>Releases the hold (VIDEO PLAY): the item's clock runs on from where it stood. False when nothing was held.</summary>
    public bool ReleaseCurrent()
    {
        if (!_sequencer.Release(NowUtc())) return false;
        _heldSource = null;
        Republish();
        return true;
    }

    /// <summary>
    /// A hold that ended without VIDEO PLAY — the held clip is gone — said once, in the log and the
    /// journal. With the playlist still on air its item's moved clock is published; off the air
    /// nothing of it is published — the air holds no playlist to publish an item of.
    /// </summary>
    private void EndHold(DateTime utcNow, string why, bool onAir)
    {
        if (!_sequencer.Release(utcNow)) return;
        SayHoldEnded(why, onAir);
        if (onAir) Republish();
    }

    /// <summary>One journal row and one log line for a hold that ended on its own; the decoder it was taken with is let go.</summary>
    private void SayHoldEnded(string why, bool onAir = true)
    {
        _heldSource = null;
        var words = $"The playlist's hold ended — {why}; {(onAir ? "the playlist runs on" : "it does not come back held")}.";
        Log.Info(words);
        _services.Journal.Record(ActionOrigin.Playlist.Label, PlaylistHoldKind, "", HoldEndedStatus, words);
    }

    /// <summary>One journal row and one log line for a scheduled entry a hold kept back past its window: it did not play, and is not played late.</summary>
    private void ReportMissed(PlaylistMissed missed)
    {
        var name = Path.GetFileName(missed.Path);
        var words = $"Scheduled '{name}' ({missed.ScheduledTime}) was skipped — the playlist was held past its time, until {missed.ReleasedLocal.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}; it is not played late.";
        Log.Warn(words);
        _services.Journal.Record(ActionOrigin.Playlist.Label, PlaylistMissedKind, name, SkippedStatus, words);
    }

    /// <summary>The journal's kind and status for a scheduled entry a hold kept back past its window.</summary>
    public const string PlaylistMissedKind = "PlaylistSchedule";
    public const string SkippedStatus = "Skipped";

    /// <summary>The journal's kind and status for a hold that ended because its clip was gone, not by VIDEO PLAY.</summary>
    public const string PlaylistHoldKind = "PlaylistHold";
    public const string HoldEndedStatus = "Ended";

    /// <summary>The item as it is now onto the bus — its start moved, so every "left" reads the new clock.</summary>
    private void Republish()
    {
        var current = _sequencer.Current;
        if (current is null) return;
        _services.Bus.PlaylistNow = new PlaylistNow(current.Path, current.IsVideo, _sequencer.CurrentIndex, _sequencer.Count,
            _sequencer.ItemStartedUtc, _sequencer.ItemDurationSeconds);
        _services.PublishRuntime();
    }

    /// <summary>Status line for the Media panel.</summary>
    public string Status
    {
        get
        {
            var now = _services.Bus.PlaylistNow;
            if (now is null) return "Playlist idle.";
            var name = Path.GetFileName(now.Path);
            var pos = now.Index >= 0 ? $"{now.Index + 1}/{now.Count}" : "scheduled";
            // The item's own clock: a held item does not count down.
            var ran = ((_sequencer.HeldSinceUtc ?? NowUtc()) - now.StartedUtc).TotalSeconds;
            var dur = now.DurationSeconds > 0 ? $" · {Math.Max(0, now.DurationSeconds - ran):0}s left" : "";
            var options = MediaLocator.FindActivePlaylist(_services.AirState)?.Playlist;
            var part = options is not null && options.Sections.Count > 1
                ? $"[{PlaylistSequencer.ActiveSectionOf(options).Name}] "
                : "";
            return _sequencer.Held
                ? $"{part}Held {pos}: {name}{dur} — VIDEO PLAY runs it on"
                : $"{part}Playing {pos}: {name}{dur}";
        }
    }

    public void Dispose() => _timer.Stop();
}
