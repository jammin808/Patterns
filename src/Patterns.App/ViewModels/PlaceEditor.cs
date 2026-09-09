using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.App.ViewModels;

/// <summary>
/// One overlay's place, in pixels. The anchor and the nudge are how the place is <em>kept</em> —
/// relative, so it survives a bigger canvas, another shape and a change of size — but "48 px in
/// from the left" is how an operator says it, and a rig sheet gives positions in pixels. So every
/// overlay page carries a pixel pair beside its Nudge sliders: it reads the box's own top-left on
/// the canvas the PREVIEW pane is showing, and typing in it writes the nudge that lands the box
/// exactly there. The two are the same number in different clothes — move one and the other
/// follows, whether it moved by a drag, a slider or a typed pixel.
/// </summary>
public sealed class PlaceEditor : Observable
{
    private readonly Func<IAnchored?> _target;
    private readonly Func<PlacedBox?> _box;
    private string _words = "";

    public PlaceEditor(Func<IAnchored?> target, Func<PlacedBox?> box)
    {
        _target = target;
        _box = box;
    }

    /// <summary>The box the last preview frame drew, the space it was drawn in, and the margin that space's renderer kept.</summary>
    public readonly record struct PlacedBox(SKRect Rect, SKSizeI Space, float Margin);

    /// <summary>The box's left edge on the canvas, in pixels. Empty (0) until a frame has drawn it.</summary>
    public double XPx
    {
        get => _box() is { } b ? Math.Round(b.Rect.Left) : 0;
        set => Move(value, YPx);
    }

    /// <summary>The box's top edge on the canvas, in pixels.</summary>
    public double YPx
    {
        get => _box() is { } b ? Math.Round(b.Rect.Top) : 0;
        set => Move(XPx, value);
    }

    /// <summary>"1408, 216 px of 1920 × 1080" — where it is and what it is measured against; empty before the first frame.</summary>
    public string Words { get => _words; private set => Set(ref _words, value); }

    /// <summary>The pixel fields do nothing until a frame has told us the box's size and the canvas's.</summary>
    public bool HasBox => _box() is not null;

    /// <summary>The desk's poll: the readout follows a drag, a slider and a change of size.</summary>
    public void Refresh()
    {
        Raise(nameof(XPx));
        Raise(nameof(YPx));
        Raise(nameof(HasBox));
        Words = _box() is { } b
            ? $"{Math.Round(b.Rect.Left):0} , {Math.Round(b.Rect.Top):0} px  ·  {b.Rect.Width:0} × {b.Rect.Height:0} on {b.Space.Width} × {b.Space.Height}"
            : "Not on the picture yet — turn it on to place it in pixels.";
    }

    private void Move(double leftPx, double topPx)
    {
        if (_target() is not { } placed || _box() is not { } b) return;
        var (x, y) = OverlayPlace.NudgeForTopLeft(
            b.Space, b.Rect.Width, b.Rect.Height, placed.Anchor, leftPx, topPx, b.Margin);
        placed.OffsetXPct = x;
        placed.OffsetYPct = y;
        Refresh();
    }
}
