using System.Globalization;
using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Round 74: the navigator's verbs and the build verbs — the desk's pages turned from a deck,
/// and the show file edited from one (a look saved, updated or deleted; a cue added or deleted;
/// a preset saved; a lower third made) — through the one action layer, so a deck's key, the
/// wire, a MIDI pad and a menu line are one path with one receipt, journaled like everything else.
/// </summary>
public sealed partial class ShowActions
{
    /// <summary>The id of the cue CUE ADD last made, so the desk can select it and open its column.</summary>
    public string LastAddedCueId { get; private set; } = "";

    private ActionResult? RunNav(ShowAction a) => a.Kind switch
    {
        ShowActionKind.NavPage => Nav(n => n.Go(a.Target, a.Value)),
        ShowActionKind.NavBack => Nav(n => n.Back()),
        ShowActionKind.NavHome => Nav(n => n.Home()),
        ShowActionKind.NavSettings => Nav(n => n.Settings(a.Value)),
        ShowActionKind.LookSave => SaveLook(a.Value),
        ShowActionKind.LookUpdate => UpdateLook(a.Value),
        ShowActionKind.LookDelete => DeleteLook(a.Value),
        ShowActionKind.CueAdd => AddCue(a.Value),
        ShowActionKind.CueDelete => DeleteCue(a.Value),
        ShowActionKind.PresetSave => SavePreset(a.Value),
        ShowActionKind.LowerThirdNew => NewLowerThird(a.Target, a.Value),
        _ => null,
    };

    private ActionResult Nav(Func<IDeskNavigator, ActionResult> go)
        => _s.Navigator is { } navigator ? go(navigator) : ActionResult.Refused("No desk here — a node has no pages to turn.");

    /// <summary>What a capture reads: the preview while EDIT SAFE is open, the picture on air otherwise — the same as the Looks page's SAVE.</summary>
    private string CaptureWords => _s.Sandbox.Active ? "the preview" : "the picture on air";

    private ActionResult SaveLook(string value)
    {
        var name = value.Trim();
        if (name.Length == 0) return ActionResult.Refused("A name: LOOK SAVE <name>.");
        var json = LookService.Capture(State);
        // "Walk-in" and "walk-in" are the same look: the resolver is case-insensitive, so the save is too.
        var existing = LookService.Find(State, name);
        if (existing is not null)
        {
            existing.Json = json;
            return ActionResult.Done($"Look '{existing.Name}' updated from {CaptureWords}.");
        }
        State.LooksAndCues.Looks.Add(new LookConfig { Name = name, Json = json });
        return ActionResult.Done($"Look '{name}' saved from {CaptureWords} — {State.LooksAndCues.Looks.Count.ToString(CultureInfo.InvariantCulture)} in the show.");
    }

    private ActionResult UpdateLook(string value)
    {
        var name = value.Trim();
        var look = name.Length > 0 ? LookService.Find(State, name) : _s.LookTally.OnAir();
        if (look is null) return ActionResult.Refused(name.Length > 0 ? $"No look '{name}'." : "No look is on air — LOOK UPDATE <name> names one.");
        look.Json = LookService.Capture(State);
        return ActionResult.Done($"Look '{look.Name}' updated from {CaptureWords}.");
    }

    private ActionResult DeleteLook(string value)
    {
        var name = value.Trim();
        var look = LookService.Find(State, name);
        if (look is null) return ActionResult.Refused($"No look '{name}'.");
        // Orphaned references fail silently at show time: refuse and say what points here.
        var refs = LookService.References(State, look);
        if (refs.Count > 0) return ActionResult.Refused($"'{look.Name}' is still used by {string.Join(", ", refs)} — change those first.");
        State.LooksAndCues.Looks.Remove(look);
        return ActionResult.Done($"Look '{look.Name}' deleted — {State.LooksAndCues.Looks.Count.ToString(CultureInfo.InvariantCulture)} left in the show.");
    }

