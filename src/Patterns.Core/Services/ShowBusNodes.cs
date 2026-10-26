using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Patterns.Core.Services;

/// <summary>
/// Round 88 (closes L51): the Stagewatch a desk follows, handed to its timer and caller nodes over the twin — the frame
/// and how old it was when it left, never a key or an address a node could dial with. A node that has no link of its own
/// shows the same time the desk does; one with its own link keeps it. Pure: the twin carries the JSON.
/// </summary>
public sealed record StagewatchRelay
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    /// <summary>"live", "stale" or "gone" as the desk sees its link.</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "gone";
    [JsonPropertyName("frame")] public StagewatchFrame? Frame { get; init; }
    /// <summary>How old the frame was when the desk sent it, in seconds (the node counts on from there).</summary>
    [JsonPropertyName("age")] public double FrameAgeSeconds { get; init; }
    [JsonPropertyName("seq")] public long Seq { get; init; }
    /// <summary>Round 89: the Stagewatch says its show is stuck — said even with no frame held, so a node's readers agree with the desk's.</summary>
    [JsonPropertyName("stuck")] public bool Stuck { get; init; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static StagewatchRelay? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024) return null;
        try
        {
            var r = JsonSerializer.Deserialize<StagewatchRelay>(json, Options);
            if (r is null || !double.IsFinite(r.FrameAgeSeconds)) return null;
            return r with { Name = r.Name.Length > 64 ? r.Name[..64] : r.Name, FrameAgeSeconds = Math.Clamp(r.FrameAgeSeconds, 0, 3600) };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What the desk follows now, as it goes to a node; an empty relay when it follows nothing.</summary>
    public static StagewatchRelay Of(StagewatchSight sight, double nowSeconds, long seq) => new()
    {
        Name = sight.Name,
        State = Word(sight.State),
        Frame = sight.Frame,
        FrameAgeSeconds = sight.Frame is null ? 0 : Math.Max(0, nowSeconds - sight.FrameAtSeconds),
        Seq = seq,
        Stuck = sight.Stuck,
    };

    public bool Empty => Name.Length == 0 && Frame is null;

    /// <summary>
    /// The relay as this node's sight: the frame placed where it would have arrived had the node heard it itself, and the
    /// state the worse of the desk's word and the relay's own silence — a desk that stops relaying goes stale here too.
    /// A stuck show reaches the node as the desk holds it: the held frame no longer live, and the stuck mark beside it.
    /// </summary>
    public StagewatchSight ToSight(double receivedAtSeconds, double nowSeconds)
    {
        if (Empty) return StagewatchSight.None;
        var desk = State switch { "live" => ShowBusState.Live, "stale" => ShowBusState.Stale, _ => ShowBusState.Gone };
        var silence = ShowBusLiveness.StateOf(TimeSpan.FromSeconds(Math.Max(0, nowSeconds - receivedAtSeconds)));
        var state = (ShowBusState)Math.Max((int)desk, (int)silence);
        var stuck = Stuck || Frame is { Live: false };
        var frame = stuck && Frame is { Live: true } f ? f with { Live = false } : Frame;
        return new StagewatchSight(Name, "via the desk", state, frame, receivedAtSeconds - FrameAgeSeconds, null, "") { Stuck = stuck };
    }

    private static string Word(ShowBusState s) => s switch { ShowBusState.Live => "live", ShowBusState.Stale => "stale", _ => "gone" };
}

/// <summary>Round 88: which Stagewatch a node's stage follows — its own link while that is on and heard, else the desk's relay.</summary>
public static class StageSightChoice
{
    public static StagewatchSight Choose(bool ownLinkEnabled, StagewatchSight own, StagewatchSight relayed)
    {
        if (ownLinkEnabled && own.State != ShowBusState.Gone && (own.Name.Length > 0 || own.Address.Length > 0)) return own;
        if (relayed.Name.Length > 0 || relayed.Frame is not null) return relayed;
        return ownLinkEnabled ? own : StagewatchSight.None;
    }
}

