using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Patterns.App.Views.Controls;

/// <summary>One explanation from a page: the heading it sits under (may be empty) and its words.</summary>
public sealed record PageTip(string Heading, string Text);

/// <summary>
/// The tips of a page, read off the page itself: every prose hint (class "tip") in visual
/// order, each under the last section heading (class "h2") before it, duplicates dropped.
/// A hidden hint stays in the tree, so ? TIPS reads the same words the page shows with hints on.
/// </summary>
public static class PageTips
{
    public static IReadOnlyList<PageTip> Collect(Visual? root, string? intro = null)
    {
        var list = new List<PageTip>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(intro) && seen.Add(intro.Trim())) list.Add(new PageTip("", intro.Trim()));
        if (root is null) return list;
        var heading = "";
        foreach (var block in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (block.Classes.Contains("h2"))
            {
                heading = TextOf(block);
                continue;
            }
            if (!block.Classes.Contains("tip")) continue;
            var text = TextOf(block);
            if (text.Length == 0 || !seen.Add(text)) continue;
            list.Add(new PageTip(heading, text));
        }
        return list;
    }

    private static string TextOf(TextBlock block) => (block.Text ?? block.Inlines?.Text ?? "").Trim();
}
