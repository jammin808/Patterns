using Avalonia;
using Avalonia.Controls;

namespace Patterns.App.Views.Controls;

/// <summary>The wall of content-target tiles with ARM ALL, CUT and TAKE — shared by the Build and Run layouts.</summary>
public partial class WallView : UserControl
{
    /// <summary>
    /// The tiles as vertical title bars — the tally, the name on its side, the BLACK badge — instead
    /// of the full tile with its miniatures and buttons: the Run area's choice, so the cue stack has
    /// the room. A pause over a bar still pops the tile up large.
    /// </summary>
    public static readonly StyledProperty<bool> CollapsedProperty =
        AvaloniaProperty.Register<WallView, bool>(nameof(Collapsed));

    public bool Collapsed
    {
        get => GetValue(CollapsedProperty);
        set => SetValue(CollapsedProperty, value);
    }

    public WallView()
    {
        InitializeComponent();
    }
}
