using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// The Media page and what feeds the picture being edited: the files and decks browsed in, the
/// playlist and its parts, the area of interest picked on the PREVIEW pane, the live inputs
/// (NDI, capture, their nicknames), and the web pages inside the engine with the controls that
/// drive them. Built by the desk and reached as <c>Media.X</c>; polled from the desk's tick. The
/// desk keeps the pattern being edited, the preview's pattern, the sandbox's state, the media
/// library and the status line, and the page asks it for those.
/// </summary>
public sealed class MediaPage : Observable
{
    private readonly MainViewModel _desk;
    private readonly AppServices _services;

    private ShowState State => _services.State;
    private PatternConfig ActivePattern => _desk.ActivePattern;

    public MediaPage(MainViewModel desk, AppServices services)
    {
        _desk = desk;
        _services = services;

        BrowseImageCommand = new RelayCommand(() => _ = _desk.PickFileAsync("Choose image", FilePickerFileTypes.ImageAll, p =>
        {
            _services.BulkEdit(() =>
            {
                ActivePattern.Media.ImagePath = p;
                ActivePattern.Media.Source = MediaSource.Image;
            });
            _desk.AddToMediaLibrary(p, isVideo: false);
        }));
        BrowseVideoCommand = new RelayCommand(() => _ = _desk.PickFileAsync("Choose video", MainViewModel.VideoTypes, p =>
        {
            _services.BulkEdit(() =>
            {
                ActivePattern.Media.VideoPath = p;
                ActivePattern.Media.Source = MediaSource.Video;
            });
            _desk.AddToMediaLibrary(p, isVideo: true);
        }));
        // A deck: a PDF — or a PowerPoint through LibreOffice — a page at a time; the desk's buttons turn the deck the pattern shows.
        BrowseDeckCommand = new RelayCommand(() => _ = _desk.PickFileAsync("Choose a deck — a PDF or a PowerPoint", MainViewModel.DeckTypes, p =>
        {
            _services.BulkEdit(() =>
            {
                ActivePattern.Kind = PatternKind.Media;
                ActivePattern.Media.Source = MediaSource.Deck;
                ActivePattern.Media.DeckPath = p;
            });
            _desk.AddToMediaLibrary(p, isVideo: false);
            _desk.StatusMessage = DeckConversion.NeedsConversion(p)
                ? $"{System.IO.Path.GetFileName(p)} is the pattern — LibreOffice converts it to PDF once, then the click-through turns its pages on air."
                : $"{System.IO.Path.GetFileName(p)} is the pattern — the click-through turns its pages once it is on air.";
        }));
        ReloadDeckCommand = new RelayCommand(() =>
        {
            var path = ActivePattern.Media.DeckPath;
            if (path.Length == 0)
            {
                _desk.StatusMessage = "Choose a deck first.";
                return;
            }
            _services.DeckIn.Reload(path);
            _services.ReconcileInputs();
            _services.PublishRuntime();
            RefreshDeck();
            _desk.StatusMessage = DeckConversion.NeedsConversion(path)
                ? $"{System.IO.Path.GetFileName(path)} is being read and converted again."
                : $"{System.IO.Path.GetFileName(path)} is being read again.";
        });
        DeckNextCommand = new RelayCommand(() => TurnDeskDeck("next"));
        DeckPrevCommand = new RelayCommand(() => TurnDeskDeck("prev"));
        DeckFirstCommand = new RelayCommand(() => TurnDeskDeck("first"));
        DeckLastCommand = new RelayCommand(() => TurnDeskDeck("last"));

        // The playlist
        AddPlaylistFilesCommand = new RelayCommand(() => _ = AddPlaylistFilesAsync());
        AddPlaylistFolderCommand = new RelayCommand(() => _ = AddPlaylistFolderAsync());
        RemovePlaylistItemCommand = new RelayCommand<PlaylistItemConfig>(item =>
        {
            if (item is not null) ActivePlaylistSection.Items.Remove(item);
        });
        MovePlaylistItemUpCommand = new RelayCommand<PlaylistItemConfig>(item => MovePlaylistItem(item, -1));
        MovePlaylistItemDownCommand = new RelayCommand<PlaylistItemConfig>(item => MovePlaylistItem(item, +1));
        RemovePlaylistFolderCommand = new RelayCommand<string>(folder =>
        {
            if (folder is not null) ActivePlaylistSection.Folders.Remove(folder);
        });
        AddPlaylistSectionCommand = new RelayCommand(() =>
        {
            var playlist = ActivePattern.Media.Playlist;
            PlaylistSequencer.Normalize(playlist);
            playlist.Sections.Add(new PlaylistSectionConfig { Name = $"Part {playlist.Sections.Count + 1}" });
            playlist.ActiveSection = playlist.Sections.Count - 1;
            RaisePlaylistSection();
        });
        RemovePlaylistSectionCommand = new RelayCommand<PlaylistSectionConfig>(section =>
        {
            var playlist = ActivePattern.Media.Playlist;
            if (section is null || !playlist.Sections.Contains(section)) return;
            if (playlist.Sections.Count <= 1)
            {
                _desk.StatusMessage = "The playlist needs at least one part — clear its files instead.";
                return;
            }
            var index = playlist.Sections.IndexOf(section);
            playlist.Sections.Remove(section);
            if (playlist.ActiveSection >= index && playlist.ActiveSection > 0) playlist.ActiveSection--;
            RaisePlaylistSection();
        });
        SetPlaylistSectionCommand = new RelayCommand<PlaylistSectionConfig>(section =>
        {
            var playlist = ActivePattern.Media.Playlist;
            var index = section is null ? -1 : playlist.Sections.IndexOf(section);
            if (index < 0) return;
            playlist.ActiveSection = index;
            RaisePlaylistSection();
            _desk.StatusMessage = $"Playlist part '{section!.Name}' is on air.";
        });

        // Live inputs & web pages
        RefreshNdiSourcesCommand = new RelayCommand(() => RefreshNdiSources());
        RefreshCaptureDevicesCommand = new RelayCommand(() => RefreshCaptureDevices());
        LoadWebUrlCommand = new RelayCommand<string>(url =>
        {
            if (url is not null) State.Web.Url = url;
        });
        RemoveWebUrlCommand = new RelayCommand<string>(url =>
        {
            if (url is not null) State.Web.SavedUrls.Remove(url);
        });

        // Web pages inside the engine: the page the desk last pointed at (else the pattern's) takes typed text and keys
        SendWebTextCommand = new RelayCommand(() =>
        {
            if (CurrentWebSource() is not { } page)
            {
                _desk.StatusMessage = "No web page to type into — put one on the pattern or a layer first, then click into it on the PREVIEW pane.";
                return;
            }
            var text = WebTypedText;
            if (text.Length == 0) return;
            page.TypeText(text);
            WebTypedText = "";
            _desk.StatusMessage = $"Typed into {WebAddress.ShortName(page.CurrentUrl)} — Enter sends it, if the page wants that.";
        });
        WebKeyCommand = new RelayCommand<string>(key =>
        {
            if (key is not null && CurrentWebSource() is { } page) page.PressKey(key);
        });
        WebBackCommand = new RelayCommand(() => CurrentWebSource()?.GoBack());
        WebForwardCommand = new RelayCommand(() => CurrentWebSource()?.GoForward());
        WebReloadCommand = new RelayCommand(() => CurrentWebSource()?.Reload());
        RememberWebUrlCommand = new RelayCommand(() =>
        {
            var url = WebAddress.Normalize(ActivePattern.Media.WebUrl);
            if (url.Length == 0) return;
            if (!State.Web.SavedUrls.Contains(url)) State.Web.SavedUrls.Add(url);
            _desk.StatusMessage = $"Remembered {WebAddress.ShortName(url)} — it is in the saved pages here and on the Remote & web page.";
        });
        PutWebPageOnPatternCommand = new RelayCommand(() =>
        {
            var typed = WebAddress.Normalize(State.Web.Url);
            if (typed.Length == 0)
            {
                _desk.StatusMessage = "Enter a page address first.";
                return;
            }
            // A YouTube, Vimeo or Slides link goes on as the player or the deck alone — the streamlined path;
            // the Media page shows the address and can put the typed one back. The service named
            // here and the CLEAN tick travel with it, so the page lands the way it was set up.
            var pick = State.Web.Service;
            var url = WebPresets.FullFrame(typed, pick);
            var preset = WebPresets.For(url, pick);
            var clean = State.Web.Clean;
            _services.BulkEdit(() =>
            {
                ActivePattern.Kind = PatternKind.Media;
                ActivePattern.Media.Source = MediaSource.Web;
                ActivePattern.Media.WebUrl = url;
                ActivePattern.Media.WebService = pick;
                ActivePattern.Media.WebClean = clean;
            });
            if (!State.Web.SavedUrls.Contains(typed)) State.Web.SavedUrls.Add(typed);
            RefreshWebControls();
            _desk.StatusMessage = preset.Service == PageService.Page
                ? $"{WebAddress.ShortName(url)} is the pattern now — drive it on the PREVIEW pane; its settings are on the Media page."
                : $"{preset.Name} is the pattern now{(url == typed ? "" : ", full frame — the player or the deck alone")}. Drive it on the PREVIEW pane, with PAGE CONTROLS, the phone, cues or KEYS → PAGE.";
        });
        WebFullFrameCommand = new RelayCommand(() =>
        {
            var pick = ActivePattern.Media.WebService;
            var url = WebAddress.Normalize(ActivePattern.Media.WebUrl);
            var full = WebPresets.FullFrame(url, pick);
            if (url.Length == 0 || full == url)
            {
                _desk.StatusMessage = url.Length == 0 ? "Enter a page address first." : "That address is already the page alone.";
                return;
            }
            _services.BulkEdit(() => ActivePattern.Media.WebUrl = full);
            RefreshWebControls();
            _desk.StatusMessage = $"{WebPresets.For(full, pick).Name} full frame: {full}";
        });
        WebActionCommand = new RelayCommand<string>(id => RunWebAction(id ?? ""));
        // The armed web VT: ARM at the time typed (else the mark set, else where the player is), MARK, DISARM.
        WebArmCommand = new RelayCommand(() => RunWebVt(ShowActionKind.WebArm, WebArmText));
        WebMarkCommand = new RelayCommand(() => RunWebVt(ShowActionKind.WebMark, WebArmText));
        WebDisarmCommand = new RelayCommand(() => RunWebVt(ShowActionKind.WebArm, "off"));

        // The area of interest
        ClearCropCommand = new RelayCommand(ClearCrop);
        CropPresetCommand = new RelayCommand<string>(p => ApplyCropPreset(p ?? ""));
        ResetAdjustmentsCommand = new RelayCommand(ResetAdjustments);
    }

