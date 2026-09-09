using Avalonia;
using Avalonia.Controls;
using Patterns.App.ViewModels;

namespace Patterns.App.Views.Controls;

/// <summary>
/// The pixel half of an overlay's place: two fields under its Nudge sliders reading where the box
/// actually is on the canvas the PREVIEW pane shows, and a line saying what they are measured
/// against. One control on every overlay page rather than seven copies of the same grid.
/// </summary>
public partial class PlaceRow : UserControl
{
    public static readonly StyledProperty<PlaceEditor?> PlaceProperty =
        AvaloniaProperty.Register<PlaceRow, PlaceEditor?>(nameof(Place));

    public PlaceRow()
    {
        InitializeComponent();
    }

    /// <summary>The overlay's place editor — the view model's own, so the poll's refresh reaches these fields.</summary>
    public PlaceEditor? Place
    {
        get => GetValue(PlaceProperty);
        set => SetValue(PlaceProperty, value);
    }
}
