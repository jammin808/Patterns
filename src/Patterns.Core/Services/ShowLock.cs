using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>Where one item of the show lock stands.</summary>
public enum LockItemState
{
    /// <summary>Held: the machine does what the lock asked.</summary>
    Done,
    /// <summary>Not asked for — the operator left this item out.</summary>
    Off,
    /// <summary>Not on this machine: another operating system, or a setting it has not got.</summary>
    NotHere,
    /// <summary>The setting exists but needs an administrator — the one-time script in docs/SHOW-MACHINE.md.</summary>
    NeedsAdmin,
    /// <summary>Tried and refused; the words say why.</summary>
    Failed,
}

/// <summary>One thing the lock holds, and how it stands.</summary>
public sealed record LockItem(string Key, string Title, LockItemState State, string Words)
{
    /// <summary>The Machine page's line: a mark, the title, the words.</summary>
    public string Line => State switch
    {
        LockItemState.Done => $"✓ {Title}{(Words.Length > 0 ? " — " + Words : "")}",
        LockItemState.Off => $"· {Title} — left as it is",
        LockItemState.NotHere => $"– {Title} — {(Words.Length > 0 ? Words : "not on this machine")}",
        LockItemState.NeedsAdmin => $"⚠ {Title} — {(Words.Length > 0 ? Words : "needs an administrator")}",
        _ => $"✗ {Title} — {(Words.Length > 0 ? Words : "could not be set")}",
    };
}

/// <summary>
/// What the lock holds right now. Pure, so the words the Machine page, the health line, the
/// super-check, the wire and the assistant read are one set, unit tested.
/// </summary>
public sealed record LockReport(bool Locked, DateTime? SinceUtc, IReadOnlyList<LockItem> Items, bool UpdateRestartPending, bool Auto = false)
{
    public static readonly LockReport Unlocked = new(false, null, Array.Empty<LockItem>(), false);

    public int Failed => Items.Count(i => i.State == LockItemState.Failed);

    public int NeedAdmin => Items.Count(i => i.State == LockItemState.NeedsAdmin);

    /// <summary>The one line: "LOCKED since 19:30:02 — notifications off · system sounds off · 3 other apps' audio muted · …", or what LOCK would hold.</summary>
    public string Summary
    {
        get
        {
            var update = UpdateRestartPending ? " · Windows Update: a restart is pending — pause updates before doors (needs an administrator: docs/SHOW-MACHINE.md)" : "";
            if (!Locked)
            {
                return "Not locked — LOCK THE MACHINE holds off notifications, system sounds, other apps' audio, the shortcut keys, sleep and the Windows key for the show." + update;
            }
            var held = Items.Where(i => i.State == LockItemState.Done).Select(i => i.Words.Length > 0 ? i.Words : i.Title.ToLowerInvariant()).ToList();
            var wrong = Items.Where(i => i.State is LockItemState.Failed or LockItemState.NeedsAdmin).Select(i => i.Line).ToList();
            var since = SinceUtc is { } at ? $" since {at.ToLocalTime():HH:mm:ss}" : "";
            var head = $"LOCKED{since}{(Auto ? " (with the outputs)" : "")}";
            var body = held.Count > 0 ? " — " + string.Join(" · ", held) : "";
            var tail = wrong.Count > 0 ? " · " + string.Join(" · ", wrong) : "";
            return head + body + tail + update;
        }
    }

    /// <summary>The health line's clause: "" while all is well, else what is wrong.</summary>
    public string HealthWords(bool outputsLive)
    {
        if (!Locked) return outputsLive ? "SHOW LOCK OFF — the outputs are live and the machine is not held: Machine page, LOCK THE MACHINE" : "";
        var failed = Items.Where(i => i.State == LockItemState.Failed).Select(i => i.Title.ToLowerInvariant()).ToList();
        return failed.Count == 0 ? "" : $"SHOW LOCK: {string.Join(", ", failed)} could not be held";
    }

    /// <summary>The super-check's light: green held, amber not locked or an admin item, red a failed item or a pending restart while locked.</summary>
    public CheckLight Light(bool outputsLive)
    {
        if (!Locked) return outputsLive ? CheckLight.Amber : CheckLight.Grey;
        if (Failed > 0 || UpdateRestartPending) return CheckLight.Red;
        return NeedAdmin > 0 ? CheckLight.Amber : CheckLight.Green;
    }

    /// <summary>The super-check's value: "held" / "held · Windows Update restart pending" / "off".</summary>
    public string Value => !Locked ? "off" : Failed > 0 ? $"{Failed} item(s) not held" : UpdateRestartPending ? "held · Windows Update restart pending" : "held";
}

/// <summary>The lock's fixed list of items, in the order the page shows them, and the words for each.</summary>
public static class ShowLockWords
{
    public const string Notifications = "notifications";
    public const string Sounds = "sounds";
    public const string Audio = "audio";
    public const string Shortcuts = "shortcuts";
    public const string Awake = "awake";
    public const string WindowsKey = "winkey";
    public const string Updates = "updates";

    public static string TitleOf(string key) => key switch
    {
        Notifications => "Notifications",
        Sounds => "System sounds",
        Audio => "Other apps' audio",
        Shortcuts => "Sticky, Filter and Toggle Keys shortcuts",
        Awake => "Sleep and the screensaver",
        WindowsKey => "The Windows key",
        Updates => "Windows Update",
        _ => key,
    };

    /// <summary>"3 other apps' audio muted" / "no other app playing".</summary>
    public static string MutedWords(int muted) => muted switch
    {
        < 0 => "could not be read",
        0 => "no other app is playing; any that starts is muted",
        1 => "1 other app's audio muted",
        _ => $"{muted} other apps' audio muted",
    };

    /// <summary>The names a lock lets through, from the config's line: "Spotify, vlc" → ["spotify", "vlc"].</summary>
    public static IReadOnlyList<string> Allowed(string line)
        => line.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant().Replace(".exe", ""))
            .Distinct()
            .ToList();

    /// <summary>Whether a process may keep its sound: named in the allowed list, by name or by a name that starts with it.</summary>
    public static bool IsAllowed(string processName, IReadOnlyList<string> allowed)
    {
        var name = processName.ToLowerInvariant().Replace(".exe", "");
        return allowed.Any(a => name == a || name.StartsWith(a, StringComparison.Ordinal));
    }
}