    public RelayCommand BrowseImageCommand { get; }
    public RelayCommand BrowseVideoCommand { get; }
    public RelayCommand BrowseDeckCommand { get; }
    public RelayCommand ReloadDeckCommand { get; }
    public RelayCommand DeckNextCommand { get; }
    public RelayCommand DeckPrevCommand { get; }
    public RelayCommand DeckFirstCommand { get; }
    public RelayCommand DeckLastCommand { get; }
    public RelayCommand AddPlaylistFilesCommand { get; }
    public RelayCommand AddPlaylistFolderCommand { get; }
    public RelayCommand<PlaylistItemConfig> RemovePlaylistItemCommand { get; }
    public RelayCommand<PlaylistItemConfig> MovePlaylistItemUpCommand { get; }
    public RelayCommand<PlaylistItemConfig> MovePlaylistItemDownCommand { get; }
    public RelayCommand<string> RemovePlaylistFolderCommand { get; }
    public RelayCommand AddPlaylistSectionCommand { get; }
    public RelayCommand<PlaylistSectionConfig> RemovePlaylistSectionCommand { get; }
    public RelayCommand<PlaylistSectionConfig> SetPlaylistSectionCommand { get; }
    public RelayCommand RefreshNdiSourcesCommand { get; }
    public RelayCommand RefreshCaptureDevicesCommand { get; }
    public RelayCommand<string> LoadWebUrlCommand { get; }
    public RelayCommand<string> RemoveWebUrlCommand { get; }
    public RelayCommand SendWebTextCommand { get; }
    public RelayCommand<string> WebKeyCommand { get; }
    public RelayCommand WebBackCommand { get; }
    public RelayCommand WebForwardCommand { get; }
    public RelayCommand WebReloadCommand { get; }
    public RelayCommand RememberWebUrlCommand { get; }
    public RelayCommand PutWebPageOnPatternCommand { get; }
    public RelayCommand WebFullFrameCommand { get; }
    public RelayCommand<string> WebActionCommand { get; }
    public RelayCommand WebArmCommand { get; }
    public RelayCommand WebMarkCommand { get; }
    public RelayCommand WebDisarmCommand { get; }
    public RelayCommand ClearCropCommand { get; }
    public RelayCommand<string> CropPresetCommand { get; }
    /// <summary>Round 77: RESET ADJUSTMENTS — no crop, no mirror, the right way up, upright.</summary>
    public RelayCommand ResetAdjustmentsCommand { get; }

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
            if (value) _desk.StatusMessage = "Drag a box on the PREVIEW pane around the part of the picture to keep — a second pick refines it.";
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
        var m = _desk.PreviewPattern.Media;
        var next = m.Crop.Within(left01, top01, right01, bottom01);
        _services.BulkEdit(() =>
        {
            m.CropLeftPct = next.LeftPct;
            m.CropTopPct = next.TopPct;
            m.CropRightPct = next.RightPct;
            m.CropBottomPct = next.BottomPct;
        });
        CropPickActive = false;
        RefreshCropSummary();
        _desk.StatusMessage = $"Area of interest set — {CropSummary} {(_desk.IsSandboxActive ? "In the preview; CUT or TAKE puts it on air." : "On air.")}";
    }

    /// <summary>The whole picture again.</summary>
    public void ClearCrop()
    {
        var m = ActivePattern.Media;
        _services.BulkEdit(() =>
        {
            m.CropLeftPct = 0;
            m.CropTopPct = 0;
            m.CropRightPct = 0;
            m.CropBottomPct = 0;
        });
        RefreshCropSummary();
        _desk.StatusMessage = "The whole picture again.";
    }

    /// <summary>
    /// Round 77: the picture as it came — every adjustment off at once. A mirror ticked on a preset
    /// long ago made every web page's writing read backwards on the wall, and the one tick that
    /// caused it sat three sections down the Media page; this is the one press that puts a picture
    /// right whatever was done to it.
    /// </summary>
    public void ResetAdjustments()
    {
        var m = ActivePattern.Media;
        if (!m.HasAdjustments)
        {
            _desk.StatusMessage = "The picture is as it came — nothing to reset.";
            return;
        }
        var was = m.AdjustmentWords();
        _services.BulkEdit(m.ResetAdjustments);
        RefreshCropSummary();
        _desk.StatusMessage = $"Adjustments reset — the picture was {was}; it is as it came now. {(_desk.IsSandboxActive ? "In the preview; CUT or TAKE puts it on air." : "On air.")}";
    }

    /// <summary>A starting point to refine with a pick: "top:8", "right:25", "bottom:12", "left:20" cut one side; "centre:80" keeps the middle share.</summary>
    public void ApplyCropPreset(string preset)
    {
        var parts = preset.Split(':');
        if (parts.Length != 2 || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct)) return;
        var m = ActivePattern.Media;
        _services.BulkEdit(() =>
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

    public void RefreshCropSummary()
    {
        var m = ActivePattern.Media;
        var words = m.Crop.Summary();
        var quarter = m.RotateQuarters % 4;
        var turn = quarter != 0 ? $" Turned {quarter * 90}°." : "";
        var flip = m.FlipHorizontal && m.FlipVertical ? " Mirrored and upside down."
            : m.FlipHorizontal ? " Mirrored."
            : m.FlipVertical ? " Upside down." : "";
        // Round 77: a mirrored page or deck is writing read backwards on the wall — said beside the tick that did it.
        var backwards = m.FlipHorizontal && m.Source is MediaSource.Web or MediaSource.Deck ? " Its writing reads backwards on the wall — RESET ADJUSTMENTS puts it right." : "";
        CropSummary = words + turn + flip + backwards;
    }

    // ---- web pages inside the engine -----------------------------------------------

    private string _webTypedText = "";
    public string WebTypedText { get => _webTypedText; set => Set(ref _webTypedText, value ?? ""); }

    private string _webControlsTarget = "";
    /// <summary>Which page the PAGE CONTROLS drive, in words.</summary>
    public string WebControlsTarget { get => _webControlsTarget; private set => Set(ref _webControlsTarget, value); }

    private string _webPageStatus = "";
    public string WebPageStatus { get => _webPageStatus; private set => Set(ref _webPageStatus, value); }

    // ---- the armed web VT ----------------------------------------------------------------------

    private string _webArmText = "";
    /// <summary>The time ARM and MARK take: "1:23", "83"; empty for the mark set, else where the player is now.</summary>
    public string WebArmText { get => _webArmText; set => Set(ref _webArmText, value ?? ""); }

    private string _webVtWords = "";
    /// <summary>The page's VT in one line: armed and where from, played and when, the player's clock, an advert over it.</summary>
    public string WebVtWords { get => _webVtWords; private set => Set(ref _webVtWords, value); }

    private bool _webArmed;
    /// <summary>A page's video is armed — the row's chip lights.</summary>
    public bool WebArmed { get => _webArmed; private set => Set(ref _webArmed, value); }

    private bool _webHasPlayer;
    /// <summary>The page has a video player answering — the row shows at all.</summary>
    public bool WebHasPlayer { get => _webHasPlayer; private set => Set(ref _webHasPlayer, value); }

    /// <summary>The pattern's own start point as text ("1:23"), written back as seconds; "" for the top.</summary>
    public string WebStartText
    {
        get => ActivePattern.Media.WebStartSeconds > 0 ? WebVt.TimeText(ActivePattern.Media.WebStartSeconds) : "";
        set
        {
            var text = (value ?? "").Trim();
            if (text.Length == 0) ActivePattern.Media.WebStartSeconds = 0;
            else if (WebVt.TryParseTime(text, out var seconds)) ActivePattern.Media.WebStartSeconds = seconds;
            Raise(nameof(WebStartText));
        }
    }

    private void RunWebVt(ShowActionKind kind, string value)
    {
        var key = CurrentWebKey();
        if (key.Length == 0 || CurrentWebSource() is null)
        {
            _desk.StatusMessage = "No web page to arm — put one on the pattern or a layer first.";
            return;
        }
        // The page the controls drive, named by its key so a page in the preview is armed rather than the one on air.
        _desk.Report(_services.Actions.Execute(new ShowAction(kind, key, value), ActionOrigin.Desk));
        RefreshWebVt();
    }

    /// <summary>The VT line, read on every poll — the player's clock moves, an arm is put on or spent.</summary>
    public void RefreshWebVt()
    {
        var key = CurrentWebKey();
        if (key.Length == 0)
        {
            if (WebVtWords.Length > 0) WebVtWords = "";
            WebArmed = false;
            WebHasPlayer = false;
            return;
        }
        var arm = _services.WebIn.ArmOf(key);
        var reading = _services.WebIn.ReadingOf(key);
        var words = WebVt.Words(arm, reading, ShowClock.UtcNow, _services.WebIn.PhaseOf(key));   // the player's observed phase, never the request
        if (words != WebVtWords) WebVtWords = words;
        WebArmed = arm.Armed;
        WebHasPlayer = reading.Ok || arm.Armed || arm.PlayedUtc is not null;
    }

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
                _desk.StatusMessage = HasWebPage
                    ? "The page is still opening — try KEYS → PAGE again in a moment."
                    : "No web page to type at — put one on the pattern or a layer first.";
                return;
            }
            if (_keysToPage == value) return;
            _keysToPage = value;
            Raise(nameof(KeysToPage));
            _desk.StatusMessage = value
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

    private string _webCleanNote = "";
    /// <summary>What CLEAN takes off this page, in a line — read before ticking it.</summary>
    public string WebCleanNote { get => _webCleanNote; private set => Set(ref _webCleanNote, value); }

    /// <summary>The actions the page the controls drive answers to — NEXT, PLAY, PRESENT… — from its service.</summary>
    public ObservableCollection<WebActionChip> WebPageActions { get; } = new();

    private string _webActionsFor = "";

    /// <summary>An action chip on the page the controls drive: "next", "play", "present"… or a key chord.</summary>
    public void RunWebAction(string idOrKey)
    {
        if (CurrentWebSource() is not { } page)
        {
            _desk.StatusMessage = "No web page to drive — put one on the pattern or a layer first.";
            return;
        }
        var name = State.InputLabel(CurrentWebKey(), WebAddress.ShortName(page.CurrentUrl));
        _desk.Report(WebActions.Press(page, name, idOrKey));
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
    public void RefreshWebControls()
    {
        var key = CurrentWebKey();
        // The service line and the FULL FRAME offer follow the pattern's address, not the page pointed at.
        var address = ActivePattern.Kind == PatternKind.Media && ActivePattern.Media.Source == MediaSource.Web ? ActivePattern.Media.WebUrl : "";
        var pick = ActivePattern.Media.WebService;
        WebPresetNote = address.Length > 0 ? WebPresets.Note(address, pick) : "";
        WebCanFullFrame = address.Length > 0 && WebPresets.CanFullFrame(address, pick);
        WebCleanNote = address.Length > 0 ? WebPresets.CleanNote(address, pick) : "";
        Raise(nameof(HasWebPage));
        Raise(nameof(WebStartText));
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
                FileTypeFilter = new[] { MainViewModel.MediaTypes, FilePickerFileTypes.All },
            });
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path is null) continue;
                ActivePlaylistSection.Items.Add(new PlaylistItemConfig { Path = path });
                _desk.AddToMediaLibrary(path, PlaylistSequencer.IsDecodedPath(path));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Playlist file picker failed.", ex);
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
    public void RaisePlaylistSection(bool onlyOnChange = false)
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

    public void RefreshNdiSources(bool quiet = false)
    {
        // Clearing the list nulls the combo selection, which would write "" into the
        // model — capture and restore the operator's choice around the rebuild.
        var current = ActivePattern.Media.NdiSourceName;
        var found = _services.NdiIn.DiscoverSources();
        var wanted = new List<string>(found);
        if (!string.IsNullOrWhiteSpace(current) && !wanted.Contains(current)) wanted.Insert(0, current);
        // Rewritten only when the sources changed: the list is the ItemsSource of three pickers,
        // and clearing it every three seconds closed an open dropdown under the pointer.
        if (!NdiSourceOptions.SequenceEqual(wanted))
        {
            NdiSourceOptions.Clear();
            foreach (var s in wanted) NdiSourceOptions.Add(s);
            ActivePattern.Media.NdiSourceName = current;
        }
        if (!quiet)
        {
            _desk.StatusMessage = found.Count == 0
                ? "No NDI sources visible yet — senders appear within a few seconds of starting."
                : $"{found.Count} NDI source{(found.Count == 1 ? "" : "s")} on the network.";
        }
    }

    public void RefreshCaptureDevices(bool quiet = false)
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
            _desk.StatusMessage = found.Count == 0
                ? "No capture devices found (device lists need Windows)."
                : $"{found.Count} capture device{(found.Count == 1 ? "" : "s")} found.";
        }
    }

    /// <summary>Nickname for the live input currently picked in Media (NDI feed or capture).</summary>
    /// <summary>The picked live input's nickname follows the pattern's source; the poll raises it.</summary>
    public void RaiseInputNickname() => Raise(nameof(InputNickname));

    public string InputNickname
    {
        get => CurrentInputKey() is { } key ? State.InputLabel(key, "") : "";
        set
        {
            if (CurrentInputKey() is not { } key) return;
            var entry = State.InputLabels.FirstOrDefault(l => l.Key == key);
            if (entry is null)
            {
                entry = new InputLabelConfig { Key = key };
                State.InputLabels.Add(entry);
            }
            entry.Label = value;
        }
    }

    private string? CurrentInputKey() => ActivePattern.Media.Source switch
    {
        MediaSource.NdiFeed when ActivePattern.Media.NdiSourceName.Length > 0 => "ndi:" + ActivePattern.Media.NdiSourceName,
        MediaSource.Capture when ActivePattern.Media.CaptureDevice.Length > 0 => "cap:" + ActivePattern.Media.CaptureDevice,
        MediaSource.Web when ActivePattern.Media.WebUrl.Length > 0 => InputKeys.Web(ActivePattern.Media.WebUrl),
        _ => null,
    };

    private string _activeInputsText = "No live inputs mounted.";
    public string ActiveInputsText { get => _activeInputsText; private set => Set(ref _activeInputsText, value); }

    private string _residencyText = "";
    /// <summary>Round 69: the residency ledger in a line — what is held, why, and when an idle picture goes.</summary>
    public string ResidencyText { get => _residencyText; private set => Set(ref _residencyText, value); }

    public void RefreshActiveInputs()
    {
        var rows = new List<string>();
        foreach (var (key, status) in _services.Video.MountStatuses.Concat(_services.NdiIn.MountStatuses).Concat(_services.WebIn.MountStatuses))
        {
            var bare = key.Length > 4 ? key[4..] : key;
            var label = key.StartsWith("vid:", StringComparison.Ordinal)
                ? Path.GetFileName(bare)
                : key.StartsWith("web:", StringComparison.Ordinal)
                    ? State.InputLabel(key, WebAddress.ShortName(bare))
                    : State.InputLabel(key, bare);
            var held = _services.Residency.ReasonWords(key);                                            // round 69: why it is in memory
            rows.Add($"{label} — {status}{(held.Length > 0 ? " · " + held : "")}");
        }
        ResidencyText = "In memory: " + _services.Residency.Words + $" · idle pictures let go after {_services.Residency.Grace.TotalSeconds:0} s on this machine.";
        var native = _services.WebVideo.Note;
        var notes = string.Join("  ",
            new[] { _services.Video.LimitNote, _services.Video.PendingNote, _services.NdiIn.LimitNote, _services.WebIn.LimitNote, native }.Where(s => s.Length > 0));
        WebNativeText = native.Length > 0 ? $"{_services.WebVideo.ToolWords}  {native}" : _services.WebVideo.ToolWords;
        var text = rows.Count == 0
            ? "No live inputs mounted."
            : $"Live inputs ({rows.Count}): {string.Join("  ·  ", rows)}";
        ActiveInputsText = notes.Length > 0 ? $"{text}  {notes}" : text;
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

    /// <summary>Admin → the operator's own path to yt-dlp (the file or its folder) for the native player (round 68.6); read on the next reconcile.</summary>
    public string YtDlpPath
    {
        get => State.Admin.YtDlpPath;
        set
        {
            if (State.Admin.YtDlpPath == (value ?? "")) return;
            State.Admin.YtDlpPath = value ?? "";
            Raise(nameof(YtDlpPath));
            RefreshActiveInputs();
        }
    }

    /// <summary>The native player's words under Play via: where yt-dlp is (or that it is not), and what it is doing for the pages that asked.</summary>
    public string WebNativeText { get => _webNativeText; private set => Set(ref _webNativeText, value); }
    private string _webNativeText = "";

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
            _desk.StatusMessage = ActivePattern.Media.DeckPath.Length == 0 ? "Choose a deck first." : "The deck is still opening.";
            return;
        }
        if (deck.PageCount == 0)
        {
            _desk.StatusMessage = deck.StatusText;
            return;
        }
        var target = Decks.Resolve(page, deck.Page, deck.PageCount);
        if (target > 0) deck.GoTo(target);
        RefreshDeck();
        _desk.StatusMessage = DeckPageText;
    }

    /// <summary>Keeps the deck readout and the click-through flag honest; cheap, runs with the tallies.</summary>
    public void RefreshDeck()
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
        _desk.RaisePresenterWords();
    }

    // ---- the tick ----------------------------------------------------------------

    /// <summary>With the status lines: the playlist's words, the area of interest, the deck's readout.</summary>
    public void PollStatus()
    {
        PlaylistStatus = _services.Playlist.Status;
        RefreshCropSummary();
        RefreshDeck();
        RefreshWebVt();
    }

    /// <summary>The pick lists, kept warm while their panels are in use: NDI discovery is push-based and cheap to read; capture enumeration is COM, so on first need only.</summary>
    public void PollPickers()
    {
        if (ActivePattern.Media.Source == MediaSource.NdiFeed && ++_ndiPollTick % 3 == 0)
        {
            RefreshNdiSources(quiet: true);
        }
        if (ActivePattern.Media.Source == MediaSource.Capture && !_captureListLoaded)
        {
            RefreshCaptureDevices(quiet: true);
        }
    }
}
