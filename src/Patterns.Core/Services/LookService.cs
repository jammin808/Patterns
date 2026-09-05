using System.Collections.ObjectModel;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>The content state a look captures (rig arrangement and infrastructure stay put).</summary>
public sealed class LookData
{
    public PatternConfig Pattern { get; init; } = new();
    public ObservableCollection<OutputAssignment> Independent { get; init; } = new();
    public OverlaySet Overlays { get; init; } = new();
    public CountdownConfig Countdown { get; init; } = new();
    public bool Blackout { get; init; }

    /// <summary>
    /// Screens that were showing their own pattern when the look was saved. Null in looks
    /// saved before this field existed — applying those leaves the flags alone.
    /// </summary>
    public List<string>? CustomScreens { get; init; }

    /// <summary>
    /// The lower third on air when the look was saved (its id), "" for none. Null in looks saved
    /// before lower thirds existed — applying those leaves whatever is on alone.
    /// </summary>
    public string? LowerThirdId { get; init; }
}

/// <summary>Capture/apply logic for looks, plus cue-firing arithmetic. Pure and unit tested.</summary>
public static class LookService
{
    public static string Capture(ShowState state) => JsonUtil.Serialize(Snapshot(state));

    private static LookData Snapshot(ShowState state) => new()
    {
        Pattern = JsonUtil.Clone(state.Pattern),
        Independent = JsonUtil.Clone(state.Independent),
        Overlays = JsonUtil.Clone(state.Overlays),
        Countdown = JsonUtil.Clone(state.Countdown),
        Blackout = state.Blackout,
        CustomScreens = state.Output.Placements.Where(p => p.UseCustomPattern).Select(p => p.ScreenId)
            .Concat(state.Output.CanvasNames.Where(c => c.UseCustomPattern).Select(c => c.MemberKey))
            .ToList(),
        LowerThirdId = state.LowerThirds.IsShowing ? state.LowerThirds.ActiveId : "",
    };

    /// <summary>
    /// The picture a state shows, in a form two captures of the same picture share: a countdown's
    /// arm time is dropped (a duration countdown re-arms on every recall) and a look saved before
    /// the screen flags existed reads as none. Two equal fingerprints are the same look on screen.
    /// </summary>
    public static string Fingerprint(ShowState state) => Normalise(Snapshot(state));

