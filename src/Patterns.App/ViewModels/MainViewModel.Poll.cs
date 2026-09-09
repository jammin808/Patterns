using System.Diagnostics;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

public sealed partial class MainViewModel
{
    // ---- the status poll: a second's work, guarded area by area, its budget kept ----------------

    private readonly Dictionary<string, DateTime> _pollFaultsLogged = new();
    private readonly Dictionary<string, object?> _pollSeen = new();
    private readonly Stopwatch _tickWatch = new();
    private readonly Stopwatch _areaWatch = new();
    private string _tickSlowestArea = "";
    private double _tickSlowestMs = -1;
    private string _deskTickText = "";
    private string _renderBudgetText = "";
    private string _startupText = "";
    private string _runtimeText = "";
    private string _qualityText = "";
    private string _memoryBudgetText = "";

    /// <summary>The QUALITY LADDER block's line: the mode, the level, why, and when it steps back.</summary>
    public string QualityText { get => _qualityText; private set => Set(ref _qualityText, value); }

    /// <summary>The MEMORY CEILINGS block's line: the app against its ceiling, the picture cache, the decoders, the held frames.</summary>
    public string MemoryBudgetText { get => _memoryBudgetText; private set => Set(ref _memoryBudgetText, value); }

    /// <summary>The ladder's second: the worst output's last complete second judged, the two lines refreshed.</summary>
    private void PollQuality()
    {
        _services.Quality.Tick();
        QualityText = _services.Quality.Describe();
        MemoryBudgetText = _services.Metrics.MemoryCeilingLine();
    }

    /// <summary>The STABILITY block's render line: the worst frame of the last minute, the stage that took it and the sink, every sink's average and rate.</summary>
    public string RenderBudgetText { get => _renderBudgetText; private set => Set(ref _renderBudgetText, value); }

    /// <summary>The STABILITY block's start-up line: how long this start took to become a desk, phase by phase.</summary>
    public string StartupText { get => _startupText; private set => Set(ref _startupText, value); }

    /// <summary>The STABILITY block's runtime line: the .NET in use and the collector's mode (sustained low latency while the outputs are live).</summary>
    public string RuntimeText { get => _runtimeText; private set => Set(ref _runtimeText, value); }

    private static readonly string RuntimeName = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    /// <summary>How often a failing area is logged and counted against the health line: once a minute, not once a second.</summary>
    public static readonly TimeSpan PollFaultLoggedEvery = TimeSpan.FromMinutes(1);

    /// <summary>Tests only: called with each area's name before it runs — a probe that throws proves the guard.</summary>
    public Action<string>? PollAreaProbe { get; set; }

    /// <summary>The STABILITY block's line: what the tick costs, its worst minute and the area that took it, what failed.</summary>
    public string DeskTickText { get => _deskTickText; private set => Set(ref _deskTickText, value); }

    /// <summary>The status-timer body, callable directly (tests drive it without waiting on the clock).</summary>
    public void PollNow() => PollStatus();

    /// <summary>
    /// One tick a second on the UI thread. Every area is guarded: a status read that throws is
    /// logged once a minute, counted as a fault, and the areas after it still run — the cue
    /// schedule, the tallies and the clock never wait on a broken probe. The whole tick is timed,
    /// with the slowest area inside it, and kept in the budget the Machine page and the
    /// super-check read.
    /// </summary>
    private void PollStatus()
    {
        _tickWatch.Restart();
        _tickSlowestArea = "";
        _tickSlowestMs = -1;
        _statusTicks++;
        Guard("controls", PollControls);
        Guard("media", PollMedia);
        Guard("audio", PollAudio);
        Guard("tallies", RefreshTallies);
        Guard("health", PollHealth);
        Guard("screens", PollOwnership);
        Guard("quality", PollQuality);
        Guard("machine", PollAdmin);
        Guard("inputs", RefreshActiveInputs);
        Guard("switcher", RefreshSwitcherTiles);
        Guard("run", PollRun);
        Guard("remote", PollRemote);
        Guard("devices", PollDevices);
        Guard("install", PollInstall);
        Guard("desk", PollDesk);
        Guard("sweep", SweepRetired);
        Guard("cues", CheckCues);
        Guard("playlist", PollPlaylist);
        Guard("pickers", PollPickers);
        Guard("web", RefreshWebControls);
        Guard("clock", PollClock);
        Guard("places", PollPlaces);
        _services.DeskTick.Record(_tickWatch.Elapsed.TotalMilliseconds, _tickSlowestArea, _tickSlowestMs);
        DeskTickText = _services.DeskTick.Describe();
        RenderBudgetText = FrameBudgets.Describe(ShowClock.Seconds);
        StartupText = _services.Startup.Describe();
        RuntimeText = $"Runtime {RuntimeName} · garbage collector {ShowGc.Describe()}.";
    }

