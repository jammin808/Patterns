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
}

/// <summary>Capture/apply logic for looks, plus cue-firing arithmetic. Pure and unit tested.</summary>
public static class LookService
{
    public static string Capture(ShowState state)
    {
        var data = new LookData
        {
            Pattern = JsonUtil.Clone(state.Pattern),
            Independent = JsonUtil.Clone(state.Independent),
            Overlays = JsonUtil.Clone(state.Overlays),
            Countdown = JsonUtil.Clone(state.Countdown),
            Blackout = state.Blackout,
        };
        return JsonUtil.Serialize(data);
    }

    /// <summary>Applies a captured look in place (bindings keep their object references).</summary>
    public static bool Apply(string lookJson, ShowState state)
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
