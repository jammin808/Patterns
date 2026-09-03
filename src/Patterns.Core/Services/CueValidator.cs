using Patterns.Core.Model;

namespace Patterns.Core.Services;

public enum IssueSeverity
{
    /// <summary>The cue cannot run as written: GO refuses with this reason. The list around it still runs.</summary>
    Hard,
    /// <summary>Worth a look; the cue runs.</summary>
    Soft,
}

public sealed record CueIssue(string CueId, IssueSeverity Severity, string Text);

/// <summary>What the validator may ask the environment (tests answer differently from the app).</summary>
public sealed class CueValidationContext
{
    public static CueValidationContext Default { get; } = new();

    public Func<string, bool> FileExists { get; init; } = path => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>libVLC present — a video stinger needs it.</summary>
    public bool VideoDecoderAvailable { get; init; } = true;
}

/// <summary>The result of validating one list.</summary>
public sealed class CueValidationReport
{
    public List<CueIssue> Issues { get; } = new();

    /// <summary>Cue id → the first hard reason. A cue here is <em>Broken</em>: GO refuses it, the rest of the list runs.</summary>
    public Dictionary<string, string> Broken { get; } = new(StringComparer.Ordinal);

    /// <summary>Cue id → the first soft note.</summary>
    public Dictionary<string, string> Warnings { get; } = new(StringComparer.Ordinal);

    /// <summary>List-level notes: numbering that repeats or runs backwards.</summary>
    public List<string> StackNotes { get; } = new();

    public bool IsBroken(string cueId) => Broken.ContainsKey(cueId);

    public string? ReasonFor(string cueId) => Broken.TryGetValue(cueId, out var r) ? r : null;

    public int BrokenCount => Broken.Count;

    internal void Add(CueIssue issue)
    {
        Issues.Add(issue);
        if (issue.Severity == IssueSeverity.Hard) Broken.TryAdd(issue.CueId, issue.Text);
        else Warnings.TryAdd(issue.CueId, issue.Text);
    }
}

/// <summary>
/// Validates a list by simulating it: cues are walked in order and every Apply Look lands on
/// a clone of the show, so a reference that resolves against "whatever is on air" (a playlist
/// part) is checked against what the preceding cues will have left there. Pure, so it runs on
/// load, on every stack edit, when the screens change and again for one cue at GO.
/// </summary>
public static class CueValidator
{
    public static CueValidationReport Validate(ShowState state, CueStackConfig stack, CueValidationContext? context = null)
    {
        var ctx = context ?? CueValidationContext.Default;
        var report = new CueValidationReport();
        var sim = JsonUtil.Clone(state);
        foreach (var cue in stack.Cues)
        {
            ValidateCue(state, sim, cue, ctx, report, simulate: true);
        }
        foreach (var note in CueNumber.Warnings(stack.Cues)) report.StackNotes.Add(note);
        return report;
    }

    /// <summary>One cue against the live state (the re-check at GO). Hard issues only matter here.</summary>
    public static CueValidationReport ValidateOne(ShowState state, RunCueConfig cue, CueValidationContext? context = null)
    {
        var report = new CueValidationReport();
        ValidateCue(state, state, cue, context ?? CueValidationContext.Default, report, simulate: false);
        return report;
    }

