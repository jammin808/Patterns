using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Particles;
using Patterns.Core.Media;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Patterns.Core.LowerThirds;

namespace Patterns.App.ViewModels;

public sealed partial class MainViewModel
{
    // ---- the status lines the tick fills, and the desk's apply-style pickers --------------

    private string _remoteStatus = "";
    public string RemoteStatus { get => _remoteStatus; private set => Set(ref _remoteStatus, value); }

    private string _oscStatus = "";
    /// <summary>The OSC port, where feedback goes, the counts and the last message — the Remote page's line.</summary>
    public string OscStatus { get => _oscStatus; private set => Set(ref _oscStatus, value); }

    private string _twinStatus = "Twin off.";
    /// <summary>The twin link's line — the main's standbys, or the standby's main and where the link stands — the Machine page's line.</summary>
    public string TwinStatus { get => _twinStatus; private set => Set(ref _twinStatus, value); }

    public EnumItem[] TwinRoles => Lists.TwinRoles;

    private RelayCommand? _twinTakeOver;
    private RelayCommand? _twinStandBy;
    private RelayCommand? _twinTakeBack;
    private RelayCommand? _showLockOn;
    private RelayCommand? _showLockOff;
    private string _showLockStatus = "";

    /// <summary>The show lock's line — what the machine is held off, or that it is not held — the Machine page's line.</summary>
    public string ShowLockStatus
    {
        get => _showLockStatus;
        private set
        {
            if (Set(ref _showLockStatus, value))
            {
                Raise(nameof(ShowLockItems));
                Raise(nameof(IsShowLocked));
                Raise(nameof(ShowLockButtonText));
            }
        }
    }

    /// <summary>Each item the lock holds, as a line for the page.</summary>
    public IReadOnlyList<string> ShowLockItems => _services.ShowLock.Report.Items.Select(i => i.Line).ToList();

    public bool IsShowLocked => _services.ShowLock.Locked;

    public string ShowLockButtonText => IsShowLocked ? "RELEASE THE MACHINE" : "LOCK THE MACHINE FOR THE SHOW";

    /// <summary>The machine held for the show — SHOWLOCK ON on the wire.</summary>
    public RelayCommand ShowLockOnCommand => _showLockOn ??= new RelayCommand(() => { Report(_services.Actions.Execute(ShowActionKind.ShowLockOn, ActionOrigin.Desk)); ShowLockStatus = _services.ShowLock.Status; });

    /// <summary>Everything put back — SHOWLOCK OFF on the wire.</summary>
    public RelayCommand ShowLockOffCommand => _showLockOff ??= new RelayCommand(() => { Report(_services.Actions.Execute(ShowActionKind.ShowLockOff, ActionOrigin.Desk)); ShowLockStatus = _services.ShowLock.Status; });

    private RelayCommand? _showLockToggle;

    /// <summary>The one button: lock, or release.</summary>
    public RelayCommand ShowLockToggleCommand => _showLockToggle ??= new RelayCommand(() =>
    {
        if (_services.ShowLock.Locked) ShowLockOffCommand.Execute(null);
        else ShowLockOnCommand.Execute(null);
    });

