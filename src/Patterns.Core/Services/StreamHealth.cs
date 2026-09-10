namespace Patterns.Core.Services;

/// <summary>How a stream is doing, in the one word a rail or a status strip has room for.</summary>
public enum StreamLight
{
    /// <summary>Nobody asked for it.</summary>
    Off,

    /// <summary>Asked for, and coming up — the encoder is starting, or no frame has reached it yet.</summary>
    Starting,

    /// <summary>Up, and taking frames at the rate it was asked for.</summary>
    Live,

    /// <summary>Up, but not keeping up — frames are going in slower than the rate asked for, or it has restarted.</summary>
    Strained,

    /// <summary>Asked for and not running: the encoder failed, or there is nowhere to send it.</summary>
    Failed,
}

/// <summary>
/// What the desk knows about the stream this second, gathered where the encoder is and read
/// everywhere it is shown. Values only, so the rules below can be read and tested without an
/// encoder, a network or a show.
/// </summary>
public readonly record struct StreamFacts(
    bool Wanted,
    bool Configured,
    int Destinations,
    bool Encoding,
    bool Starting,
    long Frames,
    double Fps,
    int TargetFps,
    int Restarts,
    string Trouble,
    double UpSeconds)
{
    public static readonly StreamFacts None = new(false, false, 0, false, false, 0, 0, 0, 0, "", 0);
}

/// <summary>
/// The stream's health as a light, a word and a line — the same reading on the Stream page, at the
/// foot of the rail, on the Show panel, on the phone and on the wire, because they all read this.
///
/// It is deliberately hard to show green: a stream that is up but taking frames slowly is the
/// failure an operator cannot see from the desk (the wall looks perfect while the online audience
/// watches a slideshow), so it reads as strained rather than live, and says by how much.
/// </summary>
public sealed record StreamHealth(StreamLight Light, string Word, string Line, StreamFacts Facts)
{
    /// <summary>Below this share of the rate asked for, the stream is not keeping up.</summary>
    public const double SlowShare = 0.8;

    /// <summary>A stream is given this long to reach its rate before "starting" becomes "strained".</summary>
    public const double SettleSeconds = 6;

    public bool IsTrouble => Light is StreamLight.Strained or StreamLight.Failed;

    /// <summary>True while the stream is up or trying to be — what a tally lights on.</summary>
    public bool IsOnAir => Light is StreamLight.Starting or StreamLight.Live or StreamLight.Strained;

    /// <summary>"1 h 04 m", "3 m 12 s", "18 s" — how long it has been up; "" when it is not.</summary>
    public string Uptime => Duration(Facts.UpSeconds);

    /// <summary>How long, as a caller reads it off a strip at a glance.</summary>
    public static string Duration(double seconds)
    {
        if (seconds < 1) return "";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes:00} m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} m {t.Seconds:00} s";
        return $"{t.Seconds} s";
    }

    public static StreamHealth Read(in StreamFacts f)
    {
        var destinations = f.Destinations == 1 ? "1 destination" : $"{f.Destinations} destinations";

        if (!f.Wanted)
        {
            return new StreamHealth(StreamLight.Off, "OFF",
                f.Configured ? $"Not streaming — {destinations} ready." : "Not streaming — no destination set.", f);
        }

        if (!f.Configured)
        {
            return new StreamHealth(StreamLight.Failed, "NO DEST",
                "Asked for, but there is nowhere to send it: tick a destination with an address on the Stream page.", f);
        }

        if (f.Trouble.Length > 0)
        {
            return new StreamHealth(StreamLight.Failed, "FAULT", $"Stream fault — {f.Trouble}", f);
        }

        if (f.Starting || f.Frames == 0)
        {
            return new StreamHealth(StreamLight.Starting, "UP…", $"Starting — {destinations}; no frame has reached the encoder yet.", f);
        }

        var rate = f.TargetFps > 0 ? f.Fps / f.TargetFps : 1;
        var settled = f.UpSeconds >= SettleSeconds;
        if (settled && f.TargetFps > 0 && rate < SlowShare)
        {
            return new StreamHealth(StreamLight.Strained, "SLOW",
                $"Not keeping up — {f.Fps:0.#} of {f.TargetFps} fps reaching the encoder. The wall is fine; the stream is not. Lower the size, the rate or the bitrate.", f);
        }

        if (f.Restarts > 0)
        {
            return new StreamHealth(StreamLight.Strained, "LIVE*",
                $"Live to {destinations} at {f.Fps:0.#} fps — the encoder has restarted {f.Restarts} time{(f.Restarts == 1 ? "" : "s")} this session.", f);
        }

        return new StreamHealth(StreamLight.Live, "LIVE", $"Live to {destinations} at {f.Fps:0.#} fps.", f);
    }

    /// <summary>The light's colour, as the desk's palette writes it — the rail, the page and the panel share one.</summary>
    public string Hue => Light switch
    {
        StreamLight.Live => "#2EE68A",
        StreamLight.Starting => "#FFC24D",
        StreamLight.Strained => "#FF9E58",
        StreamLight.Failed => "#FF5C7A",
        _ => "#5A6473",
    };
}
