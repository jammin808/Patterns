using System.Globalization;
using Patterns.Core.Model;

namespace Patterns.Core.Menus;

/// <summary>
/// The edits a layer's, an overlay's or the countdown's menu makes to the picture being built —
/// by key, on the edited state, which is the preview while EDIT SAFE is open (the desk opens it
/// first). Pure, so a test can apply a key to a show and read it back, and the words that come
/// back are the status line's. The keys:
/// layer.on:&lt;1|2&gt; · layer.source:&lt;i&gt;:&lt;Source&gt; · layer.fit:&lt;i&gt;:&lt;Fit&gt; · layer.media:&lt;i&gt;:&lt;entryId&gt; ·
/// overlay.on:&lt;kind&gt; · overlay.anchor:&lt;kind&gt;:&lt;Anchor9&gt; ·
/// countdown.start:&lt;minutes&gt; · countdown.stop · countdown.follow · countdown.label:&lt;words&gt; · countdown.anchor:&lt;Anchor9&gt;.
/// </summary>
public static class PreviewEdits
{
    /// <summary>The countdown labels the menu offers.</summary>
    public static readonly IReadOnlyList<string> CountdownLabels = new[] { "STARTING IN", "DOORS OPEN IN", "BACK IN", "BREAK ENDS IN", "LUNCH ENDS IN", "REHEARSAL RESUMES IN", "" };

    /// <summary>The countdown lengths the menu offers, in minutes.</summary>
    public static readonly IReadOnlyList<double> CountdownMinutes = new double[] { 1, 2, 5, 10, 15, 30 };

    /// <summary>The overlay kinds a menu names, in the desk's order.</summary>
    public static readonly IReadOnlyList<(string Kind, string Label)> OverlayKinds = new[]
    {
        ("clock", "Clock"), ("logo", "Logo"), ("message", "Message"), ("pip", "Picture-in-picture"), ("weather", "Weather"), ("badge", "Badge"), ("info", "Tech info chip"), ("countdown", "Countdown"),
    };

    /// <summary>The overlay of a kind — its Enabled flag and, when it has one, its anchor.</summary>
    public static (Func<bool> IsOn, Action<bool> SetOn, IAnchored? Anchored)? Overlay(ShowState state, string kind)
    {
        var o = state.Overlays;
        return kind switch
        {
            "clock" => (() => o.Clock.Enabled, v => o.Clock.Enabled = v, o.Clock),
            "logo" => (() => o.Logo.Enabled, v => o.Logo.Enabled = v, o.Logo),
            "message" => (() => o.Message.Enabled, v => o.Message.Enabled = v, o.Message),
            "pip" => (() => o.Pip.Enabled, v => o.Pip.Enabled = v, o.Pip),
            "weather" => (() => o.Weather.Enabled, v => o.Weather.Enabled = v, o.Weather),
            "badge" => (() => o.Badge.Enabled, v => o.Badge.Enabled = v, o.Badge),
            "info" => (() => o.Info.Enabled, v => o.Info.Enabled = v, null),
            "countdown" => (() => state.Countdown.Enabled, v => state.Countdown.Enabled = v, state.Countdown),
            _ => null,
        };
    }

    /// <summary>The label of an overlay kind, "" for one the menu does not name.</summary>
    public static string OverlayLabel(string kind) => OverlayKinds.FirstOrDefault(k => k.Kind == kind).Label ?? "";