    private ActionResult AddCue(string value)
    {
        // After the standby, or at the end: the same arithmetic as the Cues page's + CUE, on the caller's stack.
        var stack = CueStacks.Caller(State);
        var standby = _s.CueStack.StandbyCue;
        var index = standby is null ? stack.Cues.Count : stack.Cues.IndexOf(standby) + 1;
        if (index < 0) index = stack.Cues.Count;
        var previous = index > 0 ? stack.Cues[index - 1].Number : null;
        var next = index < stack.Cues.Count ? stack.Cues[index].Number : null;
        var cue = new RunCueConfig { Number = CueNumber.Between(previous, next) };
        if (value.Trim().Length > 0) cue.Name = value.Trim();
        stack.Cues.Insert(index, cue);
        LastAddedCueId = cue.Id;
        return ActionResult.Done($"Cue {cue.Number} '{cue.Name}' added {(standby is null ? "at the end of the stack" : $"after standby {standby.Number}")}.");
    }

    private ActionResult DeleteCue(string value)
    {
        if (_s.CueStack.Armed) return ActionResult.Refused("The cue stack is armed — disarm it before deleting cues.");
        var word = value.Trim();
        var found = CueStacks.FindCueByWord(State, word);
        if (found is null) return ActionResult.Refused($"No cue '{word}'.");
        var (stack, cue) = found.Value;
        var index = stack.Cues.IndexOf(cue);
        if (_s.CueStack.StandbyCue?.Id == cue.Id)
        {
            // Standby moves to the neighbour, so GO never points at a cue that is gone.
            var neighbour = index + 1 < stack.Cues.Count ? stack.Cues[index + 1] : index > 0 ? stack.Cues[index - 1] : null;
            _s.CueStack.Standby(neighbour?.Id);
        }
        stack.Cues.RemoveAt(index);
        return ActionResult.Done($"Cue {cue.Number} '{cue.Name}' deleted from {stack.Name}.");
    }

    private ActionResult SavePreset(string value)
    {
        var name = value.Trim();
        if (name.Length == 0) return ActionResult.Refused("A name: PRESET SAVE <name>.");
        // The editing target's picture — the one under the editors — not the programme's by default: a deck
        // saving "what I am looking at" means the tile the desk is on.
        var picture = _s.Navigator?.EditingPicture ?? State.Pattern;
        _s.Store.SavePreset(name, picture);
        return ActionResult.Done($"Preset '{name}' saved to the Library — PRESET {name} recalls it.");
    }

    private ActionResult NewLowerThird(string preset, string value)
    {
        var name = value.Trim();
        if (name.Length == 0) return ActionResult.Refused("A name: LT NEW <name> [FROM <preset>].");
        var choices = LowerThirdPresets.Names.Concat(new[] { "Blank" }).ToList();
        var chosen = preset.Trim().Length == 0 ? "Clean" : choices.FirstOrDefault(n => n.Equals(preset.Trim(), StringComparison.OrdinalIgnoreCase));
        if (chosen is null) return ActionResult.Refused($"No lower-thirds preset '{preset.Trim()}' — {string.Join(", ", choices)}.");
        var design = _s.Navigator is { } navigator
            ? navigator.NewLowerThird(chosen, name)
            : new LowerThirdDesigner(State.LowerThirds).New(chosen, name);
        return ActionResult.Done($"Lower third '{design.Name}' made from {chosen}{(design.Name == name ? "" : $" (the name '{name}' was taken)")}.");
    }

