using System.Globalization;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.Core.Menus;

/// <summary>
/// The right-click menus of the desk, built from facts: a tile of the wall, the programme, the
/// PREVIEW pane, a cue, a look, a lower-third design, a person of the library, a layer, an
/// overlay, the countdown. One builder per thing, and the same three promises on every menu:
/// what changes a picture lands in the preview (EDIT SAFE opens; CUT or TAKE puts it up), what
/// cannot be chosen says why, and every entry the wire can say carries its line.
/// </summary>
public static class DeskMenus
{
    public const string PreviewNote = "Lands in the preview — the audience sees nothing until CUT or TAKE.";
    public const string LiveNote = "Changes the desk or the air now.";
    public const string StackNote = "Edits the running order, never the picture.";
    private const string NeedKey = "Save an Anthropic API key on the Assistant page first.";

    // ---- a tile: a screen, a canvas, or the programme ----------------------------------

    public static DeskMenu Screen(DeskFacts d, ScreenFacts s)
    {
        if (s.IsProgram) return Program(d, s);
        var target = s.TargetId;
        var n = s.WireTarget;
        string W(string words) => n.Length > 0 ? $"SCREEN {n} {words}" : "";
        var look = d.LookOnAirName;

        var preview = new List<MenuEntry>
        {
            new("stage.reset", d.HasLookOnAir ? $"Reset to '{look}' as the look had it" : "Reset to the look on air", MenuScope.Preview, MenuTone.Preview)
            {
                Detail = "The look's own picture for this screen, back on its PVW",
                Wire = W("PVW RESET"),
                Action = new ShowAction(ShowActionKind.ScreenStageReset, target),
                Because = !d.HasLookOnAir ? "No look is on air to reset to — the picture the room is watching was never a look."
                    : !s.OffLook ? $"It shows '{look}' exactly as the look asked." : "",
            },
            LookDrawer("stage.look", d, id => new ShowAction(ShowActionKind.ScreenStageLook, target, id), name => W($"PVW LOOK {name}"), "The look's picture for this screen, on its PVW"),
            PresetDrawer("stage.preset", d, name => new ShowAction(ShowActionKind.ScreenStagePreset, target, name), name => W($"PVW PRESET {name}")),
            KindDrawer("stage.kind", d, s.ShowingKind, word => new ShowAction(ShowActionKind.ScreenStagePattern, target, word), word => W($"PVW PATTERN {word}")),
            new("stage.program", "Follow the programme", MenuScope.Preview, MenuTone.Preview)
            {
                Detail = "Its own picture dropped on the PVW — TAKE makes it follow again",
                Wire = W("PVW PROGRAM"),
                Action = new ShowAction(ShowActionKind.ScreenStageProgram, target),
                Because = s.IsMirror ? "A repeater draws its source's picture and has none of its own." : !s.Own && !s.Staged ? "It follows the programme already." : "",
            },
            new("to.preview", "Its air picture into the preview (→ PVW)", MenuScope.Preview, MenuTone.Preview)
            {
                Detail = "To edit; the tile stays focused, so SEND stages it back here and a FOCUSED take puts it up",
                Wire = W("PVW"),
                Action = new ShowAction(ShowActionKind.ScreenToPreview, target),
            },
        };

        var tile = new List<MenuEntry>
        {
            new("tile.lock", s.Locked ? "Unlock — follow looks and cues again" : "Lock — keep its picture", MenuScope.Live, MenuTone.Tile)
            {
                Detail = "Through looks, cues, TAKE ALL and stingers (a confidence or info screen); saved with the show",
                Wire = n.Length > 0 ? $"LOCK {n} {(s.Locked ? "OFF" : "ON")}" : "",
                Action = new ShowAction(s.Locked ? ShowActionKind.ScreenUnlock : ShowActionKind.ScreenLock, target),
                IsOn = s.Locked,
            },
            new("tile.arm", s.Armed ? "Armed for the next CUT / TAKE" : "Held through the next CUT / TAKE", MenuScope.Live, MenuTone.Tile)
            {
                Detail = s.Armed ? "Choose to hold it: it keeps the picture the audience sees" : "Choose to arm it: the next CUT / TAKE changes it",
                Edit = "tile.arm",
                IsOn = s.Armed,
                Because = s.Locked ? "A locked tile keeps its picture whatever is armed — unlock it first." : "",
            },
            GroupDrawer(s, target, W),
            new("tile.out", s.Enabled ? "Output on" : "Output off", MenuScope.Live, MenuTone.Tile)
            {
                Detail = "The screen's output window, live",
                Wire = W(s.Enabled ? "OFF" : "ON"),
                Action = new ShowAction(s.Enabled ? ShowActionKind.ScreenOff : ShowActionKind.ScreenOn, target),
                IsOn = s.Enabled,
            },
            new("tile.mon", "Draw its miniatures (MON)", MenuScope.Live, MenuTone.Tile)
            {
                Detail = "Off saves GPU on a big rig; changes nothing on air",
                Edit = "tile.mon",
                IsOn = s.Monitored,
            },
            new("tile.collapse", s.Collapsed ? "Open the tile again" : "Collapse to its title bar", MenuScope.Live, MenuTone.Tile)
            {
                Detail = "The show remembers it",
                Edit = "tile.collapse",
            },
        };

        var go = new List<MenuEntry>
        {
            Go("go.screens", "Screens page — role, follows cues, mirror of", "Screens", target),
            Go("go.pattern", "Pattern page — the editors on this picture", "Pattern", target),
            Go("go.looks", "Looks page", "Looks"),
            Go("go.multiview", "Multiview page", "Multiview"),
        };

        var state = s.OnAir ? "on air" : "not on air";
        var picture = s.IsMirror ? "a repeater of another target" : s.Own ? $"its own picture{(s.ShowingKind.Length > 0 ? $" ({s.ShowingKind})" : "")}" : "the programme";
        var offLook = s.OffLook && d.HasLookOnAir ? $" It has gone its own way since '{look}' was recalled." : "";
        var question = $"Screen {(n.Length > 0 ? n + " " : "")}'{s.Title}' is {state}, showing {picture}{(s.Locked ? ", locked" : "")}{(s.Staged ? ", with a picture staged on its PVW" : "")}.{offLook} The look on air is {(d.HasLookOnAir ? $"'{look}'" : "not a saved look")}. What would you check on this screen, and what would you put on it next?";

        var subtitle = string.Join(" · ", new[]
        {
            s.OnAir ? "ON AIR" : "off air",
            s.IsMirror ? "repeater" : s.Own ? "its own picture" : "follows the programme",
            s.Locked ? "LOCKED" : "",
            s.Staged ? "staged — CUT or TAKE puts it up" : "",
            s.OffLook && d.HasLookOnAir ? $"off the look '{look}'" : "",
            s.RoleBadge,
        }.Where(w => w.Length > 0));

        return new DeskMenu("screen", target, s.Title, subtitle, s.OffLook ? MenuTone.Warn : s.OnAir ? MenuTone.Live : MenuTone.Plain, new[]
        {
            new MenuGroup("IN THE PREVIEW", MenuTone.Preview, preview) { Note = PreviewNote },
            new MenuGroup("TO AIR", MenuTone.Live, ScreenTakeEntries(d, s, target, W).Append(NextTransitionDrawer(d)).ToList()) { Note = "This screen alone: it becomes its own picture (OWN lights up) and every other screen stays." },
            new MenuGroup("THIS TILE", MenuTone.Tile, tile) { Note = "The tile's own switches — live, as its buttons are." },
            new MenuGroup("GO TO", MenuTone.Go, go),
            new MenuGroup("ASK", MenuTone.Ask, new[] { Ask("ask.screen", "Ask the assistant about this screen", question, d) }),
        });
    }

