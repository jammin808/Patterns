using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.App.Views.Controls;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Regression for the field report "colours can't be edited in Branding": the ColorPicker
/// theme was never included, so the pickers had no template at all. These tests fail if the
/// theme include is ever lost, and cover both editing paths (visual picker and hex box).
/// </summary>
public class ColorEditingTests
{
    private static (AppServices Services, MainViewModel Vm, MainWindow Window) Boot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-color-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var services = new AppServices(new SettingsStore(dir));
        AppServices.Instance = services;
        var vm = new MainViewModel(services);
        var window = new MainWindow { DataContext = vm };
        services.AttachMainWindow(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (services, vm, window);
    }

    private static void SelectTab(MainWindow window, string header)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        var item = tabs.Items.OfType<TabItem>().First(t => HeaderText(t) == header);
        tabs.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Headers are a neon dot + text panel; older ones were plain strings.</summary>
    private static string? HeaderText(TabItem tab) => tab.Header switch
    {
        string s => s,
        StackPanel p => p.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
        _ => null,
    };

    [AvaloniaFact]
    public void BrandingColorPickersHaveTemplatesAndWriteBackToTheModel()
    {
        var (services, vm, window) = Boot();
        try
        {
            SelectTab(window, "Branding");

            var fields = window.GetVisualDescendants().OfType<ColorField>().ToList();
            Assert.True(fields.Count >= 5, $"expected ≥5 colour fields in Branding, found {fields.Count}");

            var pickers = window.GetVisualDescendants().OfType<ColorPicker>().ToList();
            Assert.True(pickers.Count >= 5, $"expected ≥5 colour pickers, found {pickers.Count}");
            foreach (var p in pickers)
            {
                Assert.True(p.GetVisualDescendants().Any(),
                    "ColorPicker rendered no template visuals — is the ColorPicker theme StyleInclude missing from App.axaml?");
            }

            // Path 1: the visual picker commits a colour.
            var primaryPicker = fields[0].GetVisualDescendants().OfType<ColorPicker>().First();
            primaryPicker.Color = Color.FromRgb(0x12, 0x34, 0x56);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("#123456", vm.State.Brand.PrimaryColor);

            // Path 2: the hex box on the Secondary row.
            var secondaryBox = fields[1].GetVisualDescendants().OfType<TextBox>().First();
            secondaryBox.Text = "#ABCDEF";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("#ABCDEF", vm.State.Brand.SecondaryColor);

            // The render engine sees the change through the snapshot bus.
            Assert.Equal("#123456", services.Bus.Current.State.Brand.PrimaryColor);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void FlatFieldColorFieldWritesToActivePattern()
    {
        var (services, vm, window) = Boot();
        try
        {
            SelectTab(window, "Pattern");
            vm.ActivePattern.Kind = Patterns.Core.Model.PatternKind.FlatField;
            Dispatcher.UIThread.RunJobs();

            var field = window.GetVisualDescendants().OfType<ColorField>().First(f => f.IsEffectivelyVisible);
            var picker = field.GetVisualDescendants().OfType<ColorPicker>().First();
            picker.Color = Color.FromRgb(0x00, 0xFF, 0x00);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("#00FF00", vm.ActivePattern.FlatField.Color);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }
}
