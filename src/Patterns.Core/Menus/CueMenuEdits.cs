using System.Globalization;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.Core.Menus;

/// <summary>
/// The cue-stack edits a cue's right-click menu offers — its look, how the look comes in, the
/// overlays it brings, a lower third in and out on a clock, its auto-follow, its mark — as
/// edits of the cue's own steps, with no vocabulary of their own: every edit adds, changes or
/// removes a <see cref="CueActionConfig"/> the editor would show and the sheet would export.
/// Pure, so the desk and a caller node apply the same edit to the same cue and a test can read
/// the stack back.
/// </summary>
public static class CueMenuEdits
{
    /// <summary>The look recall's transitions the menu offers: the wire's words and the desk's label.</summary>
    public static readonly IReadOnlyList<(string Words, string Label)> Transitions = new[]
    {
        ("", "The show's default"),
        ("cut", "Cut"),
        ("500", "Dissolve · ½ s"),
        ("1000", "Dissolve · 1 s"),
        ("2000", "Dissolve · 2 s"),
        ("dip 800", "Dip through the brand colour · 0.8 s"),
        ("wipe left 800", "Wipe left · 0.8 s"),
        ("wipe right 800", "Wipe right · 0.8 s"),
        ("push up 600", "Push up · 0.6 s"),
        ("stinger", "Brand stinger"),
    };

    /// <summary>The overlays a cue can bring in or take out: the ON step, the OFF step, the label, the ON step's value.</summary>
    public static readonly IReadOnlyList<(ShowActionKind On, ShowActionKind Off, string Label, string Value)> Overlays = new[]
    {
        (ShowActionKind.CountdownStart, ShowActionKind.CountdownStop, "Countdown · 5 min", "5"),
        (ShowActionKind.ClockOn, ShowActionKind.ClockOff, "Clock", ""),
        (ShowActionKind.MessageOn, ShowActionKind.MessageOff, "Message", ""),
        (ShowActionKind.LogoOn, ShowActionKind.LogoOff, "Logo", ""),
        (ShowActionKind.PipOn, ShowActionKind.PipOff, "Picture-in-picture", ""),
        (ShowActionKind.WeatherOn, ShowActionKind.WeatherOff, "Weather", ""),
    };

    /// <summary>When a lower third comes in after the GO and how long it stays (null = until hidden).</summary>
    public static readonly IReadOnlyList<(double In, double? Out, string Label)> LowerThirdTimings = new (double, double?, string)[]
    {
        (0, 8, "In with the GO · out after 8 s"),
        (0, 12, "In with the GO · out after 12 s"),
        (3, 8, "In after 3 s · out 8 s later"),
        (5, 10, "In after 5 s · out 10 s later"),
        (10, 10, "In after 10 s · out 10 s later"),
        (5, null, "In after 5 s · stays until hidden"),
    };

    /// <summary>The auto-follows the menu offers.</summary>
    public static readonly IReadOnlyList<(int? Seconds, string Label)> Follows = new (int?, string)[]
    {
        (null, "The caller presses GO"),
        (0, "At once"),
        (2, "After 2 s"),
        (5, "After 5 s"),
        (10, "After 10 s"),
        (30, "After 30 s"),
        (60, "After a minute"),
    };

    /// <summary>The cue's look recall, when it has one.</summary>
    public static CueActionConfig? LookStep(RunCueConfig cue) => cue.Actions.FirstOrDefault(a => a.Kind == ShowActionKind.ApplyLook);

    /// <summary>The cue recalls this look: its first look recall changes, or one is put first.</summary>
    public static void SetLook(RunCueConfig cue, string lookId)
    {
        var existing = LookStep(cue);
        if (existing is not null) existing.Target = lookId;
        else cue.Actions.Insert(0, new CueActionConfig { Kind = ShowActionKind.ApplyLook, Target = lookId });
    }

    /// <summary>The look recall's transition words ("" = the show's default); "" with no recall too.</summary>
    public static string Transition(RunCueConfig cue) => LookStep(cue)?.Value ?? "";

    /// <summary>How the look comes in. False when the cue recalls no look — a transition belongs to a recall.</summary>
    public static bool SetTransition(RunCueConfig cue, string words)
    {
        var step = LookStep(cue);
        if (step is null) return false;
        step.Value = words.Trim();
        return true;
    }

