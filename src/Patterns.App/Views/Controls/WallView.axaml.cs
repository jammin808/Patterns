using Avalonia;
using Avalonia.Controls;

namespace Patterns.App.Views.Controls;

/// <summary>The wall of content-target tiles with ARM ALL, CUT and TAKE — shared by the Build and Run layouts.</summary>
public partial class WallView : UserControl
{
    /// <summary>
    /// The tiles as vertical title bars — the tally, the name on its side, the BLACK badge — instead
    /// of the full tile with its miniatures and buttons: the Run area's choice, so the cue stack has
    /// the room. A pause over a bar still pops the tile up large. Carried to the tiles as the
    /// "collapsed" class on this control, which the wall's own styles read — a class on the tree
    /// itself, so every tile shows one branch and never both.
    /// </summary>
    public static readonly StyledProperty<bool> CollapsedProperty =
        AvaloniaProperty.Register<WallView, bool>(nameof(Collapsed));

    /// <summary>The class the styles read; the same word as the property.</summary>
    public const string CollapsedClass = "collapsed";

    public bool Collapsed
    {
        get => GetValue(CollapsedProperty);
        set => SetValue(CollapsedProperty, value);
    }

    public WallView()
    {
        InitializeComponent();
        Classes.Set(CollapsedClass, Collapsed);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CollapsedProperty) Classes.Set(CollapsedClass, change.GetNewValue<bool>());
    }
}
