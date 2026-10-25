using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>
/// Round 87: one state frame from a Stagewatch's WebSocket (/ws) — the timer as its speaker sees it. Durations, not
/// wall clocks: <see cref="Seconds"/> is what was left when the frame was made (negative past zero) and the receiver
/// counts on from the moment it arrived, re-syncing on the next frame (Show Bus 1.1 §4).
/// </summary>
public sealed record StagewatchFrame
{
    public int V { get; init; }
    public long Seq { get; init; }
    public string Mode { get; init; } = "";
    public string State { get; init; } = "";
    public double Seconds { get; init; }
    public double Total { get; init; }
    public string Text { get; init; } = "";
    public string Phase { get; init; } = "";
    public string Color { get; init; } = "";
    public string Title { get; init; } = "";
    public string Next { get; init; } = "";
    public string Message { get; init; } = "";
    public long MessageId { get; init; }
    public string MessageAck { get; init; } = "";
    /// <summary>False when the Stagewatch says its own show thread is stuck: the time on it is not to be trusted.</summary>
    public bool Live { get; init; } = true;

    public bool Counting => State is "running";
    public bool Countdown => Mode is "countdown" or "";

    /// <summary>A Stagewatch WebSocket message as a frame; null for a beat, a pong, a reply or anything that is not a state frame.</summary>
    public static StagewatchFrame? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 256 * 1024) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (Str(r, "type") is var type && type.Length > 0 && type != "state") return null;
            if (!r.TryGetProperty("seconds", out _) && !r.TryGetProperty("text", out _)) return null;
            return new StagewatchFrame
            {
                V = (int)Num(r, "v"),
                Seq = (long)Num(r, "seq"),
                Mode = Str(r, "mode").ToLowerInvariant(),
                State = Str(r, "state").ToLowerInvariant(),
                Seconds = Num(r, "seconds"),
                Total = Num(r, "total"),
                Text = Str(r, "text"),
                Phase = Str(r, "phase").ToLowerInvariant(),
                Color = Str(r, "color"),
                Title = Str(r, "title"),
                Next = Str(r, "next"),
                Message = Str(r, "message"),
                MessageId = (long)Num(r, "messageId"),
                MessageAck = Str(r, "messageAck"),
                Live = !r.TryGetProperty("live", out var live) || live.ValueKind != JsonValueKind.False,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A Stagewatch's stuck beat, <c>{"type":"beat","live":false}</c>, in the bytes Stagewatch sends: its show thread has
    /// stopped turning over, so its last frame is a frozen time (PeerLink profile 13.10.5). True only for an object whose
    /// type is "beat" and whose live is the literal false — a plain beat, a beat saying true, a state frame or junk is not one.
    /// </summary>
    public static bool IsStuckBeat(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return r.ValueKind == JsonValueKind.Object && Str(r, "type") == "beat" && r.TryGetProperty("live", out var live) && live.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The message kind of a WebSocket message: "state", "beat", "pong", "receipt"… ("" when it names none).</summary>
    public static string TypeOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, "type") : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>A pong's stamps, NTP-style: t1 echoed, the Stagewatch's t2 and t3 (Unix ms); null when the message is not one.</summary>
    public static (long T1, long T2, long T3)? Pong(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Str(r, "type") != "pong") return null;
            var t1 = (long)Num(r, "t");
            var t2 = (long)Num(r, "t2");
            var t3 = (long)Num(r, "t3");
            return t1 > 0 && t2 > 0 && t3 > 0 ? (t1, t2, t3) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The ping a client sends on a Stagewatch's WebSocket (Show Bus 1.1 §5).</summary>
    public static string Ping(long t1) => string.Create(CultureInfo.InvariantCulture, $"{{\"cmd\":\"ping\",\"t\":{t1}}}");

    private static string Str(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;
}

/// <summary>Round 87: what the Stagewatch link holds at an instant — whom it follows, the last frame and when it came, its context.</summary>
public sealed record StagewatchSight(string Name, string Address, ShowBusState State, StagewatchFrame? Frame, double FrameAtSeconds, ShowContext? Context, string Words)
{
    public static readonly StagewatchSight None = new("", "", ShowBusState.Gone, null, 0, null, "");

    /// <summary>A Stagewatch is linked and answering: its frames are the stage timer's truth.</summary>
    public bool Following => State == ShowBusState.Live && Frame is not null;

    /// <summary>
    /// The Stagewatch says its show is stuck (its stuck beat), and no state frame has come since. Show liveness, apart from
    /// the link's: the link stays live while only the show is stuck (PeerLink profile 13.10.5). A held frame is then
    /// <see cref="StagewatchFrame.Live"/> false, so nothing counts on from it; with no frame held the stage is not following
    /// and this desk's own timer runs.
    /// </summary>
    public bool Stuck { get; init; }

    /// <summary>The link is live and its show is stuck: every reader shows no time from it, the link amber.</summary>
    public bool StuckLive => Stuck && State == ShowBusState.Live;
}

/// <summary>
/// Round 87: the stage timer when a Stagewatch is linked. One room, one timer: Patterns' stage page, timer page and
/// the stage timer node show the Stagewatch's time instead of running a second countdown beside it. Pure: the frame,
/// the moment it arrived and now on the monotonic clock are handed in.
/// </summary>
public static class StageFollow
{
    /// <summary>
    /// The Stagewatch's time now: a counting countdown counts on from the frame (a second late frame does not freeze the
    /// page); anything else is shown as the frame said. A frame from a stuck Stagewatch (live:false) is shown as idle —
    /// a frozen number on a speaker's screen is worse than none.
    /// </summary>
    public static StageTime Time(StagewatchFrame frame, double frameAtSeconds, double nowSeconds)
    {
        if (!frame.Live || frame.State is "idle" or "") return StageTime.Idle;
        var seconds = frame.Seconds;
        if (frame.Counting && frame.Countdown) seconds -= Math.Max(0, nowSeconds - frameAtSeconds);
        else if (frame.Counting && frame.Mode == "countup") seconds += Math.Max(0, nowSeconds - frameAtSeconds);
        var phase = frame.State switch
        {
            "paused" => StageTimerPhase.Paused,
            "expired" => StageTimerPhase.Over,
            _ => frame.Countdown && seconds < 0 ? StageTimerPhase.Over : StageTimerPhase.Running,
        };
        var progress = frame.Countdown && frame.Total > 0 ? Math.Clamp(1 - Math.Max(0, seconds) / frame.Total, 0, 1) : 0;
        return new StageTime(phase, seconds, Colour(frame, phase), phase == StageTimerPhase.Over ? 1 : progress);
    }

    /// <summary>The Stagewatch's own colour words: overtime and its last stretch red, its warning amber, else green.</summary>
    public static string Colour(StagewatchFrame frame, StageTimerPhase phase)
    {
        if (phase == StageTimerPhase.Over) return "red";
        return frame.Phase switch
        {
            "overtime" or "critical" or "danger" or "red" => "red",
            "warning" or "caution" or "wrap" or "amber" or "yellow" => "amber",
            _ => "green",
        };
    }

    /// <summary>The page's line: "following Stagewatch Hall A", or why not.</summary>
    public static string Words(StagewatchSight sight) => sight.Name.Length == 0 && sight.Address.Length == 0
        ? ""
        : sight.State switch
        {
            ShowBusState.Live when sight.Stuck && sight.Frame is null => $"Stagewatch {sight.Name} says its show is stuck — this desk's own stage timer runs until its first frame",
            ShowBusState.Live when sight.Frame is { Live: false } => $"following Stagewatch {sight.Name} — it says its show is stuck; the time is not shown",
            ShowBusState.Live => $"following Stagewatch {sight.Name}".TrimEnd(),
            ShowBusState.Stale => $"Stagewatch {sight.Name} has gone quiet — the time is held back until it answers".Replace("  ", " "),
            _ => $"Stagewatch {sight.Name} is not answering — this desk's own stage timer runs".Replace("  ", " "),
        };

    /// <summary>A typed "host:port" (or "host", port 9924) as a Stagewatch's address; null when it is not one.</summary>
    public static (string Host, int Port)? ParseAddress(string? typed)
    {
        var t = (typed ?? "").Trim();
        if (t.Length == 0 || t.Length > 255 || t.Contains('/') || t.Contains('@') || t.Contains(' ')) return null;
        if (t.StartsWith('['))
        {
            // "[fe80::1]:9924"
            return IPEndPoint.TryParse(t, out var ep) && ep.Port is > 0 and <= 65535 && t.Contains("]:", StringComparison.Ordinal) ? (ep.Address.ToString(), ep.Port) : null;
        }
        var colons = t.Count(c => c == ':');
        if (colons > 1) return IPAddress.TryParse(t, out var v6) ? (v6.ToString(), DefaultPort) : null;   // a bare IPv6 address
        if (colons == 1)
        {
            var colon = t.IndexOf(':');
            var host = t[..colon];
            return int.TryParse(t[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 && Uri.CheckHostName(host) != UriHostNameType.Unknown
                ? (host, port) : null;
        }
        return Uri.CheckHostName(t) != UriHostNameType.Unknown ? (t, DefaultPort) : null;
    }

    /// <summary>Stagewatch's web port.</summary>
    public const int DefaultPort = 9924;
}

/// <summary>
/// Round 87: the show network as this desk shows it — one projection for the Eye's CONTROL plane, the Nodes page and the
/// context's peers. Another Patterns is left out (the beacon's node cards already draw it, by its process); a Stagewatch
/// this desk is linked to carries its context. Pure: the peers heard and the link's sight are handed in.
/// </summary>
public static class ShowBusView
{
    public static IReadOnlyList<EyeShowApp> Apps(IReadOnlyList<ShowBusPeer> peers, DateTime utcNow, StagewatchSight sight)
    {
        var apps = new List<EyeShowApp>();
        var linkedSeen = false;
        foreach (var p in peers.Where(p => p.Kind != "patterns"))
        {
            var linked = sight.Address.Length > 0 && p.Kind == "main" && LinkedTo(sight, p);
            linkedSeen |= linked;
            var words = string.Join(" · ", new[] { p.Host.Length > 0 && p.Host != p.Name ? p.Host : "", p.Address, p.Caps.Count > 0 ? string.Join(", ", p.Caps.Take(6)) : "" }.Where(w => w.Length > 0));
            var state = linked ? Worse(p.StateAt(utcNow), sight.State) : p.StateAt(utcNow);
            var stuck = linked && sight.Stuck && state == ShowBusState.Live;
            apps.Add(new EyeShowApp(p.Id, p.KindWords, p.Name.Length > 0 ? p.Name : p.Host, state, linked, words)
            {
                Context = linked ? Shown(sight.Context, stuck) : null,
                Stuck = stuck,
            });
        }
        // A Stagewatch reached by a typed address that never said hello (another subnet, a loopback-only hello) is still drawn.
        if (!linkedSeen && sight.Address.Length > 0)
        {
            var id = sight.Context?.Node.Id is { Length: > 0 } cid ? cid : "stagewatch";
            var stuck = sight.StuckLive;
            apps.Add(new EyeShowApp(id, "Stagewatch", sight.Context?.Node.Name is { Length: > 0 } n ? n : sight.Name, sight.State, true, sight.Address)
            {
                Context = Shown(sight.Context, stuck),
                Stuck = stuck,
            });
        }
        return apps;
    }

    /// <summary>What every reader says of a linked app whose show is stuck (PeerLink profile 13.16.1).</summary>
    public const string StuckWords = "says its show is stuck — no time shown";

    /// <summary>What a counting thing of a stuck app says in place of its time.</summary>
    public const string NoTimeWords = "no time shown — its show is stuck";

    /// <summary>
    /// The things whose words are a count that runs on with the show — a timer, a schedule, a playlist, a chase, a
    /// timecode, a clock. From an app whose show is stuck none is shown as current (PeerLink profile 13.10.6).
    /// </summary>
    public static bool IsCounting(string? kind) => (kind ?? "").Trim().ToLowerInvariant() is "timer" or "schedule" or "playlist" or "chase" or "timecode" or "clock";

    /// <summary>A stuck show's light: the worse of the app's own light and amber — never red for the stall alone (profile 13.16.1).</summary>
    public static string StuckLight(string? light) => ShowContext.Word(ShowContext.Worst(new[] { ShowContext.LightOf(light), CheckLight.Amber }));

    /// <summary>
    /// A linked app's context as every reader shows it while its show is stuck: its light no better than amber, its
    /// headline the stall's words (a headline holds a count), and each counting thing red with no time. Its other things
    /// keep their own words and lights. The same context in, the same context out, so the Eye rebuilds only on a change.
    /// </summary>
    public static ShowContext NoTime(ShowContext c) => c with
    {
        Light = StuckLight(c.Light),
        Headline = StuckWords,
        Items = c.Items.Select(i => IsCounting(i.Kind) ? i with { Light = "red", State = NoTimeWords } : i).ToList(),
    };

    private static ShowContext? Shown(ShowContext? context, bool stuck) => Steady(context) is { } c && stuck ? NoTime(c) : Steady(context);

    /// <summary>
    /// Round 88 (closes L52): this desk's own nodes — timers, callers, the arcade — as the Eye draws them from the context
    /// each sends over the twin: one card per node, its things inside, quiet after 6 s of silence, gone after 12.
    /// </summary>
    public static IReadOnlyList<EyeShowApp> Nodes(IReadOnlyList<(string Name, ShowContext Context, DateTime AtUtc)> nodes, DateTime utcNow)
        => nodes.Select(n => new EyeShowApp("node:" + n.Context.Node.Id, "Patterns node", n.Context.Node.Name.Length > 0 ? n.Context.Node.Name : n.Name,
                ShowBusLiveness.StateOf(TimeSpan.FromSeconds(Math.Max(5, (utcNow - n.AtUtc).TotalSeconds) - 5)), true, n.Context.Headline)
            {
                Context = n.Context with { At = 0 },
            })
            .ToList();

    /// <summary>The context's peers row: each app with its light and headline (its own context's when this desk reads it), never its peers.</summary>
    public static IReadOnlyList<ShowContextPeer> Peers(IReadOnlyList<EyeShowApp> apps) => apps.Select(a => new ShowContextPeer
    {
        Kind = a.KindWords switch { "Stagewatch" => "main", "AGMessage" => "messenger", "Slides agent" => "agent", "Patterns node" => "patterns", _ => a.KindWords.ToLowerInvariant() },
        Id = a.Id,
        Name = a.Name,
        Light = a.State switch
        {
            ShowBusState.Gone => "red",
            ShowBusState.Stale => "amber",
            _ when a.Stuck => StuckLight(a.Context?.Light),
            _ => a.Context?.Light ?? "grey",
        },
        Headline = a.Context?.Headline is { Length: > 0 } h ? h
            : a.State == ShowBusState.Live ? (a.Stuck ? StuckWords : a.Linked ? "linked" : "heard on the show network")
            : a.State == ShowBusState.Stale ? "quiet" : "gone quiet",
    }).ToList();

    private static bool LinkedTo(StagewatchSight sight, ShowBusPeer p)
    {
        var colon = sight.Address.LastIndexOf(':');
        var host = colon > 0 ? sight.Address[..colon].Trim('[', ']') : sight.Address;
        var port = colon > 0 && int.TryParse(sight.Address[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return string.Equals(host, p.Address, StringComparison.OrdinalIgnoreCase) && (port == 0 || port == p.Ports.Http || port == p.Ports.Ws);
    }

    /// <summary>
    /// The show network for the assistant's brief, in words: each app with its state, light and headline, and for the
    /// one this desk reads, its own things (the reds and ambers first), its last lines, what may be proposed to it and
    /// the apps it hears. The same evidence the Eye draws — unknown stays unknown.
    /// </summary>
    public static IReadOnlyList<string> BriefLines(IReadOnlyList<EyeShowApp> apps)
    {
        var lines = new List<string>();
        foreach (var a in apps)
        {
            var state = a.State switch { ShowBusState.Gone => "gone quiet", ShowBusState.Stale => "quiet", _ => a.Linked ? "linked" : "heard, not linked" };
            var c = a.Context;
            // A stuck show: the link is live and amber, its counting things carry no time (the context says so, NoTime).
            if (a.Stuck && a.State == ShowBusState.Live && c is null) lines.Add($"{a.KindWords} {a.Name} — {state} — {StuckLight(null)}: {StuckWords}");
            else lines.Add($"{a.KindWords} {a.Name} — {state}" + (c is null ? "" : $" — {c.Light}: {c.Headline}"));
            if (c is null || a.State == ShowBusState.Gone) continue;
            foreach (var i in c.Items.OrderByDescending(i => i.Light == "red").ThenByDescending(i => i.Light == "amber").Take(15))
                lines.Add($"  · {i.Label} ({i.Kind}) — {i.Light}" + (i.State.Length > 0 ? ": " + i.State : ""));
            foreach (var r in c.Recent.TakeLast(5)) lines.Add($"  recent: {r.Text}");
            if (c.Commands.Count > 0) lines.Add("  may be proposed to it: " + string.Join("; ", c.Commands.Take(12).Select(x => $"{x.Verb} ({x.Risk})")));
            foreach (var p in c.Peers.Take(6)) lines.Add($"  it hears {ShowBusPeers.KindWords(p.Kind)} {p.Name} — {p.Light}" + (p.Headline.Length > 0 ? ": " + p.Headline : ""));
        }
        return lines;
    }

    /// <summary>A context without its moment: the Eye rebuilds when what a context says moves, not when it was said.</summary>
    private static ShowContext? Steady(ShowContext? c) => c is null ? null : c with { At = 0 };

    private static ShowBusState Worse(ShowBusState a, ShowBusState b) => (ShowBusState)Math.Max((int)a, (int)b);
}

/// <summary>Round 87: one other show app on the Nodes page — its kind in its colour, its name, and a line of what it is doing.</summary>
public sealed record ShowAppCard(string Id, string KindLabel, string Name, string Line, string Hue, bool Linked)
{
    /// <summary>What the page compares to know the card moved.</summary>
    public string Key => $"{Id}:{KindLabel}:{Name}:{Line}:{Hue}:{Linked}";

    public static ShowAppCard Of(EyeShowApp app)
    {
        var state = app.State switch { ShowBusState.Gone => "gone quiet", ShowBusState.Stale => "quiet", _ => app.Linked ? "linked" : "heard" };
        var line = app.Stuck && app.State == ShowBusState.Live ? $"{state} · {ShowBusView.StuckWords}"
            : app.Context is { Headline.Length: > 0 } c && app.State == ShowBusState.Live ? $"{state} · {c.Headline}" : state + (app.Words.Length > 0 ? " · " + app.Words : "");
        var hue = app.State switch
        {
            ShowBusState.Gone => "#B03030",
            ShowBusState.Stale => "#FFB020",
            // A stuck show: the worse of the app's own light and amber — red only when its own light is red.
            _ when app.Stuck => ShowBusView.StuckLight(app.Context?.Light) == "red" ? "#B03030" : "#FFB020",
            _ => app.KindWords switch { "Stagewatch" => "#35E0D0", "AGMessage" => "#B98CFF", "Slides agent" => "#5FD0FF", _ => "#8A93A6" },
        };
        return new ShowAppCard(app.Id, app.KindWords, app.Name, line, hue, app.Linked);
    }

    /// <summary>"2 apps on the show network · Stagewatch link: following Hall A." — or why there are none.</summary>
    public static string NetworkLine(int count, bool hearing, string linkStatus)
    {
        var heard = !hearing ? "Not listening on the show network (remote control is off)." : count == 0 ? "No other show app heard on the show network yet (UDP 9928)." : count == 1 ? "1 app on the show network." : $"{count} apps on the show network.";
        return linkStatus.Length > 0 ? heard + " " + linkStatus : heard;
    }
}
