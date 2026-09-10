using System.Collections.ObjectModel;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// One place a monitor wall can be sent: a screen on the rig, an NDI sender, or the stream. The
/// tick does the whole job — the target's own pattern on, set to this wall, and for a feed, that
/// feed pointed at its own screen — so the operator never has to know that a wall reaches an NDI
/// send by way of a virtual screen.
/// </summary>
public sealed class MultiviewDestination : Observable
{
    private readonly Action<MultiviewDestination, bool> _set;
    private bool _isOn;
    private string _note = "";

    public MultiviewDestination(string targetId, string label, string kind, Action<MultiviewDestination, bool> set)
    {
        TargetId = targetId;
        Label = label;
        Kind = kind;
        _set = set;
    }

    public string TargetId { get; }

    public string Label { get; }

    /// <summary>"SCREEN", "NDI" or "STREAM" — what kind of output this is, in the row's corner.</summary>
    public string Kind { get; }

    /// <summary>What it is doing instead, when it is not showing this wall.</summary>
    public string Note { get => _note; set => Set(ref _note, value); }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            Raise();
            _set(this, value);
        }
    }

    /// <summary>Set from the model without writing back — the desk telling the row what is true.</summary>
    public void Follow(bool on)
    {
        if (_isOn == on) return;
        _isOn = on;
        Raise(nameof(IsOn));
    }
}

/// <summary>One of the show's walls as the page lists it: its name, whether it is the one being edited, and where it is showing.</summary>
public sealed class WallRow : Observable
{
    private bool _isSelected;
    private string _summary = "";

    public WallRow(MultiviewOptions wall) => Wall = wall;

    public MultiviewOptions Wall { get; }

    /// <summary>Two-way to the model, so renaming a wall renames it everywhere it is named.</summary>
    public string Name
    {
        get => Wall.Name;
        set
        {
            if (Wall.Name == value) return;
            Wall.Name = value ?? "";
            Raise();
        }
    }

    public bool IsSelected { get => _isSelected; set { if (Set(ref _isSelected, value)) Raise(nameof(IsNotSelected)); } }

    public bool IsNotSelected => !_isSelected;

    /// <summary>"6 tiles · on 2 outputs" — enough to tell two walls apart without opening either.</summary>
    public string Summary { get => _summary; set => Set(ref _summary, value); }
}

public sealed partial class MainViewModel
{
    private MultiviewOptions? _selectedWall;

    /// <summary>The show's monitor walls, as the page lists them; reconciled, never rebuilt.</summary>
    public ObservableCollection<WallRow> Walls { get; } = new();

    /// <summary>The wall the page is editing; the first one when the page opens.</summary>
    public MultiviewOptions? SelectedWall
    {
        get
        {
            if (_selectedWall is not null && State.Multiviews.Contains(_selectedWall)) return _selectedWall;
            _selectedWall = State.Multiviews.FirstOrDefault();
            return _selectedWall;
        }
        set
        {
            if (ReferenceEquals(_selectedWall, value)) return;
            _selectedWall = value;
            RaiseWall();
        }
    }

    public bool HasWall => SelectedWall is not null;

    public bool HasNoWall => SelectedWall is null;

    public bool CanAddWall => State.Multiviews.Count < Multiviews.Max;

    public string WallName => SelectedWall is { } w ? Multiviews.NameOf(State, w) : "";

    public EnumItem[] MultiviewLayouts => Lists.MultiviewLayouts;

    /// <summary>Only the even grid uses the columns setting for the whole wall; the rest use it for the strip.</summary>
    public string ColumnsLabel => SelectedWall?.Layout == MultiviewLayout.Grid ? "Tiles across (0 = auto)" : "Small tiles across (0 = auto)";

    /// <summary>What the chosen arrangement does, and which tiles it makes large.</summary>
    public string LayoutNote => SelectedWall?.Layout switch
    {
        MultiviewLayout.Solo => "The first tile fills the top of the wall and everything else runs along the bottom — a confidence monitor with the rest of the rig to glance at.",
        MultiviewLayout.SideColumn => "The first tile takes the left of the wall and everything else runs down the right — the shape a tall monitor beside a desk wants.",
        MultiviewLayout.Grid => "Every tile the same size, as many across as you ask for. What a monitor wall does when nothing on it matters more than anything else — a rig check, or a wall of inputs.",
        _ => "The first two tiles — the programme and the preview, unless you have dragged something else to the top — fill the upper two thirds, and everything else runs along the bottom. What is on air and what is going on air next are the two pictures that decide anything, so they are the two that are big.",
    };

    /// <summary>"6 tiles · 2 large · on 2 outputs" — the wall in one line.</summary>
    public string WallSummary
    {
        get
        {
            if (SelectedWall is not { } wall) return "";
            var tiles = wall.Tiles.Count;
            var large = MultiviewLayoutPlan.LargeCount(wall.Layout, tiles);
            var on = Multiviews.TargetsShowing(State, wall.Id).Count();
            var where = on == 0 ? "not on any output yet" : on == 1 ? "on 1 output" : $"on {on} outputs";
            return tiles == 0
                ? $"No tiles — the automatic wall (the programme, the preview, every screen and a clock), {where}."
                : $"{tiles} tile{(tiles == 1 ? "" : "s")} · {large} large · {where}.";
        }
    }