    public static bool HasStep(RunCueConfig cue, ShowActionKind kind) => cue.Actions.Any(a => a.Kind == kind);

    /// <summary>
    /// The overlay in or out of the cue: with its ON step present, both its ON and OFF steps go;
    /// without, an ON step joins the end (with the GO) and any OFF step goes. True = it is now in.
    /// </summary>
    public static bool ToggleOverlay(RunCueConfig cue, ShowActionKind on, ShowActionKind off, string value = "")
    {
        if (HasStep(cue, on))
        {
            Remove(cue, a => a.Kind == on || a.Kind == off);
            return false;
        }
        Remove(cue, a => a.Kind == off);
        cue.Actions.Add(new CueActionConfig { Kind = on, Value = value });
        return true;
    }

    /// <summary>A clean picture — every overlay off with the GO — in or out of the cue. True = it is now in.</summary>
    public static bool ToggleClean(RunCueConfig cue)
    {
        if (HasStep(cue, ShowActionKind.OverlaysOff))
        {
            Remove(cue, a => a.Kind == ShowActionKind.OverlaysOff);
            return false;
        }
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.OverlaysOff });
        return true;
    }

    /// <summary>The cue's lower third — the design, when it comes in after the GO, when it goes (null = it stays) — or null with none.</summary>
    public static (string DesignId, double InAfter, double? OutAfter)? LowerThird(RunCueConfig cue)
    {
        var plan = CueSteps.Plan(cue.Actions);
        var show = plan.FirstOrDefault(s => s.Action.Kind == ShowActionKind.LowerThirdShow);
        if (show.Action is null) return null;
        var hide = plan.Skip(show.Index + 1).FirstOrDefault(s => s.Action.Kind == ShowActionKind.LowerThirdHide);
        return (show.Action.Target, show.AtSeconds, hide.Action is null ? null : hide.AtSeconds - show.AtSeconds);
    }

    /// <summary>
    /// The cue brings this lower third in after the GO and takes it out again: one SHOW step at
    /// the end of the cue with the wait, and one HIDE step after it with its own wait (or none,
    /// when it stays). Any lower-third steps the cue had go first, so the menu never stacks two.
    /// </summary>
    public static void SetLowerThird(RunCueConfig cue, string designId, double inAfter, double? outAfter)
    {
        Remove(cue, a => a.Kind is ShowActionKind.LowerThirdShow or ShowActionKind.LowerThirdHide);
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.LowerThirdShow, Target = designId, DelaySeconds = Math.Max(0, inAfter) });
        if (outAfter is { } o && o > 0)
        {
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.LowerThirdHide, DelaySeconds = o });
        }
    }

    public static void RemoveLowerThird(RunCueConfig cue)
        => Remove(cue, a => a.Kind is ShowActionKind.LowerThirdShow or ShowActionKind.LowerThirdHide);

    public static void SetFollow(RunCueConfig cue, int? seconds) => cue.FollowSeconds = seconds;

    public static void SetMark(RunCueConfig cue, CueMark mark) => cue.Mark = mark;

    /// <summary>
    /// One edit by its key — the entry ids the cue menu carries — applied to the cue; the words
    /// for the status line come back, or null when the key is not a cue edit. The keys:
    /// cue.look:&lt;id&gt; · cue.transition:&lt;words&gt; · cue.overlay:&lt;OnKind&gt; · cue.clean ·
    /// cue.lt:&lt;designId&gt;:&lt;in&gt;:&lt;out|-&gt; · cue.lt.design:&lt;designId&gt; · cue.lt.remove ·
    /// cue.follow:&lt;seconds|-&gt; · cue.mark:&lt;mark&gt; · cue.confirm · cue.ready.
    /// </summary>
    public static string? Apply(RunCueConfig cue, string key, DeskFacts facts)
    {
        if (!key.StartsWith("cue.", StringComparison.Ordinal)) return null;
        var head = key.Length > 4 ? key[4..] : "";
        var colon = head.IndexOf(':');
        var verb = colon < 0 ? head : head[..colon];
        var arg = colon < 0 ? "" : head[(colon + 1)..];
        var who = $"{cue.Number} {cue.Name}";
        switch (verb)
        {
            case "look":
            {
                var look = facts.Looks.FirstOrDefault(l => l.Id == arg);
                if (look is null) return $"No look with that id — the show's looks changed under the menu.";
                SetLook(cue, look.Id);
                return $"{who} recalls '{look.Name}'.";
            }
            case "transition":
            {
                if (!SetTransition(cue, arg)) return $"{who} recalls no look yet — give it a look first; the transition belongs to the recall.";
                var label = Transitions.FirstOrDefault(t => t.Words == arg).Label;
                return $"{who}: the look comes in with {(label is { Length: > 0 } ? label.ToLowerInvariant() : arg)}.";
            }
            case "overlay":
            {
                if (!Enum.TryParse<ShowActionKind>(arg, out var on)) return null;
                var row = Overlays.FirstOrDefault(o => o.On == on);
                if (row.Label is null) return null;
                var now = ToggleOverlay(cue, row.On, row.Off, row.Value);
                return now ? $"{who} brings the {row.Label.ToLowerInvariant()} in with the GO." : $"{who} no longer touches the {row.Label.ToLowerInvariant()}.";
            }
            case "clean":
                return ToggleClean(cue) ? $"{who} clears every overlay with the GO." : $"{who} leaves the overlays as they are.";
            case "lt":
            {
                var parts = arg.Split(':');
                if (parts.Length < 3) return null;
                var design = facts.Designs.FirstOrDefault(d => d.Id == parts[0]);
                if (design is null) return "No design with that id — the show's lower thirds changed under the menu.";
                var inAfter = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var i) ? i : 0;
                double? outAfter = parts[2] == "-" ? null : double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : null;
                SetLowerThird(cue, design.Id, inAfter, outAfter);
                return $"{who}: '{design.Name}' {TimingWords(inAfter, outAfter)}.";
            }
            case "lt.design":
            {
                var design = facts.Designs.FirstOrDefault(d => d.Id == arg);
                if (design is null) return "No design with that id — the show's lower thirds changed under the menu.";
                var current = LowerThird(cue);
                SetLowerThird(cue, design.Id, current?.InAfter ?? 0, current is null ? 8 : current.Value.OutAfter);
                return $"{who}: the lower third is '{design.Name}' — {TimingWords(current?.InAfter ?? 0, current is null ? 8 : current.Value.OutAfter)}.";
            }
            case "lt.remove":
                RemoveLowerThird(cue);
                return $"{who} brings no lower third.";
            case "follow":
            {
                int? seconds = arg == "-" ? null : int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
                SetFollow(cue, seconds);
                return seconds is null ? $"{who}: the caller presses GO for the next cue." : seconds == 0 ? $"{who}: the next cue GOes at once." : $"{who}: the next cue GOes by itself {SecondsWords(seconds.Value)} later.";
            }
            case "mark":
            {
                if (!Enum.TryParse<CueMark>(arg, true, out var mark)) return null;
                SetMark(cue, mark);
                return mark == CueMark.None ? $"{who} marks nothing." : $"{who} is the {mark.ToString().ToLowerInvariant()} — the estimates count down to it.";
            }
            case "confirm":
                cue.RequireConfirm = !cue.RequireConfirm;
                return cue.RequireConfirm ? $"{who} asks for a second GO." : $"{who} fires on one GO.";
            case "ready":
                cue.Ready = !cue.Ready;
                return cue.Ready ? $"{who} is marked built." : $"{who} is no longer marked built.";
            default:
                return null;
        }
    }

    /// <summary>"in after 3 s, out 8 s later" / "in with the GO, out after 8 s" / "in after 5 s, stays until hidden".</summary>
    public static string TimingWords(double inAfter, double? outAfter)
    {
        var inWords = inAfter <= 0 ? "in with the GO" : $"in after {Seconds(inAfter)}";
        var outWords = outAfter is { } o && o > 0 ? (inAfter <= 0 ? $"out after {Seconds(o)}" : $"out {Seconds(o)} later") : "stays until hidden";
        return $"{inWords}, {outWords}";
    }

    private static string Seconds(double s) => s.ToString("0.#", CultureInfo.InvariantCulture) + " s";

    /// <summary>"5 s" under a minute, else the caller's m:ss.</summary>
    public static string SecondsWords(int seconds) => seconds < 60 ? $"{seconds} s" : CueTiming.FormatDuration(seconds);

    private static void Remove(RunCueConfig cue, Func<CueActionConfig, bool> which)
    {
        for (var i = cue.Actions.Count - 1; i >= 0; i--)
        {
            if (which(cue.Actions[i])) cue.Actions.RemoveAt(i);
        }
    }
}
