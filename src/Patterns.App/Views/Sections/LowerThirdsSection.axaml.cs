using Avalonia.Controls;
using Avalonia.Interactivity;
using Patterns.App.ViewModels;

namespace Patterns.App.Views.Sections;

public partial class LowerThirdsSection : UserControl
{
    public LowerThirdsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.RefreshLowerThirdFiles();
        vm.SelectedLowerThird ??= vm.State.LowerThirds.Designs.FirstOrDefault();
    }
}
