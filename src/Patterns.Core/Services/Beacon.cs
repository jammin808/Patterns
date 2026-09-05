using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Patterns.Core.Services;

/// <summary>
/// One heartbeat from a Patterns machine — who it is, what it is doing, how it is — sent as a
/// UDP datagram once a second for a second machine to watch, and one day take over from. JSON,
/// tolerant both ways: a newer field is ignored, a missing one reads its default.
/// </summary>
public sealed record Beacon
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [JsonPropertyName("patterns")]
    public int Proto { get; init; } = Version;

    /// <summary>How the machine names itself (the Machine page's name, else the computer's).</summary>
    public string Machine { get; init; } = "";

    /// <summary>A random id per process, so a machine listening on its own network ignores itself.</summary>
    public string Instance { get; init; } = "";

    public long Seq { get; init; }
    public DateTime Utc { get; init; }

    /// <summary>Seconds since the app started.</summary>
    public double Up { get; init; }

    public bool Live { get; init; }
    public bool Blackout { get; init; }

    /// <summary>What is on air, by name.</summary>
    public string Program { get; init; } = "";

    public bool Armed { get; init; }

    /// <summary>The cue on standby, "number name"; "" when none.</summary>
    public string Standby { get; init; } = "";

    /// <summary>The cue that ran last, by number; "" when none.</summary>
    public string Last { get; init; } = "";

    /// <summary>The health line as the Show page reads it.</summary>
    public string Health { get; init; } = "";

    public long Faults { get; init; }
    public int Restarts { get; init; }
    public double Fps { get; init; }
    public int Windows { get; init; }
    public bool Stream { get; init; }
    public string Show { get; init; } = "";

    /// <summary>"" for a heartbeat; "gave-up" or "could-not-start" from the supervisor when the app is not running.</summary>
    public string Event { get; init; } = "";

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(ToJson());

    /// <summary>A beacon from a datagram's text, or null when it is not one — a stray packet must never throw.</summary>
    public static Beacon? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var b = JsonSerializer.Deserialize<Beacon>(json, Options);
            return b is { Proto: >= 1 } && b.Machine.Length > 0 ? b : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Beacon? Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return Parse(Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"live · Walk-in · armed · standby 01.020 Welcome · streaming", "outputs off", "BLACKOUT · Holding", "watchdog gave-up".</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            if (Event.Length > 0) return $"watchdog {Event}";
            var parts = new List<string> { Live ? Blackout ? "BLACKOUT" : "live" : "outputs off" };
            if (Program.Length > 0) parts.Add(Program);
            if (Standby.Length > 0) parts.Add((Armed ? "armed · standby " : "standby ") + Standby);
            if (Stream) parts.Add("streaming");
            return string.Join(" · ", parts);
        }
    }
}

public enum BeaconLevel
{
    /// <summary>Listening, nothing heard yet.</summary>
    Waiting,
    Ok,
    /// <summary>Silent past the limit, or the main machine's watchdog stood down.</summary>
    Warning,
}

/// <summary>What a listening machine makes of the last beacon it heard — pure, so the words are unit tested.</summary>
public static class BeaconWatch
{
    /// <summary>A beacon comes once a second; five missed in a row is a machine that is gone, not a lost packet.</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(5);

    public static bool IsSilent(DateTime? lastSeenUtc, DateTime utcNow)
        => lastSeenUtc is { } seen && utcNow - seen > SilentAfter;

    public static BeaconLevel Level(Beacon? last, DateTime? lastSeenUtc, DateTime utcNow)
    {
        if (last is null || lastSeenUtc is null) return BeaconLevel.Waiting;
        return last.Event.Length > 0 || IsSilent(lastSeenUtc, utcNow) ? BeaconLevel.Warning : BeaconLevel.Ok;
    }

    public static string Describe(Beacon? last, DateTime? lastSeenUtc, DateTime utcNow)
    {
        if (last is null || lastSeenUtc is null) return "Listening for the main machine — nothing heard yet.";
        var age = utcNow - lastSeenUtc.Value;
        var ageText = age.TotalSeconds < 1.5 ? "just now" : $"{age.TotalSeconds:0} s ago";
        if (last.Event.Length > 0)
        {
            return $"MAIN MACHINE {last.Machine}: its watchdog {last.Event.Replace('-', ' ')} ({ageText}) — the show is down there. Take over?";
        }
        if (IsSilent(lastSeenUtc, utcNow))
        {
            return $"MAIN MACHINE {last.Machine} SILENT for {age.TotalSeconds:0} s — last seen {last.Summary}. Take over?";
        }
        return $"Main machine {last.Machine} seen {ageText}: {last.Summary}.";
    }
}