    /// <summary>
    /// Runs one area of the tick and times it. An area that throws is carried past: the fault is
    /// counted, and logged (with the health line told) once a minute per area so a probe that
    /// fails every second reads as one story, not three thousand lines an hour.
    /// </summary>
    private void Guard(string area, Action work)
    {
        _areaWatch.Restart();
        try
        {
            PollAreaProbe?.Invoke(area);
            work();
        }
        catch (Exception ex)
        {
            _services.DeskTick.RecordFault();
            var now = DateTime.UtcNow;
            if (!_pollFaultsLogged.TryGetValue(area, out var last) || now - last >= PollFaultLoggedEvery)
            {
                _pollFaultsLogged[area] = now;
                HealthMonitor.Record($"desk poll · {area}: {ex.Message}");
                Log.Warn($"The desk's poll failed in '{area}' — the other areas carried on.", ex);
            }
        }
        var ms = _areaWatch.Elapsed.TotalMilliseconds;
        if (ms > _tickSlowestMs)
        {
            _tickSlowestMs = ms;
            _tickSlowestArea = area;
        }
    }

    /// <summary>Raises a computed property only when its value moved since the last tick — a bound TextBlock re-reads nothing otherwise.</summary>
    private void RaiseIfChanged(string name, object? value)
    {
        if (_pollSeen.TryGetValue(name, out var seen) && Equals(seen, value)) return;
        _pollSeen[name] = value;
        Raise(name);
    }

    private void PollControls() => ShowControls?.Refresh();

    private void PollMedia()
    {
        var active = _services.Ndi.ActiveCount;
        foreach (var cfg in State.Ndi.Senders)
        {
            cfg.Status = _services.Ndi.StatusFor(cfg.Id);
        }
        NdiStatus = active > 0
            ? $"{active} sender{(active == 1 ? "" : "s")} active"
            : NdiRuntimeFound ? "Off" : "Runtime not found";
        PlaylistStatus = _services.Playlist.Status;
        FeedStatus = _services.Feeds.Status;
        WeatherStatus = _services.Weather.Status;
        RaiseIfChanged(nameof(WeatherCoordinatesText), WeatherCoordinatesText);
        RaiseIfChanged(nameof(DirectOutputSummary), DirectOutputSummary);
        RefreshCropSummary();
        RefreshDeck();
        SyncVirtualScreens();
    }

