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
    /// <summary>Main → a standby that took over: the main has the show again — close the outputs, follow again; the show and the air follow the word.</summary>
    HandBack,
    /// <summary>Main → a caller: the caller's stack as it runs on the main — the standby cue, the last, armed, hold — as <see cref="TwinLive"/> JSON, once a second and on every change.</summary>
    Live,
    /// <summary>A caller → main: a verb of the show to run there — a <see cref="Patterns.Core.Model.ShowAction"/> as JSON: GO, STANDBY, HOLD, the timer's, a message to stage.</summary>
    Act,
    /// <summary>A caller → main: the cues it planned at home — the Stacks section as JSON — offered for the desk to apply, never applied by itself.</summary>
    Plan,
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
            "HANDBACK" => TwinWord.HandBack,
            "LIVE" => TwinWord.Live,
            "ACT" => TwinWord.Act,
            "PLAN" => TwinWord.Plan,
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

/// <summary>
/// What a standby says as it joins: who it is and the key the main asked for — and, when it ran the
/// show while the main was away, that it has the show: the main then holds its own outputs, takes
/// nothing for granted, and TAKE BACK is the operator's press.
/// </summary>
public sealed record TwinJoin(string Name, string Machine, string Instance, string Key, int Proto = TwinMessage.Proto, bool TookOver = false, string Kind = "standby")
{
    /// <summary>A caller node joining: it follows the show and calls it, never holds an output, and may send its cues back.</summary>
    public bool IsCaller => string.Equals(Kind, "caller", StringComparison.OrdinalIgnoreCase);

    /// <summary>A stage timer node joining: it follows the show for the clock and the messages, sends a verb or a receipt, and owns nothing.</summary>
    public bool IsTimer => string.Equals(Kind, "timer", StringComparison.OrdinalIgnoreCase);

    /// <summary>A node that follows the desk's show without ever holding an output — a caller or a stage timer; a standby is not one.</summary>
    public bool IsFollower => IsCaller || IsTimer;

    public string ToJson() => JsonUtil.SerializeCompact(this);