/// <summary>What a node knows about itself, gathered on its UI thread for its context (closes L52).</summary>
public sealed record NodeContextFacts
{
    /// <summary>"caller", "timer" or "arcade" — the wire's word for the role.</summary>
    public string Role { get; init; } = "";
    public string DeskName { get; init; } = "";
    /// <summary>The twin's words for where this node stands with its desk ("in step with FOH", "not linked").</summary>
    public string DeskWords { get; init; } = "";
    public CheckLight DeskLight { get; init; } = CheckLight.Grey;
    public bool Linked { get; init; }
    public StagewatchSight Stagewatch { get; init; } = StagewatchSight.None;
    /// <summary>The followed Stagewatch came by the desk's relay rather than this node's own link.</summary>
    public bool StagewatchViaDesk { get; init; }
    /// <summary>What the stage shows now ("12:30 left, running", "message: wrap up").</summary>
    public string StageWords { get; init; } = "";
    public int StageScreens { get; init; }
    /// <summary>A caller's place in its stack ("standby 14 · last 13 · armed").</summary>
    public string StackWords { get; init; } = "";
    public CheckLight StackLight { get; init; } = CheckLight.Grey;
    public bool Arcade { get; init; }
    public string ArcadeWords { get; init; } = "";
}

/// <summary>
/// Round 88 (closes L52): a node's own context in Show Bus §6's shape — its role, its desk link, the Stagewatch its stage
/// follows, what its stage shows or where its stack stands, its last journal lines and the verbs it answers itself. A
/// desk's Eye draws its nodes from these; any app's assistant reads them. Pure.
/// </summary>
public static class NodeContextBuilder
{
    public static ShowContext Build(string id, string name, DateTime utcNow, NodeContextFacts f, IEnumerable<ShowLogEntry> journal)
    {
        var items = new List<ShowContextItem>();
        var roleWords = f.Role switch { "caller" => "caller node", "timer" => "stage timer node", "arcade" => "arcade node", _ => "node" };
        items.Add(new ShowContextItem { Id = "node", Kind = "node", Label = name, Light = "green", State = roleWords });
        items.Add(new ShowContextItem
        {
            Id = "desk", Kind = "twin", Label = f.DeskName.Length > 0 ? f.DeskName : "Desk",
            Light = ShowContext.Word(f.DeskLight), State = f.DeskWords.Length > 0 ? f.DeskWords : f.Linked ? "in step" : "not linked to a desk",
        });
        // Round 89 (profile 13.10.5, 13.16.1): a stuck show leaves the link live and amber; the stage that follows it shows
        // no time, red — a frozen count is worse than none.
        var stuck = f.Stagewatch.State == ShowBusState.Live && (f.Stagewatch.Stuck || f.Stagewatch.Frame is { Live: false });
        if (f.Stagewatch.Name.Length > 0 || f.Stagewatch.Address.Length > 0)
        {
            var light = f.Stagewatch.State switch
            {
                ShowBusState.Live when stuck => CheckLight.Amber,
                ShowBusState.Live => CheckLight.Green,
                ShowBusState.Stale => CheckLight.Amber,
                _ => CheckLight.Red,
            };
            items.Add(new ShowContextItem
            {
                Id = "stagewatch", Kind = "stagewatch", Label = "Stagewatch " + f.Stagewatch.Name, Light = ShowContext.Word(light),
                State = StageFollow.Words(f.Stagewatch) + (f.StagewatchViaDesk ? " (relayed by the desk)" : ""),
            });
        }
        var screens = f.StageScreens > 0 ? string.Create(CultureInfo.InvariantCulture, $" · {f.StageScreens} screen{(f.StageScreens == 1 ? "" : "s")} watching") : "";
        if (f.Role == "timer" || f.StageWords.Length > 0)
            items.Add(stuck && f.Stagewatch.Following
                ? new ShowContextItem { Id = "stage", Kind = "stage", Label = "Stage display", Light = "red", State = $"no time shown — Stagewatch {f.Stagewatch.Name} says its show is stuck" + screens }
                : new ShowContextItem
                {
                    Id = "stage", Kind = "stage", Label = "Stage display", Light = f.StageWords.Length > 0 && f.StageWords != "idle" ? "green" : "grey",
                    State = (f.StageWords.Length > 0 ? f.StageWords : "idle") + screens,
                });
        if (f.Role == "caller")
            items.Add(new ShowContextItem { Id = "stack", Kind = "stack", Label = "Cue stack", Light = ShowContext.Word(f.StackLight), State = f.StackWords.Length > 0 ? f.StackWords : "no cues" });
        if (f.Arcade)
            items.Add(new ShowContextItem { Id = "arcade", Kind = "arcade", Label = "Arcade", Light = "green", State = f.ArcadeWords.Length > 0 ? f.ArcadeWords : "ready" });

        var lights = items.Select(i => ShowContext.LightOf(i.Light)).ToList();
        var worst = ShowContext.Worst(lights);
        var problem = items.FirstOrDefault(i => i.Light == "red") ?? items.FirstOrDefault(i => i.Light == "amber");
        var headline = $"{name} — {roleWords}" + (f.Linked ? $" · in step with {f.DeskName}" : " · not linked")
                       + (f.Stagewatch.Following ? $" · following Stagewatch {f.Stagewatch.Name}" : "")
                       + (problem is not null ? $" — {problem.Label}: {problem.State}" : "");
        return new ShowContext
        {
            Node = new ShowContextNode { Kind = "patterns", Id = id, Name = name },
            At = ClockPing.Ms(utcNow),
            Light = ShowContext.Word(worst),
            Headline = headline,
            Items = items,
            Links = new[] { new ShowContextLink { From = "node", To = "desk", Kind = "follows", Light = ShowContext.Word(f.DeskLight) } }
                .Concat(items.Any(i => i.Id == "stagewatch") ? new[] { new ShowContextLink { From = "stage", To = "stagewatch", Kind = "follows", Light = items.First(i => i.Id == "stagewatch").Light } } : Array.Empty<ShowContextLink>())
                .ToList(),
            Recent = journal.TakeLast(ShowContext.MaxRecent).Select(ShowContextBuilder.Line).ToList(),
            Commands = Commands(f.Role),
        };
    }

