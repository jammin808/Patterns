namespace Patterns.Core.Play;

/// <summary>
/// The room's word list, matched on whole words: "cunt" stops "cunt" and "you cunt!" and lets "Scunthorpe" through.
/// An entry says otherwise with its own wildcard — "fuck*" a word that starts so, "*fuck" one that ends so,
/// "*fuck*" one that holds it anywhere — and an entry with a space is a phrase, a run of whole words in order.
/// Case never matters; a word is a run of letters and digits, so punctuation and spaces are the boundaries.
/// The audience port's nicknames, groups, answers and messages meet it, and the ticker's items do (round 83).
/// </summary>
public static class WordList
{
    /// <summary>The list a room starts with — the maintainer's to change on the room; each entry's wildcards say how it matches.</summary>
    public static readonly IReadOnlyList<string> Default = new[] { "*fuck*", "*shit*", "cunt", "cunts", "nigger*", "faggot*" };

    /// <summary>True when <paramref name="text"/> holds an entry of <paramref name="entries"/>, by each entry's own rule.</summary>
    public static bool Matches(string? text, IReadOnlyList<string> entries)
    {
        if (string.IsNullOrEmpty(text) || entries.Count == 0) return false;
        var words = Words(text);
        if (words.Count == 0) return false;
        foreach (var raw in entries)
        {
            var entry = (raw ?? "").Trim().ToLowerInvariant();
            if (entry.Length == 0) continue;
            var prefix = entry.EndsWith('*');                                          // "fuck*": a word that starts so
            var suffix = entry.StartsWith('*');                                        // "*fuck": a word that ends so
            var parts = Words(entry.Trim('*'));
            if (parts.Count == 0) continue;
            if (parts.Count > 1)
            {
                if (Phrase(words, parts)) return true;
                continue;
            }
            var w = parts[0];
            foreach (var word in words)
            {
                var hit = (prefix, suffix) switch
                {
                    (true, true) => word.Contains(w, StringComparison.Ordinal),
                    (true, false) => word.StartsWith(w, StringComparison.Ordinal),
                    (false, true) => word.EndsWith(w, StringComparison.Ordinal),
                    _ => word == w,
                };
                if (hit) return true;
            }
        }
        return false;
    }

    private static bool Phrase(List<string> words, List<string> parts)
    {
        for (var i = 0; i + parts.Count <= words.Count; i++)
        {
            var all = true;
            for (var j = 0; j < parts.Count && all; j++) all = words[i + j] == parts[j];
            if (all) return true;
        }
        return false;
    }

    /// <summary>The runs of letters and digits in <paramref name="text"/>, lower-cased, in order.</summary>
    public static List<string> Words(string text)
    {
        var words = new List<string>();
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0)
            {
                words.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words;
    }
}
