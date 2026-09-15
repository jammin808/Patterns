using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Rendering;

/// <summary>The things on a picture that arrive and leave on their own (round 63): the overlays, the countdown, the PiP inset and the two layers.</summary>
public enum AppearKey
{
    Badge,
    Logo,
    Clock,
    Weather,
    Countdown,
    Message,
    Pip,
    Layer1,
    Layer2,
}

/// <summary>
/// Where one thing is between gone and shown on one frame: <see cref="In"/> is the presence of
/// what the snapshot shows now, <see cref="Out"/> that of what is leaving, drawn from
/// <see cref="Outgoing"/> — the snapshot that had it. Settled things read as 1 or 0 and nothing out.
/// <see cref="Shown"/> is whether the snapshot shows the thing at all: an arriving thing is drawn
/// from its first frame, at a presence of nothing, so its box is on the hit map the frame the show
/// says it is there — the desk's menus and drags find it at once, the eye a frame later.
/// </summary>
public readonly record struct Presence(float In, float Out, ShowSnapshot? Outgoing, bool Shown)
{
    public static Presence Settled(bool shown) => new(shown ? 1f : 0f, 0f, null, shown);

    /// <summary>The snapshot shows it: drawn at <see cref="In"/>, from the first frame of an arrival on.</summary>
    public bool DrawsCurrent => Shown;

    public bool DrawsOutgoing => Out > 0f && Outgoing is not null;

    /// <summary>Nothing is moving: shown in full, or gone.</summary>
    public bool IsSettled => Outgoing is null && (Shown ? In >= 1f : In <= 0f);
}

/// <summary>
/// One sink's memory of what its overlays and layers were doing, so a thing switched on arrives
/// and a thing switched off leaves — over the show's transition time, eased — instead of
/// popping. An overlay used to appear the moment its flag flipped, on a live toggle and on a TAKE
/// that changed nothing else; a layer likewise. Per sink because each sink draws on its own
/// thread and at its own moment; the snapshot never knows what a sink has already shown.
/// </summary>
public sealed class AppearanceTracker
{
    private sealed class Slot
    {
        public bool Shown;
        public object? Identity;
        public double StartClock;
        public double EndClock;
        public bool Arriving;
        public bool Swap;
        public ShowSnapshot? Outgoing;
        public ShowSnapshot? Last;
    }

    private readonly Slot?[] _slots = new Slot?[Enum.GetValues<AppearKey>().Length];

    /// <summary>
    /// The thing's presence on this frame. <paramref name="shown"/> and <paramref name="identity"/>
    /// are what the snapshot says now (a layer's identity is its source; a change of it is a leave
    /// and an arrival together); a change from what this sink last saw starts a move of
    /// <paramref name="seconds"/> at <paramref name="clock"/> when <paramref name="animate"/> is
    /// on, and settles at once when it is off — a cut, the show's transitions off, a first frame,
    /// or a whole-picture crossfade already carrying everything.
    /// </summary>
    public Presence Read(AppearKey key, bool shown, object? identity, double clock, double seconds, bool animate, ShowSnapshot snap)
    {
        var slot = _slots[(int)key];
        if (slot is null)
        {
            _slots[(int)key] = new Slot { Shown = shown, Identity = identity, Last = snap };
            return Presence.Settled(shown);
        }
        var changed = shown != slot.Shown || (shown && slot.Shown && !Equals(identity, slot.Identity));   // exact equality: a value, never a hash (round 64)
        if (changed)
        {
            if (animate && seconds > 0.01)
            {
                slot.StartClock = clock;
                slot.EndClock = clock + seconds;
                slot.Arriving = shown;
                slot.Swap = shown && slot.Shown;
                slot.Outgoing = slot.Shown ? slot.Last : null;
            }
            else
            {
                slot.EndClock = 0;
                slot.Outgoing = null;
                slot.Swap = false;
            }
            slot.Shown = shown;
            slot.Identity = identity;
        }
        slot.Last = snap;
        if (slot.EndClock <= clock)
        {
            if (slot.EndClock > 0)
            {
                slot.EndClock = 0;
                slot.Outgoing = null;
                slot.Swap = false;
            }
            return Presence.Settled(slot.Shown);
        }
        var t = (float)Transitions.Ease(Math.Clamp((clock - slot.StartClock) / (slot.EndClock - slot.StartClock), 0, 1));
        if (slot.Swap) return new Presence(t, 1f - t, slot.Outgoing, true);
        return slot.Arriving ? new Presence(t, 0f, null, true) : new Presence(0f, 1f - t, slot.Outgoing, false);
    }

    /// <summary>True while any move runs: the sink needs the next frame.</summary>
    public bool Running(double clock)
    {
        foreach (var slot in _slots)
        {
            if (slot is { EndClock: > 0 } s && s.EndClock > clock) return true;
        }
        return false;
    }

    /// <summary>The state this sink last saw for a thing, for a test or a glance; null before its first frame.</summary>
    public bool? LastShown(AppearKey key) => _slots[(int)key]?.Shown;
}