    public static TwinJoin? Parse(string json)
    {
        try { return JsonUtil.Deserialize<TwinJoin>(json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// The caller's stack as it runs on the main, sent to every caller once a second and on every
/// change, so a caller's Run surface shows the desk's standby cue, its last GO, ARM and HOLD
/// rather than its own — the runtime is deliberately not in the show, so it travels as its own word.
/// </summary>
public sealed record TwinLive(string Standby, string Last, bool Armed, bool Hold, bool Executing, string AirLabel, bool Live, bool Blackout, string Timing, long Seq)
{
    public string ToJson() => JsonUtil.SerializeCompact(this);

    public static TwinLive? Parse(string json)
    {
        try { return JsonUtil.Deserialize<TwinLive>(json); }
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
/// A standby that took the show over says so on disk, in its own folder: which process has the
/// show, and since when. A main on the same machine — restarted by its watchdog, or relaunched by
/// hand — reads it before a window opens and holds its outputs while that process lives: two desks
/// driving the same screens is the one failure worse than one being down. A marker whose process
/// is gone (the standby crashed with the show) holds nothing, and the main runs the show as a
/// restart would.
/// </summary>
public sealed record TwinTookOverMarker(string Standby, string Machine, int Pid, long StartedAtUtcTicks, string ExePath, DateTime AtUtc, string MainName)
{
    public string ToJson() => JsonUtil.SerializeCompact(this);
}

/// <summary>
/// The key a main is given when it has none. A main with no key would let any machine on the
/// network join, hold its outputs closed and hand it a show, so there is no such main: the key is
/// made from the machine's random source — sixteen letters and digits in fours, with nothing that
/// reads two ways over a phone — and shown on the Machine page for the standby to copy.
/// </summary>
public static class TwinKeys
{
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";   // no i, l, o, 0 or 1

    public static string New()
    {
        var chars = new char[19];
        for (var i = 0; i < 16; i++) chars[i + i / 4] = Alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Alphabet.Length)];
        chars[4] = chars[9] = chars[14] = '-';
        return new string(chars);
    }

    /// <summary>A key this desk made, or one an operator typed in the same shape.</summary>
    public static bool LooksMade(string key) => key.Length == 19 && key[4] == '-' && key[9] == '-' && key[14] == '-';
}

/// <summary>The marker's file, its folder beside the main's, and the one decision: does it hold?</summary>
public static class TwinHandover
{
    public const string FileName = "twin.tookover.json";

    /// <summary>The folder a standby launched by this desk lives in: beside the show, its own settings, logs and crash domain.</summary>
    public const string StandbyFolder = "twin-standby";

    public static string StandbyHome(string mainHome) => Path.Combine(mainHome, StandbyFolder);

    public static string PathFor(string home) => Path.Combine(home, FileName);

    public static void Write(string home, TwinTookOverMarker marker)
    {
        Directory.CreateDirectory(home);
        File.WriteAllText(PathFor(home), marker.ToJson());
    }

    /// <summary>The marker in a folder, or null: none, or one this build cannot read.</summary>
    public static TwinTookOverMarker? Read(string home)
    {
        try
        {
            var path = PathFor(home);
            if (!File.Exists(path)) return null;
            return JsonUtil.Deserialize<TwinTookOverMarker>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Clear(string home)
    {
        try
        {
            File.Delete(PathFor(home));
        }
        catch (Exception)
        {
            // gone already, or a folder that never was
        }
    }

    /// <summary>The marker holds while its process lives and is the process that wrote it (the same start time) — a process id reused by another program holds nothing. The two-answer read: every process it can see, it can read.</summary>
    public static bool Holds(TwinTookOverMarker? marker, Func<int, long?> startTicksOf)
        => Holds(marker, ProcessSight.From(startTicksOf));

    /// <summary>
    /// The marker holds while its process lives and is the process that wrote it (the same start
    /// time), and also while its process is up but cannot be read from here: a standby this desk
    /// may not look into may still have the show, and a hold kept a little long costs a press
    /// where a hold dropped costs two desks on one set of screens. A process id reused by another
    /// program holds nothing.
    /// </summary>
    public static bool Holds(TwinTookOverMarker? marker, Func<int, ProcessSight> look)
        => marker is { Pid: > 0 } && look(marker.Pid) is { Exists: true } sight && (!sight.Readable || sight.StartTicks == marker.StartedAtUtcTicks);

    /// <summary>"the standby twin Backup desk has the show (took over at 19:41:58) — TAKE BACK on the Machine page".</summary>
    public static string HoldWords(string standby, DateTime? atUtc)
        => $"the standby twin {(standby.Length > 0 ? standby : "")}".TrimEnd() + " has the show"
           + (atUtc is { } at ? $" (took over at {at.ToLocalTime():HH:mm:ss})" : "") + " — TAKE BACK on the Machine page";
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
    /// <summary>The sections that are this machine's own: the twin link, the watchdog and the beacon, the install, the machine's admin (its graphics card), the remote's ports, the operator's monitor, the desk's layout, the show lock.</summary>
    public static readonly IReadOnlyList<string> LocalSections = new[]
    {
        nameof(ShowState.Twin), nameof(ShowState.Watchdog), nameof(ShowState.Install), nameof(ShowState.Admin),
        nameof(ShowState.Control), nameof(ShowState.Monitor), nameof(ShowState.Desk), nameof(ShowState.Lock),
    };

    private static readonly IReadOnlyList<PropertyInfo> Roots = typeof(ShowState)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
        .ToList();

    /// <summary>Every section of the show file, in the file's order — the names the publish reports dirty.</summary>
    public static IReadOnlyList<string> SectionNames { get; } = Roots.Select(p => p.Name).ToList();

    /// <summary>The sections a caller node owns and sends back to the desk: the cue stacks — the cues, their notes, the caller's pad. Nothing else a caller edits travels.</summary>
    public static readonly IReadOnlyList<string> CallerSections = new[] { nameof(ShowState.Stacks) };

    public static bool IsCallerSection(string section) => CallerSections.Contains(section);

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

    /// <summary>After a takeover by itself was refused (the hung main could not be ended, the marker could not be written): the next try, not one every second.</summary>
    public static readonly TimeSpan RetryAfterRefusal = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Why a standby may not take over by itself, or null when it may. A silence cannot tell a
    /// main that died from a cable that was cut, and two machines each deciding to be the main is
    /// the one failure worse than one being down. On this machine the fence is the kill: a main
    /// that is still up is ended first, so the decision is safe. From another machine the fence
    /// is the room's own — the wall-switch cue that puts this machine's input on the wall, so
    /// whichever desk the switcher shows is the one running the show — and without one, taking
    /// over stays the operator's press.
    /// </summary>
    public static string? AutoTakeOverBlocked(bool mainOnThisMachine, bool wallSwitchSet, string? fenceProblem = null)
    {
        if (mainOnThisMachine) return null;
        if (!wallSwitchSet) return "no wall-switch cue for a main on another machine, so not by itself: TAKE OVER is yours";
        // A cue whose box cannot answer is a hope, not a fence: the room may or may not have moved.
        return fenceProblem is null ? null : fenceProblem + ", so not by itself: TAKE OVER is yours";
    }

    /// <summary>"just now", "3 s ago".</summary>
    public static string Age(DateTime? utc, DateTime utcNow)
    {
        if (utc is null) return "never";
        var age = utcNow - utc.Value;
        return age.TotalSeconds < 1.5 ? "just now" : $"{age.TotalSeconds:0} s ago";
    }

    /// <summary>The standby's line on the Machine page and the health line.</summary>
    public static string DescribeStandby(TwinPhase phase, string mainName, DateTime? lastHeardUtc, long sectionsApplied, bool autoTakeOver, DateTime utcNow, string note = "", bool linked = false)
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
                return $"MAIN {main} SILENT for {silent} — {(autoTakeOver ? "taking over…" : "TAKE OVER?")}" + (note.Length > 0 ? $" · {note}" : "");
            case TwinPhase.TookOver:
                return linked
                    ? $"TOOK OVER from {main}{(note.Length > 0 ? " " + note : "")} — this desk runs the show; {main} is back on the link and its TAKE BACK puts the show there again, or STAND BY AGAIN here."
                    : $"TOOK OVER from {main}{(note.Length > 0 ? " " + note : "")} — this desk runs the show now. STAND BY AGAIN once {main} is back.";
            default:
                return phase.ToString();
        }
    }

    /// <summary>A caller node's line: alone with its plan, connecting, in step and calling, the desk silent, refused.</summary>
    /// <summary>A follower node's line — a caller's by default; a stage timer's with <paramref name="timer"/>, whose link shows the desk's clock rather than calling its show.</summary>
    public static string DescribeCaller(TwinPhase phase, string deskName, DateTime? lastHeardUtc, long sectionsApplied, DateTime utcNow, string note = "", bool linked = false, string airLabel = "", bool timer = false)
    {
        var desk = deskName.Length > 0 ? deskName : "the desk";
        var role = timer ? "STAGE TIMER" : "CALLER";
        switch (phase)
        {
            case TwinPhase.Off:
                return timer ? "STAGE TIMER — its own clock, alone; LINK on the Nodes page follows a desk's." : "CALLER — planning alone; LINK on the Nodes page joins a desk.";
            case TwinPhase.Connecting:
                return deskName.Length > 0 ? $"{role} — connecting to {deskName}…" : $"{role} — waiting for a desk: its beacon names it on the Nodes page.";
            case TwinPhase.Refused:
                return $"{role} — {desk} refused the link{(note.Length > 0 ? ": " + note : "")}. Enter the desk's twin key (Machine page, TWIN).";
            case TwinPhase.InStep:
                return $"{role} for {desk} — in step, heard {Age(lastHeardUtc, utcNow)}, {sectionsApplied} section{(sectionsApplied == 1 ? "" : "s")} mirrored"
                       + (airLabel.Length > 0 ? $" · on air there: {airLabel}" : "")
                       + (timer ? " · the desk's clock and its messages show here." : " · GO, STANDBY and HOLD from here run there.");
            case TwinPhase.MainSilent:
                var silent = lastHeardUtc is { } heard ? $"{(utcNow - heard).TotalSeconds:0} s" : "a while";
                return timer ? $"DESK {desk} SILENT for {silent} — the clock runs on as last heard." : $"DESK {desk} SILENT for {silent} — calling waits; the cues stay here.";
            default:
                return phase.ToString();
        }
    }

    /// <summary>The main's line: the port, and each standby with when it was last heard — and, when a standby has the show, that this desk's outputs wait on TAKE BACK.</summary>
    public static string DescribeMain(int port, IReadOnlyList<(string Name, DateTime LastBeatUtc)> standbys, long sectionsSent, DateTime utcNow, string holder = "", string launcher = "", string handover = "")
    {
        var tail = launcher.Length > 0 ? " " + launcher : "";
        if (holder.Length > 0)
        {
            var linked = standbys.Any(s => s.Name == holder);
            // A take-back that stopped at the wall switch: the picture is up here and the room
            // still shows the standby — said first, because it is the one thing to do next.
            if (handover.Length > 0) return $"MAIN — {handover}" + tail;
            return $"MAIN — the standby {holder} HAS THE SHOW; this desk's outputs are held closed. "
                   + (linked ? "TAKE BACK puts the show back here." : $"It is not on the link yet — TAKE BACK once it is, or OUTPUTS ON if it is gone.") + tail;
        }
        if (standbys.Count == 0) return $"MAIN — listening for a standby on port {port}; none connected." + tail;
        var parts = standbys.Select(s => IsSilent(s.LastBeatUtc, utcNow)
            ? $"standby {s.Name} SILENT for {(utcNow - s.LastBeatUtc).TotalSeconds:0} s"
            : $"standby {s.Name} in step (heard {Age(s.LastBeatUtc, utcNow)})");
        return $"MAIN — {string.Join(" · ", parts)} · {sectionsSent} section{(sectionsSent == 1 ? "" : "s")} sent." + tail;
    }
}
