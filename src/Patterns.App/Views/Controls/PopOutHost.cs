using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Panels;

namespace Patterns.App.Views.Controls;

/// <summary>
/// The pop-out settings column beside a page, to the left of the switcher: the selected item's
/// settings (a cue's running order and actions, a screen's role, trims and warp, a lower-third
/// element's box and words) in a column of their own, so the page keeps its list and its room.
/// The desk decides what it shows (<see cref="MainViewModel.PopOut"/>: a key, a title and the
/// page's hue); this control builds the panel for the key once and keeps it, so a change of
/// selection binds the same panel to the new item rather than building the controls again. It
/// wears the page's hue class, so its title band and the panel's bands take the page's neon like
/// the page's own, and ? TIPS reads its tips under its title after the page's.
/// </summary>
public sealed class PopOutHost : UserControl
{
    /// <summary>The column's width in pixels — what a settings form needs, and what the page column grows by while it is open.</summary>
    public const double ColumnWidth = 420;

    public static readonly StyledProperty<string> KeyProperty = AvaloniaProperty.Register<PopOutHost, string>(nameof(Key), "");
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<PopOutHost, string>(nameof(Title), "");
    public static readonly StyledProperty<string> HueProperty = AvaloniaProperty.Register<PopOutHost, string>(nameof(Hue), "");

    private static readonly Dictionary<string, Func<Control>> Factories = new()
    {
        ["cue"] = () => new CueSettingsPanel(),
        ["screen"] = () => new ScreenSettingsPanel(),
        ["element"] = () => new ElementSettingsPanel(),
    };

    private readonly Dictionary<string, Control> _built = new();
    private readonly TextBlock _title;
    private readonly ScrollViewer _body;

    public PopOutHost()
    {
        MinWidth = ColumnWidth;
        MaxWidth = ColumnWidth;
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
        Content = new Border { Classes = { "panel", "popOut" }, Padding = new Thickness(0), Child = rows };
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