    /// <summary>The PGM tile and the PROGRAM strip: what is on air, what the preview can take from it, TAKE and CUT.</summary>
    public static DeskMenu Program(DeskFacts d, ScreenFacts? s = null)
    {
        var look = d.LookOnAirName;
        var subtitle = d.HasLookOnAir ? $"On air: '{look}'{(d.LookEdited ? " — changed since it was recalled" : "")}" : "On air: a picture nobody saved as a look";
        var question = $"What is on air is {(d.HasLookOnAir ? $"the look '{look}'{(d.LookEdited ? ", changed since it was recalled" : "")}" : "a picture that is not a saved look")}. {(d.SandboxOpen ? "EDIT SAFE is open, so the preview holds an edit waiting for a TAKE." : "EDIT SAFE is off, so edits go straight to the screens.")} What would you check before the next TAKE?";
        return new DeskMenu("program", "", s?.Title ?? "PGM · the programme", subtitle, MenuTone.Live, new[]
        {
            new MenuGroup("IN THE PREVIEW", MenuTone.Preview, ProgramPreview(d)) { Note = PreviewNote },
            new MenuGroup("TO AIR", MenuTone.Live, TakeEntries(d).Append(NextTransitionDrawer(d)).ToList()) { Note = "The operator's own press — the same as the TAKE and CUT keys." },
            new MenuGroup("THIS TILE", MenuTone.Tile, new[]
            {
                new MenuEntry("tile.mon", "Draw its miniatures (MON)", MenuScope.Live, MenuTone.Tile) { Detail = "Off saves GPU on a big rig; changes nothing on air", Edit = "tile.mon", IsOn = s?.Monitored ?? true },
                new MenuEntry("tile.collapse", s?.Collapsed == true ? "Open the tile again" : "Collapse to its title bar", MenuScope.Live, MenuTone.Tile) { Detail = "The show remembers it", Edit = "tile.collapse" },
            }),
            new MenuGroup("GO TO", MenuTone.Go, new[]
            {
                Go("go.pattern", "Pattern page — the editors", "Pattern"),
                Go("go.looks", "Looks page", "Looks"),
                Go("go.layers", "Layers page", "Layers"),
                Go("go.overlays", "Overlays page", "Overlays"),
            }),
            new MenuGroup("ASK", MenuTone.Ask, new[] { Ask("ask.program", "Ask the assistant what is on air and what is waiting", question, d) }),
        });
    }

    /// <summary>The PREVIEW strip: the picture being built, EDIT SAFE, and what the next TAKE puts up.</summary>
    public static DeskMenu Preview(DeskFacts d)
    {
        var entries = new List<MenuEntry>(ProgramPreview(d))
        {
            new(d.SandboxOpen ? "preview.discard" : "preview.open", d.SandboxOpen ? "Discard the preview — back to what is on air" : "Open EDIT SAFE", MenuScope.Live, MenuTone.Preview)
            {
                Detail = d.SandboxOpen ? "The outputs never notice; the edit is gone" : "Edits build here without going live until CUT or TAKE",
                Edit = d.SandboxOpen ? "preview.discard" : "preview.open",
            },
        };
        var question = d.SandboxOpen
            ? $"EDIT SAFE is open and the preview holds an edit; the look on air is {(d.HasLookOnAir ? $"'{d.LookOnAirName}'" : "not a saved look")}. What should I check in the preview before I TAKE?"
            : "EDIT SAFE is off, so every edit goes straight to the screens. When should I open it, and what does a safe workflow look like from here?";
        return new DeskMenu("preview", "", "PREVIEW", d.SandboxOpen ? "EDIT SAFE — edits stay here until CUT or TAKE" : "LIVE — every edit goes straight to the screens", MenuTone.Preview, new[]
        {
            SourceGroup(d),
            new MenuGroup("IN THE PREVIEW", MenuTone.Preview, entries) { Note = PreviewNote },
            new MenuGroup("TO AIR", MenuTone.Live, TakeEntries(d).Append(NextTransitionDrawer(d)).ToList()) { Note = "The operator's own press — the same as the TAKE and CUT keys." },
            new MenuGroup("GO TO", MenuTone.Go, new[]
            {
                Go("go.pattern", "Pattern page — the editors", "Pattern"),
                Go("go.looks", "Looks page — save this as a look", "Looks"),
                Go("go.layers", "Layers page", "Layers"),
                Go("go.overlays", "Overlays page", "Overlays"),
                Go("go.lowerthirds", "Lower thirds page", "Lower thirds"),
            }),
            new MenuGroup("ASK", MenuTone.Ask, new[] { Ask("ask.preview", "Ask the assistant about the preview", question, d) }),
        });
    }

    /// <summary>
    /// SOURCE (round 63): what the preview's picture is — a still, a clip, the playlist, a feed, a
    /// capture device, a web page, a deck, the arcade — and a still or clip of the library straight
    /// in. A right-click on the picture itself asked for it; the Pattern drawer beside it changes the
    /// kind of picture, this one changes what a media picture shows.
    /// </summary>
    private static MenuGroup SourceGroup(DeskFacts d)
    {
        var current = Enum.TryParse<MediaSource>(d.PreviewSource, true, out var now) ? now : (MediaSource?)null;
        var sources = Enum.GetValues<MediaSource>().Select(s => new MenuEntry($"media.source:{s}", Capital(PreviewEdits.MediaSourceWords(s)), MenuScope.Preview, MenuTone.Preview)
        {
            Edit = $"media.source:{s}",
            IsOn = current == s,
        }).ToList();
        var media = d.Media.Select(m => new MenuEntry($"media.pick:{m.Id}", m.Name, MenuScope.Preview, MenuTone.Preview)
        {
            Detail = m.IsVideo ? "a clip" : "a still",
            Edit = $"media.pick:{m.Id}",
        }).ToList();
        return new MenuGroup("SOURCE", MenuTone.Preview, new[]
        {
            new MenuEntry("media.source", current is { } c ? $"Media source — {PreviewEdits.MediaSourceWords(c)}" : "Media source", MenuScope.Preview, MenuTone.Preview)
            {
                Detail = "What the picture shows: a still, a clip, the playlist, an NDI feed, a capture device, a web page, a deck, the arcade",
                Children = sources,
            },
            new MenuEntry("media.pick", "Pick from the library", MenuScope.Preview, MenuTone.Preview)
            {
                Detail = "A still or a clip of the media library, full frame",
                Children = media,
                Because = d.Media.Count == 0 ? "The library is empty — add pictures on the Media page." : "",
            },
        }) { Note = "The picture becomes a media picture with that source; its settings are on the Media page." };
    }

    private static List<MenuEntry> ProgramPreview(DeskFacts d)
    {
        var look = d.LookOnAirName;
        return new List<MenuEntry>
        {
            new("stage.reset", d.HasLookOnAir ? $"Reset the preview to '{look}'" : "Reset the preview to the look on air", MenuScope.Preview, MenuTone.Preview)
            {
                Detail = "The whole look as it was saved — pattern, overlays, every screen — back into the preview",
                Wire = "PVW RESET",
                Action = new ShowAction(ShowActionKind.ScreenStageReset, ""),
                Because = !d.HasLookOnAir ? "No look is on air to reset to — the picture the room is watching was never a look." : "",
            },
            LookDrawer("stage.look", d, id => new ShowAction(ShowActionKind.ApplyLookToPreview, id), name => $"PVW LOOK {name}", "The whole look into the preview"),
            PresetDrawer("stage.preset", d, name => new ShowAction(ShowActionKind.ScreenStagePreset, "", name), name => $"PVW PRESET {name}"),
            KindDrawer("stage.kind", d, "", word => new ShowAction(ShowActionKind.ScreenStagePattern, "", word), word => $"PVW PATTERN {word}"),
            new("to.preview", "What is on air into the preview (→ PVW)", MenuScope.Preview, MenuTone.Preview)
            {
                Detail = "The programme the room is watching, into the preview to change and take again",
                Wire = "PVW",
                Action = new ShowAction(ShowActionKind.ScreenToPreview, ""),
            },
        };
    }

