namespace Patterns.Core.Services;

/// <summary>
/// The Eye on the wire (round 66): EYE / EYE STATUS answers the picture as one JSON object — the headline, the counts, the problems,
/// the focus and the lens, every thing with its place and its words, every link with its light. Each thing's band, then at
/// the end the open clusters, what the NETWORK band leaves out and, when the fit view shows marks only, the band's words for it. A
/// thing in a folded cluster has no place (0 × 0). Every thing and link carries its mark (✓ △ ✕ ○ i), derived from its light.
/// Show Bus 1.3 §6 and §13.16 (the Eye design's §6), appended at the end of their rows: each thing's app, source and whether its
/// cluster is folded; each link's source, claim and link record — a link this desk holds, its 13.16.3 record; one its wire accepted,
/// its 13.16.5 row; null for every other link and for every link in a replay, whose records are of now; and at the end of the
/// object the apps known, this desk's links by light, what the bounds cut and the link ends not placed. EYE answers a paired
/// connection only (ADR-016), so a link's record reaches no connection that has not paired.
/// </summary>
public static class EyeJson
{
    public static string Write(EyeGraph g, EyePlacement p, string? focusId, EyeLens lens) => Write(g, p, focusId, lens, null, null);

    /// <summary>Round 84: the same JSON for a replayed moment — <c>replay</c>, <c>replayAt</c> and <c>moment</c> ride at the end of the row; false and empty for the picture of now.</summary>
    public static string Write(EyeGraph g, EyePlacement p, string? focusId, EyeLens lens, ReplayMoment? moment) => Write(g, p, focusId, lens, moment, null);

    /// <summary>
    /// The picture as JSON, each link's record read from <paramref name="now"/> — the records as the desk's last gather read them,
    /// whose clock and counts run on between rebuilds of the picture — and from the edge where <paramref name="now"/> has none.
    /// </summary>
    public static string Write(EyeGraph g, EyePlacement p, string? focusId, EyeLens lens, ReplayMoment? moment, EyeLinksNow? now)
    {
        var payload = new
        {
            headline = g.Headline,
            worst = g.Worst?.Id ?? "",
            worstLight = EyeGraph.Light(g.WorstLight),
            worstWords = g.Worst is { } w ? $"{w.Label}: {w.Sub}" : "",
            counts = new { green = g.Green, amber = g.Amber, red = g.Red, grey = g.Grey, things = g.Things, links = g.Edges.Count },
            focus = focusId ?? "",
            lens = lens.ToString().ToLowerInvariant(),
            problems = g.Problems,
            nodes = g.Nodes.Select(n =>
            {
                var r = p.Of(n.Id);
                return new
                {
                    id = n.Id,
                    kind = n.Kind.ToString().ToLowerInvariant(),
                    plane = n.Plane.ToString().ToLowerInvariant(),
                    tier = n.Tier,
                    label = n.Label,
                    sub = n.Sub,
                    light = EyeGraph.Light(n.Light),
                    x = Math.Round(r.X),
                    y = Math.Round(r.Y),
                    w = Math.Round(r.W),
                    h = Math.Round(r.H),
                    words = n.Words,
                    page = n.Route?.Page ?? "",
                    item = n.Route?.Item ?? "",
                    wire = "EYE FOCUS " + n.Id,
                    band = n.Band.ToString().ToLowerInvariant(),                               // where it is drawn; plane is its lens
                    mark = EyeMarks.Glyph(n.Mark),                                             // the shape beside its light, derived from it
                    app = n.App,                                                               // the app whose cluster holds it; "" for this desk's own
                    source = EyeNetwork.SourceWord(n.Origin),
                    collapsed = Collapsed(g, p, n),
                };
            }).ToArray(),
            edges = g.Edges.Select(e => new
            {
                from = e.From, to = e.To, kind = e.Verb, light = EyeGraph.Light(e.Light), words = e.Words, mark = EyeMarks.Glyph(e.Mark),
                source = EyeNetwork.SourceWord(e.Origin),
                claim = e.Claim,
                link = moment is null ? LinkOf(e, now) : null,
            }).ToArray(),
            replay = moment is not null,
            replayAt = moment is null ? "" : ReplayTime.Stamp(moment.AtUtc),
            moment = moment is null ? "" : EyeReplay.Words(moment),
            expanded = p.Expanded,                                                            // the open clusters, the view's own state
            left = new { apps = p.LeftApps, items = p.LeftItems, edges = g.LeftEdges },
            fit = p.MarksOnly ? p.Note : "",
            problemsCounted = g.CountedProblems,                                              // the head of problems the rail counts; another app's after
            worstMark = EyeMarks.Glyph(g.WorstLight),                                         // the rail's light as its shape
            apps = g.Apps,                                                                    // the other apps the Eye knows, those past its bound among them
            linkLights = new { green = g.LinkLights.Green, amber = g.LinkLights.Amber, red = g.LinkLights.Red, grey = g.LinkLights.Grey },
            unplaced = g.Unplaced,                                                            // the context rows whose end found no place
        };
        return JsonUtil.SerializeCompact(payload);
    }

