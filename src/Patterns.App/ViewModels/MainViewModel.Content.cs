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
    // ---- layers and drags on the PREVIEW pane ---------------------------------------

    public EnumItem[] LayerSources => Lists.LayerSources;

    public RelayCommand<LayerConfig> BrowseLayerImageCommand { get; }
    public RelayCommand<LayerConfig> BrowseLayerVideoCommand { get; }

    /// <summary>The pattern the PREVIEW pane shows, in the live model: the target's own, its source's when it repeats one, else the program.</summary>
    public PatternConfig PreviewPattern
    {
        get
        {
            var id = _services.PreviewScreenId;
            if (id is null) return State.Pattern;
            id = ScreenRoles.ResolveMirror(State, id);
            if (!ContentTargets.UsesOwnPattern(State, id)) return State.Pattern;
            return State.Independent.FirstOrDefault(a => a.ScreenId == id)?.Pattern ?? State.Pattern;
        }
    }

    public static string DragName(HitKind kind) => kind switch
    {
        HitKind.Layer1 => "Layer 1",
        HitKind.Layer2 => "Layer 2",
        HitKind.Logo => "The logo",
        HitKind.Clock => "The clock",
        HitKind.Countdown => "The countdown",
        HitKind.Message => "The message",
        HitKind.Pip => "The PiP inset",
        HitKind.Weather => "The weather",
        HitKind.WebPage => "The web page",
        HitKind.Badge => "The Patterns badge",
        _ => "The element",
    };

    /// <summary>Where a draggable thing sits now: a layer's box (a share of the canvas) or an overlay's nudge from its anchor.</summary>
    public (double X, double Y) DragPlaceOf(HitKind kind)
    {
        var p = PreviewPattern;
        if (AnchoredOf(kind) is { } placed) return (placed.OffsetXPct, placed.OffsetYPct);
        return kind switch
        {
            HitKind.Layer1 => (p.Layer1.XPct, p.Layer1.YPct),
            HitKind.Layer2 => (p.Layer2.XPct, p.Layer2.YPct),
            _ => (0, 0),
        };
    }

    /// <summary>
    /// What the last preview frame drew for a kind, in the space it drew it in — set by the window,
    /// which owns the pipeline. Null before the first frame, and for anything not on the picture:
    /// the pixel fields are then quiet rather than lying about where something is.
    /// </summary>
    public Func<HitKind, PlaceEditor.PlacedBox?>? PreviewBox { get; set; }

    private PlaceEditor PlaceFor(HitKind kind)
        => new(() => AnchoredOf(kind), () => PreviewBox?.Invoke(kind));

    /// <summary>The place editors the overlay pages' pixel rows bind to — one per overlay, built once.</summary>
    public PlaceEditor ClockPlace => _clockPlace ??= PlaceFor(HitKind.Clock);
    public PlaceEditor LogoPlace => _logoPlace ??= PlaceFor(HitKind.Logo);
    public PlaceEditor MessagePlace => _messagePlace ??= PlaceFor(HitKind.Message);
    public PlaceEditor WeatherPlace => _weatherPlace ??= PlaceFor(HitKind.Weather);
    public PlaceEditor PipPlace => _pipPlace ??= PlaceFor(HitKind.Pip);
    public PlaceEditor CountdownPlace => _countdownPlace ??= PlaceFor(HitKind.Countdown);
    public PlaceEditor BadgePlace => _badgePlace ??= PlaceFor(HitKind.Badge);

    private PlaceEditor? _clockPlace, _logoPlace, _messagePlace, _weatherPlace, _pipPlace, _countdownPlace, _badgePlace;

    /// <summary>Every place editor, for the poll's once-a-second refresh.</summary>
    public IEnumerable<PlaceEditor> Places
    {
        get
        {
            yield return ClockPlace;
            yield return LogoPlace;
            yield return MessagePlace;
            yield return WeatherPlace;
            yield return PipPlace;
            yield return CountdownPlace;
            yield return BadgePlace;
        }
    }

    /// <summary>
    /// The overlay a hit names, or null when the thing dragged has no anchor — a layer's box is
    /// given as the canvas's own share, so it has nowhere to be re-anchored to.
    /// </summary>
    public IAnchored? AnchoredOf(HitKind kind)
    {
        var o = State.Overlays;
        return kind switch
        {
            HitKind.Logo => o.Logo,
            HitKind.Clock => o.Clock,
            HitKind.Countdown => State.Countdown,
            HitKind.Message => o.Message,
            HitKind.Pip => o.Pip,
            HitKind.Weather => o.Weather,
            HitKind.Badge => o.Badge,
            _ => null,
        };
    }

    /// <summary>
    /// A drag has ended: the same pixels, told from the nearest anchor. Nothing moves — the box is
    /// where it was dropped — but the Nudge sliders come back to counting from a corner or an edge
    /// that is still there at another size and on a canvas of another shape, instead of from an
    /// anchor the element has long since left behind. One publish for the pair.
    /// </summary>
    public void DragReanchor(HitKind kind, Anchor9 anchor, double x, double y)
    {
        if (AnchoredOf(kind) is not { } placed) return;
        BulkEdit(() =>
        {
            placed.Anchor = anchor;
            placed.OffsetXPct = x;
            placed.OffsetYPct = y;
        });
    }

    /// <summary>Puts a draggable thing at a place (the same units <see cref="DragPlaceOf"/> reads); the model publishes, the panes follow.</summary>
    public void DragPlace(HitKind kind, double x, double y)
    {
        if (AnchoredOf(kind) is { } placed)
        {
            placed.OffsetXPct = x;
            placed.OffsetYPct = y;
            return;
        }
        var p = PreviewPattern;
        switch (kind)
        {
            case HitKind.Layer1: p.Layer1.XPct = x; p.Layer1.YPct = y; break;
            case HitKind.Layer2: p.Layer2.XPct = x; p.Layer2.YPct = y; break;
        }
    }

    // ---- the area of interest: a crop picked on the PREVIEW pane ----------------------------

    private bool _cropPickActive;
    private string _cropSummary = "The whole picture.";

    /// <summary>PICK ON PREVIEW: the next drag on the PREVIEW pane draws a box around the part of the input to keep.</summary>
    public bool CropPickActive
    {
        get => _cropPickActive;
        set
        {
            if (!Set(ref _cropPickActive, value)) return;
            if (value) StatusMessage = "Drag a box on the PREVIEW pane around the part of the picture to keep — a second pick refines it.";
        }
    }

    /// <summary>What the area of interest keeps, in words, for the Media page.</summary>
    public string CropSummary { get => _cropSummary; private set => Set(ref _cropSummary, value); }

    /// <summary>
    /// A box drawn on the picture as the PREVIEW pane showed it — its sides as shares (0–1) of the
    /// visible part — becomes the area of interest of the pattern the pane shows; the sides compose
    /// with any crop already there, so a second pick refines the first.
    /// </summary>
    public void ApplyCropBand(double left01, double top01, double right01, double bottom01)
    {
        var m = PreviewPattern.Media;
        var next = m.Crop.Within(left01, top01, right01, bottom01);
        BulkEdit(() =>
        {
            m.CropLeftPct = next.LeftPct;
            m.CropTopPct = next.TopPct;
            m.CropRightPct = next.RightPct;
            m.CropBottomPct = next.BottomPct;
        });
        CropPickActive = false;
        RefreshCropSummary();
        StatusMessage = $"Area of interest set — {CropSummary} {(IsSandboxActive ? "In the preview; CUT or TAKE puts it on air." : "On air.")}";
    }

    /// <summary>The whole picture again.</summary>
    public void ClearCrop()
    {
        var m = ActivePattern.Media;
        BulkEdit(() =>
        {
            m.CropLeftPct = 0;
            m.CropTopPct = 0;
            m.CropRightPct = 0;
            m.CropBottomPct = 0;
        });
        RefreshCropSummary();
        StatusMessage = "The whole picture again.";
    }

    /// <summary>A starting point to refine with a pick: "top:8", "right:25", "bottom:12", "left:20" cut one side; "centre:80" keeps the middle share.</summary>
    public void ApplyCropPreset(string preset)
    {
        var parts = preset.Split(':');
        if (parts.Length != 2 || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct)) return;
        var m = ActivePattern.Media;
        BulkEdit(() =>
        {
            switch (parts[0].Trim().ToLowerInvariant())
            {
                case "top": m.CropTopPct = pct; break;
                case "bottom": m.CropBottomPct = pct; break;
                case "left": m.CropLeftPct = pct; break;
                case "right": m.CropRightPct = pct; break;
                case "centre":
                case "center":
                {
                    var cut = Math.Max(0, (100 - pct) / 2);
                    m.CropLeftPct = cut;
                    m.CropRightPct = cut;
                    m.CropTopPct = cut;
                    m.CropBottomPct = cut;
                    break;
                }
            }
        });
        RefreshCropSummary();
    }

    private void RefreshCropSummary()
    {
        var m = ActivePattern.Media;
        var words = m.Crop.Summary();
        var quarter = m.RotateQuarters % 4;
        var turn = quarter != 0 ? $" Turned {quarter * 90}°." : "";
        var flip = m.FlipHorizontal && m.FlipVertical ? " Mirrored and upside down."
            : m.FlipHorizontal ? " Mirrored."
            : m.FlipVertical ? " Upside down." : "";
        CropSummary = words + turn + flip;
    }

    private void BulkEdit(Action edit) => _services.BulkEdit(edit);

    // ---- web pages inside the engine -----------------------------------------------

    private string _webTypedText = "";
    public string WebTypedText { get => _webTypedText; set => Set(ref _webTypedText, value ?? ""); }

    private string _webControlsTarget = "";
    /// <summary>Which page the PAGE CONTROLS drive, in words.</summary>
    public string WebControlsTarget { get => _webControlsTarget; private set => Set(ref _webControlsTarget, value); }

    private string _webPageStatus = "";
    public string WebPageStatus { get => _webPageStatus; private set => Set(ref _webPageStatus, value); }

    private string _lastWebKey = "";

    /// <summary>The desk pointed at a page on the PREVIEW pane: the controls follow it.</summary>
    public void NoteWebPage(string key)
    {
        if (key.Length == 0 || key == _lastWebKey) return;
        _lastWebKey = key;
        RefreshWebControls();
    }

    /// <summary>The page the controls drive: the one last pointed at while it is mounted, else the pattern's page, else its first web layer.</summary>
    public string CurrentWebKey()
    {
        if (_lastWebKey.Length > 0 && InputBus.For(_lastWebKey) is IWebSource) return _lastWebKey;
        var p = ActivePattern;
        if (p.Kind == PatternKind.Media && p.Media.Source == MediaSource.Web && p.Media.WebUrl.Length > 0) return InputKeys.Web(p.Media.WebUrl);
        foreach (var l in new[] { p.Layer1, p.Layer2 })
        {
            if (l.Enabled && l.Source == LayerSource.Web && l.WebUrl.Length > 0) return InputKeys.Web(l.WebUrl);
        }
        return "";
    }

    public IWebSource? CurrentWebSource() => InputBus.For(CurrentWebKey()) as IWebSource;

    /// <summary>The Media page shows the page controls while a page is in play somewhere the desk can reach.</summary>
    public bool HasWebPage => CurrentWebKey().Length > 0;

    private bool _keysToPage;

    /// <summary>
    /// KEYS → PAGE: the keyboard belongs to the page the controls drive — F5 starts a PowerPoint,
    /// the arrows move a deck, k plays a YouTube video, a search box or a sign-in takes typing —
    /// until the chip or Ctrl+Alt+K ends it. The desk's own shortcuts (F-keys, Space, D, Enter) wait meanwhile.
    /// </summary>
    public bool KeysToPage
    {
        get => _keysToPage;
        set
        {
            if (value && CurrentWebSource() is null)
            {
                Raise(nameof(KeysToPage));   // the chip springs back
                StatusMessage = HasWebPage
                    ? "The page is still opening — try KEYS → PAGE again in a moment."
                    : "No web page to type at — put one on the pattern or a layer first.";
                return;
            }
            if (_keysToPage == value) return;
            _keysToPage = value;
            Raise(nameof(KeysToPage));
            StatusMessage = value
                ? $"KEYS → PAGE: every key goes to {WebAddress.ShortName(CurrentWebKey()[4..])} — F-keys, Space and Enter too. Press the chip or Ctrl+Alt+K to get the desk's keys back."
                : "KEYS → PAGE off — the desk has its keys again.";
        }
    }

    /// <summary>A chord from the desk's keyboard to the page the controls drive (KEYS → PAGE).</summary>
    public void SendKeyToPage(string chord)
    {
        if (CurrentWebSource() is { } page) page.PressKey(chord);
        else KeysToPage = false;
    }

    private string _webPresetNote = "";
    /// <summary>The pattern's page service — YouTube, Google Slides… — and what FULL FRAME does to its address; "" for a plain page.</summary>
    public string WebPresetNote { get => _webPresetNote; private set => Set(ref _webPresetNote, value); }

    private bool _webCanFullFrame;
    public bool WebCanFullFrame { get => _webCanFullFrame; private set => Set(ref _webCanFullFrame, value); }

    /// <summary>The actions the page the controls drive answers to — NEXT, PLAY, PRESENT… — from its service.</summary>
    public ObservableCollection<WebActionChip> WebPageActions { get; } = new();

    private string _webActionsFor = "";

    /// <summary>An action chip on the page the controls drive: "next", "play", "present"… or a key chord.</summary>
    public void RunWebAction(string idOrKey)
    {
        if (CurrentWebSource() is not { } page)
        {
            StatusMessage = "No web page to drive — put one on the pattern or a layer first.";
            return;
        }
        var name = State.InputLabel(CurrentWebKey(), WebAddress.ShortName(page.CurrentUrl));
        Report(WebActions.Press(page, name, idOrKey));
    }

    private void SyncWebActions(string url)
    {
        var preset = url.Length == 0 ? null : WebPresets.For(url);
        var stamp = preset?.Service.ToString() ?? "";
        if (stamp == _webActionsFor) return;
        _webActionsFor = stamp;
        WebPageActions.Clear();
        if (preset is null) return;
        foreach (var a in preset.Actions)
        {
            var hint = a.Hint.Length > 0 ? a.Hint : a.IsScript ? $"Through {preset.Name}'s own player" : "Key " + a.Chord;
            WebPageActions.Add(new WebActionChip(a.Id, a.Label.ToUpperInvariant(), hint));
        }
    }

    private string _savedWebPick = "";

    /// <summary>The saved pages combo on the Media page: picking one puts it in the pattern's page box.</summary>
    public string? SavedWebPick
    {
        get => _savedWebPick;
        set
        {
            var pick = value ?? "";
            if (pick == _savedWebPick) return;
            _savedWebPick = pick;
            if (pick.Length > 0) ActivePattern.Media.WebUrl = pick;
            Raise(nameof(SavedWebPick));
        }
    }

    /// <summary>Keeps the PAGE CONTROLS block honest: which page, whether it is up, what it shows. Cheap; runs on the 1 s poll.</summary>
    private void RefreshWebControls()
    {
        var key = CurrentWebKey();
        // The service line and the FULL FRAME offer follow the pattern's address, not the page pointed at.
        var address = ActivePattern.Kind == PatternKind.Media && ActivePattern.Media.Source == MediaSource.Web ? ActivePattern.Media.WebUrl : "";
        WebPresetNote = address.Length > 0 ? WebPresets.Note(address) : "";
        WebCanFullFrame = address.Length > 0 && WebPresets.CanFullFrame(address);
        Raise(nameof(HasWebPage));
        if (key.Length == 0)
        {
            WebControlsTarget = "";
            WebPageStatus = "";
            if (KeysToPage) KeysToPage = false;
            SyncWebActions("");
            return;
        }
        var page = InputBus.For(key) as IWebSource;
        var name = State.InputLabel(key, WebAddress.ShortName(key[4..]));
        WebControlsTarget = $"Controls drive: {name}" + (_lastWebKey == key ? " (the page you pointed at)" : "");
        WebPageStatus = page is null
            ? WebInput.AvailabilityNote.Length > 0 ? WebInput.AvailabilityNote : "Opening…"
            : $"{page.StatusText}{(page.Title.Length > 0 ? " — " + page.Title : "")} · {page.CurrentUrl}";
        SyncWebActions(page?.CurrentUrl is { Length: > 0 } current ? current : key[4..]);
    }

    // ---- pattern editing target ---------------------------------------------

    public ObservableCollection<EditTarget> EditTargets { get; } = new() { new EditTarget("Program", null) };

    public EditTarget EditTarget
    {
        get => _editTarget;
        set
        {
            if (value is not null && Set(ref _editTarget, value))
            {
                Raise(nameof(ActivePattern));
                Raise(nameof(EditTargetBanner));
                Raise(nameof(CanvasInfo));
                Raise(nameof(ShowCanvasPanel));
                // The panes follow the editors: a target with its own pattern is selected with
                // it; "Program" keeps the selected tile unless that tile shows its own picture.
                if (value.ScreenId is not null) SelectTarget(value.ScreenId);
                else if (_selectedTargetId is { } selected && ContentTargets.UsesOwnPattern(State, selected)) SelectTarget(null);
                RefreshSwitcherTiles();
                RaisePlaylistSection();
            }
        }
    }

    /// <summary>The pattern the editor panels bind to (program, or a custom screen's).</summary>
    public PatternConfig ActivePattern
    {
        get
        {
            if (_editTarget.ScreenId is { } id)
            {
                var a = State.Independent.FirstOrDefault(x => x.ScreenId == id);
                if (a is not null) return a.Pattern;
            }
            return State.Pattern;
        }
    }

    public bool ShowEditTargets => EditTargets.Count > 1;

    private void EnsureAssignmentsForCustomScreens()
    {
        foreach (var p in State.Output.Placements.Where(p => p.UseCustomPattern))
        {
            EnsureAssignment(p.ScreenId);
        }
        foreach (var c in State.Output.CanvasNames.Where(c => c.UseCustomPattern && c.MemberKey.Length > 0))
        {
            EnsureAssignment(c.MemberKey);
        }
    }

    private void EnsureAssignment(string targetId) => ContentTargets.EnsureAssignment(State, targetId);

    private void RebuildEditTargets()
    {
        RefreshAdoptTargets();
        var current = _editTarget?.ScreenId;
        EditTargets.Clear();
        EditTargets.Add(new EditTarget("Program", null));
        // A joined canvas with its own pattern is an edit target like a screen. A member screen
        // keeps its own entry too (its pattern is what the screen shows when split off again).
        var groups = CanvasGroups();
        for (var i = 0; i < groups.Count; i++)
        {
            var key = CanvasNameConfig.KeyFor(groups[i].Select(m => m.ScreenId));
            if (!ContentTargets.UsesOwnPattern(State, key)) continue;
            var letter = ((char)('A' + i)).ToString();
            EditTargets.Add(new EditTarget($"Canvas {letter} — {CanvasNameFor(groups[i], letter)}", key));
        }
        foreach (var p in State.Output.Placements.Where(p => p.UseCustomPattern))
        {
            var info = LiveInfo(p);
            if (info is not null)
            {
                EditTargets.Add(new EditTarget($"Screen {info.Index + 1} — {LabelFor(p, info)}", p.ScreenId));
            }
        }
        // The target after the rebuild: the same one when it is still there, else Program — never
        // nothing. Clearing the list emptied the Pattern page's picker (its two-way binding wrote
        // that empty selection back, refused below), and a same target re-added as an equal record
        // used to raise nothing, so the picker stayed blank until the operator picked it again:
        // the same target is re-published as the list's own instance, and only a different one
        // goes through the full setter (the panes follow that).
        var wanted = EditTargets.FirstOrDefault(t => t.ScreenId == current) ?? EditTargets[0];
        if (wanted.ScreenId == _editTarget?.ScreenId)
        {
            _editTarget = wanted;
            Raise(nameof(EditTarget));
            Raise(nameof(EditTargetBanner));
        }
        else
        {
            EditTarget = wanted;
        }
        Raise(nameof(ShowEditTargets));
        RebuildSwitcherTiles();
    }

    // ---- media library ------------------------------------------------------

    private void AddToMediaLibrary(string path, bool isVideo)
    {
        if (State.MediaLibrary.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        State.MediaLibrary.Add(new MediaLibraryEntry
        {
            Path = path,
            IsVideo = isVideo,
            Kind = MediaLibraryEntry.KindOf(path, isVideo),
            AddedUtc = DateTime.UtcNow,
        });
        BuildLibrary();
    }

    // ---- playlist -----------------------------------------------------------

    private string _playlistStatus = "";
    public string PlaylistStatus { get => _playlistStatus; private set => Set(ref _playlistStatus, value); }

    private async Task AddPlaylistFilesAsync()
    {
        var window = _services.MainWindow;
        if (window is null) return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add media to playlist",
                AllowMultiple = true,
                FileTypeFilter = new[] { MediaTypes, FilePickerFileTypes.All },
            });
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path is null) continue;
                ActivePlaylistSection.Items.Add(new PlaylistItemConfig { Path = path });
                AddToMediaLibrary(path, PlaylistSequencer.IsDecodedPath(path));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Playlist file picker failed.", ex);
        }
    }

    private async Task AddStingerFilesAsync()
    {
        var window = _services.MainWindow;
        if (window is null) return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add VOGs / stingers (sounds or video clips)",
                AllowMultiple = true,
                FileTypeFilter = new[] { MediaTypes, FilePickerFileTypes.All },
            });
            var skipped = 0;
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path is null) continue;
                if (!PlaylistSequencer.IsDecodedPath(path))
                {
                    skipped++; // images have no natural end — nothing to revert on
                    continue;
                }
                State.Stingers.Items.Add(new StingerItemConfig { Path = path });
                AddToMediaLibrary(path, PlaylistSequencer.IsVideoPath(path));
            }
            if (skipped > 0) StatusMessage = $"VOGs and stingers are sounds or video clips — {skipped} other file{(skipped == 1 ? "" : "s")} skipped.";
            RefreshStingerGroups();
        }
        catch (Exception ex)
        {
            Log.Error("Stinger file picker failed.", ex);
        }
    }

    private async Task AddPlaylistFolderAsync()
    {
        var window = _services.MainWindow;
        if (window is null) return;
        try
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Add media folder",
                AllowMultiple = true,
            });
            foreach (var folder in folders)
            {
                var path = folder.TryGetLocalPath();
                if (path is not null && !ActivePlaylistSection.Folders.Contains(path))
                {
                    ActivePlaylistSection.Folders.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Playlist folder picker failed.", ex);
        }
    }

    /// <summary>Tracks for the audio playlist: every audio file picked becomes a row (and a library entry); a file already in the list is left where it is.</summary>
    private async Task AddAudioFilesAsync()
    {
        var window = _services.MainWindow;
        if (window is null) return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add audio tracks",
                AllowMultiple = true,
                FileTypeFilter = new[] { AudioTypes, FilePickerFileTypes.All },
            });
            var added = 0;
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path is null || !PlaylistSequencer.IsAudioPath(path)) continue;
                if (State.AudioPlayer.Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                State.AudioPlayer.Items.Add(new AudioTrackConfig { Path = path });
                AddToMediaLibrary(path, isVideo: true);
                added++;
            }
            if (added > 0) StatusMessage = $"{added} track{(added == 1 ? "" : "s")} added to the audio playlist.";
        }
        catch (Exception ex)
        {
            Log.Error("Audio track picker failed.", ex);
        }
    }

    /// <summary>A folder for the audio playlist: its audio files play after the rows, in name order, and files dropped in later are seen.</summary>
    private async Task AddAudioFolderAsync()
    {
        var window = _services.MainWindow;
        if (window is null) return;
        try
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Add a folder of audio tracks",
                AllowMultiple = true,
            });
            foreach (var folder in folders)
            {
                var path = folder.TryGetLocalPath();
                if (path is not null && !State.AudioPlayer.Folders.Contains(path)) State.AudioPlayer.Folders.Add(path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio folder picker failed.", ex);
        }
    }

    private void MoveAudioItem(AudioTrackConfig? item, int delta)
    {
        if (item is null) return;
        var items = State.AudioPlayer.Items;
        var index = items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= items.Count) return;
        items.Move(index, target);
    }

    private void MovePlaylistItem(PlaylistItemConfig? item, int delta)
    {
        if (item is null) return;
        var items = ActivePlaylistSection.Items;
        var index = items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= items.Count) return;
        items.Move(index, target);
    }

    /// <summary>The playlist part the editor shows and files land in (normalizes legacy lists).</summary>
    public PlaylistSectionConfig ActivePlaylistSection => PlaylistSequencer.ActiveSectionOf(ActivePattern.Media.Playlist);

    private PlaylistSectionConfig? _lastRaisedSection;

    /// <summary>Re-binds the section editor when the on-air part actually changes; keeps chips lit.</summary>
    private void RaisePlaylistSection(bool onlyOnChange = false)
    {
        var current = ActivePlaylistSection;
        foreach (var section in ActivePattern.Media.Playlist.Sections)
        {
            section.IsOnAir = ReferenceEquals(section, current);
        }
        if (onlyOnChange && ReferenceEquals(current, _lastRaisedSection)) return;
        _lastRaisedSection = current;
        Raise(nameof(ActivePlaylistSection));
    }

    private PlaylistItemConfig? _selectedPlaylistItem;
    public PlaylistItemConfig? SelectedPlaylistItem
    {
        get => _selectedPlaylistItem;
        set
        {
            if (Set(ref _selectedPlaylistItem, value)) Raise(nameof(HasPlaylistItemSelection));
        }
    }

    public bool HasPlaylistItemSelection => _selectedPlaylistItem is not null;

    /// <summary>Drag-reorder target from the playlist list (index clamped; no-ops in place).</summary>
    public void MovePlaylistItemTo(PlaylistItemConfig item, int targetIndex)
    {
        var items = ActivePlaylistSection.Items;
        var index = items.IndexOf(item);
        if (index < 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, items.Count - 1);
        if (targetIndex == index) return;
        items.Move(index, targetIndex);
    }

    // ---- NDI feed & capture inputs -----------------------------------------

    public ObservableCollection<string> NdiSourceOptions { get; } = new();
    public ObservableCollection<string> CaptureDeviceOptions { get; } = new();
    private bool _captureListLoaded;
    private int _ndiPollTick;

    private void RefreshNdiSources(bool quiet = false)
    {
        // Clearing the list nulls the combo selection, which would write "" into the
        // model — capture and restore the operator's choice around the rebuild.
        var current = ActivePattern.Media.NdiSourceName;
        var found = _services.NdiIn.DiscoverSources();
        NdiSourceOptions.Clear();
        foreach (var s in found)
        {
            NdiSourceOptions.Add(s);
        }
        if (!string.IsNullOrWhiteSpace(current) && !NdiSourceOptions.Contains(current))
        {
            NdiSourceOptions.Insert(0, current);
        }
        ActivePattern.Media.NdiSourceName = current;
        if (!quiet)
        {
            StatusMessage = found.Count == 0
                ? "No NDI sources visible yet — senders appear within a few seconds of starting."
                : $"{found.Count} NDI source{(found.Count == 1 ? "" : "s")} on the network.";
        }
    }

    private void RefreshCaptureDevices(bool quiet = false)
    {
        _captureListLoaded = true;
        var current = ActivePattern.Media.CaptureDevice;
        var found = CaptureDevices.List();
        CaptureDeviceOptions.Clear();
        foreach (var d in found)
        {
            CaptureDeviceOptions.Add(d);
        }
        if (!string.IsNullOrWhiteSpace(current) && !CaptureDeviceOptions.Contains(current))
        {
            CaptureDeviceOptions.Insert(0, current);
        }
        ActivePattern.Media.CaptureDevice = current;
        if (!quiet)
        {
            StatusMessage = found.Count == 0
                ? "No capture devices found (device lists need Windows)."
                : $"{found.Count} capture device{(found.Count == 1 ? "" : "s")} found.";
        }
    }


    // ---- multiview ----------------------------------------------------------

    /// <summary>Target choices for multiview tiles, in wall order: joined canvases, then every screen.</summary>
    public ObservableCollection<EditTarget> MultiviewTargets { get; } = new();

    private void RebuildMultiviewTargets()
    {
        var geo = Rig.Geometry(State, _services.Screens.All);
        var wanted = new List<EditTarget>();
        foreach (var key in geo.Targets)
        {
            if (ContentTargets.IsCanvasKey(key)) wanted.Add(new EditTarget(geo.LabelFor(State, key), key));
        }
        foreach (var s in geo.Screens)
        {
            wanted.Add(new EditTarget(geo.LabelFor(State, s.Id), s.Id));
        }
        ReplaceIfChanged(MultiviewTargets, wanted);
    }

    /// <summary>
    /// A picker bound two-way to a model id is rebuilt in place only when its entries really
    /// moved: clearing a ComboBox's items drops its selection, and the binding would write that
    /// empty selection back into the show. Same entries, same order = nothing happens.
    /// </summary>
    private static void ReplaceIfChanged(ObservableCollection<EditTarget> current, List<EditTarget> wanted)
    {
        if (current.Count == wanted.Count && current.SequenceEqual(wanted)) return;
        current.Clear();
        foreach (var t in wanted) current.Add(t);
    }

    // ---- decks: a PDF presentation, a page at a time ------------------------------

    private bool _deckOnAir;

    /// <summary>A deck is on air: the clicker's keys turn its pages whether or not a list is armed (kept fresh with the tallies).</summary>
    public bool DeckOnAir { get => _deckOnAir; private set => Set(ref _deckOnAir, value); }

    private string _deckPageText = "";

    /// <summary>The Media page's readout for the deck the pattern shows: "Page 3 / 12", or why there is none.</summary>
    public string DeckPageText { get => _deckPageText; private set => Set(ref _deckPageText, value); }

    private string _deckToolText = "";

    /// <summary>For a PowerPoint, Keynote or Impress deck: where LibreOffice was found, or what to do — "" for a PDF.</summary>
    public string DeckToolText { get => _deckToolText; private set => Set(ref _deckToolText, value); }

    private bool _deckToolMissing;

    /// <summary>The deck needs LibreOffice and none was found: the path box shows.</summary>
    public bool DeckToolMissing { get => _deckToolMissing; private set => Set(ref _deckToolMissing, value); }

    /// <summary>Admin → the operator's own path to LibreOffice (soffice.exe or its folder); a change searches again at once.</summary>
    public string LibreOfficePath
    {
        get => State.Admin.LibreOfficePath;
        set
        {
            if (State.Admin.LibreOfficePath == (value ?? "")) return;
            State.Admin.LibreOfficePath = value ?? "";
            _services.DeckIn.Converter.ForgetProbe();
            Raise(nameof(LibreOfficePath));
            RefreshDeck();
        }
    }

    /// <summary>The deck the pattern on the desk shows — the one the page buttons turn — or null.</summary>
    private IDeckSource? DeskDeck()
    {
        var m = ActivePattern.Media;
        if (ActivePattern.Kind != PatternKind.Media || m.Source != MediaSource.Deck || m.DeckPath.Length == 0) return null;
        return InputBus.For(InputKeys.Deck(m.DeckPath)) as IDeckSource;
    }

    private void TurnDeskDeck(string page)
    {
        if (DeskDeck() is not { } deck)
        {
            StatusMessage = ActivePattern.Media.DeckPath.Length == 0 ? "Choose a deck first." : "The deck is still opening.";
            return;
        }
        if (deck.PageCount == 0)
        {
            StatusMessage = deck.StatusText;
            return;
        }
        var target = Decks.Resolve(page, deck.Page, deck.PageCount);
        if (target > 0) deck.GoTo(target);
        RefreshDeck();
        StatusMessage = DeckPageText;
    }

    /// <summary>Keeps the deck readout and the click-through flag honest; cheap, runs with the tallies.</summary>
    private void RefreshDeck()
    {
        DeckOnAir = _services.DeckOnAir() is { PageCount: > 0 };
        var m = ActivePattern.Media;
        var isDeck = ActivePattern.Kind == PatternKind.Media && m.Source == MediaSource.Deck;
        if (isDeck && DeckConversion.NeedsConversion(m.DeckPath))
        {
            // The converter's word for a PowerPoint: found where, or what to do (the search itself repeats at most every 20 s).
            var tool = _services.DeckIn.Converter.LibreOffice;
            DeckToolText = DeckConversion.Describe(tool);
            DeckToolMissing = tool is null;
        }
        else
        {
            DeckToolText = "";
            DeckToolMissing = false;
        }
        if (!isDeck)
        {
            DeckPageText = "";
        }
        else if (m.DeckPath.Length == 0)
        {
            DeckPageText = "Choose a deck — a PDF, or a PowerPoint that LibreOffice converts — the click-through turns its pages once it is on air.";
        }
        else if (DeskDeck() is not { } deck)
        {
            DeckPageText = DeckInput.AvailabilityNote.Length > 0 ? DeckInput.AvailabilityNote : "Opening the deck…";
        }
        else if (deck.PageCount == 0)
        {
            DeckPageText = deck.StatusText;
        }
        else
        {
            var tail = deck.AtEnd
                ? m.DeckEndsWithGo ? " — the last page: the next click GOes the standby cue" : " — the last page"
                : "";
            DeckPageText = $"Page {deck.Page} / {deck.PageCount} · {deck.PageShape.Width:0}×{deck.PageShape.Height:0} pt{tail}";
        }
        Raise(nameof(PresenterStepText));
            Raise(nameof(ProgressionText));
    }

    public bool ClickerArmed
    {
        get => _services.Cues.For(CueStacks.Clicker(State)).Armed;
        set
        {
            var rt = _services.Cues.For(CueStacks.Clicker(State));
            if (rt.Armed == value) return;
            _services.Actions.Execute(value ? ShowActionKind.ListArm : ShowActionKind.ListDisarm, ActionOrigin.Desk, CueStacks.Clicker(State).Id);
            Raise(nameof(ClickerArmed));
        }
    }

    /// <summary>The Cues page.</summary>
    public CueEditor Cues { get; }

    /// <summary>The Run surface's state and commands.</summary>
    public RunViewModel Run { get; }

    private bool _isRunLayout;

    /// <summary>
    /// The caller's one-column layout instead of the editors and the switcher. It is the Run
    /// page of the SHOW group: setting it selects that page (or the last Build page on the way
    /// out), and leaving is refused while the caller's stack is armed.
    /// </summary>
    public bool IsRunLayout
    {
        get => _isRunLayout;
        set
        {
            if (value == _isRunLayout) return;
            SelectPage(value ? Shell.RunPage : _lastBuildPage);
        }
    }

    private void SetRunLayout(bool value)
    {
        if (_isRunLayout == value) return;
        _isRunLayout = value;
        Raise(nameof(IsRunLayout));
        Raise(nameof(RunLayoutButtonText));
        if (value)
        {
            Run.Refresh();
            if (_services.RecoveryBanner.Length > 0)
            {
                Run.Banner = _services.RecoveryBanner;
                _services.RecoveryBanner = "";
            }
            StatusMessage = "RUN — Enter is GO while armed, ↑ ↓ move standby, Esc twice is STOP ALL. Space is still blackout.";
        }
    }

    // ---- audio / fonts / feed / LED map ------------------------------------

    public EnumItem[] ToneModes => Lists.ToneModes;
    public EnumItem[] ToneChannelsList => Lists.ToneChannelsList;
    public EnumItem[] FeedKinds => Lists.FeedKinds;
    public EnumItem[] MessageBackgrounds => Lists.MessageBackgrounds;
    public EnumItem[] Rotations => Lists.Rotations;
    public EnumItem[] PipSources => Lists.PipSources;

    private string _toneStatus = "Off";
    public string ToneStatus { get => _toneStatus; private set => Set(ref _toneStatus, value); }

    private string _feedStatus = "";
    public string FeedStatus { get => _feedStatus; private set => Set(ref _feedStatus, value); }

    public const string BuiltInFontLabel = "Inter (built-in)";

    private List<string>? _fontFamilies;
    public List<string> FontFamilies => _fontFamilies ??= BuildFontFamilies();

    private static List<string> BuildFontFamilies()
    {
        var list = new List<string> { BuiltInFontLabel };
        try
        {
            list.AddRange(SkiaSharp.SKFontManager.Default.FontFamilies
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct()
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Warn("System font enumeration failed.", ex);
        }
        return list;
    }

    /// <summary>Maps the empty model value ⇄ the built-in entry for the combo.</summary>
    public string? SelectedFontFamily
    {
        get => string.IsNullOrEmpty(State.Brand.FontFamily) ? BuiltInFontLabel : State.Brand.FontFamily;
        set
        {
            // The combo coerces values missing from its list to null (e.g. a show file made
            // on a machine with fonts this one lacks) — never let that clear the model; the
            // renderer already falls back to the built-in font for missing families.
            if (value is null) return;
            State.Brand.FontFamily = value == BuiltInFontLabel ? "" : value;
            Raise();
        }
    }

    private LedTileConfig? _selectedLedTile;
    public LedTileConfig? SelectedLedTile
    {
        get => _selectedLedTile;
        set
        {
            if (Set(ref _selectedLedTile, value)) Raise(nameof(HasLedTileSelection));
        }
    }

    public bool HasLedTileSelection => _selectedLedTile is not null;

    private void AddLedTile()
    {
        var led = ActivePattern.LedWall;
        var tiles = led.CustomTiles;
        var x = tiles.Count == 0 ? 0 : tiles.Max(t => t.X + t.Width);
        var tile = new LedTileConfig { X = x, Y = 0, Width = led.TileWidth, Height = led.TileHeight };
        tiles.Add(tile);
        led.UseCustomMap = true;
        SelectedLedTile = tile;
    }

    /// <summary>Seeds the custom map from the current regular grid — then edit the exceptions.</summary>
    private void ImportGridToMap()
    {
        var led = ActivePattern.LedWall;
        var layout = CanvasResolver.Led(led);
        _services.BulkEdit(() =>
        {
            led.CustomTiles.Clear();
            for (var r = 0; r < layout.Rows; r++)
            {
                for (var col = 0; col < layout.Columns; col++)
                {
                    led.CustomTiles.Add(new LedTileConfig
                    {
                        X = col * layout.TileWidth,
                        Y = r * layout.TileHeight,
                        Width = layout.TileWidth,
                        Height = layout.TileHeight,
                    });
                }
            }
            led.UseCustomMap = true;
        });
        SelectedLedTile = led.CustomTiles.FirstOrDefault();
        StatusMessage = $"Imported {led.CustomTiles.Count} tiles from the grid — drag or edit the exceptions.";
    }

    // ---- lists for the views ------------------------------------------------

    public EnumItem[] PatternKinds => Lists.PatternKinds;
    public EnumItem[] MultiviewSourceKinds => Lists.MultiviewSources;
    public EnumItem[] Anchors => Lists.Anchors;
    public EnumItem[] FitModes => Lists.FitModes;
    public EnumItem[] BarsVariants => Lists.BarsVariants;
    public EnumItem[] RampVariants => Lists.RampVariants;
    public EnumItem[] MotionVariants => Lists.MotionVariants;
    public EnumItem[] BlendCurves => Lists.BlendCurves;
    public EnumItem[] BlendOrientations => Lists.BlendOrientations;
    public EnumItem[] TileNumberings => Lists.TileNumberings;
    public EnumItem[] MediaSources => Lists.MediaSources;
    public EnumItem[] ParticleShapes => Lists.ParticleShapes;
    public EnumItem[] ParticleEmitters => Lists.ParticleEmitters;
    public EnumItem[] CountdownKinds => Lists.CountdownKinds;
    public EnumItem[] CountdownEnds => Lists.CountdownEnds;
    public EnumItem[] ScaleModes => Lists.ScaleModes;
    public ResolutionPreset[] Resolutions => Lists.Resolutions;
    public string[] CountdownLabels => Lists.CountdownLabels;
    public string[] ParticlePresetNames => ParticlePresets.Names;

    // ---- fractal ------------------------------------------------------------

    public string[] FractalPresetNames => FractalPresets.Names;

    /// <summary>The Fractals page's chips: every family in order, then "Custom" — the operator's saved fractal presets.</summary>
    public ObservableCollection<FractalSceneGroup> FractalSceneGroups { get; } = new();

    private RelayCommand<FractalChip>? _applyFractalChip;

    public RelayCommand<FractalChip> ApplyFractalChipCommand => _applyFractalChip ??= new RelayCommand<FractalChip>(chip => chip?.Apply());

    private RelayCommand? _applyBrandPaletteToFractal;

    /// <summary>The brand kit's five colours as the fractal's palette — the ground first, the text colour last, so a scene reads in the client's colours.</summary>
    public RelayCommand ApplyBrandPaletteToFractalCommand => _applyBrandPaletteToFractal ??= new RelayCommand(() =>
    {
        var b = State.Brand;
        var palette = string.Join(",", b.BackgroundColor, b.PrimaryColor, b.SecondaryColor, b.AccentColor, b.TextColor);
        _services.BulkEdit(() => ActivePattern.Fractal.ColorsCsv = palette);
        StatusMessage = $"The brand kit's colours are the fractal's palette: {palette}.";
    });

    private void RefreshFractalSceneGroups()
    {
        var groups = new List<FractalSceneGroup>();
        foreach (var category in FractalPresets.Categories)
        {
            groups.Add(new FractalSceneGroup(category, FractalPresets.In(category)
                .Select(scene => new FractalChip(scene.Name, () => _services.BulkEdit(() => FractalPresets.Apply(scene.Name, ActivePattern.Fractal))))
                .ToList()));
        }
        var custom = new List<FractalChip>();
        foreach (var (name, path) in _services.Store.ListPresets())
        {
            var p = path;
            if (_services.Store.LoadPreset(p) is not { Kind: PatternKind.Fractal }) continue;
            custom.Add(new FractalChip(name, () =>
            {
                if (_services.Store.LoadPreset(p) is not { } cfg) return;
                _services.BulkEdit(() => ModelCopier.Copy(cfg.Fractal, ActivePattern.Fractal));
            }));
        }
        if (custom.Count > 0) groups.Add(new FractalSceneGroup("Custom", custom));
        if (FractalSceneGroups.Count == groups.Count &&
            FractalSceneGroups.Zip(groups).All(z => z.First.Category == z.Second.Category &&
                                                    z.First.Chips.Select(c => c.Name).SequenceEqual(z.Second.Chips.Select(c => c.Name))))
        {
            return; // the same chips: leave the page alone
        }
        FractalSceneGroups.Clear();
        foreach (var g in groups) FractalSceneGroups.Add(g);
    }

    // ---- library ------------------------------------------------------------

    /// <summary>The section chips, in the order they are shown; "All" first.</summary>
    public static readonly string[] SectionNames = { "All", "Patterns", "Images", "Videos", "Audio", "Particles", "Fractals", "Presets", "Brand kits" };

    public string[] LibrarySections => SectionNames;

    /// <summary>Every tile, whatever the chips and the search say.</summary>
    public List<PresetItem> LibraryAll { get; } = new();

    /// <summary>The tiles the page shows: <see cref="LibraryAll"/> through the section chip and the search box.</summary>
    public ObservableCollection<PresetItem> Library { get; } = new();

    private string _selectedLibrarySection = "All";
    public string SelectedLibrarySection
    {
        get => _selectedLibrarySection;
        set
        {
            if (!Set(ref _selectedLibrarySection, value ?? "All")) return;
            ApplyLibraryFilter();
        }
    }

    private string _librarySearch = "";
    public string LibrarySearch
    {
        get => _librarySearch;
        set
        {
            if (!Set(ref _librarySearch, value ?? "")) return;
            ApplyLibraryFilter();
        }
    }

    private string _librarySummary = "";

    /// <summary>"74 tiles", or "12 of 74 · Images · 'logo'".</summary>
    public string LibrarySummary { get => _librarySummary; private set => Set(ref _librarySummary, value); }

    /// <summary>The thumbnails in flight for the current library — awaited by tests and the screenshot pass.</summary>
    public Task LibraryThumbnails { get; private set; } = Task.CompletedTask;

    private RelayCommand<PresetItem>? _removeLibraryItem;

    public RelayCommand<PresetItem> RemoveLibraryItemCommand => _removeLibraryItem ??= new RelayCommand<PresetItem>(item =>
    {
        if (item?.Remove is null) return;
        item.Remove();
        StatusMessage = $"'{item.Name}' taken out of the library — the file itself stays.";
        RefreshLibrary();
    });

    private RelayCommand? _refreshLibrary;

    public RelayCommand RefreshLibraryCommand => _refreshLibrary ??= new RelayCommand(RefreshLibrary);

    /// <summary>Rebuilds every tile — the factory table, the show's media, the saved presets, the brand kits — and re-renders the thumbnails.</summary>
    public void RefreshLibrary() => BuildLibrary();

    // ---- particle scenes, by pack ----------------------------------------------

    /// <summary>The Particles page's chips: every factory pack in order, then "Custom" — the operator's saved particle presets.</summary>
    public ObservableCollection<ParticlePackGroup> ParticlePackGroups { get; } = new();

    private RelayCommand<ParticleChip>? _applyParticleChip;

    public RelayCommand<ParticleChip> ApplyParticleChipCommand => _applyParticleChip ??= new RelayCommand<ParticleChip>(chip => chip?.Apply());

    private void RefreshParticlePackGroups()
    {
        var groups = new List<ParticlePackGroup>();
        foreach (var category in ParticlePresets.Categories)
        {
            groups.Add(new ParticlePackGroup(category, ParticlePresets.In(category)
                .Select(pack => new ParticleChip(pack.Name, () => _services.BulkEdit(() => ParticlePresets.Apply(pack.Name, ActivePattern.Particles))))
                .ToList()));
        }
        var custom = new List<ParticleChip>();
        foreach (var (name, path) in _services.Store.ListPresets())
        {
            var p = path;
            if (_services.Store.LoadPreset(p) is not { Kind: PatternKind.Particles }) continue;
            custom.Add(new ParticleChip(name, () =>
            {
                if (_services.Store.LoadPreset(p) is not { } cfg) return;
                _services.BulkEdit(() => ModelCopier.Copy(cfg.Particles, ActivePattern.Particles));
            }));
        }
        if (custom.Count > 0) groups.Add(new ParticlePackGroup("Custom", custom));
        if (ParticlePackGroups.Count == groups.Count &&
            ParticlePackGroups.Zip(groups).All(z => z.First.Category == z.Second.Category &&
                                                    z.First.Chips.Select(c => c.Name).SequenceEqual(z.Second.Chips.Select(c => c.Name))))
        {
            return; // the same chips: leave the page alone
        }
        ParticlePackGroups.Clear();
        foreach (var g in groups) ParticlePackGroups.Add(g);
    }

    private void BuildLibrary()
    {
        RefreshParticlePackGroups();
        RefreshFractalSceneGroups();
        LibraryAll.Clear();
        foreach (var b in BuiltInPresets.All)
        {
            var preset = b;
            LibraryAll.Add(new PresetItem
            {
                Id = $"builtin:{preset.Category}:{preset.Name}",
                Section = preset.Section,
                Category = preset.Category,
                Name = preset.Name,
                Apply = () => _services.BulkEdit(() => preset.Apply(ActivePattern)),
                ThumbConfig = baseState =>
                {
                    var config = JsonUtil.ClonePattern(baseState.Pattern);
                    preset.Apply(config);
                    return config;
                },
            });
        }

        foreach (var media in State.MediaLibrary.ToList())
        {
            var entry = media;
            var kind = entry.Kind == LibraryMediaKind.Unknown ? MediaLibraryEntry.KindOf(entry.Path, entry.IsVideo) : entry.Kind;
            var (section, category) = kind switch
            {
                LibraryMediaKind.Video => ("Videos", "My videos"),
                LibraryMediaKind.Audio => ("Audio", "My audio"),
                LibraryMediaKind.Deck => ("Decks", "My decks"),
                _ => ("Images", "My images"),
            };
            LibraryAll.Add(new PresetItem
            {
                Id = "media:" + entry.Id,
                Section = section,
                Category = category,
                Name = entry.DisplayName,
                Apply = () => _services.BulkEdit(() => ApplyMedia(ActivePattern, entry, kind)),
                ThumbConfig = baseState =>
                {
                    var config = JsonUtil.ClonePattern(baseState.Pattern);
                    ApplyMedia(config, entry, kind);
                    return config;
                },
                Remove = () => State.MediaLibrary.Remove(entry),
            });
        }

        foreach (var (name, path) in _services.Store.ListPresets())
        {
            var p = path;
            LibraryAll.Add(new PresetItem
            {
                Id = "preset:" + p,
                Section = "Presets",
                Category = "My presets",
                Name = name,
                Apply = () =>
                {
                    var cfg = _services.Store.LoadPreset(p);
                    if (cfg is not null) _services.BulkEdit(() => ModelCopier.Copy(cfg, ActivePattern));
                },
                ThumbConfig = _ => _services.Store.LoadPreset(p),
            });
        }

        foreach (var (name, path) in _services.Store.ListBrandKits())
        {
            var p = path;
            var kit = _services.Store.LoadBrandKit(p);
            if (kit is null) continue;
            var kitName = name;
            LibraryAll.Add(new PresetItem
            {
                Id = "brand:" + p,
                Section = "Brand kits",
                Category = "Brand kit",
                Name = kitName,
                Apply = () =>
                {
                    var fresh = _services.Store.LoadBrandKit(p);
                    if (fresh is null) return;
                    _services.BulkEdit(() => ModelCopier.Copy(fresh, State.Brand));
                    StatusMessage = $"Brand kit '{kitName}' applied.";
                },
                Swatch = new[] { kit.PrimaryColor, kit.SecondaryColor, kit.AccentColor, kit.BackgroundColor, kit.TextColor },
            });
        }

        ApplyLibraryFilter();
        LibraryThumbnails = RenderThumbnailsAsync(LibraryAll.ToList());
    }

    /// <summary>A media tile on a pattern: an image shows; a deck opens at its first page; a video or an audio file plays through the decoder.</summary>
    private static void ApplyMedia(PatternConfig target, MediaLibraryEntry entry, LibraryMediaKind kind)
    {
        target.Kind = PatternKind.Media;
        if (kind == LibraryMediaKind.Image)
        {
            target.Media.Source = MediaSource.Image;
            target.Media.ImagePath = entry.Path;
        }
        else if (kind == LibraryMediaKind.Deck)
        {
            target.Media.Source = MediaSource.Deck;
            target.Media.DeckPath = entry.Path;
        }
        else
        {
            target.Media.Source = MediaSource.Video;
            target.Media.VideoPath = entry.Path;
        }
    }

    /// <summary>The chip and the search box together: every search word must appear in the tile's name, category or section.</summary>
    private void ApplyLibraryFilter()
    {
        var words = LibrarySearch.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var section = SelectedLibrarySection;
        var shown = LibraryAll
            .Where(i => section == "All" || i.Section == section)
            .Where(i => words.All(w => i.SearchKey.Contains(w, StringComparison.Ordinal)))
            .ToList();
        Library.Clear();
        foreach (var i in shown) Library.Add(i);
        var where = section == "All" ? "" : $" · {section}";
        var searched = words.Length == 0 ? "" : $" · '{LibrarySearch.Trim()}'";
        LibrarySummary = shown.Count == LibraryAll.Count
            ? $"{LibraryAll.Count} tiles"
            : $"{shown.Count} of {LibraryAll.Count}{where}{searched}";
    }

    /// <summary>One thumbnail per tile, keyed by the tile itself — two files of one name in two folders each get their own.</summary>
    private async Task RenderThumbnailsAsync(IReadOnlyList<PresetItem> items)
    {
        var baseState = JsonUtil.Clone(State);
        foreach (var item in items)
        {
            try
            {
                Bitmap? bmp = null;
                if (item.Swatch is { } swatch)
                {
                    var caption = item.Name;
                    bmp = await Task.Run(() => ThumbnailRenderer.Swatch(swatch, caption));
                }
                else if (item.ThumbConfig?.Invoke(baseState) is { } cfg)
                {
                    bmp = await Task.Run(() => ThumbnailRenderer.Render(baseState, cfg));
                }
                if (bmp is not null) item.Thumbnail = bmp;
            }
            catch (Exception ex)
            {
                Log.Warn($"Thumbnail for '{item.Name}' failed.", ex);
            }
        }
    }

    private void SaveUserPreset()
    {
        var name = string.IsNullOrWhiteSpace(NewPresetName) ? $"Preset {DateTime.Now:HHmmss}" : NewPresetName.Trim();
        try
        {
            _services.Store.SavePreset(name, ActivePattern);
            StatusMessage = $"Preset '{name}' saved.";
            NewPresetName = "";
            BuildLibrary();
        }
        catch (Exception ex)
        {
            Log.Error("Preset save failed.", ex);
            StatusMessage = $"Preset save failed: {ex.Message}";
        }
    }

    private void SaveBrandKit()
    {
        var name = string.IsNullOrWhiteSpace(State.Brand.CompanyName) ? "brand" : State.Brand.CompanyName;
        try
        {
            _services.Store.SaveBrandKit(name, State.Brand);
            StatusMessage = $"Brand kit '{name}' saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Brand kit save failed: {ex.Message}";
        }
    }

    private async Task LoadBrandKitAsync()
    {
        var path = await PickOpenPathAsync("Load brand kit", new FilePickerFileType("Brand kit") { Patterns = new[] { "*.json" } },
            _services.Store.BrandKitsDirectory);
        if (path is null) return;
        var kit = _services.Store.LoadBrandKit(path);
        if (kit is not null)
        {
            _services.BulkEdit(() => ModelCopier.Copy(kit, State.Brand));
            StatusMessage = "Brand kit loaded.";
        }
    }
}