    /// <summary>The standby runs the show from here — the same verb the wire's TWIN TAKEOVER sends.</summary>
    public RelayCommand TwinTakeOverCommand => _twinTakeOver ??= new RelayCommand(() => Report(_services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk)));

    private RelayCommand? _twinTakeOverAnyway;

    /// <summary>TAKE OVER ANYWAY: over a refusal — the hung main could not be ended, the marker could not be written — by hand only.</summary>
    public RelayCommand TwinTakeOverAnywayCommand => _twinTakeOverAnyway ??= new RelayCommand(() => Report(_services.Actions.Execute(new ShowAction(ShowActionKind.TwinTakeOver, "", "force"), ActionOrigin.Desk)));

    /// <summary>After a takeover: hold the outputs and follow the main again.</summary>
    public RelayCommand TwinStandByCommand => _twinStandBy ??= new RelayCommand(() => Report(_services.Actions.Execute(ShowActionKind.TwinStandBy, ActionOrigin.Desk)));

    /// <summary>The main takes the show back from the standby that ran it — TWIN TAKEBACK on the wire.</summary>
    public RelayCommand TwinTakeBackCommand => _twinTakeBack ??= new RelayCommand(() => Report(_services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk)));

    /// <summary>
    /// The twin mirrored the show (null) or the sections named onto this desk's state, in place:
    /// the lists that read those sections follow. The rest reconciles on the poll.
    /// </summary>
    internal void RefreshAfterMirror(IReadOnlyCollection<string>? sections)
    {
        if (sections is null)
        {
            RefreshAfterShowReplaced();
            return;
        }
        var set = new HashSet<string>(sections, StringComparer.Ordinal);
        if (set.Contains(nameof(ShowState.Output)) || set.Contains(nameof(ShowState.Independent)) || set.Contains(nameof(ShowState.Multiviews)))
        {
            _services.Screens.Refresh();
            RefreshWallDestinations();
            ReconcilePlacements();
            RebuildEditTargets();
            RaiseModeChanged();
        }
        if (set.Contains(nameof(ShowState.LooksAndCues)) || set.Contains(nameof(ShowState.Stacks)))
        {
            Cues.OnShowLoaded();
            Show.RaiseLookNames();
            RefreshTallies();
        }
        if (set.Contains(nameof(ShowState.LowerThirds)))
        {
            if (SelectedLowerThird is { } selected && !State.LowerThirds.Designs.Contains(selected)) SelectedLowerThird = State.LowerThirds.Designs.FirstOrDefault();
            RefreshLowerThirdTallies();
        }
        if (set.Contains(nameof(ShowState.MediaLibrary))) BuildLibrary();
        if (set.Contains(nameof(ShowState.Transition))) HookTransition();
        if (set.Contains(nameof(ShowState.Pattern))) Raise(nameof(ActivePattern));
    }

    private string _beaconStatus = "";
    /// <summary>The beacon going out, the listener, and what it makes of the main machine — the Machine page's line.</summary>
    public string BeaconStatus { get => _beaconStatus; private set => Set(ref _beaconStatus, value); }

    private bool _reviewSeen;

    /// <summary>
    /// The preview fills every multiview — a screen's own multiview pattern, an NDI send of it,
    /// /multiview — so the next look is checked on the monitor wall before the TAKE. A runtime
    /// flag on the bus (never saved), set through the action layer like every switch on the desk.
    /// </summary>
    public bool ReviewOnMultiview
    {
        get => _services.Bus.ReviewOnMultiview;
        set
        {
            if (value == _services.Bus.ReviewOnMultiview) return;
            _services.Actions.Execute(value ? ShowActionKind.ReviewOn : ShowActionKind.ReviewOff, ActionOrigin.Desk);
            _reviewSeen = _services.Bus.ReviewOnMultiview;
            Raise(nameof(ReviewOnMultiview));
        }
    }

    // ---- sound: two pages of their own (AudioPage, MusicPage) ----------------------

    /// <summary>The Audio page and the Show panel's sound: the tone, the track player, the sync check, VOGs and stingers, the reactive input.</summary>
    public AudioPage Audio { get; }

    /// <summary>The break-music page — the Audio section's Spotify block, the Show page's transport and the looks' Music picker read it.</summary>
    public MusicPage Music { get; }

    private string _healthText = "";
    public string HealthText { get => _healthText; private set => Set(ref _healthText, value); }

    private string _streamStatus = "";
    public string StreamStatus { get => _streamStatus; private set => Set(ref _streamStatus, value); }

    private StreamHealth _streamHealth = StreamHealth.Read(StreamFacts.None);

    /// <summary>
    /// The stream's health this second, read once on the poll and shown everywhere — the foot of
    /// the rail, the Stream page, the Show panel, the phone. One reading, so no two of them can
    /// say different things about the same stream.
    /// </summary>
    public StreamHealth StreamHealth
    {
        get => _streamHealth;
        private set
        {
            if (_streamHealth == value) return;
            _streamHealth = value;
            Raise();
            Raise(nameof(StreamWord));
            Raise(nameof(StreamLine));
            Raise(nameof(StreamUptime));
            Raise(nameof(StreamHue));
            Raise(nameof(StreamOnAir));
            Raise(nameof(StreamTrouble));
            Raise(nameof(StreamRailText));
            Raise(nameof(StreamCounts));
        }
    }

    /// <summary>"LIVE", "SLOW", "FAULT", "UP…", "OFF" — the one word a rail has room for.</summary>
    public string StreamWord => _streamHealth.Word;

    public string StreamLine => _streamHealth.Line;

    public string StreamUptime => _streamHealth.Uptime;

    public string StreamHue => _streamHealth.Hue;

    public bool StreamOnAir => _streamHealth.IsOnAir;

    public bool StreamTrouble => _streamHealth.IsTrouble;

    /// <summary>The numbers under the line on the Stream page: frames in, the rate, the restarts.</summary>
    public string StreamCounts
    {
        get
        {
            var f = _streamHealth.Facts;
            if (!f.Wanted || f.Frames == 0) return "";
            var parts = new List<string>
            {
                $"{f.Frames:N0} frames encoded",
                f.TargetFps > 0 ? $"{f.Fps:0.#} of {f.TargetFps} fps in" : $"{f.Fps:0.#} fps in",
                f.Destinations == 1 ? "1 destination" : $"{f.Destinations} destinations",
            };
            if (f.Restarts > 0) parts.Add($"{f.Restarts} encoder restart{(f.Restarts == 1 ? "" : "s")}");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The rail's foot: the word, and how long it has been up under it.</summary>
    public string StreamRailText => _streamHealth.Uptime.Length > 0 ? $"{_streamHealth.Word}\n{_streamHealth.Uptime}" : _streamHealth.Word;

    public string RemoteUrlsText => string.Join("\n", _services.Control.RemoteUrls());

    /// <summary>The audience listener's addresses with the room's door, for the Remote page.</summary>
    public string AudienceUrlsText => _services.Control.AudienceUrls().Count == 0 ? "the audience port is off" : string.Join("\n", _services.Control.AudienceUrls().Select(u => $"{u}play?room={_services.Play.Code}"));

    public EnumItem[] AudienceNetworks => Lists.AudienceNetworks;

    // ---- fractal, canvas, LED tile and video-wall pickers --------------------------------

    public EnumItem[] FractalKinds => Lists.FractalKinds;
    public EnumItem[] AudioSources => Lists.AudioSources;
    public EnumItem[] FractalQualities => Lists.FractalQualities;

    private RelayCommand<string>? _applyFractalPreset;

    public RelayCommand<string> ApplyFractalPresetCommand => _applyFractalPreset ??= new RelayCommand<string>(name =>
    {
        if (name is null) return;
        _services.BulkEdit(() => FractalPresets.Apply(name, ActivePattern.Fractal));
    });

    public ResolutionPreset? SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            // Apply-style combo: applying resets the selection, so the same preset can be
            // re-applied after switching edit targets.
            if (Set(ref _selectedResolution, value) && value is not null)
            {
                BulkEdit(() =>
                {
                    ActivePattern.Canvas.FollowOutput = false;
                    ActivePattern.Canvas.Width = value.W;
                    ActivePattern.Canvas.Height = value.H;
                });
                _selectedResolution = null;
                Raise();
            }
        }
    }

    public int[] TileSizes => Lists.TileSizes;

    public int SelectedTileSize
    {
        get => _selectedTileSize;
        set
        {
            if (Set(ref _selectedTileSize, value) && value > 0)
            {
                BulkEdit(() =>
                {
                    ActivePattern.LedWall.TileWidth = value;
                    ActivePattern.LedWall.TileHeight = value;
                });
                _selectedTileSize = 0;
                Raise();
            }
        }
    }

    private ResolutionPreset? _selectedVideoWallResolution;

    public ResolutionPreset? SelectedVideoWallResolution
    {
        get => _selectedVideoWallResolution;
        set
        {
            if (Set(ref _selectedVideoWallResolution, value) && value is not null)
            {
                BulkEdit(() =>
                {
                    ActivePattern.VideoWall.ElementWidth = value.W;
                    ActivePattern.VideoWall.ElementHeight = value.H;
                });
                _selectedVideoWallResolution = null;
                Raise();
            }
        }
    }
}
