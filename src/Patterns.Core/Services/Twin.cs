using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>The words of the twin link, one per line: a standby joins, the main welcomes or refuses it, then the show, its sections as they move, the air record, and a beat each way every second.</summary>
public enum TwinWord
{
    Unknown,
    /// <summary>Standby → main: <see cref="TwinJoin"/> as JSON.</summary>
    Join,
    /// <summary>Main → standby: <see cref="TwinWelcome"/> as JSON; the whole show follows.</summary>
    Welcome,
    /// <summary>Main → standby: the reason, in words; the main then closes the link.</summary>
    Refused,
    /// <summary>Main → standby: the whole show as JSON — on welcome, and whenever a change could not be named by section.</summary>
    Show,
    /// <summary>Main → standby: one section of the show — its name, a space, its JSON.</summary>
    Section,
    /// <summary>Main → standby: the recovery record (what is on air, the caller's place) as JSON, or "null" when nothing is live.</summary>
    Air,
    /// <summary>Either way, once a second: a sequence number. Five missed in a row is a peer that is gone.</summary>
    Beat,
    /// <summary>Either way: the peer is leaving on purpose (a role changed, a clean exit) — not a fault.</summary>
    Bye,
}

/// <summary>
/// One line of the twin link: the word, the section's name after SECTION, and the payload — JSON,
/// a number, a reason — after that. Pure, so the words are unit tested and a stray line never throws.
/// </summary>
public readonly record struct TwinMessage(TwinWord Word, string Name, string Payload)
{
    /// <summary>The link's version; a peer speaking another answers REFUSED.</summary>
    public const int Proto = 1;

    public static TwinMessage Parse(string? line)
    {
        var s = (line ?? "").Trim();
        if (s.Length == 0) return new(TwinWord.Unknown, "", "");
        var parts = s.Split(' ', 2, StringSplitOptions.TrimEntries);
        var word = parts[0].ToUpperInvariant() switch
        {
            "JOIN" => TwinWord.Join,
            "WELCOME" => TwinWord.Welcome,
            "REFUSED" => TwinWord.Refused,
            "SHOW" => TwinWord.Show,
            "SECTION" => TwinWord.Section,
            "AIR" => TwinWord.Air,
            "BEAT" => TwinWord.Beat,
            "BYE" => TwinWord.Bye,
            _ => TwinWord.Unknown,
        };
        var rest = parts.Length > 1 ? parts[1] : "";
        if (word == TwinWord.Section)
        {
            var named = rest.Split(' ', 2, StringSplitOptions.TrimEntries);
            return new(word, named[0], named.Length > 1 ? named[1] : "");
        }
        return new(word, word == TwinWord.Unknown ? parts[0] : "", rest);
    }

    /// <summary>The line for a word: "SECTION LooksAndCues {…}", "BEAT 12", "BYE".</summary>
    public static string Format(TwinWord word, string payload = "", string name = "")
    {
        var head = word.ToString().ToUpperInvariant();
        if (name.Length > 0) head += " " + name;
        return payload.Length > 0 ? head + " " + payload : head;
    }
}

/// <summary>What a standby says as it joins: who it is and the key the main asked for.</summary>
public sealed record TwinJoin(string Name, string Machine, string Instance, string Key, int Proto = TwinMessage.Proto)
{
    public string ToJson() => JsonUtil.SerializeCompact(this);

