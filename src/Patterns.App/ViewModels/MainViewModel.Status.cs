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
