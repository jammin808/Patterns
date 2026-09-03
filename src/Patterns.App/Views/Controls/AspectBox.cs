using Avalonia;
using Avalonia.Controls;

namespace Patterns.App.Views.Controls;

/// <summary>
/// Sizes its child to a fixed aspect ratio inside the available space, centred — the PGM and
/// PVW panes take the shape of the target they show instead of the shape of the window.
/// Ratio ≤ 0 means "fill" (the child gets the whole box).
/// </summary>
public sealed class AspectBox : Decorator
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectBox, double>(nameof(Ratio), 16.0 / 9.0);

    /// <summary>Width divided by height.</summary>
    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    static AspectBox()
    {
        AffectsMeasure<AspectBox>(RatioProperty);
        AffectsArrange<AspectBox>(RatioProperty);
    }

    private Size Fit(Size available)
    {
        var ratio = Ratio;
        if (ratio <= 0 || double.IsNaN(ratio) || double.IsInfinity(available.Width) || double.IsInfinity(available.Height))
        {
            return available;
        }
        var w = available.Width;
        var h = w / ratio;
        if (h > available.Height)
        {
            h = available.Height;
            w = h * ratio;
        }
        return new Size(Math.Max(0, w), Math.Max(0, h));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var fit = Fit(availableSize);
        Child?.Measure(fit);
        return double.IsInfinity(availableSize.Width) || double.IsInfinity(availableSize.Height)
            ? Child?.DesiredSize ?? default
            : availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is null) return finalSize;
        var fit = Fit(finalSize);
        var x = (finalSize.Width - fit.Width) / 2;
        var y = (finalSize.Height - fit.Height) / 2;
        Child.Arrange(new Rect(x, y, fit.Width, fit.Height));
        return finalSize;
    }
}
