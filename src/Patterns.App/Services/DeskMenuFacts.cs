using Patterns.Core.LowerThirds;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The facts a right-click menu is built from, read off the desk's services: what is open, what
/// is on air, what the show has to offer, and the state of the one thing the menu is for. The
/// desk's view models and the wire's MENU query both read them here, so a menu on the screen and
/// the same menu answered to a tablet never disagree.
/// </summary>
public static class DeskMenuFacts
{
    /// <summary>The desk's facts every menu reads.</summary>
    public static DeskFacts Desk(AppServices s)
    {
        var state = s.State;
        var onAir = s.LookTally.OnAir();
        var usedBy = UsedBy(state);
        return new DeskFacts
        {
            SandboxOpen = s.Sandbox.Active,
            PrepMode = state.Mode == ShowMode.Prep,
            AssistantReady = s.Assistant.HasKey,
            LookOnAirId = onAir?.Id ?? "",
            LookOnAirName = onAir?.Name ?? "",
            LookEdited = s.LookTally.AirEdited(),
            Looks = state.LooksAndCues.Looks.Select(l => new MenuLook(l.Id, l.Name, l.Hotkey, onAir?.Id == l.Id, s.Sandbox.Active && s.PreviewLookId == l.Id)
            {
                UsedBy = usedBy.TryGetValue(l.Id, out var cues) ? cues : Array.Empty<string>(),
            }).ToList(),
            Presets = s.Store.PresetNames().ToList(),
            Designs = Designs(state),
            People = People(state),
            Media = Media(state),
            TransitionDefault = TransitionWords(state.Transition),
            PreviewSource = state.Pattern.Kind == PatternKind.Media ? state.Pattern.Media.Source.ToString() : "",
            NextTake = s.NextTake.Pending?.Words ?? "",
            NextTakeWire = s.NextTake.Pending?.WireWords ?? "",
            Stings = state.Stingers.Items.Where(i => i.Kind == StingerKind.Sting && i.Source == StingerSource.File).Select(i => new MenuSting(i.Id, i.DisplayName)).ToList(),
        };
    }

    /// <summary>A node's facts: the show it mirrors, nothing of a desk's own (no preview, no presets folder, no assistant).</summary>
    public static DeskFacts Node(ShowState state)
    {
        var usedBy = UsedBy(state);
        return new DeskFacts
        {
            IsNode = true,
            Looks = state.LooksAndCues.Looks.Select(l => new MenuLook(l.Id, l.Name, l.Hotkey, false, false)
            {
                UsedBy = usedBy.TryGetValue(l.Id, out var cues) ? cues : Array.Empty<string>(),
            }).ToList(),
            Designs = Designs(state),
            People = People(state),
            Media = Media(state),
            TransitionDefault = TransitionWords(state.Transition),
        };
    }

    /// <summary>A tile of the wall by its target: a screen id or a canvas key; "" is the programme.</summary>
    public static ScreenFacts Screen(AppServices s, string targetId, bool monitored = true)
    {
        var state = s.State;
        if (targetId.Length == 0)
        {
            return new ScreenFacts
            {
                TargetId = "",
                Title = "PGM",
                OnAir = s.Outputs.IsLive && !state.Blackout,
                Armed = true,
                Enabled = true,
                Monitored = monitored,
                Collapsed = state.Desk.CollapsedTiles.Contains(""),
                ShowingKind = state.Pattern.Kind.ToString(),
            };
        }
        var screens = s.Screens.All;
        var isCanvas = ContentTargets.IsCanvasKey(targetId);
        var members = isCanvas ? ContentTargets.Members(targetId) : new[] { targetId };
        var placements = members.Select(id => state.Output.Placements.FirstOrDefault(p => p.ScreenId == id)).Where(p => p is not null).Select(p => p!).ToList();
        var first = placements.FirstOrDefault();
        var number = Number(s, targetId);
        var geo = Rig.Geometry(state, screens);
        var title = isCanvas ? CanvasTitle(s, targetId) : $"{number} · {geo.LabelFor(state, targetId)}";
        var oneRole = placements.Select(p => p.Role).Distinct().Count() == 1;
        return new ScreenFacts
        {
            TargetId = targetId,
            Number = number,
            Title = title,
            IsCanvas = isCanvas,
            OnAir = s.Outputs.IsLive && !state.Blackout && placements.Count > 0 && placements.All(p => p.Enabled) && !s.Bus.BlackTargets.Contains(targetId),
            Own = ContentTargets.UsesOwnPattern(state, targetId),
            Locked = ScreenRoles.IsLocked(state, targetId),
            Armed = s.Arming.IsArmed(targetId),
            Enabled = placements.Count > 0 && placements.All(p => p.Enabled),
            OffLook = s.LookTally.IsOffLook(targetId),
            Staged = s.Sandbox.IsStaged(targetId),
            IsMirror = !isCanvas && first is not null && first.MirrorOf.Length > 0 && ContentTargets.IsInRig(state, first.MirrorOf),
            Monitored = monitored,
            Collapsed = state.Desk.CollapsedTiles.Contains(targetId),
            RoleBadge = first is not null && oneRole ? ScreenRoles.Badge(first.Role) : "",
            // Round 67.7: the group — the role's word; a canvas whose screens differ is mixed; a repeater names its source.
            Group = first is not null && oneRole ? ScreenRoles.Word(first.Role) : "",
            GroupsMixed = placements.Count > 1 && !oneRole,
            MirrorSource = !isCanvas && first is { MirrorOf.Length: > 0 } && ContentTargets.IsInRig(state, first.MirrorOf) ? geo.LabelFor(state, first.MirrorOf) : "",
            ShowingKind = LookService.Shown(state, targetId).Kind.ToString(),
        };
    }