    /// <summary>One edit by its key on the picture being built; the status words, or null when the key is not a preview edit.</summary>
    public static string? Apply(ShowState state, PatternConfig picture, string key, DateTime utcNow, DeskFacts facts)
    {
        var parts = key.Split(':');
        var verb = parts[0];
        switch (verb)
        {
            case "layer.on":
            case "layer.source":
            case "layer.fit":
            case "layer.media":
            {
                if (parts.Length < 2 || !int.TryParse(parts[1], out var index)) return null;
                var layer = index == 2 ? picture.Layer2 : index == 1 ? picture.Layer1 : null;
                if (layer is null) return null;
                var who = $"Layer {index}";
                switch (verb)
                {
                    case "layer.on":
                        layer.Enabled = !layer.Enabled;
                        return layer.Enabled ? $"{who} is on in the preview." : $"{who} is off in the preview.";
                    case "layer.source":
                        if (parts.Length < 3 || !Enum.TryParse<LayerSource>(parts[2], true, out var source)) return null;
                        layer.Source = source;
                        layer.Enabled = true;
                        return $"{who} shows {SourceWords(source)} — in the preview.";
                    case "layer.fit":
                        if (parts.Length < 3 || !Enum.TryParse<FitMode>(parts[2], true, out var fit)) return null;
                        layer.Fit = fit;
                        return $"{who}: {fit.ToString().ToLowerInvariant()} — in the preview.";
                    default:
                    {
                        if (parts.Length < 3) return null;
                        var entry = state.MediaLibrary.FirstOrDefault(m => m.Id == parts[2]);
                        if (entry is null) return "That picture is no longer in the library.";
                        if (entry.IsVideo)
                        {
                            layer.Source = LayerSource.Video;
                            layer.VideoPath = entry.Path;
                        }
                        else
                        {
                            layer.Source = LayerSource.Image;
                            layer.ImagePath = entry.Path;
                        }
                        layer.Enabled = true;
                        return $"{who} shows '{(entry.Name.Length > 0 ? entry.Name : Path.GetFileName(entry.Path))}' — in the preview.";
                    }
                }
            }
            case "overlay.on":
            {
                if (parts.Length < 2 || Overlay(state, parts[1]) is not { } overlay) return null;
                var on = !overlay.IsOn();
                overlay.SetOn(on);
                if (parts[1] == "countdown" && on && state.Countdown.TargetKind == CountdownTargetKind.Duration) state.Countdown.ArmedAtUtc = utcNow;
                return $"{OverlayLabel(parts[1])} {(on ? "on" : "off")} — in the preview.";
            }
            case "overlay.anchor":
            {
                if (parts.Length < 3 || Overlay(state, parts[1]) is not { Anchored: { } anchored } || !Enum.TryParse<Anchor9>(parts[2], true, out var anchor)) return null;
                anchored.Anchor = anchor;
                anchored.OffsetXPct = 0;
                anchored.OffsetYPct = 0;
                return $"{OverlayLabel(parts[1])} at {AnchorWords(anchor).ToLowerInvariant()} — in the preview.";
            }
            case "countdown.start":
            {
                if (parts.Length < 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0) return null;
                state.Countdown.TargetKind = CountdownTargetKind.Duration;
                state.Countdown.DurationMinutes = minutes;
                state.Countdown.ArmedAtUtc = utcNow;
                state.Countdown.Enabled = true;
                return $"Countdown of {minutes:0.#} min running in the preview — TAKE puts it on air, running from now.";
            }
            case "countdown.stop":
                state.Countdown.Enabled = false;
                return "Countdown off in the preview.";
            case "countdown.follow":
                state.Countdown.FollowPlan = !state.Countdown.FollowPlan;
                return state.Countdown.FollowPlan
                    ? "The countdown follows the running order — the standby cue's planned start is its target."
                    : "The countdown no longer follows the running order.";
            case "countdown.label":
            {
                var words = parts.Length > 1 ? string.Join(":", parts.Skip(1)) : "";
                state.Countdown.Label = words;
                return words.Length == 0 ? "Countdown label cleared — in the preview." : $"Countdown label '{words}' — in the preview.";
            }
            case "countdown.anchor":
            {
                if (parts.Length < 2 || !Enum.TryParse<Anchor9>(parts[1], true, out var anchor)) return null;
                state.Countdown.Anchor = anchor;
                state.Countdown.OffsetXPct = 0;
                state.Countdown.OffsetYPct = 0;
                return $"Countdown at {AnchorWords(anchor).ToLowerInvariant()} — in the preview.";
            }
            default:
                return null;
        }
    }

    public static string SourceWords(LayerSource source) => source switch
    {
        LayerSource.Image => "a still",
        LayerSource.Video => "a clip",
        LayerSource.NdiFeed => "an NDI feed",
        LayerSource.Capture => "a capture device",
        LayerSource.Screen => "another screen's picture",
        LayerSource.Web => "a web page",
        LayerSource.Arcade => "the arcade",
        _ => source.ToString(),
    };

    public static string AnchorWords(Anchor9 anchor) => anchor switch
    {
        Anchor9.TopLeft => "Top left",
        Anchor9.TopCenter => "Top centre",
        Anchor9.TopRight => "Top right",
        Anchor9.MiddleLeft => "Middle left",
        Anchor9.Center => "Centre",
        Anchor9.MiddleRight => "Middle right",
        Anchor9.BottomLeft => "Bottom left",
        Anchor9.BottomCenter => "Bottom centre",
        Anchor9.BottomRight => "Bottom right",
        _ => anchor.ToString(),
    };
}
