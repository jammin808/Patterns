using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Panels;
using Patterns.Core.Model;

namespace Patterns.App.Views.Controls;

/// <summary>
/// The pop-out settings column beside a page, to the left of the switcher: the selected item's
/// settings (a cue's running order and actions, a screen's role, trims and warp, a lower-third
/// element's box and words) in a column of their own, so the page keeps its list and its room.
/// The desk decides what it shows (<see cref="MainViewModel.PopOut"/>: a key, a title and the
/// page's hue); this control builds the panel for the key once and keeps it, so a change of
/// selection binds the same panel to the new item rather than building the controls again. It
/// wears the page's hue class, so its title band and the panel's bands take the page's neon like
/// the page's own, and ? TIPS reads its tips under its title after the page's. Round 73: the
/// handle on its left edge drags its width (the window keeps it in the show's desk layout), and
/// the Machine and Audio pages keep their settings groups here, so the pages themselves stay
/// the health lines and the lists.
/// </summary>
public sealed class PopOutHost : UserControl
{
    /// <summary>The column's default width in pixels — what a settings form needs, and what the page column grows by while it is open; the handle drags it between the desk layout's bounds.</summary>
    public const double ColumnWidth = DeskLayoutConfig.DefaultPopOutWidth;

    public static readonly StyledProperty<string> KeyProperty = AvaloniaProperty.Register<PopOutHost, string>(nameof(Key), "");
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<PopOutHost, string>(nameof(Title), "");
    public static readonly StyledProperty<string> HueProperty = AvaloniaProperty.Register<PopOutHost, string>(nameof(Hue), "");

    private static readonly Dictionary<string, Func<Control>> Factories = new()
    {
        ["cue"] = () => new CueSettingsPanel(),
        ["screen"] = () => new ScreenSettingsPanel(),
        ["element"] = () => new ElementSettingsPanel(),
        ["machine"] = () => new MachineSettingsPanel(),   // round 73: show lock, rig day, the watchdog, the beacon, the twin, the earlier versions
        ["audio"] = () => new AudioSettingsPanel(),       // round 73: the master clock and sync, the tone generator
    };

    /// <summary>Round 73: the handle was dragged — the width the column should be, within the desk layout's bounds; the window writes it into the show and lays the desk out again.</summary>
    public event Action<double>? WidthDragged;

    private readonly Dictionary<string, Control> _built = new();
    private readonly TextBlock _title;
    private readonly ScrollViewer _body;

    public PopOutHost()
    {
        Width = ColumnWidth;
        // The title is a section band like the page's own ("SELECTED CUE · 01.010 Doors"), so it takes the page's neon and heads the column's tips.
        _title = new TextBlock { Classes = { "h2" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "◀ CLOSE", Classes = { "mini" }, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => (DataContext as MainViewModel)?.ClosePopOutCommand.Execute(null);
        ToolTip.SetTip(close, "Close this column — the page's SETTINGS ▸ opens it again, and the next selection brings it back");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto"), Margin = new Thickness(12, 8, 8, 4) };
        header.Children.Add(_title);
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        _body = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var rows = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        rows.Children.Add(header);
        Grid.SetRow(_body, 1);
        rows.Children.Add(_body);
        var panel = new Border { Classes = { "panel", "popOut" }, Padding = new Thickness(0), Child = rows };
        // Round 73: the handle on the left edge. Dragging left widens the column (the page column
        // grows with it, the page keeps its own width), dragging right narrows it; the window keeps
        // the width in the show's desk layout, so every show opens its column where it was left.
        var handle = new Thumb { Classes = { "popOutHandle" }, Cursor = new Cursor(StandardCursorType.SizeWestEast) };
        ToolTip.SetTip(handle, "Drag: the column's width — the show remembers it");
        handle.DragDelta += (_, e) => WidthDragged?.Invoke(Math.Clamp((double.IsNaN(Width) ? ColumnWidth : Width) - e.Vector.X, DeskLayoutConfig.MinPopOutWidth, DeskLayoutConfig.MaxPopOutWidth));
        var frame = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        frame.Children.Add(handle);
        Grid.SetColumn(panel, 1);
        frame.Children.Add(panel);
        Content = frame;
    }

    /// <summary>Which settings to show: "cue", "screen", "element" — or empty for none.</summary>
    public string Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    /// <summary>The header: "SELECTED CUE · 01.010 Doors".</summary>
    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The page's hue class ("hue-cues"): the column's bands wear the page's neon like the page's own.</summary>
    public string Hue
    {
        get => GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }

    /// <summary>The keys this host can show.</summary>
    public static IReadOnlyCollection<string> Known => Factories.Keys;

    /// <summary>The panel on show, or null with the column closed.</summary>
    public Control? Panel => _body.Content as Control;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KeyProperty) Show(change.GetNewValue<string>() ?? "");
        else if (change.Property == TitleProperty) _title.Text = change.GetNewValue<string>() ?? "";
        else if (change.Property == HueProperty)
        {
            var was = change.GetOldValue<string>();
            if (!string.IsNullOrEmpty(was)) Classes.Remove(was);
            var now = change.GetNewValue<string>();
            if (!string.IsNullOrEmpty(now)) Classes.Add(now);
        }
    }

    private void Show(string key)
    {
        if (key.Length == 0)
        {
            _body.Content = null;
            return;
        }
        if (!_built.TryGetValue(key, out var panel))
        {
            if (!Factories.TryGetValue(key, out var factory))
            {
                _body.Content = new TextBlock { Text = $"No settings for '{key}'.", Margin = new Thickness(12) };
                return;
            }
            Control? built = null;
            UiFaults.Guard(() => built = factory(), $"building the {key} settings");
            if (built is null) return;
            panel = built;
            _built[key] = panel;
        }
        _body.Content = panel;
    }
}
