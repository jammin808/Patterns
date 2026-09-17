using System.Text.Json;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Round 73: MIDI learn from any button. Right-click a look, a cue, a tile, a lower third, a
/// layer, an overlay, the GO button — anything with a wire line — choose MIDI LEARN and the
/// line, and the desk waits; the next control moved on any open surface is bound to that line,
/// into the surface's own trigger table (the Interactive area's map), saved with the show.
///
/// This is the desk's learn state and nothing else: what line it waits for, since when, what
/// was last bound; the shaping of rows is <see cref="MidiBindings"/> (pure) and the listening
/// is <see cref="Patterns.Devices.DeviceService.LearnAny"/>. Every way in is the action layer —
/// a menu line, the wire's MIDI LEARN, a Companion key — so a learn is journaled like a press,
/// and every reader (the menus' ticks, STATE's midi row, the Interactive page's banner and
/// MAPPED CONTROLS, the Eye's desk node, the deck's variables) reads the one table.
///
/// The rules taken from the field (docs/MIDI-LEARN.md): the target is chosen first and the
/// control second, with a visible armed state and a way out (Esc, CANCEL, MIDI LEARN OFF); a
/// release never binds; a press binds the pad at any velocity and a sweep the fader anywhere;
/// the last mapping of a control wins and says what it replaced; a learn is refused, with the
/// reason, when there is nowhere for a press to come from.
/// </summary>
public sealed class MidiLearnService
{
    private readonly AppServices _s;

    public MidiLearnService(AppServices s)
    {
        _s = s;
    }

    /// <summary>The wire line learn waits for a control for; "" when it is not armed.</summary>
    public string Wire { get; private set; } = "";

    /// <summary>The kind's label for the line armed ("Look — on air"), for the banner.</summary>
    public string Label { get; private set; } = "";

    /// <summary>When learn was armed.</summary>
    public DateTime SinceUtc { get; private set; }

    /// <summary>The words of the last binding written — "NOTE 1 53 on APC40 → LOOK Walk-in" — for the status line, the page and a test.</summary>
    public string LastLearned { get; private set; } = "";

    public bool Armed => Wire.Length > 0;

    /// <summary>Armed, disarmed, bound or forgotten: the menus, the page and the Eye re-read.</summary>
    public event Action? Changed;

    /// <summary>The MIDI surfaces in the show, open or not.</summary>
    public IReadOnlyList<DeviceConfig> Surfaces => _s.State.Interactive.Devices.Where(MidiBindings.IsSurface).ToList();

    public bool HasSurface => _s.State.Interactive.Devices.Any(MidiBindings.IsSurface);

    /// <summary>One surface is open now — a press will arrive.</summary>
    public bool SurfaceOpen => _s.Devices.OpenMidiSurfaces.Count > 0;

    /// <summary>Every control bound on every surface — the trigger rows read as bindings.</summary>
    public IReadOnlyList<MidiBinding> Bindings => MidiBindings.All(_s.State.Interactive.Devices);

    /// <summary>
    /// Learn armed for a wire line: the next control moved on any open surface is bound to it.
    /// Refused, with the reason, for a line the wire does not know or that acts on nothing (a
    /// question is not something a pad can do), and when there is no surface in the show or none
    /// open — a learn that waits for a press that cannot come is a desk that looks stuck.
    /// </summary>
    public ActionResult Arm(string wire)
    {
        var line = MidiBindings.Normalise(wire);
        if (line.Length == 0) return ActionResult.Refused("MIDI LEARN what? A wire line — LOOK Walk-in, CUE GO, LT 1, SCREEN 2 PVW PATTERN Grid.");
        var parsed = ControlProtocol.Parse(line);
        if (parsed.Kind != RemoteCommandKind.Action)
        {
            return ActionResult.Refused(parsed.Kind == RemoteCommandKind.Unknown
                ? $"The wire has no verb for '{line}' — a control can only be bound to a line the wire knows (docs/REMOTE.md)."
                : $"'{line}' is a question, not something a control can do — bind a line that acts (LOOK, CUE GO, BLACKOUT, LT…).");
        }
        if (ActionSpec.CarriesSecret(parsed.Action.Kind))
        {
            // Round 75: a bound line sits in the show file in clear, and the show file travels (the twin, a stick, a bundle).
            return ActionResult.Refused("An admin verb (UPDATE APPLY, RESTART) carries the passcode — not a line to bind. The admin verbs stay on the desk and the Remote page.");
        }
        if (!HasSurface) return ActionResult.Refused("No MIDI control surface in the show — add one on the Interactive page (+ MIDI CONTROL SURFACE) first.");
        if (!SurfaceOpen) return ActionResult.Refused("No MIDI surface is open — switch the Interactive area on and check the surface's port on the Interactive page.");
        Wire = line;
        Label = ActionSpec.Label(parsed.Action.Kind);
        SinceUtc = _s.Clock.UtcNow;
        _s.Devices.LearnAny(OnLearned);
        Changed?.Invoke();
        return ActionResult.Done(Words);
    }

