using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>13.16.1's closed set of a link's states.</summary>
public enum LinkState
{
    Off,
    Finding,
    Connecting,
    Live,
    Stale,
    Down,
    Held,
    Refused,
}

/// <summary>A link's show liveness (13.10.5), kept apart from the link's own.</summary>
public enum LinkShow
{
    Unknown,
    Live,
    Stuck,
}

/// <summary>A connector's carrier (13.1): a line wire, or a node link on Stagewatch's <c>/ws</c>.</summary>
public enum LinkCarrier
{
    Line,
    Ws,
}

/// <summary>How a certificate came to be pinned (13.8.2): seen first on a public view, by a proof that verified over it, or by a person on this desk.</summary>
public enum PinTrust
{
    First,
    Proof,
    Person,
}

/// <summary>A pin: the node it was pinned for (null until a verified link names it), its certificate's fingerprint (64 lowercase hex) and how it came to be.</summary>
public sealed record LinkPin(string? NodeId, string Fingerprint, PinTrust Trust);

/// <summary>13.9.3: what a verified link to a node reached — TLS, its level, its proof — which a later link may not offer less than.</summary>
public sealed record LinkFloor(bool Tls, int Level, string Proof);

/// <summary>
/// What a connector keeps across a restart (13.15.3, 13.9.3, 13.8, 13.4.3): the pin, the floor, a refused key's hold with its
/// end in wall time, the word it was refused with and whether the public view may be read inside it (false after a refusal that
/// let no keyless link read either), and the node id and kind a verified link or the operator bound it to. Never a key, code, K,
/// S, proof or nonce.
/// </summary>
public sealed record LinkKept(LinkPin? Pin, LinkFloor? Floor, long? HeldUntilWall, string? HeldRefusal, string? NodeId = null, string? NodeKind = null, bool HeldPublic = true);

/// <summary>One injected now: the monotonic clock and the wall clock, in ms. One number stands for both, as 13.20's machine rows read it.</summary>
public readonly record struct LinkNow(long Mono, long Wall)
{
    public static implicit operator LinkNow(long ms) => new(ms, ms);
}

/// <summary>
/// What the connector put on the wire, in order: a line's verb or a message's <c>type</c> or <c>cmd</c> (pings left out), and
/// which credential it carried — <c>proof</c>, <c>auth-proof</c>, <c>bearer</c> or <c>session</c> — never the credential.
/// </summary>
public readonly record struct LinkWrite(long At, string? Verb, string? Credential, string? Detail = null);

/// <summary>What one run of the evaluator did (13.10.3–4): pinged every link after its own gap, or closed a link gone down.</summary>
public readonly record struct LinkEvaluation(bool PingedAll, bool Closed, long? GapMs);

/// <summary>13.16.3's counts.</summary>
public readonly record struct LinkCounts(int Unconfirmed, int Late, int Replayed, int Oversize, int Malformed, int Unmatched);

/// <summary>13.16.3's clock: the believed sample's offset and error, its age, the samples kept and CLOCKS APART.</summary>
public readonly record struct LinkRecordClock(double? OffsetMs, long? ErrMs, long? AgeMs, int Samples, bool Apart);

