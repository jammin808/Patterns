namespace Patterns.Core.Services;

/// <summary>
/// The words a child host and the desk exchange over the child's own stdin and stdout — one line
/// each, a word and the rest. The desk sends START with a plan, STOP, PING and QUIT; the host
/// answers HELLO once, BEAT every second while it lives, STARTED / STOPPED as it obeys, STATUS
/// and LOG as it has something to say, and ERROR when it cannot go on. Pure, so both ends read
/// the same table; the transport (a process, a fake in the tests) is the App's.
/// </summary>
public static class HostProtocol
{
    public const string Start = "START";
    public const string Stop = "STOP";
    public const string Ping = "PING";
    public const string Quit = "QUIT";

    public const string Hello = "HELLO";
    public const string Beat = "BEAT";
    public const string Started = "STARTED";
    public const string Stopped = "STOPPED";
    public const string Status = "STATUS";
    public const string Error = "ERROR";
    public const string Log = "LOG";

    /// <summary>How often a living host says BEAT.</summary>
    public static readonly TimeSpan BeatEvery = TimeSpan.FromSeconds(1);

    /// <summary>Silence past this from a host that has spoken is a hung host: the desk ends it and starts another.</summary>
    public static readonly TimeSpan BeatTimeout = TimeSpan.FromSeconds(6);

    /// <summary>A host that has said nothing at all in this long did not come up — a cold start of the exe on a slow disk can take a while, so this is the watchdog's own patience, not the beat's.</summary>
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A host that has been bringing its plan up for this long without STARTED — beating "starting" all the while — is stuck in libVLC, a capture device or a destination that never answers: the desk ends it and starts another.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The code word an ERROR line starts with — this machine has no libVLC: not a fault, said and held.</summary>
    public const string ErrorLibVlc = "libvlc";

    /// <summary>The code word — the plan could not be started: a destination that will not open, media that will not read.</summary>
    public const string ErrorStart = "start";

    /// <summary>The code word — the encoder failed while it ran.</summary>
    public const string ErrorEncoder = "encoder";

    /// <summary>An ERROR line's rest as its code word (lower-case) and its text.</summary>
    public static (string Code, string Text) SplitError(string rest)
    {
        var (word, text) = Parse(rest);
        return (word.ToLowerInvariant(), text);
    }

    /// <summary>One line: the word, a space, the rest with its line breaks flattened (a line is the unit).</summary>
    public static string Line(string word, string rest = "")
        => rest.Length == 0 ? word : word + " " + rest.Replace("\r", " ").Replace("\n", " ");

    /// <summary>The word (upper-cased, "" for a blank line) and the rest of the line as it came.</summary>
    public static (string Word, string Text) Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return ("", "");
        var trimmed = line.Trim();
        var space = trimmed.IndexOf(' ');
        return space < 0
            ? (trimmed.ToUpperInvariant(), "")
            : (trimmed[..space].ToUpperInvariant(), trimmed[(space + 1)..].Trim());
    }

    /// <summary>Whether the last beat is too long ago for a living host.</summary>
    public static bool IsSilent(DateTime lastBeatUtc, DateTime utcNow) => utcNow - lastBeatUtc > BeatTimeout;
}

/// <summary>
/// What the desk asks an encoder host to run, as the START line's payload: the libVLC plan the
/// desk built (the same <see cref="StreamMrl"/> plan as before), the ring the engine draws the
/// rendered frames into, and the frames' geometry. <see cref="Null"/> is a host that reads the
/// ring and encodes nothing — the tests' encoder, and a rig without libVLC saying so.
/// </summary>
public sealed record EncoderPlan(string Kind, string Mrl, string[] Options, string Ring, int Width, int Height, int Fps)
{
    /// <summary>The engine renders into the ring; the host feeds libVLC from it.</summary>
    public const string Rendered = "rendered";

    /// <summary>libVLC captures a display itself (screen://); the ring is unused.</summary>
    public const string Capture = "capture";

    /// <summary>The ring is read and counted, nothing is encoded.</summary>
    public const string Null = "null";

    public bool UsesRing => Kind != Capture && Ring.Length > 0;
}

/// <summary>The host's first line: who it is, so the desk's log and status can name it.</summary>
public sealed record HostHello(int Pid, string Role, bool LibVlc);

/// <summary>The host's heartbeat: frames taken from the ring so far, and the encoder's own state word.</summary>
public sealed record HostBeat(long Frames, string State);
