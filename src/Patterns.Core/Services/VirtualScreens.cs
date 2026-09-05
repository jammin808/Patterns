using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The virtual screens: every NDI sender owns one, the stream owns one while it is set to its
/// own screen. Each is a planned placement (sized from the model, never a window) marked with
/// its feed in <see cref="ScreenPlacement.Virtual"/>. <see cref="Sync"/> keeps the set, the
/// sizes and the default labels in step with the feeds; idempotent, so it runs on load, on the
/// desk's poll and after every add or remove.
/// </summary>
public static class VirtualScreens
{
    public const string NdiLabelPrefix = "NDI · ";
    public const string StreamLabel = "STREAM";

    public static bool IsVirtualId(string? id)
        => id is not null && (id.StartsWith("ndi:", StringComparison.Ordinal) || id == StreamConfig.OwnScreenId);

    /// <summary>The feed a virtual screen id belongs to: "ndi:&lt;id&gt;" or "stream"; "" for anything else.</summary>
    public static string OwnerOf(string? id)
        => id is null ? "" : id.StartsWith("ndi:", StringComparison.Ordinal) ? id : id == StreamConfig.OwnScreenId ? "stream" : "";

    /// <summary>Brings the placements in step with the feeds. True when anything changed.</summary>
    public static bool Sync(ShowState state)
    {
        var changed = false;
        var placements = state.Output.Placements;
        var wanted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sender in state.Ndi.Senders)
        {
            var id = sender.OwnScreenId;
            wanted.Add(id);
            var name = string.IsNullOrWhiteSpace(sender.Name) ? "Patterns" : sender.Name.Trim();
            var placement = placements.FirstOrDefault(p => p.ScreenId == id);
            if (placement is null)
            {
                placements.Add(new ScreenPlacement
                {
                    ScreenId = id,
                    Planned = true,
                    Virtual = id,
                    PlannedWidth = sender.Width,
                    PlannedHeight = sender.Height,
                    CustomLabel = NdiLabelPrefix + name,
                    Enabled = true,
                    UserPinned = true,
                    X = NextX(placements),
                    Y = NextY(placements),
                });
                changed = true;
                continue;
            }
            changed |= Follow(placement, id, sender.Width, sender.Height, NdiLabelPrefix + name, NdiLabelPrefix);
        }

        if (state.Stream.UsesOwnScreen)
        {
            var id = StreamConfig.OwnScreenId;
            wanted.Add(id);
            var placement = placements.FirstOrDefault(p => p.ScreenId == id);
            if (placement is null)
            {
                placements.Add(new ScreenPlacement
                {
                    ScreenId = id,
                    Planned = true,
                    Virtual = "stream",
                    PlannedWidth = state.Stream.Width,
                    PlannedHeight = state.Stream.Height,
                    CustomLabel = StreamLabel,
                    Enabled = true,
                    UserPinned = true,
                    X = NextX(placements),
                    Y = NextY(placements),
                });
                changed = true;
            }
            else
            {
                changed |= Follow(placement, "stream", state.Stream.Width, state.Stream.Height, StreamLabel, StreamLabel);
            }
        }

        // A feed that went takes its screen — and its own content — with it.
        for (var i = placements.Count - 1; i >= 0; i--)
        {
            var p = placements[i];
            if (!IsVirtualId(p.ScreenId) || wanted.Contains(p.ScreenId)) continue;
            placements.RemoveAt(i);
            var assignment = state.Independent.FirstOrDefault(a => a.ScreenId == p.ScreenId);
            if (assignment is not null) state.Independent.Remove(assignment);
            changed = true;
        }
        return changed;
    }

    private static bool Follow(ScreenPlacement p, string owner, int width, int height, string label, string labelPrefix)
    {
        var changed = false;
        if (!p.Planned) { p.Planned = true; changed = true; }
        if (p.Virtual != owner) { p.Virtual = owner; changed = true; }
        if (p.PlannedWidth != width) { p.PlannedWidth = width; changed = true; }
        if (p.PlannedHeight != height) { p.PlannedHeight = height; changed = true; }
        // The operator's own name stays; a default label follows the feed's name.
        if ((p.CustomLabel.Length == 0 || p.CustomLabel.StartsWith(labelPrefix, StringComparison.Ordinal)) && p.CustomLabel != label)
        {
            p.CustomLabel = label;
            changed = true;
        }
        return changed;
    }

    /// <summary>Virtual screens line up in a row below everything arranged, so they never touch a display.</summary>
    private static int NextX(IList<ScreenPlacement> placements)
    {
        var x = 0;
        foreach (var p in placements)
        {
            if (IsVirtualId(p.ScreenId)) x = Math.Max(x, p.X + p.PlannedWidth + 64);
        }
        return x;
    }

    private static int NextY(IList<ScreenPlacement> placements)
    {
        var bottom = 0;
        foreach (var p in placements)
        {
            if (IsVirtualId(p.ScreenId)) return p.Y;
            bottom = Math.Max(bottom, p.Y + (p.Planned ? p.PlannedHeight : 1080));
        }
        return bottom + 240;
    }
}