    private void PollAudio()
    {
        ToneStatus = _services.Audio.Status;
        AudioPlayerStatus = _services.AudioPlayer.Status;
        SyncStatus = BuildSyncStatus();
        StingerStatus = _services.Stingers.Status;
        RefreshStingerGroups();
        RefreshAfterChoices();
        StingerHolding = _services.Stingers.Holding;
        StingerHoldText = StingerHolding ? $"'{_services.Stingers.HoldName}' is holding the screens." : "";
        SpotifyStatus = _services.Spotify.Status;
        SpotifyAccountText = _services.Spotify.AccountText;
        SpotifyNowPlaying = _services.Spotify.NowPlaying;
        if (!ReferenceEquals(_spotifyDevicesSeen, _services.Spotify.Devices)) RefreshSpotifyDevices();       // CONNECT filled them in
        if (!ReferenceEquals(_spotifyPlaylistsSeen, _services.Spotify.Playlists)) RefreshSpotifyPlaylists();
        if (!ReferenceEquals(_spotifyTracksSeen, _services.Spotify.Tracks) ||
            !ReferenceEquals(_spotifySearchSeen, _services.Spotify.SearchHits) ||
            SpotifyBrowseStatus != _services.Spotify.BrowseStatus)
        {
            RefreshSpotifyBrowse();
        }
        RefreshLookMusicChoices(); // a renamed or added entry, a loaded show
        FractalAudioStatus = _services.Analyser.Status;
        if (ActivePattern.Kind is PatternKind.Fractal or PatternKind.Reactive) RefreshAudioCaptureDevices();
    }

    private void PollHealth()
    {
        var watch = _services.Beacon.WatchText;
        HealthText = watch.Length > 0 ? $"{HealthMonitor.Summary(DateTime.UtcNow)} · {watch}" : HealthMonitor.Summary(DateTime.UtcNow);
        StreamStatus = _services.Stream.Status;
        var beacon = _services.Beacon;
        BeaconStatus = beacon.Sending || beacon.Listening
            ? $"{beacon.Status}{(beacon.Sent > 0 ? $" {beacon.Sent} sent." : "")}{(beacon.Listening ? " " + beacon.WatchText : "")}"
            : beacon.Status;
    }

    /// <summary>
    /// The screens' beat. While the outputs are live this desk's ownership record is refreshed
    /// every second — from this very tick on purpose, so a desk whose UI thread has stopped
    /// answering stops beating while its render windows play on, and the next start reads that
    /// silence as "still playing, nobody at the controls" and takes the screens back. The same
    /// tick answers another desk asking for them.
    /// </summary>
    private void PollOwnership()
    {
        _services.Ownership.Tick();
        var live = _services.Outputs.IsLive;
        var took = _services.Takeover;
        ScreensOwnedText = live
            ? $"The screens are this desk's: {OutputOwnership.TargetWords(_services.Outputs.LiveTargetNames())} live under pid {Environment.ProcessId}."
            : took.Words.Length > 0
                ? took.Words
                : "The outputs are closed — no screen is this desk's.";
    }

    private void PollRun()
    {
        Run.Tick();
        RefreshVideoClock();
        var progression = ProgressionText;
        if (progression != _progressionSeen)
        {
            _progressionSeen = progression;                                          // an auto-follow ticking, the playlist moving
            Raise(nameof(ProgressionText));
        }
    }

    private void PollRemote()
    {
        RemoteStatus = State.Control.Enabled
            ? $"Remote: {_services.Control.RemoteUrls().Skip(1).FirstOrDefault() ?? _services.Control.RemoteUrls()[0]}"
            : "Remote control off.";
        OscStatus = _services.Osc.StatusLine;
    }

    private void PollDesk()
    {
        if (_reviewSeen != _services.Bus.ReviewOnMultiview)
        {
            _reviewSeen = _services.Bus.ReviewOnMultiview; // a remote flipped it: the desk's toggles follow
            Raise(nameof(ReviewOnMultiview));
        }
        if (_selectedPlacement is { Gaps.Count: > 0 }) RaiseIfChanged(nameof(GapSummary), GapSummary); // a gap row edited in place: the words follow
        ObserveWalkChecks();                                                          // a walkthrough step ticks itself as the desk does the work
        if (_frozenSeen != _services.Bus.Frozen)
        {
            _frozenSeen = _services.Bus.Frozen;                                       // a remote froze or released: the desk's button follows
            Raise(nameof(IsFrozen));
        }
        var previous = PreviousLookName;
        if (previous != _previousLookSeen)
        {
            _previousLookSeen = previous;
            Raise(nameof(PreviousLookName));
            Raise(nameof(LookBackText));
        }
    }