    /// <summary>
    /// The tile's own CUT and TAKE (round 63): the preview to this one screen, as its own picture —
    /// OWN lights up on it, the programme and every other screen stay. Live, because they are the
    /// operator's press on the tile, exactly as the wall's keys are.
    /// </summary>
    private static List<MenuEntry> ScreenTakeEntries(DeskFacts d, ScreenFacts s, string target, Func<string, string> w)
    {
        var because = s.IsMirror ? "A repeater draws its source's picture and has none of its own."
            : !d.SandboxOpen ? "EDIT SAFE is off — the preview is the air; there is nothing to take." : "";
        return new List<MenuEntry>
        {
            new("screen.take", "TAKE — the preview to this screen alone, with the transition", MenuScope.Live, MenuTone.Live)
            {
                Detail = "It becomes the screen's own picture (OWN lights up); every other screen stays",
                Wire = w("TAKE"),
                Action = new ShowAction(ShowActionKind.ScreenTake, target),
                Because = because,
            },
            new("screen.cut", "CUT — the same, instantly", MenuScope.Live, MenuTone.Live)
            {
                Wire = w("CUT"),
                Action = new ShowAction(ShowActionKind.ScreenCut, target),
                Because = because,
            },
        };
    }

    /// <summary>
    /// THIS TILE → Group (round 67.7): the group the screen is in — its role — with the others to choose.
    /// Main follows looks, cues and TAKE; Confidence and Info keep their own picture (the choice locks the
    /// tile, as the Screens page does); Repeater draws another target's picture and needs a source chosen
    /// on the Screens page. A canvas sets every screen in it. The lettered canvas (GROUP A) is geometry —
    /// read in the tile's title, never chosen here. The same verb as SCREEN n ROLE / GROUP on the wire.
    /// </summary>
    private static MenuEntry GroupDrawer(ScreenFacts s, string target, Func<string, string> W)
    {
        var current = s.Group;
        MenuEntry Choice(string word, string text, string detail, string because = "") => new($"tile.group:{word}", text, MenuScope.Live, MenuTone.Tile)
        {
            Detail = detail,
            Wire = W($"GROUP {word}"),
            Action = new ShowAction(ShowActionKind.ScreenRole, target, word),
            IsOn = !s.GroupsMixed && current.Equals(word, StringComparison.OrdinalIgnoreCase),
            Because = because,
        };
        var name = s.GroupsMixed ? "mixed — its screens differ" : ScreenRoles.Parse(current) is { } role ? role.ToString() : "not set";
        var repeater = s.IsCanvas ? "A canvas cannot repeat — make one screen a repeater on the Screens page."
            : s.MirrorSource.Length == 0 && !current.Equals("repeater", StringComparison.OrdinalIgnoreCase) ? "Choose the screen it repeats on the Screens page first (Mirror of) — a repeater needs a source."
            : "";
        return new MenuEntry("tile.group", $"Group — {name}", MenuScope.Live, MenuTone.Tile)
        {
            Detail = s.IsCanvas ? "What these screens are for — the choice sets every screen of the canvas" : "What the screen is for — Main follows the programme; Confidence and Info keep their own picture",
            Children = new[]
            {
                Choice("main", "Main — the audience's picture", "Follows looks, cues and TAKE; the tile unlocks"),
                Choice("confidence", "Confidence — a stage monitor", "Its own picture, left alone by looks and cues; the tile locks"),
                Choice("info", "Info — a foyer or info screen", "Its own picture, left alone by looks and cues; the tile locks"),
                Choice("repeater", "Repeater — a copy of another screen", s.MirrorSource.Length > 0 ? $"Repeats {s.MirrorSource}" : "Draws another target's picture", repeater),
            },
        };
    }

    /// <summary>
    /// NEXT TRANSITION (round 67.6): what the next TAKE alone arrives by — a kind, a rate, a video sting
    /// of the library, or the show's own again. One shot: the show's transition never moves. The same
    /// drawer on every TAKE the desk has — the wall's, a tile's, the PGM and PREVIEW strips' — and TAKE NEXT on the wire.
    /// </summary>
    private static MenuEntry NextTransitionDrawer(DeskFacts d)
    {
        // The pending wire words split into the kind ("wipe left", "cut", "STING Whoosh") and the rate ("800"):
        // a kind row is on when the kind matches whatever the rate, a rate row when the rate matches whatever the kind.
        var current = d.NextTakeWire;
        var isSting = current.StartsWith("STING", StringComparison.OrdinalIgnoreCase);
        var tokens = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentKind = isSting ? current : string.Join(' ', tokens.Where(t => !t.All(char.IsDigit)));
        var currentRate = isSting ? "" : tokens.FirstOrDefault(t => t.All(char.IsDigit)) ?? "";
        bool KindOn(string words) => currentKind.Length > 0 && words.Equals(currentKind, StringComparison.OrdinalIgnoreCase);
        bool RateOn(int ms) => currentRate == ms.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MenuEntry Choice(string id, string text, string words, string detail = "", bool on = false) => new($"take.next:{id}", text, MenuScope.Live, MenuTone.Live)
        {
            Detail = detail,
            Wire = "TAKE NEXT " + words,
            Action = new ShowAction(ShowActionKind.NextTransition, "", words),
            IsOn = on,
        };
        // A rate keeps the kind that is on — "wipe left" pending and 0.5 s pressed is "wipe left 500", never a bare rate.
        var rateKind = !isSting && !currentKind.Equals("cut", StringComparison.OrdinalIgnoreCase) ? currentKind : "";
        string Rate(int ms) => (rateKind.Length > 0 ? rateKind + " " : "") + ms.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MenuEntry Kind(string id, string text, string words, string detail = "") => Choice(id, text, words, detail, KindOn(words));
        var children = new List<MenuEntry>
        {
            Choice("default", "The show's own transition", "CLEAR", $"As set on the Outputs page — {d.TransitionDefault}", on: current.Length == 0),
            Kind("dissolve", "Dissolve", "dissolve", "A crossfade at the show's rate"),
            Kind("dip", "Dip", "dip", "Through the dip colour"),
            Kind("wipe", "Wipe", "wipe", "The show's direction"),
            Kind("wipe-left", "Wipe left", "wipe left"),
            Kind("wipe-right", "Wipe right", "wipe right"),
            Kind("push", "Push", "push", "The new picture pushes the old one off"),
            Kind("brand", "Brand stinger", "brand", "The brand kit sweeps over the cut, the logo at its peak"),
            Kind("reactive", "Reactive", "reactive", "The show's reactive scene wipes"),
            Kind("cut", "Cut", "cut", "No transition — the TAKE key cuts this once"),
            Choice("rate-200", "0.2 s", Rate(200), "The rate for the next take alone", RateOn(200)),
            Choice("rate-500", "0.5 s", Rate(500), on: RateOn(500)),
            Choice("rate-1000", "1 s", Rate(1000), on: RateOn(1000)),
            Choice("rate-2000", "2 s", Rate(2000), on: RateOn(2000)),
        };
        foreach (var sting in d.Stings)
        {
            var words = $"STING {sting.Name}";
            children.Add(Choice($"sting:{sting.Id}", $"Sting — {sting.Name}", words, "The clip covers the screens and the preview lands when it ends", KindOn(words)));
        }
        return new MenuEntry("take.next", d.NextTake.Length > 0 ? $"Next take — {d.NextTake} (one shot)" : "Next take — the show's transition", MenuScope.Live, MenuTone.Live)
        {
            Detail = "How the next TAKE alone arrives; the show's transition on the Outputs page never moves",
            Children = children,
        };
    }

    private static List<MenuEntry> TakeEntries(DeskFacts d) => new()
    {
        new("take", "TAKE — the preview to every armed screen, with the transition", MenuScope.Live, MenuTone.Live)
        {
            Action = new ShowAction(ShowActionKind.Take),
            Because = !d.SandboxOpen ? "EDIT SAFE is off — the preview is the air; there is nothing to take." : "",
        },
        new("cut", "CUT — the same, instantly", MenuScope.Live, MenuTone.Live)
        {
            Action = new ShowAction(ShowActionKind.Cut),
            Because = !d.SandboxOpen ? "EDIT SAFE is off — the preview is the air; there is nothing to cut." : "",
        },
    };

