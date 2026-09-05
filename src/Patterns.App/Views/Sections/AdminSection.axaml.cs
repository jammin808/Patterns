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

    private void CopySupportInfo(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        _ = CopyAsync(vm, vm.BuildSupportInfo(), "Support info copied — paste it into an email or ticket.");
    }

    private void CopySuperCheck(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        _ = CopyAsync(vm, vm.SuperCheckText, "Super-check report copied — paste it into an email or ticket.");
    }

    private async Task CopyAsync(MainViewModel vm, string text, string done)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(text);
            vm.StatusMessage = done;
        }
        catch (Exception ex)
        {
            Patterns.Core.Services.Log.Warn("Clipboard copy failed.", ex);
            vm.StatusMessage = "Could not reach the clipboard — try again.";
        }
    }
}
