using Avalonia.Controls;

namespace Patterns.App.Views.Controls;

/// <summary>The right-click menu's face: a header in the thing's tone, the groups, and the drawer beside them. Bound to a <see cref="ViewModels.DeskMenuVm"/>.</summary>
public partial class DeskMenuControl : UserControl
{
    public DeskMenuControl()
    {
        InitializeComponent();
    }
}