    /// <summary>
    /// The walls a multiview pattern may draw, for the Pattern page's picker: the show's, plus the
    /// row a show made before the walls existed sits on — the tiles the pattern itself carries.
    /// </summary>
    public ObservableCollection<EditTarget> WallChoices { get; } = new();

    private void RefreshWallChoices()
    {
        var wanted = new List<EditTarget> { new("The tiles on this pattern", "") };
        foreach (var wall in State.Multiviews) wanted.Add(new EditTarget(Multiviews.NameOf(State, wall), wall.Id));
        if (WallChoices.Count == wanted.Count && WallChoices.SequenceEqual(wanted)) return;
        WallChoices.Clear();
        foreach (var w in wanted) WallChoices.Add(w);
    }

    /// <summary>Every output a wall can be sent to, in the order an operator reads the rig.</summary>
    public ObservableCollection<MultiviewDestination> WallDestinations { get; } = new();

    public RelayCommand AddWallCommand { get; private set; } = null!;

    public RelayCommand<MultiviewOptions> RemoveWallCommand { get; private set; } = null!;

    public RelayCommand<MultiviewOptions> SelectWallCommand { get; private set; } = null!;

    public RelayCommand AddWallTileCommand { get; private set; } = null!;

    public RelayCommand<MultiviewTileConfig> RemoveWallTileCommand { get; private set; } = null!;

    public RelayCommand<MultiviewTileConfig> MoveWallTileUpCommand { get; private set; } = null!;

    public RelayCommand<MultiviewTileConfig> MoveWallTileDownCommand { get; private set; } = null!;

    public RelayCommand FillWallFromRigCommand { get; private set; } = null!;

    private void BuildMultiviewCommands()
    {
        AddWallCommand = new RelayCommand(() =>
        {
            if (!CanAddWall) return;
            MultiviewOptions wall = null!;
            _services.BulkEdit(() => wall = Multiviews.Add(State));
            SelectedWall = wall;
            StatusMessage = $"{Multiviews.NameOf(State, wall)} added — tick where it should show.";
        });
        RemoveWallCommand = new RelayCommand<MultiviewOptions>(wall =>
        {
            if (wall is null) return;
            var name = Multiviews.NameOf(State, wall);
            _services.BulkEdit(() => Multiviews.Remove(State, wall));
            SelectedWall = State.Multiviews.FirstOrDefault();
            StatusMessage = $"{name} removed — every output it was on is back on the programme.";
        });
        SelectWallCommand = new RelayCommand<MultiviewOptions>(wall => { if (wall is not null) SelectedWall = wall; });
        AddWallTileCommand = new RelayCommand(() => SelectedWall?.Tiles.Add(new MultiviewTileConfig()));
        RemoveWallTileCommand = new RelayCommand<MultiviewTileConfig>(tile =>
        {
            if (tile is not null) SelectedWall?.Tiles.Remove(tile);
        });
        MoveWallTileUpCommand = new RelayCommand<MultiviewTileConfig>(tile => MoveWallTile(tile, -1));
        MoveWallTileDownCommand = new RelayCommand<MultiviewTileConfig>(tile => MoveWallTile(tile, +1));
        FillWallFromRigCommand = new RelayCommand(() =>
        {
            if (SelectedWall is not { } wall) return;
            _services.BulkEdit(() =>
            {
                wall.Tiles.Clear();
                foreach (var tile in Multiviews.DefaultTiles(State)) wall.Tiles.Add(tile);
            });
            RaiseWall();
            StatusMessage = $"{Multiviews.NameOf(State, wall)} filled from the rig.";
        });
    }

    private void MoveWallTile(MultiviewTileConfig? tile, int by)
    {
        if (tile is null || SelectedWall is not { } wall) return;
        var from = wall.Tiles.IndexOf(tile);
        MoveWallTileTo(tile, from + by);
    }

    /// <summary>
    /// A tile dragged into a new place. The order is the layout: the first one or two tiles are
    /// the large ones, so dragging a screen to the top is how an operator says "that is the one
    /// I am watching" — no separate setting, and what they see is the finished wall.
    /// </summary>
    public void MoveWallTileTo(MultiviewTileConfig? tile, int to)
    {
        if (tile is null || SelectedWall is not { } wall) return;
        var from = wall.Tiles.IndexOf(tile);
        if (from < 0 || to < 0 || to >= wall.Tiles.Count || from == to) return;
        wall.Tiles.Move(from, to);
        RaiseWall();
    }