    /// <summary>
    /// Its cluster is folded: an app's card that names things while its cluster is not open, or one of those things (it has no place,
    /// 0 × 0). False for everything else — this desk's own things and a card that names nothing to fold. An open cluster places every
    /// thing the picture holds of it: the picture holds at most the dozen a cluster draws, and the rest are no rows here, only
    /// counted in <c>left.items</c>.
    /// </summary>
    private static bool Collapsed(EyeGraph g, EyePlacement p, EyeNode n)
    {
        if (n.App.Length == 0) return false;
        if (n.Kind == EyeKind.ShowAppItem) return !p.IsExpanded(EyeNetwork.CardOf(n.App));
        return n.Kind == EyeKind.ShowApp && g.NamedBy(n.Id) > 0 && !p.IsExpanded(n.Id);
    }

    /// <summary>A link's record as JSON: the 13.16.3 record of a link this desk holds, the 13.16.5 row of one its wire accepted, else null.</summary>
    private static object? LinkOf(EyeEdge e, EyeLinksNow? now)
    {
        if (e.Accepted is { } accepted) return AcceptedRow(now?.Accepted(accepted.Lid) ?? accepted);
        if (e.Record is { } record) return (now?.Record(e.RecordOf) ?? record).ToElement();
        return null;
    }

    /// <summary>
    /// Show Bus 1.3 §13.16.5's row of a link this desk's wire accepted, as the acceptor names it: its role, the connector's kind, id and
    /// name, its carrier, level, TLS, what proved it and the kid, its scope now, since when (ms since 1970), its light and mark, its flags,
    /// whether it has ended, and for an ended row when (ms since 1970, 0 while it stands) and why — the why the edge's own words already
    /// carry. Never the row's words, which name the address it was dialled from, and never a key, a proof, a nonce, a lid or an address.
    /// </summary>
    private static object AcceptedRow(AppLinkRow r) => new
    {
        role = "acceptor",
        t = r.T,
        id = r.Id,
        name = r.Label,
        carrier = "line",
        level = r.Level,
        tls = r.Tls,
        verified = r.Verified,
        kid = r.Kid,
        scope = r.Scope,
        since = new DateTimeOffset(DateTime.SpecifyKind(r.SinceUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        light = LinkRecord.LightWord(r.Light),
        mark = r.Mark,
        flags = r.Flags,
        ended = r.Ended,
        endedAt = r.EndedUtc is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : 0,
        endWords = r.EndWords,
    };
}

/// <summary>
/// The links as the desk's last gather read them — each record this desk holds by the app it is to, each row its wire accepted by
/// its lid — so EYE JSON carries a record's clock and counts as they stand, not as they stood when the picture was last rebuilt
/// (the picture moves only when a record's face does).
/// </summary>
public sealed class EyeLinksNow
{
    private readonly Dictionary<string, LinkRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AppLinkRow> _accepted = new(StringComparer.Ordinal);

    public static EyeLinksNow Empty { get; } = new();

    public static EyeLinksNow Of(EyeFacts f)
    {
        var now = new EyeLinksNow();
        foreach (var a in f.ShowApps)
            if (a.Link is { } r) now._records.TryAdd(a.Id, r);
        foreach (var r in f.AppLinks) now._accepted.TryAdd(r.Lid, r);
        return now;
    }

    public LinkRecord? Record(string app) => app.Length > 0 && _records.TryGetValue(app, out var r) ? r : null;

    public AppLinkRow? Accepted(string lid) => _accepted.TryGetValue(lid, out var r) ? r : null;
}