    /// <summary>A surface's line arrived while learn waited — on the desk's thread, as every device line is.</summary>
    private void OnLearned(DeviceConfig device, string line)
    {
        var wire = Wire;
        if (wire.Length == 0) return;
        var words = "";
        _s.BulkEdit(() => words = MidiBindings.Bind(device, line, wire));
        Wire = "";
        Label = "";
        LastLearned = words.Length > 0 ? "⌁ " + words : "";
        if (words.Length > 0) Log.Info($"MIDI learn: {words}");
        Changed?.Invoke();
    }

    /// <summary>Learn disarmed; nothing is bound. Esc, the banner's CANCEL and MIDI LEARN OFF all come here.</summary>
    public ActionResult Cancel()
    {
        if (!Armed) return ActionResult.Done("MIDI learn was not waiting for anything.");
        var was = Wire;
        Wire = "";
        Label = "";
        _s.Devices.CancelLearn();
        Changed?.Invoke();
        return ActionResult.Done($"MIDI learn cancelled — nothing was bound to {was}.");
    }

    /// <summary>Every control bound to a wire line forgotten, on every surface.</summary>
    public ActionResult Forget(string wire)
    {
        var line = MidiBindings.Normalise(wire);
        if (line.Length == 0) return ActionResult.Refused("MIDI FORGET what? The wire line whose controls should be unbound.");
        var gone = 0;
        _s.BulkEdit(() => gone = MidiBindings.Forget(_s.State.Interactive.Devices, line));
        Changed?.Invoke();
        return gone == 0
            ? ActionResult.Done($"No control was bound to {line}.")
            : ActionResult.Done($"{gone} control{(gone == 1 ? "" : "s")} bound to {line} forgotten — the control does nothing until it is learned again.");
    }

    /// <summary>One binding's row forgotten — the page's ✕ beside a mapped control.</summary>
    public string ForgetRow(MidiBinding binding)
    {
        var gone = false;
        _s.BulkEdit(() => gone = MidiBindings.ForgetRow(_s.State.Interactive.Devices, binding));
        Changed?.Invoke();
        return gone ? $"{binding.Words} → {binding.Command} forgotten." : $"{binding.Words} was not bound.";
    }

    /// <summary>The banner and the status line while learn waits: which line, and the way out.</summary>
    public string Words => Armed
        ? $"MIDI LEARN — press a control on the surface for {Wire}{(Label.Length > 0 ? $" ({Label})" : "")} · Esc or CANCEL stops it"
        : "";

    /// <summary>The Eye's desk node: learn armed, or the surfaces' map in one line; "" with no surface in the show.</summary>
    public string EyeWords
    {
        get
        {
            if (Armed) return $"learning {Wire} — press a control";
            var surfaces = Surfaces;
            if (surfaces.Count == 0) return "";
            var bound = Bindings.Count;
            var names = string.Join(", ", surfaces.Select(d => d.Name));
            return bound == 0 ? $"no control bound yet on {names}" : $"{bound} control{(bound == 1 ? "" : "s")} bound on {names}";
        }
    }

    /// <summary>STATE's midi row and the MIDI query: learn, the surfaces and every binding.</summary>
    public object Row()
    {
        var open = _s.Devices.OpenMidiSurfaces;
        return new
        {
            learning = Armed,
            wire = Wire,
            label = Label,
            since = Armed ? Math.Round((_s.Clock.UtcNow - SinceUtc).TotalSeconds, 1) : 0,
            surfaces = Surfaces.Select(d => new { name = d.Name, port = d.Port, open = open.Any(o => ReferenceEquals(o, d) || o.Id == d.Id), bound = MidiBindings.Count(d) }).ToArray(),
            bindings = Bindings.Select(b => new { device = b.Device, control = b.Control, match = b.Match, command = b.Command }).ToArray(),
            count = Bindings.Count,
            last = LastLearned,
            words = EyeWords,
        };
    }

    public string Json() => JsonSerializer.Serialize(Row());
}
