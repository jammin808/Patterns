using Avalonia.Controls;

namespace Patterns.App.Views.Controls;

/// <summary>
/// PAGE CONTROLS for the web page on air — the pattern's page, else the first web layer: the
/// keyboard chip, the page's own actions, typed text and the keys. One control, carried by the
/// Media page and the Layers page; it binds to the desk's view model through its data context.
/// </summary>
public partial class WebPageControls : UserControl
{
    public WebPageControls()
    {
        InitializeComponent();
    }
}
