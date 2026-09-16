using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.Core.Menus;

/// <summary>
/// The Eye's own right-click menu (round 66), for a thing the desk has no menu of its own for —
/// a display, a source, a device, a deck, a node, an audio output: FOCUS and the whole picture,
/// the things it is linked to (each a FOCUS of its own), the page of the rail that shows it, and
/// ASK with the thing's facts in the question. A screen opens the wall tile's menu instead, and
/// the cue stack its standby cue's — the Eye never keeps a copy of a menu the desk already has.
/// </summary>
public static class EyeMenus
{
    private const string NeedKey = "Save an Anthropic API key on the Assistant page first.";

    public static DeskMenu For(DeskFacts d, EyeGraph g, EyeNode n)
    {
        var tone = n.Light switch
        {
            CheckLight.Red => MenuTone.Warn,
            CheckLight.Amber => MenuTone.Preview,
            _ => MenuTone.Tile,
        };
        var eye = new List<MenuEntry>
        {
            new("eye.focus", "Focus the eye here", MenuScope.Live, MenuTone.Tile)
            {
                Detail = "The camera on this and what it links to; the rest dims",
                Wire = "EYE FOCUS " + n.Id,
                Action = new ShowAction(ShowActionKind.EyeFocus, "", n.Id),
            },
            new("eye.reset", "The whole picture", MenuScope.Live, MenuTone.Tile)
            {
                Detail = "Every band, nothing dimmed, the view you had before the focus",
                Wire = "EYE RESET",
                Action = new ShowAction(ShowActionKind.EyeReset),
            },
        };
        if (g.Problems.Count > 0)
        {
            eye.Add(new MenuEntry("eye.next", "Next problem", MenuScope.Live, MenuTone.Warn)
            {
                Detail = g.Problems.Count == 1 ? "The one red or amber thing in the picture" : $"The next of {g.Problems.Count} red and amber things",
                Wire = "EYE NEXT",
                Action = new ShowAction(ShowActionKind.EyeNext),
            });
        }

        var linked = new List<MenuEntry>();
        foreach (var id in g.Neighbours(n.Id))
        {
            var c = g.Find(id);
            if (c is null) continue;
            var edge = g.EdgeBetween(n.Id, id);
            var verb = edge is null ? "" : edge.From == n.Id ? $"{verbWord(edge)} it" : $"{verbWord(edge)} this";
            linked.Add(new MenuEntry("eye.contact:" + c.Id, c.Label, MenuScope.Live, ToneOf(c.Light))
            {
                Detail = string.Join(" · ", new[] { verb, c.Sub }.Where(w => w.Length > 0)),
                Wire = "EYE FOCUS " + c.Id,
                Action = new ShowAction(ShowActionKind.EyeFocus, "", c.Id),
            });
        }

        var go = new List<MenuEntry>();
        if (n.Route is { } route)
        {
            go.Add(new MenuEntry("eye.open", $"Open {route.Page}", MenuScope.Go, MenuTone.Go)
            {
                Detail = route.Item.Length > 0 ? "The page, with this selected" : "The page of the rail that shows it",
                Route = route,
            });
        }

        var facts = string.Join("; ", n.Words);
        var linkedWords = string.Join(", ", g.Neighbours(n.Id).Select(id => g.Find(id)).Where(c => c is not null).Select(c => $"{c!.Label} ({EyeGraph.Light(c.Light)})"));
        var ask = new List<MenuEntry>
        {
            new("eye.ask", "What is this?", MenuScope.Ask, MenuTone.Ask)
            {
                Detail = "The thing, its facts and what it links to, in the question",
                Question = $"On the God's Eye, '{n.Label}' ({n.Kind}, {EyeGraph.Light(n.Light)}) reads: {n.Sub}. Its facts: {(facts.Length > 0 ? facts : "none recorded")}. It is linked to: {(linkedWords.Length > 0 ? linkedWords : "nothing")}. What is it and what does it need from me now?",
                Because = d.AssistantReady ? "" : NeedKey,
            },
        };
        if (n.IsProblem)
        {
            ask.Add(new MenuEntry("eye.why", $"Why is it {EyeGraph.Light(n.Light)}?", MenuScope.Ask, MenuTone.Ask)
            {
                Detail = "The fault in words, and the first thing to try",
                Question = $"On the God's Eye, '{n.Label}' ({n.Kind}) is {EyeGraph.Light(n.Light)}: {n.Sub}. Facts: {(facts.Length > 0 ? facts : "none recorded")}. Linked to: {(linkedWords.Length > 0 ? linkedWords : "nothing")}. Why, and what is the first thing to check — answer from these facts, and say unknown where they are silent.",
                Because = d.AssistantReady ? "" : NeedKey,
            });
        }

        var groups = new List<MenuGroup>
        {
            new("EYE", MenuTone.Tile, eye) { Note = "Moves what the desk is looking at, never the show." },
        };
        if (linked.Count > 0) groups.Add(new MenuGroup("LINKED TO", MenuTone.Tile, linked) { Note = "Each is a focus of its own." });
        if (go.Count > 0) groups.Add(new MenuGroup("GO TO", MenuTone.Go, go));
        groups.Add(new MenuGroup("ASK", MenuTone.Ask, ask) { Note = "The assistant answers from the facts; the desk runs nothing." });
        return new DeskMenu("eye", n.Id, n.Label, n.Sub, tone, groups);

        static string verbWord(EyeEdge e) => e.Kind switch
        {
            EyeEdgeKind.Feeds => "feeds",
            EyeEdgeKind.Shows => "shows",
            EyeEdgeKind.Drives => "drives",
            EyeEdgeKind.Carries => "carries to",
            EyeEdgeKind.Controls => "controls",
            EyeEdgeKind.Mirrors => "mirrors",
            EyeEdgeKind.Follows => "follows",
            EyeEdgeKind.Hears => "is heard by",
            EyeEdgeKind.Routes => "routes to",
            EyeEdgeKind.Serves => "serves",
            _ => "sends to",
        };
    }

    private static MenuTone ToneOf(CheckLight light) => light switch
    {
        CheckLight.Red => MenuTone.Warn,
        CheckLight.Amber => MenuTone.Preview,
        _ => MenuTone.Tile,
    };
}