    /// <summary>The fingerprint of a saved look; "" when its JSON cannot be read.</summary>
    public static string Fingerprint(string lookJson)
    {
        try
        {
            var data = JsonUtil.Deserialize<LookData>(lookJson);
            return data is null ? "" : Normalise(data);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>True when the state shows exactly the look's picture — the look is in use, unedited.</summary>
    public static bool Matches(string lookJson, ShowState state)
    {
        var look = Fingerprint(lookJson);
        return look.Length > 0 && look == Fingerprint(state);
    }

    private static string Normalise(LookData data)
    {
        data.Countdown.ArmedAtUtc = null;
        var custom = data.CustomScreens ?? new List<string>();
        var normalised = new LookData
        {
            Pattern = data.Pattern,
            Independent = data.Independent,
            Overlays = data.Overlays,
            Countdown = data.Countdown,
            Blackout = data.Blackout,
            CustomScreens = custom.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            LowerThirdId = data.LowerThirdId ?? "",
        };
        return JsonUtil.Serialize(normalised);
    }

    /// <summary>
    /// The one resolver for everything that names a look — F-keys aside, looks are referenced
    /// by name, and four code paths used to disagree on case. Id first, then name, case-insensitive.
    /// </summary>
    public static LookConfig? Find(ShowState state, string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return null;
        var looks = state.LooksAndCues.Looks;
        return looks.FirstOrDefault(l => l.Id == nameOrId)
               ?? looks.FirstOrDefault(l => string.Equals(l.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What still points at a look. A delete lists these instead of orphaning them.</summary>
    public static IReadOnlyList<string> References(ShowState state, LookConfig look)
    {
        var refs = new List<string>();
        foreach (var cue in state.LooksAndCues.Cues)
        {
            if (string.Equals(cue.LookName, look.Name, StringComparison.OrdinalIgnoreCase))
            {
                refs.Add($"scheduled cue at {cue.Time}");
            }
        }
        foreach (var (stack, cue, action) in CueStacks.AllActions(state))
        {
            if (action.Kind != CueActionKind.ApplyLook) continue;
            if (string.Equals(action.Target, look.Id, StringComparison.Ordinal) ||
                string.Equals(action.Target, look.Name, StringComparison.OrdinalIgnoreCase))
            {
                refs.Add($"{stack.Name} cue {cue.Number} {cue.Name}");
            }
        }
        for (var i = 0; i < state.Presenter.Steps.Count; i++)
        {
            if (string.Equals(state.Presenter.Steps[i].LookName, look.Name, StringComparison.OrdinalIgnoreCase))
            {
                refs.Add($"presenter step {i + 1}");
            }
        }
        return refs;
    }

    /// <summary>Applies a captured look in place (bindings keep their object references).</summary>
    /// <param name="rearmCountdown">A recall (F-key, cue, schedule): a duration countdown the look carries starts
    /// now rather than from the stale moment it was saved. State transfers (sandbox restore, recovery) leave it.</param>
    public static bool Apply(string lookJson, ShowState state, bool rearmCountdown = false)
    {
        LookData? data;
        try
        {
            data = JsonUtil.Deserialize<LookData>(lookJson);
        }
        catch (Exception ex)
        {
            Log.Warn("Look could not be read.", ex);
            return false;
        }
        if (data is null) return false;

        ModelCopier.Copy(data.Pattern, state.Pattern);
        state.Independent.Clear();
        foreach (var a in data.Independent)
        {
            state.Independent.Add(JsonUtil.Clone(a));
        }
        ModelCopier.Copy(data.Overlays, state.Overlays);
        ModelCopier.Copy(data.Countdown, state.Countdown);
        state.Blackout = data.Blackout;

        // Which screens show their own pattern is part of the picture: without it the same
        // look shows a different program after any per-screen send than it did in rehearsal.
        if (data.CustomScreens is { } custom)
        {
            foreach (var p in state.Output.Placements)
            {
                p.UseCustomPattern = custom.Contains(p.ScreenId);
            }
            // Joined canvases are targets too (keyed a+b); a look saved before canvases could
            // hold content simply lists none, which reads as "no canvas has its own pattern".
            // A canvas the show has no row for yet gets one, so the look lands as saved.
            foreach (var c in state.Output.CanvasNames)
            {
                c.UseCustomPattern = custom.Contains(c.MemberKey);
            }
            foreach (var key in custom)
            {
                if (ContentTargets.IsCanvasKey(key)) ContentTargets.SetOwnPattern(state, key, true);
            }
        }

        if (rearmCountdown && state.Countdown.Enabled && state.Countdown.TargetKind == CountdownTargetKind.Duration)
        {
            state.Countdown.ArmedAtUtc = DateTime.UtcNow;
        }

        // The lower third is part of the picture: a recall shows the look's design afresh (or takes
        // the one on air off when the look had none); a state transfer only makes sure it is on.
        if (data.LowerThirdId is { } lowerThird)
        {
            var lowers = state.LowerThirds;
            if (lowerThird.Length == 0)
            {
                lowers.Hide(ShowClock.UtcNow);
            }
            else if (lowers.Find(lowerThird) is { } design)
            {
                if (rearmCountdown || lowers.ActiveId != design.Id || !lowers.IsShowing) lowers.Show(design, ShowClock.UtcNow);
            }
        }

        // Looks captured before playlist sections existed carry flat lists — lift them.
        PlaylistSequencer.Normalize(state.Pattern.Media.Playlist);
        foreach (var a in state.Independent)
        {
            PlaylistSequencer.Normalize(a.Pattern.Media.Playlist);
        }
        return true;
    }

    /// <summary>A cue fires when enabled, its minute matches, and it hasn't fired today.</summary>
    public static bool ShouldFire(CueConfig cue, DateTime localNow)
    {
        if (!cue.Enabled || string.IsNullOrWhiteSpace(cue.LookName)) return false;
        if (!CountdownService.TryParseTime(cue.Time, out var tod)) return false;
        if (localNow.Hour != tod.Hours || localNow.Minute != tod.Minutes) return false;
        return cue.LastFiredDate?.Date != localNow.Date;
    }

    /// <summary>Next enabled cue occurrence after now (for the "next cue" readout).</summary>
    public static (CueConfig Cue, DateTime At)? NextCue(IEnumerable<CueConfig> cues, DateTime localNow)
    {
        (CueConfig Cue, DateTime At)? best = null;
        foreach (var cue in cues)
        {
            if (!cue.Enabled || !CountdownService.TryParseTime(cue.Time, out var tod)) continue;
            var at = localNow.Date + tod;
            if (at <= localNow) at = at.AddDays(1);
            if (best is null || at < best.Value.At) best = (cue, at);
        }
        return best;
    }
}