    private static void ValidateCue(ShowState state, ShowState sim, RunCueConfig cue, CueValidationContext ctx,
        CueValidationReport report, bool simulate)
    {
        void Hard(string text) => report.Add(new CueIssue(cue.Id, IssueSeverity.Hard, text));
        void Soft(string text) => report.Add(new CueIssue(cue.Id, IssueSeverity.Soft, text));

        var hasVideoStinger = false;
        var hasContent = false;
        var hasBlackoutOn = false;
        var k = 0;
        foreach (var a in cue.Actions)
        {
            k++;
            var where = $"action {k}";
            switch (a.Kind)
            {
                case CueActionKind.Unknown:
                    Hard($"{where} comes from a newer build and cannot run here.");
                    break;
                case CueActionKind.Note:
                    break;
                case CueActionKind.ApplyLook:
                {
                    hasContent = true;
                    var look = LookService.Find(sim, a.Target);
                    if (look is null)
                    {
                        Hard($"{where}: look '{a.Target}' not found.");
                        break;
                    }
                    if (!CueActionSpec.TryParseTransition(a.Value, out _, out _))
                    {
                        Hard($"{where}: transition '{a.Value}' is not 'cut' or a fade in milliseconds.");
                    }
                    SoftLookChecks(state, look, ctx, Soft, where);
                    if (simulate) LookService.Apply(look.Json, sim);
                    break;
                }
                case CueActionKind.AudioPlay:
                    if (string.IsNullOrWhiteSpace(state.AudioPlayer.Path)) Hard($"{where}: no audio track is chosen (Audio tab).");
                    else if (!ctx.FileExists(state.AudioPlayer.Path)) Hard($"{where}: audio file missing — {Path.GetFileName(state.AudioPlayer.Path)}.");
                    break;
                case CueActionKind.StingerFire:
                {
                    var s = CueSummary.FindStinger(state, a.Target);
                    if (s is null)
                    {
                        Hard($"{where}: stinger '{a.Target}' not found.");
                        break;
                    }
                    if (!ctx.FileExists(s.Path)) Hard($"{where}: stinger file missing — {Path.GetFileName(s.Path)}.");
                    if (PlaylistSequencer.IsVideoPath(s.Path))
                    {
                        hasVideoStinger = true;
                        if (!ctx.VideoDecoderAvailable) Hard($"{where}: a video stinger needs the video runtime (libVLC), which is not present.");
                    }
                    break;
                }
                case CueActionKind.PlaylistPart:
                {
                    hasContent = true;
                    var playlist = MediaLocator.FindActivePlaylist(sim)?.Playlist ?? sim.Pattern.Media.Playlist;
                    var found = playlist.Sections.Any(x => string.Equals(x.Name, a.Target, StringComparison.OrdinalIgnoreCase));
                    if (!found) Hard($"{where}: playlist part '{a.Target}' is not in the playlist that will be on air.");
                    break;
                }
                case CueActionKind.StreamStart:
                    if (!state.Stream.Destinations.Any(d => d.Enabled && d.Url.Length > 0)) Hard($"{where}: no enabled stream destination (Stream tab).");
                    break;
                case CueActionKind.BlackoutOn:
                    hasBlackoutOn = true;
                    break;
                case CueActionKind.ScreenOn:
                case CueActionKind.ScreenOff:
                    hasContent = true;
                    if (state.Output.Placements.All(p => p.ScreenId != a.Target)) Hard($"{where}: screen '{a.Target}' is not in the rig.");
                    break;
                case CueActionKind.CanvasOn:
                case CueActionKind.CanvasOff:
                {
                    hasContent = true;
                    var members = ContentTargets.Members(a.Target);
                    if (members.Length < 2 || members.Any(m => state.Output.Placements.All(p => p.ScreenId != m)))
                    {
                        Hard($"{where}: canvas '{a.Target}' is not in the rig.");
                    }
                    break;
                }
                case CueActionKind.CountdownStart:
                    if (!double.TryParse(a.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
                    {
                        Hard($"{where}: countdown minutes '{a.Value}' is not a number above zero.");
                    }
                    break;
                case CueActionKind.MessageOn:
                    if (string.IsNullOrWhiteSpace(a.Value)) Soft($"{where}: the message text is empty.");
                    break;
                case CueActionKind.ListArm:
                case CueActionKind.ListDisarm:
                case CueActionKind.ListGo:
                case CueActionKind.ListBack:
                case CueActionKind.ListReset:
                    if (CueStacks.Find(state, a.Target) is null) Hard($"{where}: list '{a.Target}' not found.");
                    break;
            }
        }

        if (hasVideoStinger && hasContent)
        {
            Hard("A video stinger takes every screen — it cannot share a cue with a look, part, screen or canvas change.");
        }
        if (hasVideoStinger && hasBlackoutOn)
        {
            Hard("A video stinger lifts blackout — it cannot share a cue with Blackout on.");
        }
    }

    private static void SoftLookChecks(ShowState state, LookConfig look, CueValidationContext ctx, Action<string> soft, string where)
    {
        LookData? data;
        try
        {
            data = JsonUtil.Deserialize<LookData>(look.Json);
        }
        catch
        {
            data = null;
        }
        if (data is null) return;
        if (data.CustomScreens is null)
        {
            soft($"{where}: look '{look.Name}' was saved before looks recorded which screens show their own pattern — re-save it.");
        }
        foreach (var pattern in Patterns(data))
        {
            if (pattern.Kind != PatternKind.Media) continue;
            var path = pattern.Media.Source switch
            {
                MediaSource.Image => pattern.Media.ImagePath,
                MediaSource.Video => pattern.Media.VideoPath,
                _ => "",
            };
            if (path.Length > 0 && !ctx.FileExists(path))
            {
                soft($"{where}: look '{look.Name}' uses a media file that is missing — {Path.GetFileName(path)} (a slate shows instead).");
            }
        }
        if (data.CustomScreens is { } custom)
        {
            foreach (var id in custom)
            {
                var placement = state.Output.Placements.FirstOrDefault(p => p.ScreenId == id);
                if (placement is { Planned: true })
                {
                    soft($"{where}: look '{look.Name}' targets planned screen '{Rig(placement)}' — adopt it at the venue.");
                }
            }
        }
    }

    private static IEnumerable<PatternConfig> Patterns(LookData data)
    {
        if (data.Pattern is not null) yield return data.Pattern;
        if (data.Independent is null) yield break;
        foreach (var a in data.Independent) yield return a.Pattern;
    }

    private static string Rig(ScreenPlacement p) => p.CustomLabel.Length > 0 ? p.CustomLabel : p.ScreenId;
}
