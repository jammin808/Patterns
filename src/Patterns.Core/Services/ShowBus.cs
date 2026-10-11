using System.Text.Json;
using System.Text.Json.Serialization;

namespace Patterns.Core.Services;

/// <summary>
/// Round 86: the show network's hello — what other show apps (Stagewatch, its presenter PCs, a
/// messenger) listen for on UDP 9928 to find this machine: what it is, which ports its wire is on and
/// what it can do. It carries no token, no show name, no look, nothing on air: a hello says who is
/// here, never what the show is. The machine-to-machine beacon on 9700 is separate and unchanged —
/// it is the twin's watchdog and only Patterns reads it.
/// </summary>
public sealed record ShowBusHello
{
    /// <summary>The port every show app hears hellos on.</summary>
    public const int Port = 9928;

    /// <summary>How often a hello goes out: the other apps forget a node after a few missed.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    /// <summary>
    /// What this machine can do for another app — the verbs and rows a caller may rely on. Round 87 adds Show Bus 1.1's
    /// words (docs/SHOWBUS.md §2): video, the Eye, a context any app's assistant can read, receipts and the clock exchange.
    /// </summary>
    public static readonly string[] Capabilities = { "video", "video.clock", "video.end", "video.seek", "video.transport", "state", "eye", "context", "receipts", "clock" };

    /// <summary>The kind of node: "patterns" (other apps' kinds are "main" for Stagewatch, "agent", "messenger").</summary>
    [JsonPropertyName("t")] public string Kind { get; init; } = "patterns";
    [JsonPropertyName("v")] public int Version { get; init; } = 1;
    /// <summary>Round 87: this install's id (<see cref="ShowBusIdentity"/>), the same across restarts so a renamed desk is still the same node.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    /// <summary>How the machine names itself — the beacon's name.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("machine")] public string Machine { get; init; } = "";
    /// <summary>The wire's HTTP port (the pages, /api), 0 when the remote control is off.</summary>
    [JsonPropertyName("web")] public int Web { get; init; }
    /// <summary>The wire's TCP port (Companion, remotes, STATE pushes), 0 when off.</summary>
    [JsonPropertyName("wire")] public int Wire { get; init; }
    /// <summary>The role: desk, caller, arcade, timer.</summary>
    [JsonPropertyName("role")] public string Role { get; init; } = "";
    [JsonPropertyName("caps")] public string[] Caps { get; init; } = Capabilities;
    /// <summary>The wire's version as other apps read it: 1 is Show Bus 1.1's contract.</summary>
    [JsonPropertyName("proto")] public int Proto { get; init; } = 1;
    /// <summary>Round 87: the machine's name under Show Bus 1.1's word for it (the same as <see cref="Machine"/>).</summary>
    [JsonPropertyName("host")] public string Host { get; init; } = "";
    /// <summary>Round 87: the doors, by Show Bus 1.1's names — http, wire, osc; only those served.</summary>
    [JsonPropertyName("ports")] public ShowBusPorts Ports { get; init; } = new();
    /// <summary>Round 87: this machine's Unix time in ms as it sent the hello — a first rough offset; the clock exchange is the real one.</summary>
    [JsonPropertyName("clock")] public long Clock { get; init; }
    /// <summary>Round 87: this process (the beacon's instance), so a machine ignores its own hello coming back.</summary>
    [JsonPropertyName("inst")] public string Instance { get; init; } = "";
    /// <summary>Round 88 (Show Bus 1.2 §10): the fingerprint of this desk's certificate when the web remote serves TLS; absent otherwise.</summary>
    [JsonPropertyName("tls")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Tls { get; init; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public byte[] ToBytes() => System.Text.Encoding.UTF8.GetBytes(ToJson());

    /// <summary>
    /// Whether a hello may go out for a control listener bound to <paramref name="bind"/>: "" (every
    /// interface) or a network address may; a loopback-only wire is this machine's alone, so telling
    /// the network about it would name a door nobody can open.
    /// </summary>
    public static bool MayAnnounce(bool controlEnabled, string bind)
    {
        if (!controlEnabled) return false;
        var b = (bind ?? "").Trim();
        if (b.Length == 0 || b == "0.0.0.0" || b == "*") return true;
        if (b.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        return !(System.Net.IPAddress.TryParse(b, out var address) && System.Net.IPAddress.IsLoopback(address));
    }
}

/// <summary>
/// Round 86: the clip's count of discontinuities — STATE video.seq. It moves when the clip changes,
/// starts, pauses, plays on, ends, learns its length, changes speed, or its position jumps (a seek, a
/// restart, a loop coming round) — never for the playhead running on as expected. A caller that counts
/// the clip down by itself between pushes resyncs on a new seq and trusts its own clock otherwise:
/// the whole state, latest wins, durations not wall clocks. Pure: the caller hands in a monotonic time.
/// </summary>
public sealed class VideoSequence
{
    /// <summary>A position this far from where the clip should be is a jump, not jitter between two reads.</summary>
    public const double JumpSeconds = 1.5;

    private long _seq;
    private VideoReading? _last;
    private double _lastAt;

    public long Current => _seq;

    /// <summary>The seq for this reading, taken at <paramref name="nowSeconds"/> on a monotonic clock.</summary>
    public long Next(VideoReading? r, double nowSeconds)
    {
        var last = _last;
        var elapsed = Math.Max(0, nowSeconds - _lastAt);
        _last = r;
        _lastAt = nowSeconds;
        if (Moved(last, r, elapsed)) _seq++;
        return _seq;
    }

    private static bool Moved(VideoReading? last, VideoReading? now, double elapsed)
    {
        if (last is null || now is null) return last is not null || now is not null;
        if (last.Key != now.Key || last.Playing != now.Playing || last.Ended != now.Ended || last.Paused != now.Paused) return true;
        if (last.HasLength != now.HasLength || Math.Abs(last.LengthSeconds - now.LengthSeconds) > 0.5) return true;
        if (Math.Abs(last.Rate - now.Rate) > 0.001) return true;
        var running = last.Playing && !last.Paused && !last.Ended;
        var expected = last.PositionSeconds + (running ? elapsed * last.Rate : 0);
        return Math.Abs(now.PositionSeconds - expected) > JumpSeconds;
    }
}