    private void SweepRetired()
    {
        _services.Video.SweepRetired();
        _services.NdiIn.SweepRetired();
        _services.WebIn.SweepRetired();
    }

    private void PollPlaylist()
    {
        // Now-playing marker on explicit playlist rows.
        var nowPath = _services.Bus.PlaylistNow?.Path;
        foreach (var item in PlaylistSequencer.AllItems(ActivePattern.Media.Playlist))
        {
            item.IsNowPlaying = nowPath is not null && string.Equals(item.Path, nowPath, StringComparison.OrdinalIgnoreCase);
        }
        RaisePlaylistSection(onlyOnChange: true);
    }

    private void PollPickers()
    {
        // Keep pick lists warm while their panels are in use (NDI discovery is push-based
        // and cheap to read; capture enumeration is COM, so on demand + first need only).
        if (ActivePattern.Media.Source == MediaSource.NdiFeed && ++_ndiPollTick % 3 == 0)
        {
            RefreshNdiSources(quiet: true);
        }
        if (ActivePattern.Media.Source == MediaSource.Capture && !_captureListLoaded)
        {
            RefreshCaptureDevices(quiet: true);
        }
        // The Format pickers follow their device; a refresh is free while the device is unchanged.
        if (ActivePattern.Media.Source == MediaSource.Capture) CaptureFormat.Refresh();
        if (State.Overlays.Pip.Enabled && State.Overlays.Pip.Source == PipSource.Capture) PipCaptureFormat.Refresh();
    }

    private void PollClock()
    {
        Raise(nameof(HeaderClock));
        RaiseIfChanged(nameof(CountdownPreview), CountdownPreview);
    }

    /// <summary>
    /// The overlay pages' pixel fields follow the picture: a drag, a nudge slider, a change of size
    /// or a canvas of another shape all move the box, and the pixels say where it ended up.
    /// </summary>
    private void PollPlaces()
    {
        foreach (var place in Places)
        {
            place.Refresh();
        }
    }

    private void OnSnapshotPublished()
    {
        // The sandbox can open or close without going through the toggle (startup arming, the
        // re-arm after a send, a discard from a service) — keep the switcher honest about it.
        Raise(nameof(IsSandboxActive));
        Raise(nameof(CanvasInfo));
        Raise(nameof(ShowCanvasPanel));
        Raise(nameof(InputNickname));
        RaiseArrangement();
        Raise(nameof(IsBlackout)); // Space, Shift+F8, a remote or an output-window key moved it
        RefreshSwitcherTiles(); // tally: blackout, a screen switched, the sandbox opened or closed
    }

    private void RefreshOutputsStatus()
    {
        // Planned screens are counted separately — an operator must never read "4 detected"
        // and believe four displays are plugged in.
        var detected = _services.Screens.Real.Count;
        var planned = PlannedScreenCount;
        var enabled = State.Output.Placements.Count(p => p.Enabled && !p.Planned && LiveInfo(p) is not null);
        var plannedText = planned > 0 ? $" · {planned} planned" : "";
        OutputsStatus = _services.Outputs.IsLive
            ? "LIVE — outputs running"
            : IsPrepMode
                ? $"PREP — {detected} display{(detected == 1 ? "" : "s")} detected{plannedText} · outputs held"
                : $"{detected} screen{(detected == 1 ? "" : "s")} detected{plannedText} · {enabled} enabled — press OUTPUTS ON";
        Raise(nameof(IsLive));

        // Every path that can change the mode or the planned set — a show load, a display
        // hot-plug, adoption — reaches here, so the mode UI is refreshed in one place.
        Raise(nameof(IsPrepMode));
        Raise(nameof(ModeBanner));
        Raise(nameof(PlannedScreenCount));
        Raise(nameof(VirtualScreenCount));
        Raise(nameof(PrepSummary));
    }

    public bool IsLive => _services.Outputs.IsLive;
}
