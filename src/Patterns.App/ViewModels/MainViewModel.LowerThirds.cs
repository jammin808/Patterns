using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Rendering.Effects;
using Patterns.Core.Model;
using Patterns.Ndi;
using Patterns.Rendering.Particles;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Patterns.Core.LowerThirds;
using Patterns.Rendering.LowerThirds;

namespace Patterns.App.ViewModels;

public sealed partial class MainViewModel
{
    // ---- lower thirds --------------------------------------------------------------------------

    private LowerThirdDesign? _selectedLowerThird;
    private LowerThirdElement? _selectedElement;
    private string _newLowerThirdPreset = "Clean";
    private double _previewTimeMs;
    private bool _previewPlaying;
    private DispatcherTimer? _previewTimer;
    private string _lowerThirdStatus = "No lower third on air.";
    private LowerThirdDesigner? _designer;

    /// <summary>The editing logic, with no desk in it: every edit the page makes to the show goes through it.</summary>
    private LowerThirdDesigner Designer => _designer ??= new LowerThirdDesigner(State.LowerThirds);

    /// <summary>The presets a new design starts from, plus an empty box.</summary>
    public IReadOnlyList<string> LowerThirdPresetNames { get; } = LowerThirdPresets.Names.Concat(new[] { "Blank" }).ToList();

    public string NewLowerThirdPreset { get => _newLowerThirdPreset; set => Set(ref _newLowerThirdPreset, string.IsNullOrWhiteSpace(value) ? "Clean" : value); }

    /// <summary>Designs saved as files in the lowerthirds folder (Id = the path).</summary>
    public ObservableCollection<PickItem> LowerThirdFiles { get; } = new();

    /// <summary>What is on screen, for the pages.</summary>
    public string LowerThirdStatus { get => _lowerThirdStatus; private set => Set(ref _lowerThirdStatus, value); }

    private string _lowerThirdPreviewText = "Nothing in the preview.";
    private bool _hasLowerThirdInPreview;
    private bool _lowerThirdAirEdited;
    private bool _isLowerThirdOnAir;

    /// <summary>What is in the preview for a sign-off, for the pages.</summary>
    public string LowerThirdPreviewText { get => _lowerThirdPreviewText; private set => Set(ref _lowerThirdPreviewText, value); }

    /// <summary>A design is showing in the preview: TAKE TO AIR and CLEAR PREVIEW make sense.</summary>
    public bool HasLowerThirdInPreview { get => _hasLowerThirdInPreview; private set => Set(ref _hasLowerThirdInPreview, value); }

    /// <summary>The design on air has been edited since it went there (EDIT SAFE holds the copy the audience sees): UPDATE ON AIR pushes the edit.</summary>
    public bool LowerThirdAirEdited { get => _lowerThirdAirEdited; private set => Set(ref _lowerThirdAirEdited, value); }

    /// <summary>A design is arriving, holding or leaving on air.</summary>
    public bool IsLowerThirdOnAir { get => _isLowerThirdOnAir; private set => Set(ref _isLowerThirdOnAir, value); }

    private bool _lowerThirdChipsToPreview;

    /// <summary>PVW FIRST on the Show panel: its design and people chips go to the preview for a sign-off instead of straight to air (a desk setting, never saved).</summary>
    public bool LowerThirdChipsToPreview { get => _lowerThirdChipsToPreview; set => Set(ref _lowerThirdChipsToPreview, value); }

    private LowerThirdEntry? _selectedEntry;

