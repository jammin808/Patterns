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
    // ---- lower thirds --------------------------------------------------------------------------

    private LowerThirdDesign? _selectedLowerThird;
    private LowerThirdElement? _selectedElement;
    private string _newLowerThirdPreset = "Clean";
    private double _previewTimeMs;
    private bool _previewPlaying;
    private DispatcherTimer? _previewTimer;
    private string _lowerThirdStatus = "No lower third on air.";

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
            if (!Set(ref _selectedEntry, value)) return;
            Raise(nameof(HasEntry));
        }
    }

    public bool HasEntry => _selectedEntry is not null;

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
        }
    }

    private void OnSelectedElementChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LowerThirdElement.Kind)) RaiseElementKind();
    }

    private void RaiseElementKind()
    {
        Raise(nameof(HasElement));
        Raise(nameof(ElementIsText));
        Raise(nameof(ElementHasFile));
        Raise(nameof(ElementIsMedia));
        Raise(nameof(ElementIsParticles));
        Raise(nameof(ElementIsFractal));
    }

    public bool HasElement => _selectedElement is not null;
    public bool ElementIsText => _selectedElement?.Kind == LowerThirdElementKind.Text;
    public bool ElementHasFile => _selectedElement?.Kind is LowerThirdElementKind.Image or LowerThirdElementKind.Media;
    public bool ElementIsMedia => _selectedElement?.Kind == LowerThirdElementKind.Media;
    public bool ElementIsParticles => _selectedElement?.Kind == LowerThirdElementKind.Particles;
    public bool ElementIsFractal => _selectedElement?.Kind == LowerThirdElementKind.Fractal;

    /// <summary>The scrubber's range: the way in, a hold (its own, or 1.5 s when it waits to be hidden), the way out.</summary>
    public double PreviewLengthMs
        => _selectedLowerThird is null ? 1000 : _selectedLowerThird.InMs + (_selectedLowerThird.HoldMs > 0 ? _selectedLowerThird.HoldMs : 1500) + _selectedLowerThird.OutMs;

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
                _previewTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
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
        var design = preset == "Blank" ? LowerThirdPresets.Blank() : LowerThirdPresets.Create(preset);
        design.Name = UniqueLowerThirdName(design.Name);
        State.LowerThirds.Designs.Add(design);
        AdoptDefaultLowerThird(design);
        SelectedLowerThird = design;
        StatusMessage = $"Lower third '{design.Name}' added.";
        return design;
    }

    /// <summary>The first design of a show is its default (★) until another is chosen; a default that was deleted moves to the next one added.</summary>
    private void AdoptDefaultLowerThird(LowerThirdDesign design)
    {
        var lowers = State.LowerThirds;
        if (lowers.DefaultDesignId.Length == 0 || lowers.Find(lowers.DefaultDesignId) is null) lowers.DefaultDesignId = design.Id;
    }

    /// <summary>The show's default design (★): where PERSON, the PEOPLE chips and a cue with no design named put the next name when none is on air.</summary>
    public void SetDefaultLowerThird(LowerThirdDesign design)
    {
        State.LowerThirds.DefaultDesignId = design.Id;
        RefreshLowerThirdTallies();
        StatusMessage = $"'{design.Name}' is the show's default lower third.";
    }

    private string UniqueLowerThirdName(string name)
    {
        var candidate = name;
        var n = 2;
        while (State.LowerThirds.Designs.Any(d => string.Equals(d.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{name} {n++}";
        }
        return candidate;
    }

    private void DuplicateLowerThird(LowerThirdDesign? design)
    {
        if (design is null) return;
        var copy = design.Clone();
        copy.Name = UniqueLowerThirdName(design.Name);
        State.LowerThirds.Designs.Add(copy);
        SelectedLowerThird = copy;
    }

    private void DeleteLowerThird(LowerThirdDesign? design)
    {
        if (design is null) return;
        // Off the preview, and off the air when the audience is seeing its copy (through the frozen program or live).
        if (State.LowerThirds.ActiveId == design.Id) State.LowerThirds.Hide(ShowClock.UtcNow);
        var air = _services.AirState.LowerThirds;
        if (!ReferenceEquals(air, State.LowerThirds) && air.ActiveId == design.Id && air.IsShowing)
        {
            _services.Actions.Execute(ShowActionKind.LowerThirdHide, ActionOrigin.Desk);
        }
        var index = State.LowerThirds.Designs.IndexOf(design);
        // The page's list clears its selection the moment the item goes: decide before, reselect after.
        var wasSelected = ReferenceEquals(SelectedLowerThird, design);
        State.LowerThirds.Designs.Remove(design);
        var designs = State.LowerThirds.Designs;
        if (State.LowerThirds.DefaultDesignId == design.Id) State.LowerThirds.DefaultDesignId = designs.FirstOrDefault()?.Id ?? "";
        if (wasSelected)
        {
            SelectedLowerThird = designs.Count == 0 ? null : designs[Math.Clamp(index, 0, designs.Count - 1)];
        }
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

    private ActionResult Report(ActionResult result)
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
        var bar = kind == LowerThirdElementKind.Bar;
        var full = kind is LowerThirdElementKind.Bar or LowerThirdElementKind.Particles or LowerThirdElementKind.Fractal or LowerThirdElementKind.Media;
        var e = new LowerThirdElement
        {
            Kind = kind,
            Name = kind.ToString(),
            X = 0,
            Y = 0,
            W = full ? d.Width : Math.Min(kind == LowerThirdElementKind.Text ? 600 : 200, d.Width),
            H = full ? d.Height : Math.Min(kind == LowerThirdElementKind.Text ? 80 : 200, d.Height),
            Fill = bar ? LowerThirdFill.Solid : LowerThirdFill.None,
        };
        if (kind == LowerThirdElementKind.Text) e.Text = "Text";
        LowerThirdMotions.Apply(e, d, LowerThirdMotion.Fade, LowerThirdMotion.Fade);
        d.Elements.Add(e);
        SelectedElement = e;
        return e;
    }

    private void RemoveElement(LowerThirdElement? e)
    {
        var d = SelectedLowerThird;
        if (d is null || e is null) return;
        var index = d.Elements.IndexOf(e);
        var wasSelected = ReferenceEquals(SelectedElement, e);
        d.Elements.Remove(e);
        if (wasSelected)
        {
            SelectedElement = d.Elements.Count == 0 ? null : d.Elements[Math.Clamp(index, 0, d.Elements.Count - 1)];
        }
    }

    private void MoveElement(LowerThirdElement? e, int delta)
    {
        var d = SelectedLowerThird;
        if (d is null || e is null) return;
        var index = d.Elements.IndexOf(e);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= d.Elements.Count) return;
        // The page's list sees a move as a removal and an insert and drops its selection on the way: put it back.
        var selected = SelectedElement;
        d.Elements.Move(index, target);
        if (selected is not null && !ReferenceEquals(SelectedElement, selected)) SelectedElement = selected;
    }

    /// <summary>A motion chip: the ready-made keys for the way in or out, editable afterwards.</summary>
    public void ApplyMotion(string? motionName, bool isIn)
    {
        var d = SelectedLowerThird;
        var e = SelectedElement;
        if (d is null || e is null || !Enum.TryParse<LowerThirdMotion>(motionName, true, out var motion)) return;
        LowerThirdMotions.Apply(e, motion, isIn, LowerThirdMotions.DefaultDistance(motion, d));
        PreviewTimeMs = isIn ? d.InMs * 0.5 : d.InMs + (d.HoldMs > 0 ? d.HoldMs : 1500) + d.OutMs * 0.5;
    }

    private void AddKey(bool isIn)
    {
        var e = SelectedElement;
        if (e is null) return;
        var keys = isIn ? e.In : e.Out;
        var last = keys.Count == 0 ? null : keys[^1];
        var key = last?.Clone() ?? new LowerThirdKeyframe { U = isIn ? 0 : 1 };
        if (last is not null) key.U = Math.Min(1, last.U + 0.25);
        keys.Add(key);
    }

    private void RemoveKey(LowerThirdKeyframe? key, bool isIn)
    {
        var e = SelectedElement;
        if (e is null || key is null) return;
        (isIn ? e.In : e.Out).Remove(key);
    }

    /// <summary>"TextColor:primary" — a brand word into one of the element's colour fields.</summary>
    private void SetElementColorWord(string? spec)
    {
        var e = SelectedElement;
        if (e is null || string.IsNullOrWhiteSpace(spec)) return;
        var parts = spec.Split(':', 2);
        if (parts.Length != 2) return;
        var word = parts[1];
        switch (parts[0])
        {
            case nameof(LowerThirdElement.TextColor): e.TextColor = word; break;
            case nameof(LowerThirdElement.FillColor): e.FillColor = word; break;
            case nameof(LowerThirdElement.FillColor2): e.FillColor2 = word; break;
            case nameof(LowerThirdElement.BorderColor): e.BorderColor = word; break;
            case nameof(LowerThirdElement.GlowColor): e.GlowColor = word; break;
            case nameof(LowerThirdElement.ChaserColor): e.ChaserColor = word; break;
            case nameof(LowerThirdElement.ShadowColor): e.ShadowColor = word; break;
        }
    }

    private async Task PickElementFileAsync()
    {
        var e = SelectedElement;
        var window = _services.MainWindow;
        if (e is null || window is null) return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = e.Kind == LowerThirdElementKind.Media ? "Choose a clip or still for this element" : "Choose a picture for this element",
                AllowMultiple = false,
                FileTypeFilter = new[] { MediaTypes, FilePickerFileTypes.All },
            });
            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (path is null) return;
            e.Path = path;
            AddToMediaLibrary(path, isVideo: PlaylistSequencer.IsVideoPath(path));
        }
        catch (Exception ex)
        {
            Log.Warn("Element file pick failed.", ex);
        }
    }

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
        var design = loaded.Clone();
        design.Name = UniqueLowerThirdName(loaded.Name.Length > 0 ? loaded.Name : Path.GetFileNameWithoutExtension(path));
        State.LowerThirds.Designs.Add(design);
        AdoptDefaultLowerThird(design);
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
        var entry = new LowerThirdEntry { Name = UniqueEntryName(name) };
        State.LowerThirds.Entries.Add(entry);
        SelectedEntry = entry;
        StatusMessage = $"'{entry.Name}' added to the library — fill in the name, the role and the company.";
        return entry;
    }

    private string UniqueEntryName(string name)
    {
        var candidate = name;
        var n = 2;
        while (State.LowerThirds.Entries.Any(e => string.Equals(e.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{name} {n++}";
        }
        return candidate;
    }

    private void DeleteEntry(LowerThirdEntry? entry)
    {
        if (entry is null) return;
        var entries = State.LowerThirds.Entries;
        var index = entries.IndexOf(entry);
        if (index < 0) return;
        var wasSelected = ReferenceEquals(SelectedEntry, entry);
        entries.Remove(entry);
        if (wasSelected) SelectedEntry = entries.Count == 0 ? null : entries[Math.Clamp(index, 0, entries.Count - 1)];
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
        TableData table;
        try
        {
            table = path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? XlsxTable.Read(File.ReadAllBytes(path))
                : CsvTable.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Error("People list read failed.", ex);
            return $"Could not read {Path.GetFileName(path)}: {ex.Message}";
        }
        var report = LowerThirdLibrary.Import(table);
        var entries = State.LowerThirds.Entries;
        if (!append && report.Entries.Count > 0)
        {
            entries.Clear();
            SelectedEntry = null;
        }
        var (added, updated) = LowerThirdLibrary.Merge(entries, report.Entries);
        if (SelectedEntry is null && entries.Count > 0) SelectedEntry = entries[0];
        var words = $"{report.Summary}: {added} added, {updated} updated ({Path.GetFileName(path)})";
        if (report.Notes.Count > 0) words += " — " + report.Notes[0];
        return words;
    }

    public string ExportPeopleCsv() => LowerThirdLibrary.Export(State.LowerThirds.Entries);

    /// <summary>
    /// The tally: the design on air lights its row and chip with its phase, the one in the preview
    /// (EDIT SAFE open) its own, the show's default its ★; true while either is on the move.
    /// </summary>
    private bool RefreshLowerThirdTallies()
    {
        var now = ShowClock.UtcNow;
        var air = _services.AirState.LowerThirds;
        var (onAir, airPhase) = PhaseOf(air, now);
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
        var (inPreview, previewPhase) = _services.LowerThirdInPreview() ? PhaseOf(preview, now) : (null, LowerThirdPhase.Gone);
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
        return airLive || previewLive;
    }

    private static (LowerThirdDesign? Design, LowerThirdPhase Phase) PhaseOf(LowerThirdsConfig cfg, DateTime now)
    {
        var active = cfg.Active;
        if (active is null || LowerThirdClock.Instants(cfg) is not { } at) return (active, LowerThirdPhase.Gone);
        return (active, LowerThirdClock.Evaluate(active, at.ShownAt, at.HiddenAt, ShowClock.SecondsAt(now)).Phase);
    }
}
