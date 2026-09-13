using Avalonia.Input;
using Patterns.Core.Arcade;

namespace Patterns.App.Views.Controls;

/// <summary>
/// The keyboard as four pads: P1 the arrows, Space and Enter; P2 WASD, Left Shift and Q; P3 IJKL, U
/// and H; P4 the numpad's 8 4 5 6, 0 and +. B is Right Ctrl, Left Ctrl, O and the numpad's point.
/// </summary>
public static class ArcadeKeys
{
    public static (int Player, PadButtons Button)? Map(Key key) => key switch
    {
        Key.Up => (0, PadButtons.Up),
        Key.Down => (0, PadButtons.Down),
        Key.Left => (0, PadButtons.Left),
        Key.Right => (0, PadButtons.Right),
        Key.Space => (0, PadButtons.A),
        Key.RightCtrl => (0, PadButtons.B),
        Key.Return or Key.Enter => (0, PadButtons.Start),
        Key.W => (1, PadButtons.Up),
        Key.S => (1, PadButtons.Down),
        Key.A => (1, PadButtons.Left),
        Key.D => (1, PadButtons.Right),
        Key.LeftShift => (1, PadButtons.A),
        Key.LeftCtrl => (1, PadButtons.B),
        Key.Q => (1, PadButtons.Start),
        Key.I => (2, PadButtons.Up),
        Key.K => (2, PadButtons.Down),
        Key.J => (2, PadButtons.Left),
        Key.L => (2, PadButtons.Right),
        Key.U => (2, PadButtons.A),
        Key.O => (2, PadButtons.B),
        Key.H => (2, PadButtons.Start),
        Key.NumPad8 => (3, PadButtons.Up),
        Key.NumPad5 or Key.NumPad2 => (3, PadButtons.Down),
        Key.NumPad4 => (3, PadButtons.Left),
        Key.NumPad6 => (3, PadButtons.Right),
        Key.NumPad0 => (3, PadButtons.A),
        Key.Decimal => (3, PadButtons.B),
        Key.Add => (3, PadButtons.Start),
        _ => null,
    };
}