/// <summary>
/// Show Bus 1.3 §13.16.3, the link record: the one object every reader takes, so no reader derives a link fact of its own. It
/// has no field for a key, a code, K, S, a proof or a nonce — the machine it comes from never holds one — and
/// <see cref="ToJson"/> writes 13.16.3's fields in 13.16.3's order.
/// </summary>
public sealed record LinkRecord(
    string? PeerT,
    string? PeerId,
    string PeerName,
    string Carrier,
    int Level,
    string Tls,
    string Pin,
    string Proof,
    string Kid,
    string Scope,
    string State,
    string Refusal,
    string Show,
    IReadOnlyList<string> Flags,
    string Light,
    string Mark,
    string Words,
    long Since,
    long? HeardMs,
    int Attempt,
    long? NextMs,
    LinkRecordClock Clock,
    LinkCounts Counts,
    LinkFloor? Floor)
{
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A link's light as the record writes it.</summary>
    public static string LightWord(CheckLight light) => light switch
    {
        CheckLight.Green => "green",
        CheckLight.Amber => "amber",
        CheckLight.Red => "red",
        _ => "grey",
    };

    /// <summary>The shape a light is drawn with beside its colour (13.16.1): ✓ green, △ amber, ✕ red, ○ grey.</summary>
    public static string MarkOf(CheckLight light) => light switch
    {
        CheckLight.Green => "✓",
        CheckLight.Amber => "△",
        CheckLight.Red => "✕",
        _ => "○",
    };

    /// <summary>The state's word as the record writes it.</summary>
    public static string StateWord(LinkState state) => state.ToString().ToLowerInvariant();

    /// <summary>The show's word as the record writes it.</summary>
    public static string ShowWord(LinkShow show) => show.ToString().ToLowerInvariant();

    /// <summary>13.16.3's object, its fields — the nested ones too — in its order.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Options))
        {
            w.WriteStartObject();
            w.WriteStartObject("peer");
            StringOrNull(w, "t", PeerT);
            StringOrNull(w, "id", PeerId);
            w.WriteString("name", PeerName);
            w.WriteEndObject();
            w.WriteString("carrier", Carrier);
            w.WriteNumber("level", Level);
            w.WriteString("tls", Tls);
            w.WriteString("pin", Pin);
            w.WriteString("proof", Proof);
            w.WriteString("kid", Kid);
            w.WriteString("scope", Scope);
            w.WriteString("state", State);
            w.WriteString("refusal", Refusal);
            w.WriteString("show", Show);
            w.WriteStartArray("flags");
            foreach (var flag in Flags) w.WriteStringValue(flag);
            w.WriteEndArray();
            w.WriteString("light", Light);
            w.WriteString("mark", Mark);
            w.WriteString("words", Words);
            w.WriteNumber("since", Since);
            NumberOrNull(w, "heardMs", HeardMs);
            w.WriteNumber("attempt", Attempt);
            NumberOrNull(w, "nextMs", NextMs);
            w.WriteStartObject("clock");
            if (Clock.OffsetMs is { } offset) w.WriteNumber("offsetMs", offset);
            else w.WriteNull("offsetMs");
            NumberOrNull(w, "errMs", Clock.ErrMs);
            NumberOrNull(w, "ageMs", Clock.AgeMs);
            w.WriteNumber("samples", Clock.Samples);
            w.WriteBoolean("apart", Clock.Apart);
            w.WriteEndObject();
            w.WriteStartObject("counts");
            w.WriteNumber("unconfirmed", Counts.Unconfirmed);
            w.WriteNumber("late", Counts.Late);
            w.WriteNumber("replayed", Counts.Replayed);
            w.WriteNumber("oversize", Counts.Oversize);
            w.WriteNumber("malformed", Counts.Malformed);
            w.WriteNumber("unmatched", Counts.Unmatched);
            w.WriteEndObject();
            if (Floor is { } floor)
            {
                w.WriteStartObject("floor");
                w.WriteBoolean("tls", floor.Tls);
                w.WriteNumber("level", floor.Level);
                w.WriteString("proof", floor.Proof);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("floor");
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void StringOrNull(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    private static void NumberOrNull(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }
}

/// <summary>
/// What a connector is set to for one link: its carrier and peer, the address it dials, what it holds for the peer — the kid,
/// never the key — and what the show asks of it.
/// </summary>
public sealed record LinkSettings
{
    public LinkCarrier Carrier { get; init; }

    /// <summary>The link is configured (13.16.1's <c>off</c> when not).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The peer's name, kind and id as this desk knows them, for the words and the hello it listens for.</summary>
    public string Name { get; init; } = "the peer";

    public string? Kind { get; init; }

    public string? NodeId { get; init; }

    /// <summary>A typed address to dial; null until a hello names the node.</summary>
    public string? Address { get; init; }

    /// <summary>The kid, or <c>code</c>, of what this connector holds for the node; null when it holds none.</summary>
    public string? Key { get; init; }

    /// <summary>The dialled address is a loopback address (13.1's local) — never because of a host name.</summary>
    public bool Local { get; init; }

    /// <summary>The show needs this link (13.1): followed, a playout link, a grant's. Only then may its light be red.</summary>
    public bool ShowNeeds { get; init; }

    /// <summary>The scope the link is used for (<c>follow</c>, <c>post</c>, <c>drive</c>, <c>operate</c>); a verdict's lower one is flagged.</summary>
    public string? Uses { get; init; }

    /// <summary>The tolerance of the timed commands this link schedules, in ms; null when it schedules none.</summary>
    public long? TimedToleranceMs { get; init; }

    /// <summary>The features the link starts with; a verified link's verdict replaces them.</summary>
    public IReadOnlyList<string> Can { get; init; } = Array.Empty<string>();

    /// <summary>The request ids' prefix and this process's hello inst (13.12.1).</summary>
    public string IdPrefix { get; init; } = "p";

    public string Inst { get; init; } = "00000000";
}
