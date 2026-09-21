using Patterns.Core.Services;

namespace Patterns.Rendering;

/// <summary>What a sink's frame drew: the show, or the calibration's structured light in its place.</summary>
public enum FrameKind
{
    Show,
    Calibration,
}

/// <summary>
/// One frame's world, captured once at its start: the program snapshot the sink draws, the
/// preview snapshot its PREVIEW tile draws, the clock, and what kind of frame it is. The bus can
/// publish while a frame is in flight; every read inside the frame — the picture, the stage
/// clock, the version the GO's clock is told — comes from this capture and never from the bus
/// again, so a frame is drawn from one generation and reported as that generation. A calibration
/// frame draws no show at all, and says so, so it never counts as the show's version shown.
/// Immutable; a readonly record struct, nothing allocated per frame.
/// </summary>
public readonly record struct FrameInput(ShowSnapshot Program, ShowSnapshot? Preview, double Clock, FrameKind Kind)
{
    public long ProgramVersion => Program.Version;

    /// <summary>The preview's version, or -1 with no sandbox open.</summary>
    public long PreviewVersion => Preview?.Version ?? -1;

    public bool IsShow => Kind == FrameKind.Show;

    /// <summary>
    /// The capture: one read of the bus's pair (round 79: the programme and the sandbox are published as one, so a
    /// frame cannot read a new programme beside an old sandbox). The preview side (the preview window, a monitor's
    /// PVW pane) draws the sandbox while one is open; every other sink draws the program — and both read the same
    /// sandbox reference for the PREVIEW tile, so one frame cannot mix two generations of it.
    /// </summary>
    public static FrameInput Capture(SnapshotBus bus, bool previewSide, double clock, FrameKind kind = FrameKind.Show)
    {
        var pair = bus.Pair;
        var sandbox = pair.Sandbox;
        return new FrameInput(previewSide && sandbox is not null ? sandbox : pair.Current, sandbox, clock, kind);
    }
}
