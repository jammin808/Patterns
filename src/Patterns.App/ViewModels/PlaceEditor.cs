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
/// exactly there. It also owns the Position picker's value and RESET, because both mean the same
/// thing as the pixels: a position chosen is a position gone to, with the nudge a drag or the
/// sliders had built up dropped, and RESET drops it without changing the position. The drag, the
/// picker, the sliders and the pixels are four ways of saying one thing — move any of them and the
/// rest follow.
/// </summary>
public sealed class PlaceEditor : Observable
{
    private readonly Func<IAnchored?> _target;
    private readonly Func<PlacedBox?> _box;
    private readonly Action<Action> _edit;
    private string _words = "";

    /// <param name="edit">Runs a group of model writes as one publish (the desk's BulkEdit); each write publishes on its own without it.</param>
    public PlaceEditor(Func<IAnchored?> target, Func<PlacedBox?> box, Action<Action>? edit = null)
    {
        _target = target;
        _box = box;
        _edit = edit ?? (work => work());
        ResetCommand = new RelayCommand(Reset);
    }

    /// <summary>The box the last preview frame drew, the space it was drawn in, and the margin that space's renderer kept.</summary>
    public readonly record struct PlacedBox(SKRect Rect, SKSizeI Space, float Margin);

    /// <summary>
    /// The Position picker's own value. Choosing a position <em>puts the element there</em>: the
    /// nudge a drag or the sliders had built up goes with it, because "bottom right" means the
    /// bottom right of the screen, not the bottom right plus wherever it was last dragged. The
    /// nudge is then a fresh offset from the position that was chosen, which is what it is for.
    /// </summary>
    public Anchor9 Anchor
    {
        get => _target()?.Anchor ?? Anchor9.Center;
        set
        {
            if (_target() is not { } placed || placed.Anchor == value) return;
            _edit(() =>
            {
                placed.Anchor = value;
                placed.OffsetXPct = 0;
                placed.OffsetYPct = 0;
            });
            Refresh();
        }
    }

    /// <summary>RESET: back onto the position the picker names, with no nudge — the way out of a drag that went wrong.</summary>
    public RelayCommand ResetCommand { get; }

    /// <summary>The element has been moved off the position it is anchored to — RESET has something to undo.</summary>
    public bool IsNudged => _target() is { } placed && (placed.OffsetXPct != 0 || placed.OffsetYPct != 0);

    private void Reset()
    {
        if (_target() is not { } placed) return;
        _edit(() =>
        {
            placed.OffsetXPct = 0;
            placed.OffsetYPct = 0;
        });
        Refresh();
    }

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

    private Observable? _hooked;

    /// <summary>
    /// The overlay's own change notice, so the Position picker and RESET follow a look recall, a
    /// cue or a nudge slider the instant it lands rather than at the next second's poll — the pages
    /// used to bind straight at the model and must not lose that. Re-hooked if the object under us
    /// is ever replaced; the poll's refresh is what establishes it.
    /// </summary>
    private void Hook()
    {
        if (_target() is not Observable obs || ReferenceEquals(obs, _hooked)) return;
        if (_hooked is not null) _hooked.PropertyChanged -= OnTargetChanged;
        _hooked = obs;
        obs.PropertyChanged += OnTargetChanged;
    }

    private void OnTargetChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(IAnchored.Anchor) or nameof(IAnchored.OffsetXPct) or nameof(IAnchored.OffsetYPct))) return;
        Raise(nameof(Anchor));
        Raise(nameof(IsNudged));
    }

    /// <summary>The desk's poll: the readout follows a drag, a slider and a change of size.</summary>
    public void Refresh()
    {
        Hook();
        Raise(nameof(XPx));
        Raise(nameof(YPx));
        Raise(nameof(HasBox));
        Raise(nameof(Anchor));      // a drag re-anchors: the Position picker follows it
        Raise(nameof(IsNudged));
        Words = _box() is { } b
            ? $"{Math.Round(b.Rect.Left):0} , {Math.Round(b.Rect.Top):0} px  ·  {b.Rect.Width:0} × {b.Rect.Height:0} on {b.Space.Width} × {b.Space.Height}"
            : "Not on the picture yet — turn it on to place it in pixels.";
    }

    private void Move(double leftPx, double topPx)
    {
        if (_target() is not { } placed || _box() is not { } b) return;
        var (x, y) = OverlayPlace.NudgeForTopLeft(
            b.Space, b.Rect.Width, b.Rect.Height, placed.Anchor, leftPx, topPx, b.Margin);
        _edit(() =>
        {
            placed.OffsetXPct = x;
            placed.OffsetYPct = y;
        });
        Refresh();
    }
}
