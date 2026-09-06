using Avalonia.Controls;

namespace Patterns.App.Views.Sections;

/// <summary>
/// The Layers page: the two pictures over the editing target's pattern, on the BUILD rail before
/// the library and the assistant. Bindings only — the editors and the drag live in the view model
/// and the preview pane.
/// </summary>
public partial class LayersSection : UserControl
{
    public LayersSection()
    {
        InitializeComponent();
    }
}
