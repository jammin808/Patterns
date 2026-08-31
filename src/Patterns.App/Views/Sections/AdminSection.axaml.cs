using Avalonia.Controls;
using Avalonia.Interactivity;
using Patterns.App.ViewModels;

namespace Patterns.App.Views.Sections;

public partial class AdminSection : UserControl
{
    public AdminSection()
    {
        InitializeComponent();
    }

    private async void CopySupportInfo(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(vm.BuildSupportInfo());
            vm.StatusMessage = "Support info copied — paste it into an email or ticket.";
        }
        catch (Exception ex)
        {
            Patterns.Core.Services.Log.Warn("Clipboard copy failed.", ex);
            vm.StatusMessage = "Could not reach the clipboard — try again.";
        }
    }
}