    /// <summary>The verbs a node answers itself (the rest go to its desk while linked).</summary>
    public static IReadOnlyList<ShowContextCommand> Commands(string role)
    {
        var list = new List<ShowContextCommand>
        {
            new() { Verb = "STATUS", Risk = "low", Words = "read the node's state" },
            new() { Verb = "NOTE <words>", Risk = "low", Words = "a line on the node's pad" },
        };
        if (role == "timer")
        {
            list.Add(new() { Verb = "STAGE STATUS", Risk = "low", Words = "what the stage display shows" });
            list.Add(new() { Verb = "STAGE MESSAGE <words>", Risk = "review", Words = "words to the speaker (run on the desk while linked)" });
        }
        if (role == "caller")
        {
            list.Add(new() { Verb = "CUE LIST", Risk = "low", Words = "the caller's cues" });
            list.Add(new() { Verb = "CUE GO", Risk = "high", Words = "fire the standby cue (on the desk while linked)" });
        }
        return list;
    }
}

/// <summary>A proposal as the approval desk holds it (Show Bus 1.2 §11), as far as this desk reads it.</summary>
public sealed record DeskProposal
{
    public string Id { get; init; } = "";
    /// <summary>pending, approved, rejected, done, failed, undelivered.</summary>
    public string State { get; init; } = "";
    public string Line { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Risk { get; init; } = "";
    public string TargetId { get; init; } = "";
    public string FromId { get; init; } = "";
    public string Result { get; init; } = "";
    public string DecidedBy { get; init; } = "";

    public bool Final => State is "rejected" or "done" or "failed" or "undelivered";
}

/// <summary>
/// Round 88 (Show Bus 1.2 §11): this desk as a client of the shared approval queue — the request it posts, the replies
/// it reads (tolerant of field order and extra fields), the result it reports, and the line that names one of this
/// desk's assistant proposals by its local id (the desk shows the words; this desk keeps the proposal itself and applies
/// it through its own action layer when it comes back approved). Pure.
/// </summary>
public static class DeskProposals
{
    public const string Cap = "approvals";

    public static string Request(string fromId, string fromName, string targetId, string line, string reason, string risk)
        => JsonSerializer.Serialize(new
        {
            from = new { t = "patterns", id = fromId, name = fromName },
            target = new { t = "patterns", id = targetId },
            line = Cap256(line),
            reason = Cap256(reason),
            risk = risk is "low" or "review" or "high" ? risk : "high",
        });

    public static string Result(bool ok, string words) => JsonSerializer.Serialize(new { ok, words = Cap256(words) });