    // ---- a cue ------------------------------------------------------------------------

    public static DeskMenu Cue(DeskFacts d, CueFacts c)
    {
        var who = $"{c.Number} · {c.Name}";
        var run = new List<MenuEntry>
        {
            new("cue.standby", "Standby here", MenuScope.Live, MenuTone.Plain)
            {
                Detail = "Changes nothing on air — the next GO fires it",
                Wire = $"CUE STANDBY {c.Number}",
                Edit = "cue.standby",
                Because = c.IsStandby ? "It is on standby now." : c.IsClicker ? "The clicker list has no standby — NEXT and PREV step it." : "",
            },
            new("cue.go", "GO this cue now", MenuScope.Live, MenuTone.Live)
            {
                Detail = "Fires it wherever the standby is",
                Edit = "cue.go",
                Because = c.IsBroken ? c.Problem : !c.Enabled ? "It is skipped — put it back in the run first." : "",
            },
            new("cue.note", "Note…", MenuScope.Live, MenuTone.Plain) { Detail = "The caller's in-show note, typed on the row and saved with the show", Edit = "cue.note" },
            new("cue.skip", c.Enabled ? "Skip this cue" : "Back in the run", MenuScope.Stack, MenuTone.Stack) { Detail = c.Enabled ? "GO passes over it" : "GO fires it again", Edit = "cue.skip", IsOn = !c.Enabled },
        };

        var lookChildren = d.Looks.Select(l => new MenuEntry($"cue.look:{l.Id}", l.Name, MenuScope.Stack, MenuTone.Stack)
        {
            Detail = Join(l.Hotkey > 0 ? $"F{l.Hotkey}" : "", l.OnAir ? "on air" : ""),
            Edit = $"cue.look:{l.Id}",
            IsOn = l.Id == c.LookId,
        }).ToList();

        var transitionChildren = CueMenuEdits.Transitions.Select(t => new MenuEntry($"cue.transition:{t.Words}", t.Label, MenuScope.Stack, MenuTone.Stack)
        {
            Detail = t.Words.Length == 0 ? (d.TransitionDefault.Length > 0 ? d.TransitionDefault : "As the Looks page sets it") : "",
            Edit = $"cue.transition:{t.Words}",
            IsOn = string.Equals(t.Words, c.Transition, StringComparison.OrdinalIgnoreCase),
        }).ToList();

        var overlayChildren = CueMenuEdits.Overlays.Select(o => new MenuEntry($"cue.overlay:{o.On}", o.Label, MenuScope.Stack, MenuTone.Stack)
        {
            Detail = c.Overlays.Contains(o.On) ? "In with the GO — choose to take it out of the cue" : "Choose to bring it in with the GO",
            Edit = $"cue.overlay:{o.On}",
            IsOn = c.Overlays.Contains(o.On),
        }).ToList();
        overlayChildren.Add(new MenuEntry("cue.clean", "Clean picture — every overlay off", MenuScope.Stack, MenuTone.Stack)
        {
            Detail = "The clock, the message, the countdown, the logo, the PiP and the weather all off with the GO",
            Edit = "cue.clean",
            IsOn = c.CleanPicture,
        });

        var design = d.Designs.FirstOrDefault(x => x.Id == c.LowerThirdDesignId) ?? d.Designs.FirstOrDefault(x => x.IsDefault) ?? d.Designs.FirstOrDefault();
        var timingChildren = new List<MenuEntry>();
        if (design is not null)
        {
            foreach (var t in CueMenuEdits.LowerThirdTimings)
            {
                var outWord = t.Out is { } o ? o.ToString("0.#", CultureInfo.InvariantCulture) : "-";
                timingChildren.Add(new MenuEntry($"cue.lt:{design.Id}:{t.In.ToString("0.#", CultureInfo.InvariantCulture)}:{outWord}", t.Label, MenuScope.Stack, MenuTone.Stack)
                {
                    Detail = $"with '{design.Name}'{(design.IsDefault ? " (the default)" : "")}",
                    Edit = $"cue.lt:{design.Id}:{t.In.ToString("0.#", CultureInfo.InvariantCulture)}:{outWord}",
                    IsOn = c.HasLowerThird && c.LowerThirdDesignId == design.Id && Math.Abs(c.LowerThirdInAfter - t.In) < 0.01 && Same(c.LowerThirdOutAfter, t.Out),
                });
            }
            timingChildren.Add(new MenuEntry("cue.lt.remove", "No lower third in this cue", MenuScope.Stack, MenuTone.Stack)
            {
                Edit = "cue.lt.remove",
                Because = c.HasLowerThird ? "" : "It brings no lower third.",
            });
        }
        var designChildren = d.Designs.Select(x => new MenuEntry($"cue.lt.design:{x.Id}", (x.IsDefault ? "★ " : "") + x.Name, MenuScope.Stack, MenuTone.Stack)
        {
            Edit = $"cue.lt.design:{x.Id}",
            IsOn = x.Id == c.LowerThirdDesignId,
        }).ToList();

        var followChildren = CueMenuEdits.Follows.Select(f => new MenuEntry($"cue.follow:{(f.Seconds is { } s ? s.ToString(CultureInfo.InvariantCulture) : "-")}", f.Label, MenuScope.Stack, MenuTone.Stack)
        {
            Edit = $"cue.follow:{(f.Seconds is { } s2 ? s2.ToString(CultureInfo.InvariantCulture) : "-")}",
            IsOn = f.Seconds == c.FollowSeconds,
        }).ToList();

        var markChildren = Enum.GetValues<CueMark>().Select(m => new MenuEntry($"cue.mark:{m}", m == CueMark.None ? "Nothing" : m.ToString(), MenuScope.Stack, MenuTone.Stack)
        {
            Detail = m == CueMark.None ? "" : "The estimates count down to it",
            Edit = $"cue.mark:{m}",
            IsOn = m == c.Mark,
        }).ToList();

        var stack = new List<MenuEntry>
        {
            new("cue.look", c.HasLook ? $"Look — '{c.LookName}'" : "Look — none yet", MenuScope.Stack, MenuTone.Stack)
            {
                Detail = "Which look the cue recalls",
                Children = lookChildren,
                Because = d.Looks.Count == 0 ? "No looks yet — save one on the Looks page." : "",
            },
            new("cue.transition", $"Transition in — {TransitionLabel(c.Transition)}", MenuScope.Stack, MenuTone.Stack)
            {
                Detail = "How the look comes in when the cue fires",
                Children = transitionChildren,
                Because = c.HasLook ? "" : "Give the cue a look first — a transition belongs to its recall.",
            },
            new("cue.overlays", "Overlays", MenuScope.Stack, MenuTone.Stack)
            {
                Detail = c.Overlays.Count > 0 ? $"Brings in: {string.Join(", ", CueMenuEdits.Overlays.Where(o => c.Overlays.Contains(o.On)).Select(o => o.Label.ToLowerInvariant()))}" : "What the cue brings in or takes out with the GO",
                Children = overlayChildren,
            },
            new("cue.lt", c.HasLowerThird ? $"Lower third — '{c.LowerThirdName}', {CueMenuEdits.TimingWords(c.LowerThirdInAfter, c.LowerThirdOutAfter)}" : "Lower third — none", MenuScope.Stack, MenuTone.Stack)
            {
                Detail = "When it comes in after the GO and when it goes",
                Children = timingChildren,
                Because = d.Designs.Count == 0 ? "No lower-third designs yet — make one on the Lower thirds page." : "",
            },
            new("cue.lt.design", "Lower third design", MenuScope.Stack, MenuTone.Stack)
            {
                Detail = c.HasLowerThird ? $"'{c.LowerThirdName}' — choose another" : "Which design the cue brings in",
                Children = designChildren,
                Because = d.Designs.Count == 0 ? "No lower-third designs yet — make one on the Lower thirds page." : "",
            },
            new("cue.follow", $"Auto-follow — {FollowLabel(c.FollowSeconds)}", MenuScope.Stack, MenuTone.Stack)
            {
                Detail = "After this cue fires, the next GOes by itself",
                Children = followChildren,
                Because = c.IsClicker ? "The clicker list is the speaker's own: every step waits for the click." : "",
            },
            new("cue.mark", c.Mark == CueMark.None ? "Mark — nothing" : $"Mark — {c.Mark}", MenuScope.Stack, MenuTone.Stack)
            {
                Detail = "A break, lunch or the end: what the caller's estimates count down to",
                Children = markChildren,
            },
            new("cue.confirm", "Confirm twice", MenuScope.Stack, MenuTone.Stack) { Detail = "GO asks for a second press within a few seconds", Edit = "cue.confirm", IsOn = c.RequireConfirm },
            new("cue.ready", "Built (ready)", MenuScope.Stack, MenuTone.Stack) { Detail = "The visual operator's tick — not a gate", Edit = "cue.ready", IsOn = c.Ready },
        };

        var go = new List<MenuEntry>
        {
            Go("cue.edit", "Open in the cue editor", "Cues", c.Id),
            Go("go.looks", "Looks page", "Looks"),
            Go("go.lowerthirds", "Lower thirds page", "Lower thirds"),
        };

        var question = $"Cue {c.Number} '{c.Name}' does: {(c.Summary.Length > 0 ? c.Summary : "nothing yet")}.{(c.IsBroken ? $" It reads as broken: {c.Problem}." : "")}{(c.FollowSeconds is { } fs ? $" The next cue auto-follows {(fs == 0 ? "at once" : $"after {fs} s")}." : "")} Is anything missing for this moment of the show, and what would you add to it?";
        var subtitle = Join(c.IsStandby ? "STANDBY" : "", c.Enabled ? "" : "SKIPPED", c.IsBroken ? "BROKEN — " + c.Problem : c.Summary);
        return new DeskMenu("cue", c.Id, who, subtitle, c.IsBroken ? MenuTone.Live : c.IsStandby ? MenuTone.Preview : MenuTone.Stack, new[]
        {
            new MenuGroup("RUN", MenuTone.Plain, run) { Note = "The row's own verbs." },
            new MenuGroup("THE CUE", MenuTone.Stack, stack) { Note = StackNote },
            new MenuGroup("GO TO", MenuTone.Go, go),
            new MenuGroup("ASK", MenuTone.Ask, new[] { Ask("ask.cue", "Ask the assistant about this cue", question, d) }),
        });
    }

    // ---- a look -----------------------------------------------------------------------

    public static DeskMenu Look(DeskFacts d, MenuLook l)
    {
        var hotkeys = new List<MenuEntry> { new("look.hotkey:0", "No F-key", MenuScope.Stack, MenuTone.Stack) { Edit = "look.hotkey:0", IsOn = l.Hotkey == 0 } };
        for (var n = 1; n <= 12; n++)
        {
            var taken = d.Looks.FirstOrDefault(x => x.Hotkey == n && x.Id != l.Id);
            hotkeys.Add(new MenuEntry($"look.hotkey:{n}", $"F{n}", MenuScope.Stack, MenuTone.Stack)
            {
                Detail = taken is not null ? $"now '{taken.Name}' — it moves here" : "",
                Edit = $"look.hotkey:{n}",
                IsOn = l.Hotkey == n,
            });
        }
        var groups = new[]
        {
            new MenuGroup("IN THE PREVIEW", MenuTone.Preview, new[]
            {
                new MenuEntry("look.preview", "Into the preview", MenuScope.Preview, MenuTone.Preview)
                {
                    Detail = "The whole look — pattern, overlays, every screen — for a sign-off; TAKE puts it up",
                    Wire = $"PVW LOOK {l.Name}",
                    Action = new ShowAction(ShowActionKind.ApplyLookToPreview, l.Id),
                    IsOn = l.InPreview,
                },
            }) { Note = PreviewNote },
            new MenuGroup("TO AIR", MenuTone.Live, new[]
            {
                new MenuEntry("look.air", "On air now — with the transition", MenuScope.Live, MenuTone.Live)
                {
                    Detail = "What its button and its F-key do",
                    Wire = $"LOOK {l.Name}",
                    Action = new ShowAction(ShowActionKind.ApplyLook, l.Id),
                    IsOn = l.OnAir,
                    Because = d.PrepMode ? "PREP mode — the show is not live; looks land in the preview." : "",
                },
                new MenuEntry("look.cut", "On air now — cut", MenuScope.Live, MenuTone.Live)
                {
                    Detail = "No transition, whatever the show's setting",
                    Action = new ShowAction(ShowActionKind.ApplyLook, l.Id, "cut"),
                    Because = d.PrepMode ? "PREP mode — the show is not live; looks land in the preview." : "",
                },
            }) { Note = LiveNote },
            new MenuGroup("THE LOOK", MenuTone.Stack, new[]
            {
                new MenuEntry("look.update", "Update it from the preview", MenuScope.Stack, MenuTone.Stack)
                {
                    Detail = "The picture in the preview becomes this look; the cues that recall it follow",
                    Edit = "look.update",
                    Because = d.SandboxOpen ? "" : "Open EDIT SAFE and build the picture first.",
                },
                new MenuEntry("look.hotkey", l.Hotkey > 0 ? $"F-key — F{l.Hotkey}" : "F-key — none", MenuScope.Stack, MenuTone.Stack) { Detail = "The key that puts it on air", Children = hotkeys },
            }) { Note = "Edits the look in the show file." },
            new MenuGroup("GO TO", MenuTone.Go, new[] { Go("go.looks", "Looks page", "Looks", l.Id), Go("go.cues", "Cues page", "Cues") }),
            new MenuGroup("ASK", MenuTone.Ask, new[]
            {
                Ask("ask.look", "Ask the assistant about this look", $"The look '{l.Name}' {(l.OnAir ? "is on air" : l.InPreview ? "is in the preview" : "is saved")}{(l.Hotkey > 0 ? $" on F{l.Hotkey}" : "")}{(l.UsedBy.Count > 0 ? $" and cues {string.Join(", ", l.UsedBy)} recall it" : " and no cue recalls it")}. What would you check about it, and where else could it be used?", d),
            }),
        };
        var subtitle = Join(l.OnAir ? "ON AIR" : "", l.InPreview ? "in the preview" : "", l.Hotkey > 0 ? $"F{l.Hotkey}" : "", l.UsedBy.Count > 0 ? $"used by {string.Join(", ", l.UsedBy)}" : "no cue recalls it");
        return new DeskMenu("look", l.Id, l.Name, subtitle, l.OnAir ? MenuTone.Live : l.InPreview ? MenuTone.Preview : MenuTone.Stack, groups);
    }

    // ---- a lower third, a person -----------------------------------------------------

    public static DeskMenu LowerThird(DeskFacts d, MenuDesign design)
    {
        List<MenuEntry> People(string prefix, Func<string, ShowAction> action, Func<string, string> wire) => d.People.Select(p => new MenuEntry($"{prefix}:{p.Id}", p.Name, MenuScope.Preview, MenuTone.Preview)
        {
            Detail = p.Summary,
            Action = action(p.Id),
            Wire = wire(p.Name),
        }).ToList();
        var groups = new[]
        {
            new MenuGroup("IN THE PREVIEW", MenuTone.Preview, new[]
            {
                new MenuEntry("lt.preview", "Into the preview for a sign-off", MenuScope.Preview, MenuTone.Preview)
                {
                    Detail = "The PREVIEW pane, the multiview's Preview tile and REVIEW show it; TAKE TO AIR puts it on",
                    Wire = $"LT PVW {design.Name}",
                    Action = new ShowAction(ShowActionKind.LowerThirdPreview, design.Id),
                    IsOn = design.InPreview,
                },
                new MenuEntry("lt.preview.with", "Into the preview with a person", MenuScope.Preview, MenuTone.Preview)
                {
                    Detail = "Filled from the library first — name, role, company, photo",
                    Children = People("lt.preview.with", id => new ShowAction(ShowActionKind.LowerThirdPreview, design.Id, id), name => $"LT PVW {design.Name} WITH {name}"),
                    Because = d.People.Count == 0 ? "The library is empty — add people on the Lower thirds page." : "",
                },
            }) { Note = PreviewNote },
            new MenuGroup("TO AIR", MenuTone.Live, new[]
            {
                new MenuEntry("lt.air", "On air now", MenuScope.Live, MenuTone.Live)
                {
                    Detail = "What the chip does; again restarts its way in",
                    Wire = $"LT {design.Name}",
                    Action = new ShowAction(ShowActionKind.LowerThirdShow, design.Id),
                    IsOn = design.OnAir,
                },
                new MenuEntry("lt.air.with", "On air with a person", MenuScope.Live, MenuTone.Live)
                {
                    Children = People("lt.air.with", id => new ShowAction(ShowActionKind.LowerThirdShow, design.Id, id), name => $"LT {design.Name} WITH {name}").Select(e => e with { Scope = MenuScope.Live, Tone = MenuTone.Live }).ToList(),
                    Because = d.People.Count == 0 ? "The library is empty — add people on the Lower thirds page." : "",
                },
                new MenuEntry("lt.take", "Take the preview's lower third to air", MenuScope.Live, MenuTone.Live)
                {
                    Detail = "It arrives the way it was designed to; the preview clears",
                    Wire = "LT TAKE",
                    Action = new ShowAction(ShowActionKind.LowerThirdTake),
                    Because = design.InPreview ? "" : "It is not in the preview.",
                },
                new MenuEntry("lt.update", "Update the copy on air", MenuScope.Live, MenuTone.Live)
                {
                    Detail = "Every edit, the words too, without it leaving and arriving again",
                    Wire = "LT UPDATE",
                    Action = new ShowAction(ShowActionKind.LowerThirdUpdate),
                    Because = design.OnAir ? "" : "It is not on air.",
                },
                new MenuEntry("lt.off", "Off air", MenuScope.Live, MenuTone.Live)
                {
                    Detail = "It leaves the way it was designed to",
                    Wire = "LT OFF",
                    Action = new ShowAction(ShowActionKind.LowerThirdHide),
                    Because = design.OnAir ? "" : "It is not on air.",
                },
            }) { Note = LiveNote },
            new MenuGroup("THE DESIGN", MenuTone.Stack, new[]
            {
                new MenuEntry("lt.default", "Make it the show's default ★", MenuScope.Stack, MenuTone.Stack)
                {
                    Detail = "PERSON on the wire and the library's chips use the default",
                    Edit = "lt.default",
                    Because = design.IsDefault ? "It is the default." : "",
                },
            }) { Note = "Edits the show file." },
            new MenuGroup("GO TO", MenuTone.Go, new[] { Go("go.lowerthirds", "Lower thirds page — the designer", "Lower thirds", design.Id) }),
            new MenuGroup("ASK", MenuTone.Ask, new[]
            {
                Ask("ask.lt", "Ask the assistant about this lower third", $"The lower third '{design.Name}' {(design.OnAir ? "is on air" : design.InPreview ? "is in the preview" : "is ready")}{(design.IsDefault ? " and is the show's default" : "")}. What would you check about it before it goes on, and who should it name?", d),
            }),
        };
        var subtitle = Join(design.OnAir ? "ON AIR" : "", design.InPreview ? "in the preview" : "", design.IsDefault ? "★ the default" : "");
        return new DeskMenu("lowerthird", design.Id, design.Name, subtitle, design.OnAir ? MenuTone.Live : design.InPreview ? MenuTone.Preview : MenuTone.Plain, groups);
    }

    public static DeskMenu Person(DeskFacts d, MenuPerson p)
    {
        var defaultDesign = d.Designs.FirstOrDefault(x => x.IsDefault) ?? d.Designs.FirstOrDefault();
        var designs = d.Designs.Select(x => new MenuEntry($"person.preview.with:{x.Id}", (x.IsDefault ? "★ " : "") + x.Name, MenuScope.Preview, MenuTone.Preview)
        {
            Action = new ShowAction(ShowActionKind.LowerThirdPreview, x.Id, p.Id),
            Wire = $"LT PVW {x.Name} WITH {p.Name}",
        }).ToList();
        var groups = new[]
        {
            new MenuGroup("IN THE PREVIEW", MenuTone.Preview, new[]
            {
                new MenuEntry("person.preview", defaultDesign is null ? "Into the preview" : $"Into the preview with '{defaultDesign.Name}'", MenuScope.Preview, MenuTone.Preview)
                {
                    Detail = "The default design filled with this person, for a sign-off",
                    Wire = $"LT PVW WITH {p.Name}",
                    Action = new ShowAction(ShowActionKind.LowerThirdPreview, "", p.Id),
                    Because = defaultDesign is null ? "No lower-third designs yet — make one on the Lower thirds page." : "",
                },
                new MenuEntry("person.preview.with", "Into the preview with a design", MenuScope.Preview, MenuTone.Preview)
                {
                    Children = designs,
                    Because = d.Designs.Count == 0 ? "No lower-third designs yet — make one on the Lower thirds page." : "",
                },
            }) { Note = PreviewNote },
            new MenuGroup("TO AIR", MenuTone.Live, new[]
            {
                new MenuEntry("person.air", defaultDesign is null ? "On air now" : $"On air now with '{defaultDesign.Name}'", MenuScope.Live, MenuTone.Live)
                {
                    Detail = "What the wire's PERSON does",
                    Wire = $"PERSON {p.Name}",
                    Action = new ShowAction(ShowActionKind.LowerThirdShow, "", p.Id),
                    Because = defaultDesign is null ? "No lower-third designs yet — make one on the Lower thirds page." : "",
                },
            }) { Note = LiveNote },
            new MenuGroup("GO TO", MenuTone.Go, new[] { Go("go.lowerthirds", "Lower thirds page — the library", "Lower thirds", p.Id) }),
            new MenuGroup("ASK", MenuTone.Ask, new[]
            {
                Ask("ask.person", "Ask the assistant about this name", $"The lower-thirds library has '{p.Name}'{(p.Summary.Length > 0 ? $" — {p.Summary}" : "")}. Which cues should name them, and what would you check on the card?", d),
            }),
        };
        return new DeskMenu("person", p.Id, p.Name, p.Summary, MenuTone.Plain, groups);
    }

    // ---- a layer, an overlay, the countdown -------------------------------------------

    public static DeskMenu Layer(DeskFacts d, LayerFacts l)
    {
        var i = l.Index;
        var sources = Enum.GetValues<LayerSource>().Select(s => new MenuEntry($"layer.source:{i}:{s}", Capital(PreviewEdits.SourceWords(s)), MenuScope.Preview, MenuTone.Preview)
        {
            Edit = $"layer.source:{i}:{s}",
            IsOn = s == l.Source,
        }).ToList();
        var fits = Enum.GetValues<FitMode>().Select(f => new MenuEntry($"layer.fit:{i}:{f}", f.ToString(), MenuScope.Preview, MenuTone.Preview)
        {
            Detail = f switch { FitMode.Fill => "Crops to cover the box", FitMode.Fit => "Letterboxes", FitMode.Stretch => "Ignores the shape", FitMode.Center => "Draws 1:1", FitMode.Tile => "Repeats", _ => "" },
            Edit = $"layer.fit:{i}:{f}",
            IsOn = f == l.Fit,
        }).ToList();
        var media = d.Media.Select(m => new MenuEntry($"layer.media:{i}:{m.Id}", m.Name, MenuScope.Preview, MenuTone.Preview)
        {
            Detail = m.IsVideo ? "a clip" : "a still",
            Edit = $"layer.media:{i}:{m.Id}",
        }).ToList();
        var groups = new[]
        {
            new MenuGroup("IN THE PREVIEW", MenuTone.Preview, new[]
            {
                new MenuEntry($"layer.on:{i}", l.Enabled ? "On" : "Off", MenuScope.Preview, MenuTone.Preview) { Detail = l.Enabled ? "Choose to switch it off in the preview" : "Choose to switch it on in the preview", Edit = $"layer.on:{i}", IsOn = l.Enabled },
                new MenuEntry("layer.source", $"Source — {PreviewEdits.SourceWords(l.Source)}", MenuScope.Preview, MenuTone.Preview) { Detail = "What the layer shows", Children = sources },
                new MenuEntry("layer.media", "Pick from the library", MenuScope.Preview, MenuTone.Preview)
                {
                    Detail = "A still or a clip of the media library",
                    Children = media,
                    Because = d.Media.Count == 0 ? "The library is empty — add pictures on the Media page." : "",
                },
                new MenuEntry($"layer.browse:{i}", "Choose a file…", MenuScope.Preview, MenuTone.Preview) { Detail = "A still or a clip from this machine", Edit = $"layer.browse:{i}" },
                new MenuEntry("layer.fit", $"Fit — {l.Fit}", MenuScope.Preview, MenuTone.Preview) { Detail = "How the picture sits in the box", Children = fits },
            }) { Note = PreviewNote },
            new MenuGroup("GO TO", MenuTone.Go, new[]
            {
                Go("go.layers", "Layers page — the box, the crop, the border", "Layers", $"layer{i}"),
                Go("go.media", "Media page", "Media"),
                Go("go.library", "Library page", "Library"),
                Go("go.ndi", "NDI page — the feeds", "NDI"),
            }),
            new MenuGroup("ASK", MenuTone.Ask, new[]
            {
                Ask("ask.layer", "Ask the assistant about this layer", $"Layer {i} is {(l.Enabled ? "on" : "off")} and shows {PreviewEdits.SourceWords(l.Source)}{(l.Words.Length > 0 ? $" ('{l.Words}')" : "")}, fitted as {l.Fit.ToString().ToLowerInvariant()}. What would you put on it for this show, and what should I check?", d),
            }),
        };
        return new DeskMenu("layer", $"layer{i}", $"Layer {i}", Join(l.Enabled ? "ON" : "off", PreviewEdits.SourceWords(l.Source), l.Words), l.Enabled ? MenuTone.Preview : MenuTone.Plain, groups);
    }

    public static DeskMenu Overlay(DeskFacts d, OverlayFacts o)
    {
        var label = o.Label.Length > 0 ? o.Label : PreviewEdits.OverlayLabel(o.Kind);
        var preview = new List<MenuEntry>();
        if (o.IsCountdown)
        {
            var minutes = PreviewEdits.CountdownMinutes.Select(m => new MenuEntry($"countdown.start:{m.ToString("0.#", CultureInfo.InvariantCulture)}", $"{m:0.#} min", MenuScope.Preview, MenuTone.Preview)
            {
                Edit = $"countdown.start:{m.ToString("0.#", CultureInfo.InvariantCulture)}",
                IsOn = o.PreviewOn && Math.Abs(o.CountdownMinutes - m) < 0.01,
            }).ToList();
            var labels = PreviewEdits.CountdownLabels.Select(w => new MenuEntry($"countdown.label:{w}", w.Length == 0 ? "No label" : w, MenuScope.Preview, MenuTone.Preview)
            {
                Edit = $"countdown.label:{w}",
                IsOn = string.Equals(w, o.Words, StringComparison.OrdinalIgnoreCase),
            }).ToList();
            preview.Add(new MenuEntry("countdown.start", "Start a countdown", MenuScope.Preview, MenuTone.Preview) { Detail = "Runs in the preview from now; TAKE puts it on air", Children = minutes });
            preview.Add(new MenuEntry("countdown.stop", "Stop it", MenuScope.Preview, MenuTone.Preview) { Edit = "countdown.stop", Because = o.PreviewOn ? "" : "It is not running in the preview." });
            preview.Add(new MenuEntry("countdown.follow", "Follow the running order", MenuScope.Preview, MenuTone.Preview) { Detail = "Its target is the standby cue's planned start, and moves with the plan", Edit = "countdown.follow", IsOn = o.CountdownFollow });
            preview.Add(new MenuEntry("countdown.label", o.Words.Length > 0 ? $"Label — {o.Words}" : "Label — none", MenuScope.Preview, MenuTone.Preview) { Children = labels });
            preview.Add(new MenuEntry("countdown.anchor", "Position", MenuScope.Preview, MenuTone.Preview) { Children = Anchors("countdown.anchor", a => $"countdown.anchor:{a}", o.Anchor) });
        }
        else
        {
            preview.Add(new MenuEntry($"overlay.on:{o.Kind}", o.PreviewOn ? "On" : "Off", MenuScope.Preview, MenuTone.Preview) { Detail = o.PreviewOn ? "Choose to switch it off in the preview" : "Choose to switch it on in the preview", Edit = $"overlay.on:{o.Kind}", IsOn = o.PreviewOn });
            if (o.Anchor is not null) preview.Add(new MenuEntry("overlay.anchor", "Position", MenuScope.Preview, MenuTone.Preview) { Children = Anchors("overlay.anchor", a => $"overlay.anchor:{o.Kind}:{a}", o.Anchor) });
        }

        var live = new List<MenuEntry>();
        if (o.IsCountdown)
        {
            var m = o.CountdownMinutes.ToString("0.#", CultureInfo.InvariantCulture);
            live.Add(new MenuEntry("countdown.air.start", $"Start {m} min on air now", MenuScope.Live, MenuTone.Live) { Detail = "What SHOW CONTROLS' START does", Wire = $"COUNTDOWN {m}", Action = new ShowAction(ShowActionKind.CountdownStart, "", m) });
            live.Add(new MenuEntry("countdown.air.stop", "Stop it on air", MenuScope.Live, MenuTone.Live) { Wire = "COUNTDOWN STOP", Action = new ShowAction(ShowActionKind.CountdownStop), Because = o.AirOn ? "" : "It is not running on air." });
        }
        else if (LiveKinds(o.Kind) is { } kinds)
        {
            live.Add(new MenuEntry($"overlay.air:{o.Kind}", o.AirOn ? "Off air now" : "On air now", MenuScope.Live, MenuTone.Live)
            {
                Detail = "Straight to the screens, like SHOW CONTROLS' SEND",
                Wire = $"{kinds.Word} {(o.AirOn ? "OFF" : "ON")}",
                Action = new ShowAction(o.AirOn ? kinds.Off : kinds.On),
                IsOn = o.AirOn,
            });
        }

        var groups = new List<MenuGroup>
        {
            new("IN THE PREVIEW", MenuTone.Preview, preview) { Note = PreviewNote },
        };
        if (live.Count > 0) groups.Add(new MenuGroup("TO AIR", MenuTone.Live, live) { Note = LiveNote });
        groups.Add(new MenuGroup("GO TO", MenuTone.Go, o.IsCountdown
            ? new[] { Go("go.countdown", "Countdown page — the target, the look at zero", "Countdown"), Go("go.overlays", "Overlays page", "Overlays") }
            : new[] { Go("go.overlays", $"Overlays page — {label.ToLowerInvariant()}", "Overlays", o.Kind) }));
        groups.Add(new MenuGroup("ASK", MenuTone.Ask, new[]
        {
            Ask($"ask.{o.Kind}", $"Ask the assistant about the {label.ToLowerInvariant()}", $"The {label.ToLowerInvariant()} is {(o.AirOn ? "on air" : "off air")}{(o.PreviewOn != o.AirOn ? $" and {(o.PreviewOn ? "on" : "off")} in the preview" : "")}{(o.Words.Length > 0 ? $", with the words '{o.Words}'" : "")}. When should it be on for this show, and what would you check?", d),
        }));
        var subtitle = Join(o.AirOn ? "ON AIR" : "off air", o.PreviewOn != o.AirOn ? (o.PreviewOn ? "on in the preview" : "off in the preview") : "", o.Words);
        return new DeskMenu("overlay", o.Kind, label, subtitle, o.AirOn ? MenuTone.Live : o.PreviewOn ? MenuTone.Preview : MenuTone.Plain, groups);
    }

    // ---- the pieces --------------------------------------------------------------------

    private static MenuEntry LookDrawer(string id, DeskFacts d, Func<string, ShowAction> action, Func<string, string> wire, string detail)
        => new(id, "Look", MenuScope.Preview, MenuTone.Preview)
        {
            Detail = detail,
            Children = d.Looks.Select(l => new MenuEntry($"{id}:{l.Id}", l.Name, MenuScope.Preview, MenuTone.Preview)
            {
                Detail = Join(l.Hotkey > 0 ? $"F{l.Hotkey}" : "", l.OnAir ? "on air" : "", l.InPreview ? "in the preview" : ""),
                Action = action(l.Id),
                Wire = wire(l.Name),
                IsOn = l.OnAir,
            }).ToList(),
            Because = d.Looks.Count == 0 ? "No looks yet — save one on the Looks page." : "",
        };

    private static MenuEntry PresetDrawer(string id, DeskFacts d, Func<string, ShowAction> action, Func<string, string> wire)
        => new(id, "Preset", MenuScope.Preview, MenuTone.Preview)
        {
            Detail = "A pattern saved on the Pattern page — no overlays, no screens, just the picture",
            Children = d.Presets.Select(p => new MenuEntry($"{id}:{p}", p, MenuScope.Preview, MenuTone.Preview) { Action = action(p), Wire = wire(p) }).ToList(),
            Because = d.Presets.Count == 0 ? "No presets yet — save one on the Pattern page." : "",
        };

    private static MenuEntry KindDrawer(string id, DeskFacts d, string showing, Func<string, ShowAction> action, Func<string, string> wire)
        => new(id, "Pattern", MenuScope.Preview, MenuTone.Preview)
        {
            Detail = "The kind of picture; its settings are kept, so a grid comes back a grid",
            Children = d.Kinds.Select(k => new MenuEntry($"{id}:{k.Word}", k.Label, MenuScope.Preview, MenuTone.Preview)
            {
                Action = action(k.Word),
                Wire = wire(k.Word),
                IsOn = string.Equals(k.Word, showing, StringComparison.OrdinalIgnoreCase),
            }).ToList(),
        };

    private static List<MenuEntry> Anchors(string prefix, Func<Anchor9, string> key, Anchor9? current)
        => Enum.GetValues<Anchor9>().Select(a => new MenuEntry($"{prefix}:{a}", PreviewEdits.AnchorWords(a), MenuScope.Preview, MenuTone.Preview) { Edit = key(a), IsOn = a == current }).ToList();

    private static (ShowActionKind On, ShowActionKind Off, string Word)? LiveKinds(string kind) => kind switch
    {
        "clock" => (ShowActionKind.ClockOn, ShowActionKind.ClockOff, "CLOCK"),
        "logo" => (ShowActionKind.LogoOn, ShowActionKind.LogoOff, "LOGO"),
        "message" => (ShowActionKind.MessageOn, ShowActionKind.MessageOff, "MESSAGE"),
        "pip" => (ShowActionKind.PipOn, ShowActionKind.PipOff, "PIP"),
        "weather" => (ShowActionKind.WeatherOn, ShowActionKind.WeatherOff, "WEATHER"),
        _ => null,
    };

    // ---- the RUN surface's monitor: one screen large, for the caller's eye ----

    /// <summary>
    /// The monitor's menu (round 62): which screen it shows — the main screen, any screen or canvas
    /// of the rig, the programme — or hidden. The desk's own eye, so its entries are live and wear
    /// the rig's violet: nothing here reaches the air.
    /// </summary>
    public static DeskMenu Monitor(DeskFacts d, MonitorFacts m)
    {
        var show = new List<MenuEntry>
        {
            new("monitor.main", m.MainTitle.Length > 0 ? $"The main screen — {m.MainTitle}" : "The main screen", MenuScope.Live, MenuTone.Tile)
            {
                Detail = "The first screen whose role is Main; the monitor follows it when the rig changes",
                Wire = "RUN MONITOR MAIN",
                Action = new ShowAction(ShowActionKind.RunMonitor, "MAIN"),
                IsOn = m.IsMain,
                Because = m.MainTargetId.Length == 0 ? "No screen is in the rig yet." : "",
            },
        };
        foreach (var c in m.Choices)
        {
            show.Add(new($"monitor.{c.TargetId}", c.Title, MenuScope.Live, MenuTone.Tile)
            {
                Detail = c.Number.Length > 0 ? $"Screen {c.Number}, as its output shows it" : "The canvas, as its outputs show it",
                Wire = $"RUN MONITOR {c.Wire}",
                Action = new ShowAction(ShowActionKind.RunMonitor, c.TargetId),
                IsOn = !m.IsMain && !m.IsOff && !m.IsProgram && string.Equals(m.Current, c.TargetId, StringComparison.Ordinal),
            });
        }
        show.Add(new("monitor.pgm", "The programme (PGM)", MenuScope.Live, MenuTone.Tile)
        {
            Detail = "What every screen that follows the programme shows",
            Wire = "RUN MONITOR PGM",
            Action = new ShowAction(ShowActionKind.RunMonitor, "PGM"),
            IsOn = m.IsProgram,
        });
        show.Add(new("monitor.off", "Hide the monitor", MenuScope.Live, MenuTone.Tile)
        {
            Detail = "The history takes the room; RUN MONITOR MAIN or a choice here brings it back",
            Wire = "RUN MONITOR OFF",
            Action = new ShowAction(ShowActionKind.RunMonitorOff),
            IsOn = m.IsOff,
        });

        var shown = m.ShownTargetId ?? "";
        var go = new List<MenuEntry>
        {
            Go("go.screens", "Screens page — the screen it shows", "Screens", shown),
            Go("go.multiview", "Multiview page — every screen at once", "Multiview"),
        };
        var question = $"The RUN surface's monitor shows {m.ShowingWords}. What should the caller be watching during this show, and why?";

        return new DeskMenu("monitor", m.Current, "MONITOR", $"showing {m.ShowingWords}", m.IsOff ? MenuTone.Plain : MenuTone.Tile, new[]
        {
            new MenuGroup("SHOW ON THE MONITOR", MenuTone.Tile, show) { Note = "The desk's own eye — nothing here changes the air. The show remembers the choice." },
            new MenuGroup("GO TO", MenuTone.Go, go),
            new MenuGroup("ASK", MenuTone.Ask, new[] { Ask("ask.monitor", "Ask the assistant what to watch", question, d) }),
        });
    }

    private static MenuEntry Go(string id, string text, string page, string item = "")
        => new(id, text, MenuScope.Go, MenuTone.Go) { Route = new MenuRoute(page, item) };

    private static MenuEntry Ask(string id, string text, string question, DeskFacts d)
        => new(id, text, MenuScope.Ask, MenuTone.Ask)
        {
            Detail = "The facts go with the question; the answer comes to the Assistant page",
            Question = question,
            Because = d.AssistantReady ? "" : NeedKey,
        };

    private static string TransitionLabel(string words)
    {
        if (words.Length == 0) return "the show's default";
        var known = CueMenuEdits.Transitions.FirstOrDefault(t => string.Equals(t.Words, words, StringComparison.OrdinalIgnoreCase)).Label;
        return known is { Length: > 0 } ? known.ToLowerInvariant() : words;
    }

    private static string FollowLabel(int? seconds)
        => seconds is null ? "the caller presses GO" : seconds == 0 ? "at once" : $"after {CueMenuEdits.SecondsWords(seconds.Value)}";

    private static bool Same(double? a, double? b) => a is null && b is null || (a is { } x && b is { } y && Math.Abs(x - y) < 0.01);

    private static string Join(params string[] words) => string.Join(" · ", words.Where(w => w.Length > 0));

    private static string Capital(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
}
