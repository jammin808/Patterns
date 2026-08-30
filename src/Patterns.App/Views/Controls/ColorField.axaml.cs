using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Patterns.App.Views.Controls;

/// <summary>A colour editor row: visual ColorPicker plus a hex text field, sharing one hex-string value.</summary>
public partial class ColorField : UserControl
{
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<ColorField, string?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public ColorField()
    {
        InitializeComponent();
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