    /// <summary>The RUN monitor's facts (round 62): what it shows, the main screen, every canvas and screen of the rig in the wall's order.</summary>
    public static MonitorFacts Monitor(AppServices s)
    {
        var state = s.State;
        var screens = s.Screens.All;
        var ordered = Rig.OrderedLivePlacements(state, screens);
        var geo = Rig.Geometry(state, screens);
        var groups = Rig.CanvasGroups(state, screens);
        var grouped = groups.SelectMany(g => g).Select(p => p.ScreenId).ToHashSet(StringComparer.Ordinal);
        var choices = new List<MonitorChoice>();
        foreach (var members in groups)
        {
            var key = CanvasNameConfig.KeyFor(members.Select(m => m.ScreenId));
            choices.Add(new MonitorChoice(key, "", CanvasTitle(s, key)));
        }
        for (var i = 0; i < ordered.Count; i++)
        {
            var id = ordered[i].Placement.ScreenId;
            if (grouped.Contains(id)) continue;
            var n = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            choices.Add(new MonitorChoice(id, n, $"{n} · {geo.LabelFor(state, id)}"));
        }
        var main = MainTarget(s);
        return new MonitorFacts
        {
            Current = state.Desk.RunMonitor,
            MainTargetId = main ?? "",
            MainTitle = main is null ? "" : choices.FirstOrDefault(c => c.TargetId == main)?.Title ?? geo.LabelFor(state, main),
            Choices = choices,
        };
    }

    /// <summary>
    /// The main screen: the first screen of the rig whose role is Main, else the first screen —
    /// as the canvas it belongs to when it is joined into one, since the audience's picture is the
    /// whole wall. Null with no rig.
    /// </summary>
    public static string? MainTarget(AppServices s)
    {
        var state = s.State;
        var ordered = Rig.OrderedLivePlacements(state, s.Screens.All);
        if (ordered.Count == 0) return null;
        var main = ordered.FirstOrDefault(x => x.Placement.Role == ScreenRole.Main).Placement ?? ordered[0].Placement;
        foreach (var members in Rig.CanvasGroups(state, s.Screens.All))
        {
            if (members.Any(m => m.ScreenId == main.ScreenId)) return CanvasNameConfig.KeyFor(members.Select(m => m.ScreenId));
        }
        return main.ScreenId;
    }

    /// <summary>The monitor's word for STATE and the wire: OFF, PGM, MAIN, a screen's number, or a canvas key.</summary>
    public static string MonitorWord(AppServices s)
    {
        var current = s.State.Desk.RunMonitor;
        if (current.Equals("OFF", StringComparison.OrdinalIgnoreCase)) return "OFF";
        if (current.Equals("PGM", StringComparison.OrdinalIgnoreCase)) return "PGM";
        if (current.Length == 0) return "MAIN";
        var number = Number(s, current);
        return number.Length > 0 ? number : current;
    }

    /// <summary>The wire's number for a screen (overview order, 1-based); "" for a canvas or a screen not in the rig.</summary>
    public static string Number(AppServices s, string targetId)
    {
        if (targetId.Length == 0 || ContentTargets.IsCanvasKey(targetId)) return "";
        var ordered = Rig.OrderedLivePlacements(s.State, s.Screens.All);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Placement.ScreenId == targetId) return (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return "";
    }