    /// <summary>
    /// The action as a deck should read it: the desk's own buttons name looks, designs, people,
    /// tracks, music, stingers and cues by id (stable across a rename) and screens by target id, and
    /// a line recorded from those would work but read as a hash. The recorder writes the operator's
    /// words instead — the look's name, the screen's number, the cue's number — which the parser
    /// resolves the same way. A word that resolves to nothing is kept as it was.
    /// </summary>
    public ShowAction Readable(ShowAction a)
    {
        string Look(string word) => word.Length == 0 ? word : ResolveLook(word, out _)?.Name ?? word;
        string Design(string word) => word.Length == 0 ? word : State.LowerThirds.Find(word)?.Name ?? word;
        string Person(string word) => word.Length == 0 ? word : State.LowerThirds.FindEntry(word)?.Name ?? word;
        string Screen(string word)
        {
            if (word.Length == 0 || ContentTargets.IsProgramTarget(word)) return word;
            var number = DeskMenuFacts.Number(_s, word);
            return number.Length > 0 ? number : word;
        }
        string Cue(string word) => word.Length == 0 ? word : CueStacks.FindCue(State, word)?.Cue.Number ?? word;
        var spec = ActionSpec.For(a.Kind);
        if (spec.Target is TargetKind.Screen or TargetKind.Stage) a = a with { Target = Screen(a.Target) };
        return a.Kind switch
        {
            ShowActionKind.ApplyLook or ShowActionKind.ApplyLookToPreview => a with { Target = Look(a.Target) },
            ShowActionKind.ScreenLook or ShowActionKind.ScreenStageLook => a with { Value = Look(a.Value) },
            ShowActionKind.LowerThirdShow or ShowActionKind.LowerThirdPreview => a with { Target = Design(a.Target), Value = Person(a.Value) },
            ShowActionKind.SpotifyPlay => a with { Target = a.Target.Length == 0 ? "" : SpotifyLibrary.Find(State, a.Target)?.DisplayName ?? a.Target },
            ShowActionKind.AudioPlay => a with { Target = State.AudioPlayer.Items.FirstOrDefault(t => t.Id == a.Target)?.DisplayName ?? a.Target },
            ShowActionKind.StingerFire => a with { Target = StingerLibrary.Find(State, a.Target)?.DisplayName ?? a.Target },
            ShowActionKind.CueGo or ShowActionKind.CueFire => a with { Target = Cue(a.Target) },
            ShowActionKind.CueStandby when a.Target is not ("next" or "prev") => a with { Target = Cue(a.Target) },
            _ => a,
        };
    }

    // ---- what the wire and STATE read --------------------------------------------------------

    /// <summary>Where the desk is, in words, for the Eye's desk node; "" on a node.</summary>
    public string NavWords() => _s.Navigator?.Facts().Words ?? "";

    /// <summary>STATE's nav row: the page, the rail, the column, the page before, the selection, and each deck's whereabouts; null on a node.</summary>
    public object? NavRow()
    {
        var f = _s.Navigator?.Facts();
        return f is null ? null : new
        {
            page = f.Page,
            rail = f.Rail,
            hue = f.Hue,
            run = f.Run,
            settings = new { open = f.SettingsOpen, key = f.SettingsKey, title = f.SettingsTitle, identity = f.SettingsIdentity },
            back = f.Back,
            selection = f.Selection,
            words = f.Words,
            decks = _s.Control.Decks.Select(d => new { name = d.Name, where = d.Where, recording = d.Recording }).ToArray(),
        };
    }

    /// <summary>NAV on the wire: the rails and their pages, every page with its rail and hue, and where the desk is — what a deck lays its navigator out from.</summary>
    public string NavJson() => JsonUtil.SerializeCompact(new
    {
        protocol = ControlProtocol.DescriptorVersion,
        rails = DeskPages.Rails.Select(r => new { id = r.Id, label = r.Label, hue = r.Hue, hint = r.Hint, pages = DeskPages.Of(r.Id).Select(p => p.Header).ToArray() }).ToArray(),
        pages = DeskPages.All.Select(p => new { header = p.Header, rail = p.Rail, hue = p.Hue, room = p.Rail == "Admin", settings = p.Header is "Cues" or "Screens" or "Lower thirds" or "Machine" or "Audio" }).ToArray(),
        desk = NavRow(),
    });
}