    /// <summary>"APPLY a3f9: Look "Keynote blue"" — the desk shows it; this desk finds its proposal by the id.</summary>
    public static string Line(string localId, string kindLabel, string title) => $"APPLY {localId}: {kindLabel} \"{Cap256(title).Replace('"', '\'')}\"";

    /// <summary>The local id an APPLY line names, or null when the line is not one of this desk's.</summary>
    public static string? LocalId(string? line)
    {
        var s = (line ?? "").Trim();
        if (!s.StartsWith("APPLY ", StringComparison.Ordinal)) return null;
        var colon = s.IndexOf(':', 6);
        var id = colon < 0 ? s[6..].Trim() : s[6..colon].Trim();
        return id.Length is > 0 and <= 32 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? id : null;
    }

    /// <summary>What an approved APPLY that is not this desk's own proposal answers: refused, with this reason.</summary>
    public const string OnlyOwnApply = "an APPLY runs only for the proposal this desk made, under its own id";

    /// <summary>What an approved APPLY answers when this desk no longer holds the proposal it names.</summary>
    public const string NoLongerHeld = "this desk no longer holds that proposal (restarted since?) — ask the assistant again";

    /// <summary>What an approved APPLY answers when the proposal it names has already run here: a proposal runs once.</summary>
    public const string AlreadyApplied = "this desk already applied that proposal — it runs once";

    /// <summary>
    /// Why an approved APPLY naming <paramref name="local"/> may not run here, or null when it may. It runs only as the
    /// proposal this desk made, and once: proposed from this desk's own id (<paramref name="selfId"/>), held here
    /// (<paramref name="held"/>), collected under the very id the approval desk gave this desk's own post of the proposal
    /// the line names (<paramref name="postedAs"/>: that proposal's approval-desk id as this desk recorded it, "" when it
    /// has none), and not run yet (<paramref name="ran"/>). The same words re-proposed by another node, or the same
    /// proposal under another id, name this desk's proposal without being it — refused, never applied.
    /// </summary>
    public static string? ApplyRefusal(DeskProposal p, string local, string selfId, bool held, string postedAs, bool ran)
    {
        if (selfId.Length == 0 || !string.Equals(p.FromId, selfId, StringComparison.Ordinal)) return OnlyOwnApply + " — another node proposed this one";
        if (!held) return NoLongerHeld;
        if (postedAs.Length == 0 || !string.Equals(postedAs, p.Id, StringComparison.Ordinal)) return OnlyOwnApply + $" — this desk posted {local} under another id";
        return ran ? AlreadyApplied : null;
    }

    public static DeskProposal? ParseOne(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 256 * 1024) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Read(doc.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A list reply: a bare array, or an object with "proposals" (or "items").</summary>
    public static IReadOnlyList<DeskProposal> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1024 * 1024) return Array.Empty<DeskProposal>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var array = r.ValueKind == JsonValueKind.Array ? r
                : r.ValueKind == JsonValueKind.Object && (r.TryGetProperty("proposals", out var p) || r.TryGetProperty("items", out p)) && p.ValueKind == JsonValueKind.Array ? p
                : default;
            if (array.ValueKind != JsonValueKind.Array) return Array.Empty<DeskProposal>();
            return array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Take(200).Select(Read).OfType<DeskProposal>().ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<DeskProposal>();
        }
    }

    /// <summary>The state in the operator's words for the chip.</summary>
    public static string StateWords(string state, string deskName) => state switch
    {
        "pending" => $"waiting for approval on {deskName}",
        "approved" => $"approved on {deskName} — applying here",
        "rejected" => $"rejected on {deskName}",
        "done" => "applied",
        "failed" => "approved, but this desk refused it",
        "undelivered" => $"approved on {deskName}, but this desk did not collect it in time",
        _ => state,
    };

    private static DeskProposal? Read(JsonElement e)
    {
        var id = Str(e, "id");
        if (id.Length is 0 or > 64) return null;
        return new DeskProposal
        {
            Id = id,
            State = Str(e, "state").ToLowerInvariant(),
            Line = Str(e, "line"),
            Reason = Str(e, "reason"),
            Risk = Str(e, "risk"),
            TargetId = e.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Object ? Str(t, "id") : Str(e, "targetId"),
            FromId = e.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Object ? Str(f, "id") : Str(e, "fromId"),
            Result = e.TryGetProperty("result", out var res) ? res.ValueKind == JsonValueKind.String ? res.GetString() ?? "" : res.ValueKind == JsonValueKind.Object ? Str(res, "words") : "" : "",
            DecidedBy = Str(e, "decidedBy"),
        };
    }

    private static string Str(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    private static string Cap256(string s) => s.Length <= 256 ? s : s[..256];
}
