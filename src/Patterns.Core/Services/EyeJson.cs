namespace Patterns.Core.Services;

/// <summary>The Eye on the wire (round 66): EYE / EYE STATUS answers the picture as one JSON object — the headline, the counts, the problems, the focus and the lens, every thing with its place and its words, every link with its light.</summary>
public static class EyeJson
{
    public static string Write(EyeGraph g, EyePlacement p, string? focusId, EyeLens lens)
    {
        var payload = new
        {
            headline = g.Headline,
            worst = g.Worst?.Id ?? "",
            worstLight = EyeGraph.Light(g.WorstLight),
            worstWords = g.Worst is { } w ? $"{w.Label}: {w.Sub}" : "",
            counts = new { green = g.Green, amber = g.Amber, red = g.Red, grey = g.Grey, things = g.Nodes.Count, links = g.Edges.Count },
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
                };
            }).ToArray(),
            edges = g.Edges.Select(e => new { from = e.From, to = e.To, kind = e.Verb, light = EyeGraph.Light(e.Light), words = e.Words }).ToArray(),
        };
        return JsonUtil.SerializeCompact(payload);
    }
}