    /// <summary>The wall list follows the show: a row per wall, with its name, its line and which is being edited.</summary>
    private void RefreshWallRows()
    {
        for (var i = Walls.Count - 1; i >= 0; i--)
        {
            if (State.Multiviews.Contains(Walls[i].Wall)) continue;
            Walls.RemoveAt(i);
        }
        for (var i = 0; i < State.Multiviews.Count; i++)
        {
            var wall = State.Multiviews[i];
            var at = -1;
            for (var j = 0; j < Walls.Count; j++)
            {
                if (ReferenceEquals(Walls[j].Wall, wall)) { at = j; break; }
            }
            if (at < 0) Walls.Insert(i, new WallRow(wall));
            else if (at != i) Walls.Move(at, i);
            var row = Walls[i];
            row.IsSelected = ReferenceEquals(wall, SelectedWall);
            var tiles = wall.Tiles.Count;
            var on = Multiviews.TargetsShowing(State, wall.Id).Count();
            row.Summary = $"{(tiles == 0 ? "automatic" : $"{tiles} tile{(tiles == 1 ? "" : "s")}")} · {(on == 0 ? "not shown yet" : on == 1 ? "on 1 output" : $"on {on} outputs")}";
        }
    }

    /// <summary>The destination rows, kept level with the rig and with what each output is doing.</summary>
    public void RefreshWallDestinations()
    {
        var wall = SelectedWall;
        var wanted = new List<(string Id, string Label, string Kind)>();
        if (wall is not null)
        {
            var geo = Rig.Geometry(State, _services.Screens.All);
            foreach (var key in geo.Targets)
            {
                if (ContentTargets.IsCanvasKey(key)) wanted.Add((key, geo.LabelFor(State, key), "CANVAS"));
            }
            foreach (var s in geo.Screens)
            {
                if (VirtualScreens.IsVirtualId(s.Id)) continue;
                wanted.Add((s.Id, geo.LabelFor(State, s.Id), "SCREEN"));
            }
            foreach (var sender in State.Ndi.Senders)
            {
                wanted.Add((sender.OwnScreenId, sender.Name.Length > 0 ? sender.Name : "NDI sender", "NDI"));
            }
            wanted.Add((StreamConfig.OwnScreenId, "The stream", "STREAM"));
        }

        // Reconciled rather than rebuilt: a tick the operator is reaching for must not move.
        for (var i = WallDestinations.Count - 1; i >= 0; i--)
        {
            if (wanted.Any(w => w.Id == WallDestinations[i].TargetId)) continue;
            WallDestinations.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var (id, label, kind) = wanted[i];
            var at = -1;
            for (var j = 0; j < WallDestinations.Count; j++)
            {
                if (WallDestinations[j].TargetId == id) { at = j; break; }
            }
            if (at < 0 || WallDestinations[at].Label != label)
            {
                var row = new MultiviewDestination(id, label, kind, OnDestinationSet);
                if (at < 0) WallDestinations.Insert(Math.Min(i, WallDestinations.Count), row);
                else WallDestinations[at] = row;
            }
            else if (at != i)
            {
                WallDestinations.Move(at, i);
            }
            var dest = WallDestinations[i];
            dest.Follow(wall is not null && Multiviews.IsShowing(State, id, wall.Id));
            dest.Note = NoteFor(id, dest.IsOn);
        }
        RefreshWallRows();
        RefreshWallChoices();
        RaiseWallSummary();
    }

    private string NoteFor(string targetId, bool showingThisWall)
    {
        if (showingThisWall) return "showing this wall";
        if (!ContentTargets.UsesOwnPattern(State, targetId)) return "showing the programme";
        foreach (var a in State.Independent)
        {
            if (a.ScreenId != targetId) continue;
            if (a.Pattern.Kind != PatternKind.Multiview) return "on its own picture";
            var other = Multiviews.Find(State, a.Pattern.MultiviewId);
            return other is null ? "on its own picture" : $"showing {Multiviews.NameOf(State, other)}";
        }
        return "on its own picture";
    }

    private void OnDestinationSet(MultiviewDestination dest, bool on)
    {
        if (SelectedWall is not { } wall) return;
        _services.BulkEdit(() =>
        {
            if (on) Multiviews.Show(State, dest.TargetId, wall);
            else Multiviews.Clear(State, dest.TargetId);
        });
        RefreshWallDestinations();
        StatusMessage = on
            ? $"{dest.Label} is showing {Multiviews.NameOf(State, wall)}."
            : $"{dest.Label} is back on the programme.";
    }

    /// <summary>The whole page follows the wall: the pickers, the note, the line and the ticks.</summary>
    private void RaiseWall()
    {
        Raise(nameof(SelectedWall));
        Raise(nameof(HasWall));
        Raise(nameof(HasNoWall));
        Raise(nameof(CanAddWall));
        Raise(nameof(WallName));
        Raise(nameof(LayoutNote));
        Raise(nameof(ColumnsLabel));
        RefreshWallDestinations();
    }

    private void RaiseWallSummary()
    {
        Raise(nameof(WallSummary));
        Raise(nameof(CanAddWall));
    }
}