    public static TwinJoin? Parse(string json)
    {
        try { return JsonUtil.Deserialize<TwinJoin>(json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// What the main answers: who it is, the process behind it (so a standby on the same machine can
/// end a main that has hung, and only that process), and the show's name.
/// </summary>
public sealed record TwinWelcome(string Name, string Machine, string Instance, int Pid, long StartedAtUtcTicks, string ExePath, string Show, int Proto = TwinMessage.Proto)
{
    public string ToJson() => JsonUtil.SerializeCompact(this);

    public static TwinWelcome? Parse(string json)
    {
        try { return JsonUtil.Deserialize<TwinWelcome>(json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// The mirror: which sections of the show travel to a standby and how they land. The main sends a
/// section whenever the publish that follows an edit names it dirty — the same names the snapshot
/// bus copies by — and the whole show when a change could not be named. The standby copies each
/// one onto its own show in place, so its bindings, its lists and its outputs (held closed) follow
/// without a reload. The machine's own sections never travel: a standby that copied the main's
/// twin settings would become a main, and one that copied its beacon settings would start
/// sending the main's heartbeat.
/// </summary>
public static class TwinSync
{
    /// <summary>The sections that are this machine's own: the twin link, the watchdog and the beacon, the install, the machine's admin (its graphics card), the remote's ports, the operator's monitor, the desk's layout.</summary>
    public static readonly IReadOnlyList<string> LocalSections = new[]
    {
        nameof(ShowState.Twin), nameof(ShowState.Watchdog), nameof(ShowState.Install), nameof(ShowState.Admin),
        nameof(ShowState.Control), nameof(ShowState.Monitor), nameof(ShowState.Desk),
    };

    private static readonly IReadOnlyList<PropertyInfo> Roots = typeof(ShowState)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
        .ToList();

    /// <summary>Every section of the show file, in the file's order — the names the publish reports dirty.</summary>
    public static IReadOnlyList<string> SectionNames { get; } = Roots.Select(p => p.Name).ToList();

    /// <summary>The sections that travel: every section of the file but the machine's own.</summary>
    public static IReadOnlyList<string> MirroredSections { get; } = Roots.Select(p => p.Name).Where(n => !LocalSections.Contains(n)).ToList();

    public static bool IsMirrored(string section) => Section(section) is not null && !LocalSections.Contains(section);

    private static PropertyInfo? Section(string name) => Roots.FirstOrDefault(p => p.Name == name);

    /// <summary>The dirty sections a publish reported, kept to the ones that travel, each once, in the file's order.</summary>
    public static IReadOnlyList<string> Mirrored(IEnumerable<string> sections)
    {
        var wanted = new HashSet<string>(sections, StringComparer.Ordinal);
        return MirroredSections.Where(wanted.Contains).ToList();
    }

    /// <summary>One section as compact JSON: the SECTION line's payload.</summary>
    public static string SectionJson(ShowState state, string section)
    {
        var pi = Section(section) ?? throw new ArgumentException($"'{section}' is not a section of the show.", nameof(section));
        return JsonSerializer.Serialize(pi.GetValue(state), pi.PropertyType, JsonUtil.CloneOptions);
    }

    /// <summary>The whole show as compact JSON: the SHOW line's payload.</summary>
    public static string ShowJson(ShowState state) => JsonUtil.SerializeCompact(state);

    /// <summary>
    /// A section's JSON onto the show, in place. False — and nothing touched — for a section that
    /// does not travel, one this build does not know, or JSON that is not that section.
    /// </summary>
    public static bool ApplySection(ShowState target, string section, string json)
    {
        var pi = Section(section);
        if (pi is null || !IsMirrored(section)) return false;
        object? value;
        try
        {
            value = JsonSerializer.Deserialize(json, pi.PropertyType, JsonUtil.Options);
        }
        catch (JsonException)
        {
            return false;
        }
        if (value is null && !pi.PropertyType.IsValueType && pi.PropertyType != typeof(string)) return false;
        ModelCopier.CopyValue(pi, value, target);
        return true;
    }

    /// <summary>The whole show onto this one, section by section, the machine's own left as they are. False for JSON that is not a show.</summary>
    public static bool ApplyShow(ShowState target, string json)
    {
        ShowState? incoming;
        try
        {
            incoming = JsonUtil.Deserialize<ShowState>(json);
        }
        catch (JsonException)
        {
            return false;
        }
        if (incoming is null) return false;
        foreach (var pi in Roots)
        {
            if (LocalSections.Contains(pi.Name)) continue;
            ModelCopier.CopyValue(pi, pi.GetValue(incoming), target);
        }
        return true;
    }
}

/// <summary>Where the twin link stands, on either side.</summary>
public enum TwinPhase
{
    Off,
    /// <summary>The main: the port is open; standbys may join.</summary>
    Listening,
    /// <summary>The standby: no link yet — dialling the address, or waiting for a main's beacon to name one.</summary>
    Connecting,
    /// <summary>The standby: joined, the show mirrored, the main heard within the limit.</summary>
    InStep,
    /// <summary>The standby: the main has been silent past the limit, or said goodbye without a role change here.</summary>
    MainSilent,
    /// <summary>The standby ran the show from here — by hand or on silence — and mirrors nothing until told to stand by again.</summary>
    TookOver,
    /// <summary>The standby: the main refused the link (a wrong key, another version); nothing is retried until the settings change.</summary>
    Refused,
}

/// <summary>What either side makes of the beats it hears — pure, so the timing and the words are unit tested.</summary>
public static class TwinWatch
{
    /// <summary>A beat comes once a second; five missed in a row is a peer that is gone, not a lost packet.</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(5);

    /// <summary>How often each side beats, and how often a standby with no link dials again.</summary>
    public static readonly TimeSpan BeatEvery = TimeSpan.FromSeconds(1);

    public static bool IsSilent(DateTime? lastHeardUtc, DateTime utcNow)
        => lastHeardUtc is { } heard && utcNow - heard > SilentAfter;

    /// <summary>A standby takes the show on its own only when told it may, and only once the main has been silent past the limit.</summary>
    public static bool ShouldTakeOver(bool autoTakeOver, TwinPhase phase, DateTime? lastHeardUtc, DateTime utcNow)
        => autoTakeOver && phase is TwinPhase.InStep or TwinPhase.MainSilent && IsSilent(lastHeardUtc, utcNow);

    /// <summary>"just now", "3 s ago".</summary>
    public static string Age(DateTime? utc, DateTime utcNow)
    {
        if (utc is null) return "never";
        var age = utcNow - utc.Value;
        return age.TotalSeconds < 1.5 ? "just now" : $"{age.TotalSeconds:0} s ago";
    }

    /// <summary>The standby's line on the Machine page and the health line.</summary>
    public static string DescribeStandby(TwinPhase phase, string mainName, DateTime? lastHeardUtc, long sectionsApplied, bool autoTakeOver, DateTime utcNow, string note = "")
    {
        var main = mainName.Length > 0 ? mainName : "the main";
        switch (phase)
        {
            case TwinPhase.Off:
                return "Twin off.";
            case TwinPhase.Connecting:
                return mainName.Length > 0
                    ? $"STANDBY — connecting to {mainName}…"
                    : "STANDBY — waiting for a main: its beacon names it, or type its address above.";
            case TwinPhase.Refused:
                return $"STANDBY — {main} refused the link{(note.Length > 0 ? ": " + note : "")}. Check the key on both machines.";
            case TwinPhase.InStep:
                return $"STANDBY for {main} — in step, heard {Age(lastHeardUtc, utcNow)}, {sectionsApplied} section{(sectionsApplied == 1 ? "" : "s")} mirrored · outputs held closed"
                       + (autoTakeOver ? " · takes over on silence." : " · TAKE OVER is yours.");
            case TwinPhase.MainSilent:
                var silent = lastHeardUtc is { } heard ? $"{(utcNow - heard).TotalSeconds:0} s" : "a while";
                return $"MAIN {main} SILENT for {silent} — {(autoTakeOver ? "taking over…" : "TAKE OVER?")}";
            case TwinPhase.TookOver:
                return $"TOOK OVER from {main}{(note.Length > 0 ? " " + note : "")} — this desk runs the show now. STAND BY AGAIN once {main} is back.";
            default:
                return phase.ToString();
        }
    }

    /// <summary>The main's line: the port, and each standby with when it was last heard.</summary>
    public static string DescribeMain(int port, IReadOnlyList<(string Name, DateTime LastBeatUtc)> standbys, long sectionsSent, DateTime utcNow)
    {
        if (standbys.Count == 0) return $"MAIN — listening for a standby on port {port}; none connected.";
        var parts = standbys.Select(s => IsSilent(s.LastBeatUtc, utcNow)
            ? $"standby {s.Name} SILENT for {(utcNow - s.LastBeatUtc).TotalSeconds:0} s"
            : $"standby {s.Name} in step (heard {Age(s.LastBeatUtc, utcNow)})");
        return $"MAIN — {string.Join(" · ", parts)} · {sectionsSent} section{(sectionsSent == 1 ? "" : "s")} sent.";
    }
}