/// <summary>The rules the renderers share for arrivals and departures.</summary>
public static class Appearances
{
    /// <summary>
    /// Whether this frame animates arrivals at all: a real sink's own top-level draw (never a
    /// fade source, a multiview tile, a layer's inner draw or a thumbnail), with the show's
    /// transitions on, no whole-picture crossfade running (that one carries the overlays with it)
    /// and this publish not a CUT.
    /// </summary>
    public static bool Animates(in PatternFrame f)
        => f.Ctx.Sink != SinkKind.Thumbnail && !f.Ctx.IsFadeSource && !f.Ctx.InMultiview && !f.Ctx.InLayer
           && !f.Snapshot.TransitionsOff && f.Sink.TransitionFrom is null
           && f.Snapshot.CutAtVersion != f.Snapshot.Version;

    /// <summary>The frame is one whose draws bypass the tracker altogether: not the sink's own picture.</summary>
    public static bool Bypasses(RenderContext ctx)
        => ctx.Sink == SinkKind.Thumbnail || ctx.IsFadeSource || ctx.InMultiview || ctx.InLayer;

    /// <summary>The move's length: the thing's own time, or the show's transition time.</summary>
    public static double Seconds(AppearanceConfig cfg, ShowSnapshot snap)
        => cfg.DurationMs > 0 ? cfg.DurationMs / 1000.0 : snap.FadeSecondsFor(snap.Version);

    /// <summary>
    /// A slide's offset for a presence: from the anchor's edge (a thing at the top slides down in,
    /// one at the right slides in from the right; one in the middle comes up from below), six
    /// hundredths of the space, all the way out at presence 0 and home at 1.
    /// </summary>
    public static SKPoint SlideOffset(Anchor9 anchor, SKSizeI space, float presence)
    {
        var col = (int)anchor % 3;
        var row = (int)anchor / 3;
        var reach = 1f - Math.Clamp(presence, 0f, 1f);
        var dx = col == 0 ? -space.Width * 0.06f : col == 2 ? space.Width * 0.06f : 0f;
        var dy = row == 0 ? -space.Height * 0.06f : row == 2 ? space.Height * 0.06f : dx == 0 ? space.Height * 0.06f : 0f;
        return new SKPoint(dx * reach, dy * reach);
    }

    /// <summary>A layer's slide: from the nearest edge of the canvas to its box.</summary>
    public static SKPoint SlideOffset(SKRect box, SKSizeI canvas, float presence)
    {
        var reach = 1f - Math.Clamp(presence, 0f, 1f);
        var left = box.MidX;
        var right = canvas.Width - box.MidX;
        var top = box.MidY;
        var bottom = canvas.Height - box.MidY;
        var min = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
        var dx = min == left ? -canvas.Width * 0.06f : min == right ? canvas.Width * 0.06f : 0f;
        var dy = dx != 0 ? 0f : min == top ? -canvas.Height * 0.06f : canvas.Height * 0.06f;
        return new SKPoint(dx * reach, dy * reach);
    }

    /// <summary>The anchor a thing sits at, for its slide.</summary>
    public static Anchor9 AnchorOf(ShowState s, AppearKey key) => key switch
    {
        AppearKey.Badge => s.Overlays.Badge.Anchor,
        AppearKey.Logo => s.Overlays.Logo.Anchor,
        AppearKey.Clock => s.Overlays.Clock.Anchor,
        AppearKey.Weather => s.Overlays.Weather.Anchor,
        AppearKey.Countdown => s.Countdown.Anchor,
        AppearKey.Message => s.Overlays.Message.Anchor,
        AppearKey.Pip => s.Overlays.Pip.Anchor,
        _ => Anchor9.Center,
    };

    /// <summary>The frame as the outgoing snapshot had it: its picture, marked a fade source so it records no hits and claims no live source.</summary>
    public static PatternFrame OutgoingFrame(in PatternFrame f, ShowSnapshot outgoing) => new()
    {
        Snapshot = outgoing,
        Config = outgoing.PatternFor(f.Ctx.ScreenId),
        Ctx = f.Ctx with { IsFadeSource = true },
        Sink = f.Sink,
        Canvas = f.Canvas,
        Palette = f.Palette,
        Gaps = f.Gaps,
        DeviceScale = f.DeviceScale,
    };

    /// <summary>A layer's identity: what it shows — a change of source or of picture is a leave and an arrival. A value compared exactly (round 64 retired the hash: two pictures whose hashes met would have read as one).</summary>
    public static LayerPicture LayerIdentity(LayerConfig l) => new(l.Source, l.ImagePath, l.VideoPath, l.NdiSourceName, l.CaptureDevice, l.WebUrl, l.TargetId);
}

/// <summary>What a layer shows, exactly: its source and the picture that source names. Two layers with the same values are the same picture; nothing else is.</summary>
public readonly record struct LayerPicture(LayerSource Source, string ImagePath, string VideoPath, string NdiSourceName, string CaptureDevice, string WebUrl, string TargetId);