    /// <summary>The library entry being edited on the Lower thirds page.</summary>
    public LowerThirdEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            var previous = _selectedEntry;
            if (!Set(ref _selectedEntry, value)) return;
            if (previous is not null) previous.PropertyChanged -= OnSelectedEntryChanged;
            if (value is not null) value.PropertyChanged += OnSelectedEntryChanged;
            Raise(nameof(HasEntry));
            Raise(nameof(EntryMoreHeader));
        }
    }

    public bool HasEntry => _selectedEntry is not null;

    private bool _entryMoreExpanded;

    /// <summary>The entry editor's drop-down (company, photo, note) — folded by default so the list keeps its room; a desk setting, never saved.</summary>
    public bool EntryMoreExpanded { get => _entryMoreExpanded; set => Set(ref _entryMoreExpanded, value); }

    /// <summary>The drop-down's header reads what is folded inside it: "More — Acme Ltd · photo · note", or what is missing.</summary>
    public string EntryMoreHeader
    {
        get
        {
            if (_selectedEntry is not { } e) return "More — company, photo, note";
            var company = e.Company.Length > 0 ? e.Company : "no company (the brand kit's)";
            var photo = e.Photo.Length > 0 ? "photo" : "no photo";
            var note = e.Note.Length > 0 ? "note" : "no note";
            return $"More — {company} · {photo} · {note}";
        }
    }

    private void OnSelectedEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LowerThirdEntry.Company) or nameof(LowerThirdEntry.Photo) or nameof(LowerThirdEntry.Note)) Raise(nameof(EntryMoreHeader));
    }

    /// <summary>Over the pinned preview: the selected design's name and what it holds, or that none is selected.</summary>
    public string LowerThirdPreviewTitle => _selectedLowerThird is { } d
        ? $"{d.Name} — {d.Elements.Count} element{(d.Elements.Count == 1 ? "" : "s")}{(d.IsOnAir ? " · ON AIR" : d.IsInPreview ? " · IN PREVIEW" : "")}"
        : "no design selected";

    public LowerThirdDesign? SelectedLowerThird
    {
        get => _selectedLowerThird;
        set
        {
            var old = _selectedLowerThird;
            if (!Set(ref _selectedLowerThird, value)) return;
            if (old is not null) old.PropertyChanged -= OnSelectedLowerThirdChanged;
            if (value is not null) value.PropertyChanged += OnSelectedLowerThirdChanged;
            SelectedElement = value?.Elements.FirstOrDefault();
            PreviewTimeMs = value?.InMs ?? 0;
            Raise(nameof(HasLowerThird));
            Raise(nameof(LowerThirdPreviewTitle));
            Raise(nameof(PreviewLengthMs));
        }
    }

    private void OnSelectedLowerThirdChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LowerThirdDesign.InMs) or nameof(LowerThirdDesign.HoldMs) or nameof(LowerThirdDesign.OutMs))
        {
            Raise(nameof(PreviewLengthMs));
            PreviewTimeMs = Math.Min(PreviewTimeMs, PreviewLengthMs);
        }
    }

    public bool HasLowerThird => _selectedLowerThird is not null;

    public LowerThirdElement? SelectedElement
    {
        get => _selectedElement;
        set
        {
            var old = _selectedElement;
            if (!Set(ref _selectedElement, value)) return;
            if (old is not null) old.PropertyChanged -= OnSelectedElementChanged;
            if (value is not null) value.PropertyChanged += OnSelectedElementChanged;
            RaiseElementKind();
            RefreshPopOut();
        }
    }

    private void OnSelectedElementChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LowerThirdElement.Kind)) RaiseElementKind();
        // The file is typed as well as chosen: a path the desk cannot open must say so on the
        // keystroke, in the designer, and not in front of the room.
        else if (e.PropertyName == nameof(LowerThirdElement.Path)) Raise(nameof(ElementFileTrouble));
    }

    private void RaiseElementKind()
    {
        Raise(nameof(HasElement));
        Raise(nameof(ElementIsText));
        Raise(nameof(ElementHasFile));
        Raise(nameof(ElementIsMedia));
        Raise(nameof(ElementIsParticles));
        Raise(nameof(ElementIsFractal));
        Raise(nameof(ElementFileTrouble));
    }

    public bool HasElement => _selectedElement is not null;
    public bool ElementIsText => _selectedElement?.Kind == LowerThirdElementKind.Text;
    public bool ElementHasFile => _selectedElement?.Kind is LowerThirdElementKind.Image or LowerThirdElementKind.Media;
    public bool ElementIsMedia => _selectedElement?.Kind == LowerThirdElementKind.Media;
    public bool ElementIsParticles => _selectedElement?.Kind == LowerThirdElementKind.Particles;
    public bool ElementIsFractal => _selectedElement?.Kind == LowerThirdElementKind.Fractal;

    /// <summary>The scrubber's range: the way in, a hold (its own, or 1.5 s when it waits to be hidden), the way out.</summary>
    public double PreviewLengthMs => LowerThirdDesigner.PreviewLength(_selectedLowerThird);

    /// <summary>Where the preview stands on the design's own timeline.</summary>
    public double PreviewTimeMs { get => _previewTimeMs; set => Set(ref _previewTimeMs, Math.Clamp(value, 0, Math.Max(1, PreviewLengthMs))); }

    /// <summary>Runs the preview round and round.</summary>
    public bool PreviewPlaying
    {
        get => _previewPlaying;
        set
        {
            if (!Set(ref _previewPlaying, value)) return;
            if (value)
            {
                _previewTimer ??= global::Patterns.App.Services.DeskTimers.Make(TimeSpan.FromMilliseconds(33));
                _previewTimer.Tick -= PreviewTick;
                _previewTimer.Tick += PreviewTick;
                _previewTimer.Start();
            }
            else
            {
                _previewTimer?.Stop();
            }
        }
    }

    private void PreviewTick(object? sender, EventArgs e)
    {
        var next = _previewTimeMs + 33;
        PreviewTimeMs = next >= PreviewLengthMs ? 0 : next;
    }

    /// <summary>A new design from a preset (or an empty box), named so it never collides, selected.</summary>
    public LowerThirdDesign NewLowerThird(string preset)
    {
        var design = Designer.New(preset);
        SelectedLowerThird = design;
        StatusMessage = $"Lower third '{design.Name}' added.";
        return design;
    }

    /// <summary>The show's default design (★): where PERSON, the PEOPLE chips and a cue with no design named put the next name when none is on air.</summary>
    public void SetDefaultLowerThird(LowerThirdDesign design)
    {
        State.LowerThirds.DefaultDesignId = design.Id;
        RefreshLowerThirdTallies();
        StatusMessage = $"'{design.Name}' is the show's default lower third.";
    }

    private void DuplicateLowerThird(LowerThirdDesign? design)
    {
        if (design is null) return;
        SelectedLowerThird = Designer.Duplicate(design);
    }

    private void DeleteLowerThird(LowerThirdDesign? design)
    {
        if (design is null) return;
        // Off the air when the audience is seeing its copy through the frozen program; the designer takes it off the preview.
        var air = _services.AirState.LowerThirds;
        if (!ReferenceEquals(air, State.LowerThirds) && air.ActiveId == design.Id && air.IsShowing)
        {
            _services.Actions.Execute(ShowActionKind.LowerThirdHide, ActionOrigin.Desk);
        }
        // The page's list clears its selection the moment the item goes: decide before, reselect after.
        var wasSelected = ReferenceEquals(SelectedLowerThird, design);
        var next = Designer.Remove(design, ShowClock.UtcNow);
        if (wasSelected) SelectedLowerThird = next;
        RefreshLowerThirdTallies();
        StatusMessage = $"Lower third '{design.Name}' deleted.";
    }

    /// <summary>On air now, through the action layer (journaled, sandbox-aware).</summary>
    public void ShowLowerThird(LowerThirdDesign design)
        => _services.Actions.Execute(ShowActionKind.LowerThirdShow, ActionOrigin.Desk, design.Id);

    public void HideLowerThird() => _services.Actions.Execute(ShowActionKind.LowerThirdHide, ActionOrigin.Desk);

    /// <summary>Into the preview for a sign-off (the PREVIEW pane, the multiview's Preview tile, REVIEW); refused without EDIT SAFE.</summary>
    public ActionResult PreviewLowerThird(LowerThirdDesign design)
        => Report(_services.Actions.Execute(ShowActionKind.LowerThirdPreview, ActionOrigin.Desk, design.Id));

    /// <summary>The lower third in the preview to air, afresh; the preview clears.</summary>
    public ActionResult TakeLowerThird() => Report(_services.Actions.Execute(ShowActionKind.LowerThirdTake, ActionOrigin.Desk));

    /// <summary>The design on air replaced by the design as it is now, in place — no leaving, no arriving again.</summary>
    public ActionResult UpdateLowerThird() => Report(_services.Actions.Execute(ShowActionKind.LowerThirdUpdate, ActionOrigin.Desk));

    public ActionResult ClearLowerThirdPreview() => Report(_services.Actions.Execute(ShowActionKind.LowerThirdPreviewOff, ActionOrigin.Desk));

    /// <summary>The entry into a design and the preview: the given one, else the one in the preview, on air, or the show's default.</summary>
    public ActionResult PreviewEntry(LowerThirdEntry entry, LowerThirdDesign? design)
        => Report(_services.Actions.Execute(ShowActionKind.LowerThirdPreview, ActionOrigin.Desk, design?.Id ?? "", entry.Id));

    internal ActionResult Report(ActionResult result)
    {
        if (result.Message.Length > 0) StatusMessage = result.Message;
        RefreshLowerThirdTallies();
        return result;
    }

    /// <summary>A new element of a kind, sized to the design and given a plain fade both ways, selected.</summary>
    public LowerThirdElement? AddElement(LowerThirdElementKind kind)
    {
        var d = SelectedLowerThird;
        if (d is null) return null;
        var e = LowerThirdDesigner.AddElement(d, kind);
        SelectedElement = e;
        return e;
    }

    private void RemoveElement(LowerThirdElement? e)
    {
        var d = SelectedLowerThird;
        if (d is null || e is null) return;
        var wasSelected = ReferenceEquals(SelectedElement, e);
        var next = LowerThirdDesigner.RemoveElement(d, e);
        if (wasSelected) SelectedElement = next;
    }

    private void MoveElement(LowerThirdElement? e, int delta)
    {
        var d = SelectedLowerThird;
        if (d is null || e is null) return;
        // The page's list sees a move as a removal and an insert and drops its selection on the way: put it back.
        var selected = SelectedElement;
        if (!LowerThirdDesigner.MoveElement(d, e, delta)) return;
        if (selected is not null && !ReferenceEquals(SelectedElement, selected)) SelectedElement = selected;
    }

    /// <summary>A motion chip: the ready-made keys for the way in or out, editable afterwards; the preview scrubs to where it shows.</summary>
    public void ApplyMotion(string? motionName, bool isIn)
    {
        var d = SelectedLowerThird;
        var e = SelectedElement;
        if (d is null || e is null || !Enum.TryParse<LowerThirdMotion>(motionName, true, out var motion)) return;
        PreviewTimeMs = LowerThirdDesigner.ApplyMotion(d, e, motion, isIn);
    }

    private void AddKey(bool isIn)
    {
        if (SelectedElement is { } e) LowerThirdDesigner.AddKey(e, isIn);
    }

    private void RemoveKey(LowerThirdKeyframe? key, bool isIn)
    {
        if (SelectedElement is { } e && key is not null) LowerThirdDesigner.RemoveKey(e, key, isIn);
    }

    /// <summary>"TextColor:primary" — a brand word into one of the element's colour fields.</summary>
    private void SetElementColorWord(string? spec)
    {
        if (SelectedElement is { } e && !string.IsNullOrWhiteSpace(spec)) LowerThirdDesigner.SetColorWord(e, spec);
    }

    private async Task PickElementFileAsync()
    {
        var e = SelectedElement;
        var window = _services.MainWindow;
        if (e is null || window is null) return;
        var clip = e.Kind == LowerThirdElementKind.Media;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = clip ? "Choose a clip or still for this element" : "Choose a picture for this element",
                AllowMultiple = false,
                // Only what this element can actually draw. The old filter offered audio and decks
                // as well, which put a file in the box that the renderer draws as a dark rectangle.
                FileTypeFilter = new[] { clip ? ClipOrStillTypes : PictureTypes, FilePickerFileTypes.All },
            });
            var picked = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (picked is null) return;
            AdoptElementFile(e, picked);
        }
        catch (Exception ex)
        {
            Log.Warn("Element file pick failed.", ex);
            StatusMessage = $"Could not open that file: {ex.Message}";
        }
    }

    /// <summary>
    /// A chosen file into an element: imported beside the show where it can be, named after the
    /// file so the element list reads as what it holds, and in the media library so the same
    /// picture is one click away in the next design.
    /// </summary>
    public void AdoptElementFile(LowerThirdElement e, string picked)
    {
        var imported = ShowFiles.Import(picked);
        LowerThirdDesigner.PlaceFile(e, imported.Path);
        AddToMediaLibrary(imported.Path, isVideo: PlaylistSequencer.IsVideoPath(imported.Path));
        StatusMessage = imported.Words.Length > 0
            ? imported.Words
            : $"'{Path.GetFileName(imported.Path)}' is on '{e.Name}'.";
    }

    /// <summary>"The file is not there" — empty while the selected element's picture or clip opens.</summary>
    public string ElementFileTrouble => LowerThirdDesigner.FileTrouble(SelectedElement);

    /// <summary>The selected design as a file of its own in the lowerthirds folder (its name is the file name).</summary>
    public void SaveLowerThirdFile()
    {
        var d = SelectedLowerThird;
        if (d is null) return;
        try
        {
            var path = _services.Store.SaveLowerThird(d.Name, d);
            RefreshLowerThirdFiles();
            StatusMessage = $"Saved '{Path.GetFileName(path)}' in the lowerthirds folder.";
        }
        catch (Exception ex)
        {
            Log.Warn("Lower third save failed.", ex);
            StatusMessage = $"Could not save the lower third: {ex.Message}";
        }
    }

    /// <summary>A saved file into the show as a new design (fresh ids, a name that never collides), selected.</summary>
    public LowerThirdDesign? LoadLowerThirdFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var loaded = _services.Store.LoadLowerThird(path);
        if (loaded is null)
        {
            StatusMessage = $"Could not read '{Path.GetFileName(path)}'.";
            return null;
        }
        var design = Designer.Adopt(loaded, Path.GetFileNameWithoutExtension(path));
        SelectedLowerThird = design;
        StatusMessage = $"Lower third '{design.Name}' loaded from file.";
        return design;
    }

    public void RefreshLowerThirdFiles()
    {
        LowerThirdFiles.Clear();
        foreach (var (name, path) in _services.Store.ListLowerThirds())
        {
            LowerThirdFiles.Add(new PickItem(path, name));
        }
    }

    // ---- the library: people and lines ----------------------------------------------------------

    /// <summary>A new library entry, named so it never collides, selected.</summary>
    public LowerThirdEntry NewEntry(string name = "New person")
    {
        var entry = Designer.NewEntry(name);
        SelectedEntry = entry;
        StatusMessage = $"'{entry.Name}' added to the library — fill in the name, the role and the company.";
        return entry;
    }

    private void DeleteEntry(LowerThirdEntry? entry)
    {
        if (entry is null) return;
        var wasSelected = ReferenceEquals(SelectedEntry, entry);
        if (!Designer.RemoveEntry(entry, out var next)) return;
        if (wasSelected) SelectedEntry = next;
        StatusMessage = $"'{entry.Name}' removed from the library.";
    }

    /// <summary>The entry into the selected design (else the first): its fields and its photo; nothing goes on air.</summary>
    public LowerThirdDesign? UseEntry(LowerThirdEntry entry)
    {
        var design = SelectedLowerThird ?? State.LowerThirds.Designs.FirstOrDefault();
        if (design is null)
        {
            StatusMessage = "No design to put the person into — add one first.";
            return null;
        }
        var picture = LowerThirdsConfig.Fill(design, entry);
        StatusMessage = entry.Photo.Length > 0 && picture is null
            ? $"'{entry.Name}' is in '{design.Name}' — the design has no picture element for the photo."
            : $"'{entry.Name}' is in '{design.Name}'.";
        return design;
    }

    /// <summary>The entry into a design and on air: the given one, else the one on air (else the last shown, else the first).</summary>
    public ActionResult ShowEntry(LowerThirdEntry entry, LowerThirdDesign? design)
        => _services.Actions.Execute(ShowActionKind.LowerThirdShow, ActionOrigin.Desk, design?.Id ?? "", entry.Id);

    private async Task BrowseEntryPhotoAsync()
    {
        var entry = SelectedEntry;
        var window = _services.MainWindow;
        if (entry is null || window is null) return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = entry.Name.Length > 0 ? $"Choose a photo for {entry.Name}" : "Choose a photo",
                AllowMultiple = false,
                FileTypeFilter = new[] { FilePickerFileTypes.ImageAll, FilePickerFileTypes.All },
            });
            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (path is null) return;
            entry.Photo = path;
            AddToMediaLibrary(path, isVideo: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Photo pick failed.", ex);
        }
    }

    private async Task ImportPeopleAsync(bool append)
    {
        var path = await PickOpenPathAsync(append ? "Append a people list" : "Import a people list", PeopleTypes, null);
        if (path is null) return;
        StatusMessage = ImportPeopleFrom(path, append);
    }

    /// <summary>
    /// Reads a CSV or the first sheet of an .xlsx into the library — replacing it, or appended
    /// (a name already there is updated, never doubled); returns the words for the status line.
    /// </summary>
    public string ImportPeopleFrom(string path, bool append)
    {
        var import = Designer.ImportPeople(path, append);
        if (import.Replaced) SelectedEntry = null;
        var entries = State.LowerThirds.Entries;
        if (import.Ok && SelectedEntry is null && entries.Count > 0) SelectedEntry = entries[0];
        return import.Words;
    }

    public string ExportPeopleCsv() => Designer.ExportPeopleCsv();

    /// <summary>
    /// The tally: the design on air lights its row and chip with its phase, the one in the preview
    /// (EDIT SAFE open) its own, the show's default its ★; true while either is on the move.
    /// </summary>
    private bool RefreshLowerThirdTallies()
    {
        Raise(nameof(HasLowerThirds));
        var now = ShowClock.UtcNow;
        var air = _services.AirState.LowerThirds;
        var (onAir, airPhase) = LowerThirdDesigner.Phase(air, now);
        var airLive = airPhase is LowerThirdPhase.In or LowerThirdPhase.Hold or LowerThirdPhase.Out;
        var airText = airPhase switch
        {
            LowerThirdPhase.In => "ARRIVING",
            LowerThirdPhase.Out => "LEAVING",
            LowerThirdPhase.Hold => "ON AIR",
            _ => "",
        };

        var sandboxed = _services.Sandbox.Active;
        var preview = State.LowerThirds;
        // A run of the preview's own — not the program's run mirrored into the edited state when the sandbox opened.
        var (inPreview, previewPhase) = _services.LowerThirdInPreview() ? LowerThirdDesigner.Phase(preview, now) : (null, LowerThirdPhase.Gone);
        var previewLive = previewPhase is LowerThirdPhase.In or LowerThirdPhase.Hold or LowerThirdPhase.Out;
        var previewText = previewPhase switch
        {
            LowerThirdPhase.In => "ARRIVING",
            LowerThirdPhase.Out => "LEAVING",
            LowerThirdPhase.Hold => "IN PREVIEW",
            _ => "",
        };
        var defaultId = preview.DefaultDesign?.Id ?? "";

        foreach (var d in preview.Designs)
        {
            var on = airLive && onAir is not null && d.Id == onAir.Id;
            d.IsOnAir = on;
            d.OnAirText = on ? airText : "";
            var pvw = previewLive && inPreview is not null && d.Id == inPreview.Id;
            d.IsInPreview = pvw;
            d.PreviewText = pvw ? previewText : "";
            d.IsDefault = d.Id == defaultId;
        }

        // The people: the entry whose name the design on air (or in the preview) reads lights, with the design named on its line.
        var airPerson = airLive ? LowerThirdsConfig.PersonOf(onAir, preview.Entries) : null;
        var previewPerson = previewLive ? LowerThirdsConfig.PersonOf(inPreview, preview.Entries) : null;
        foreach (var e in preview.Entries)
        {
            var on = ReferenceEquals(e, airPerson);
            e.IsOnAir = on;
            e.OnAirText = on ? $"{airText} · {onAir?.Name}" : "";
            var pvw = ReferenceEquals(e, previewPerson);
            e.IsInPreview = pvw;
            e.PreviewText = pvw ? $"{previewText} · {inPreview?.Name}" : "";
        }

        var edited = airLive && _services.LowerThirdAirEdited();
        LowerThirdAirEdited = edited;
        IsLowerThirdOnAir = airLive;
        HasLowerThirdInPreview = previewLive && preview.HiddenAtUtc is null;
        var who = onAir is { PersonName.Length: > 0 } ? $" — {onAir.PersonName}" : "";
        var frozen = _services.Bus.Frozen ? " FROZEN: the outputs hold their frame until the freeze lifts." : "";
        var stale = edited ? " EDITED since — UPDATE ON AIR carries the edit." : "";
        LowerThirdStatus = airLive && onAir is not null
            ? $"On air: {onAir.Name}{who} ({airText.ToLowerInvariant()}).{stale}{frozen}"
            : "No lower third on air.";
        LowerThirdPreviewText = !sandboxed
            ? "EDIT SAFE is off — AIR puts a design on straight away; switch it on to sign one off in the preview first."
            : previewLive && inPreview is not null
                ? $"In preview: {inPreview.Name}{(inPreview.PersonName.Length > 0 ? $" — {inPreview.PersonName}" : "")} ({previewText.ToLowerInvariant()}) — on the PREVIEW pane, the multiview's Preview tile and REVIEW. TAKE TO AIR when it is signed off."
                : "Nothing in the preview — PVW a design (or a person) to sign it off before it goes to air.";
        var title = LowerThirdPreviewTitle;
        if (title != _lastPreviewTitle)
        {
            _lastPreviewTitle = title;
            Raise(nameof(LowerThirdPreviewTitle));   // the pinned preview's line follows ON AIR / IN PREVIEW
        }
        return airLive || previewLive;
    }

    private string _lastPreviewTitle = "";
}
