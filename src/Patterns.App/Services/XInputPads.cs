using System.Runtime.InteropServices;
using Patterns.Core.Arcade;
using Patterns.Arcade;

namespace Patterns.App.Services;

/// <summary>
/// XInput on Windows through one P/Invoke: every Xbox pad and most USB pads, four at once, read at
/// the simulation tick. A pad that is not there is asked again once a second, not every frame (the
/// runtime is slow to say no); a machine without the library is one that has no pads.
/// </summary>
internal static class XInputPads
{
    private const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008, Start = 0x0010, A = 0x1000, B = 0x2000;
    private const short Deadzone = 7849;

    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint Packet;
        public Gamepad Pad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState(uint index, out State state);

    private static bool _missing;
    private static readonly long[] _retryAt = new long[4];

    public static bool Available => OperatingSystem.IsWindows() && !_missing;

    /// <summary>The pad's buttons, the left stick folded into the d-pad; false when the pad is not there.</summary>
    public static bool TryRead(int index, out PadButtons buttons)
    {
        buttons = PadButtons.None;
        if (!Available || index < 0 || index > 3) return false;
        var now = Environment.TickCount64;
        if (now < _retryAt[index]) return false;
        try
        {
            if (GetState((uint)index, out var state) != 0)
            {
                _retryAt[index] = now + 1000;
                return false;
            }
            var b = state.Pad.Buttons;
            if ((b & DpadUp) != 0 || state.Pad.ThumbLY > Deadzone) buttons |= PadButtons.Up;
            if ((b & DpadDown) != 0 || state.Pad.ThumbLY < -Deadzone) buttons |= PadButtons.Down;
            if ((b & DpadLeft) != 0 || state.Pad.ThumbLX < -Deadzone) buttons |= PadButtons.Left;
            if ((b & DpadRight) != 0 || state.Pad.ThumbLX > Deadzone) buttons |= PadButtons.Right;
            if ((b & A) != 0) buttons |= PadButtons.A;
            if ((b & B) != 0) buttons |= PadButtons.B;
            if ((b & Start) != 0) buttons |= PadButtons.Start;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _missing = true;
            return false;
        }
    }
}