    /// <summary>"A · Main wall" for a canvas key, as the wall titles it.</summary>
    public static string CanvasTitle(AppServices s, string key)
    {
        var groups = Rig.CanvasGroups(s.State, s.Screens.All);
        for (var i = 0; i < groups.Count; i++)
        {
            if (CanvasNameConfig.KeyFor(groups[i].Select(m => m.ScreenId)) != key) continue;
            var letter = ((char)('A' + i)).ToString();
            var stored = s.State.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == key)?.Name;
            return $"{letter} · {(string.IsNullOrWhiteSpace(stored) ? $"Canvas {letter}" : stored)}";
        }
        return key;
    }

    /// <summary>A cue as its menu sees it.</summary>
    public static CueFacts Cue(ShowState state, RunCueConfig cue, bool standby, string problem, string summary)
    {
        var lookStep = CueMenuEdits.LookStep(cue);
        var look = lookStep is null ? null : LookService.Find(state, lookStep.Target);
        var lt = CueMenuEdits.LowerThird(cue);
        var design = lt is null ? null : state.LowerThirds.Find(lt.Value.DesignId);
        var overlays = new HashSet<ShowActionKind>();
        foreach (var o in CueMenuEdits.Overlays)
        {
            if (CueMenuEdits.HasStep(cue, o.On)) overlays.Add(o.On);
        }
        return new CueFacts
        {
            Id = cue.Id,
            Number = cue.Number,
            Name = cue.Name,
            Enabled = cue.Enabled,
            IsStandby = standby,
            Problem = problem,
            Summary = summary,
            LookId = look?.Id ?? lookStep?.Target ?? "",
            LookName = look?.Name ?? lookStep?.Target ?? "",
            Transition = lookStep?.Value ?? "",
            FollowSeconds = cue.FollowSeconds,
            Mark = cue.Mark,
            RequireConfirm = cue.RequireConfirm,
            Ready = cue.Ready,
            Overlays = overlays,
            CleanPicture = CueMenuEdits.HasStep(cue, ShowActionKind.OverlaysOff),
            LowerThirdDesignId = lt?.DesignId ?? "",
            LowerThirdName = design?.Name ?? lt?.DesignId ?? "",
            LowerThirdInAfter = lt?.InAfter ?? 0,
            LowerThirdOutAfter = lt?.OutAfter,
            IsClicker = CueStacks.Clicker(state).Cues.Contains(cue),
        };
    }

    /// <summary>One of the two layers of the picture being edited.</summary>
    public static LayerFacts Layer(PatternConfig picture, int index)
    {
        var layer = index == 2 ? picture.Layer2 : picture.Layer1;
        var words = layer.Source switch
        {
            LayerSource.Image => Path.GetFileName(layer.ImagePath),
            LayerSource.Video => Path.GetFileName(layer.VideoPath),
            LayerSource.NdiFeed => layer.NdiSourceName,
            LayerSource.Capture => layer.CaptureDevice,
            LayerSource.Screen => layer.TargetId,
            LayerSource.Web => layer.WebUrl,
            _ => "",
        };
        return new LayerFacts { Index = index, Enabled = layer.Enabled, Source = layer.Source, Words = words, Fit = layer.Fit };
    }

    /// <summary>An overlay or the countdown, on the preview and on the air.</summary>
    public static OverlayFacts Overlay(AppServices s, string kind)
    {
        var preview = PreviewEdits.Overlay(s.State, kind);
        var air = PreviewEdits.Overlay(s.AirState, kind);
        var countdown = kind == "countdown";
        return new OverlayFacts
        {
            Kind = kind,
            Label = PreviewEdits.OverlayLabel(kind),
            PreviewOn = preview?.IsOn() ?? false,
            AirOn = air?.IsOn() ?? false,
            Words = kind == "message" ? s.State.Overlays.Message.Text : countdown ? s.State.Countdown.Label : "",
            Anchor = preview?.Anchored?.Anchor,
            CountdownMinutes = s.State.Countdown.DurationMinutes,
            CountdownFollow = s.State.Countdown.FollowPlan,
        };
    }

    private static List<MenuDesign> Designs(ShowState state)
        => state.LowerThirds.Designs.Select(d => new MenuDesign(d.Id, d.Name, d.IsDefault, d.IsOnAir, d.IsInPreview)).ToList();

    private static List<MenuPerson> People(ShowState state)
        => state.LowerThirds.Entries.Select(e => new MenuPerson(e.Id, e.Name, e.Summary)).ToList();

    private static List<MenuMedia> Media(ShowState state)
        => state.MediaLibrary.Select(m => new MenuMedia(m.Id, m.Name.Length > 0 ? m.Name : Path.GetFileName(m.Path), m.IsVideo)).ToList();

    /// <summary>The cues that recall each look, by number — "used by 02.010, 05.030".</summary>
    private static Dictionary<string, List<string>> UsedBy(ShowState state)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (_, cue, action) in CueStacks.AllActions(state))
        {
            if (action.Kind != ShowActionKind.ApplyLook) continue;
            var look = LookService.Find(state, action.Target);
            if (look is null) continue;
            if (!map.TryGetValue(look.Id, out var list)) map[look.Id] = list = new List<string>();
            if (!list.Contains(cue.Number)) list.Add(cue.Number);
        }
        return map;
    }

    private static string TransitionWords(TransitionConfig t)
        => !t.Enabled ? "cut" : $"{t.Kind.ToString().ToLowerInvariant()} · {t.DurationMs:0} ms";
}
