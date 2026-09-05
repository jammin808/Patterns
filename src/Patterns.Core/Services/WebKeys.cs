namespace Patterns.Core.Services;

/// <summary>
/// One key as a browser sees it: what a page reads in event.key and event.code, the Windows
/// virtual key, the text the press types, and the modifier bits held with it.
/// </summary>
public readonly record struct WebKeyPress(string Key, string Code, int VirtualKey, string Text, int Modifiers)
{
    /// <summary>The browser's modifier bits (the DevTools Input domain's).</summary>
    public const int Alt = 1, Ctrl = 2, Meta = 4, Shift = 8;

    public bool HasText => Text.Length > 0;

    public bool Has(int modifier) => (Modifiers & modifier) != 0;

    /// <summary>"Ctrl+Shift+F5", "Space", "k" — the chord as the desk writes it.</summary>
    public string Chord => WebKeys.Describe(this);
}

/// <summary>
/// Key chords the way an operator writes them — "ArrowRight", "Space", "k", "Shift+N",
/// "Ctrl+Shift+F5" — turned into what a browser expects (a US layout; a character no US key
/// types is inserted as text instead). Pure: the desk's keyboard, cues, the wire, OSC and
/// Companion all speak this.
/// </summary>
public static class WebKeys
{
    private sealed record Named(string Key, string Code, int VirtualKey, string Text = "");

    private static readonly Dictionary<string, Named> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = new("Enter", "Enter", 13, "\r"),
        ["return"] = new("Enter", "Enter", 13, "\r"),
        ["escape"] = new("Escape", "Escape", 27),
        ["esc"] = new("Escape", "Escape", 27),
        ["tab"] = new("Tab", "Tab", 9),
        ["space"] = new(" ", "Space", 32, " "),
        ["spacebar"] = new(" ", "Space", 32, " "),
        ["backspace"] = new("Backspace", "Backspace", 8),
        ["bksp"] = new("Backspace", "Backspace", 8),
        ["back"] = new("Backspace", "Backspace", 8),
        ["delete"] = new("Delete", "Delete", 46),
        ["del"] = new("Delete", "Delete", 46),
        ["insert"] = new("Insert", "Insert", 45),
        ["ins"] = new("Insert", "Insert", 45),
        ["home"] = new("Home", "Home", 36),
        ["end"] = new("End", "End", 35),
        ["pageup"] = new("PageUp", "PageUp", 33),
        ["pgup"] = new("PageUp", "PageUp", 33),
        ["pagedown"] = new("PageDown", "PageDown", 34),
        ["pgdn"] = new("PageDown", "PageDown", 34),
        ["arrowleft"] = new("ArrowLeft", "ArrowLeft", 37),
        ["left"] = new("ArrowLeft", "ArrowLeft", 37),
        ["arrowup"] = new("ArrowUp", "ArrowUp", 38),
        ["up"] = new("ArrowUp", "ArrowUp", 38),
        ["arrowright"] = new("ArrowRight", "ArrowRight", 39),
        ["right"] = new("ArrowRight", "ArrowRight", 39),
        ["arrowdown"] = new("ArrowDown", "ArrowDown", 40),
        ["down"] = new("ArrowDown", "ArrowDown", 40),
        ["capslock"] = new("CapsLock", "CapsLock", 20),
        ["plus"] = new("+", "Equal", 187, "+"),
        ["minus"] = new("-", "Minus", 189, "-"),
        ["comma"] = new(",", "Comma", 188, ","),
        ["period"] = new(".", "Period", 190, "."),
        ["slash"] = new("/", "Slash", 191, "/"),
        ["semicolon"] = new(";", "Semicolon", 186, ";"),
        ["quote"] = new("'", "Quote", 222, "'"),
        ["backslash"] = new("\\", "Backslash", 220, "\\"),
        ["bracketleft"] = new("[", "BracketLeft", 219, "["),
        ["bracketright"] = new("]", "BracketRight", 221, "]"),
        ["backquote"] = new("`", "Backquote", 192, "`"),
    };

    /// <summary>The unshifted punctuation keys of a US keyboard: the character → its code, virtual key and shifted character.</summary>
    private static readonly Dictionary<char, (string Code, int VirtualKey, char Shifted)> Punctuation = new()
    {
        [';'] = ("Semicolon", 186, ':'),
        ['='] = ("Equal", 187, '+'),
        [','] = ("Comma", 188, '<'),
        ['-'] = ("Minus", 189, '_'),
        ['.'] = ("Period", 190, '>'),
        ['/'] = ("Slash", 191, '?'),
        ['`'] = ("Backquote", 192, '~'),
        ['['] = ("BracketLeft", 219, '{'),
        ['\\'] = ("Backslash", 220, '|'),
        [']'] = ("BracketRight", 221, '}'),
        ['\''] = ("Quote", 222, '"'),
    };

    private const string ShiftedDigits = ")!@#$%^&*(";

    static WebKeys()
    {
        for (var n = 1; n <= 24; n++) NamedKeys["f" + n] = new("F" + n, "F" + n, 111 + n);
    }

    /// <summary>A chord — "ArrowRight", "k", "Shift+N", "Ctrl+Shift+F5", "+" — as a key press; false for words that are not keys.</summary>
    public static bool TryParse(string? chord, out WebKeyPress press)
    {
        press = default;
        var s = (chord ?? "").Trim();
        if (s.Length == 0) return false;

        // "+" is a key too: "Ctrl++" is Ctrl and the plus key, "+" the key alone ("Ctrl+" is nothing).
        var plusKey = s == "+" || s.EndsWith("++", StringComparison.Ordinal);
        var body = plusKey ? s[..^1].TrimEnd('+') : s;
        var tokens = body.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (plusKey) tokens.Add("+");
        if (tokens.Count == 0) return false;

        var modifiers = 0;
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var m = Modifier(tokens[i]);
            if (m == 0) return false;
            modifiers |= m;
        }

        var name = tokens[^1];
        Named? key;
        if (name.Length == 1) key = ForCharCore(name[0], ref modifiers);
        else if (!NamedKeys.TryGetValue(name, out key)) key = null;
        if (key is null) return false;

        // A shortcut types nothing: Ctrl+k is a command, not a k.
        var text = (modifiers & (WebKeyPress.Ctrl | WebKeyPress.Alt | WebKeyPress.Meta)) != 0 ? "" : key.Text;
        press = new WebKeyPress(key.Key, key.Code, key.VirtualKey, text, modifiers);
        return true;
    }

    /// <summary>The key that types a character on a US keyboard (Shift held for a capital or a shifted symbol), or null when none does.</summary>
    public static WebKeyPress? ForChar(char c)
    {
        var modifiers = 0;
        var key = ForCharCore(c, ref modifiers);
        return key is null ? null : new WebKeyPress(key.Key, key.Code, key.VirtualKey, key.Text, modifiers);
    }

    /// <summary>The chord written the one way — "Ctrl+Shift+F5", "Shift+N", "Space" — or "" when the text is not a key.</summary>
    public static string Normalize(string? chord) => TryParse(chord, out var press) ? Describe(press) : "";

    /// <summary>"Ctrl+Alt+Shift+Meta+key" in that order; the space bar reads Space.</summary>
    public static string Describe(in WebKeyPress p)
    {
        var parts = new List<string>(5);
        if (p.Has(WebKeyPress.Ctrl)) parts.Add("Ctrl");
        if (p.Has(WebKeyPress.Alt)) parts.Add("Alt");
        if (p.Has(WebKeyPress.Shift)) parts.Add("Shift");
        if (p.Has(WebKeyPress.Meta)) parts.Add("Meta");
        parts.Add(p.Key == " " ? "Space" : p.Key);
        return string.Join('+', parts);
    }

    private static Named? ForCharCore(char c, ref int modifiers)
    {
        var shift = (modifiers & WebKeyPress.Shift) != 0;
        if (char.IsAsciiLetter(c))
        {
            var upper = char.ToUpperInvariant(c);
            if (char.IsUpper(c)) shift = true;
            if (shift) modifiers |= WebKeyPress.Shift;
            var key = shift ? upper.ToString() : char.ToLowerInvariant(c).ToString();
            return new Named(key, "Key" + upper, upper, key);
        }
        if (char.IsAsciiDigit(c))
        {
            var d = c - '0';
            var key = shift ? ShiftedDigits[d].ToString() : c.ToString();
            return new Named(key, "Digit" + c, 48 + d, key);
        }
        if (Punctuation.TryGetValue(c, out var p))
        {
            var key = shift ? p.Shifted.ToString() : c.ToString();
            return new Named(key, p.Code, p.VirtualKey, key);
        }
        foreach (var (baseChar, info) in Punctuation)
        {
            if (info.Shifted != c) continue;
            modifiers |= WebKeyPress.Shift;
            return new Named(c.ToString(), info.Code, info.VirtualKey, c.ToString());
        }
        var digit = ShiftedDigits.IndexOf(c);
        if (digit >= 0)
        {
            modifiers |= WebKeyPress.Shift;
            return new Named(c.ToString(), "Digit" + digit, 48 + digit, c.ToString());
        }
        return c switch
        {
            ' ' => NamedKeys["space"],
            '\n' or '\r' => NamedKeys["enter"],
            '\t' => NamedKeys["tab"],
            _ => null,
        };
    }

    private static int Modifier(string token) => token.ToLowerInvariant() switch
    {
        "ctrl" or "control" or "ctl" => WebKeyPress.Ctrl,
        "shift" => WebKeyPress.Shift,
        "alt" or "option" or "opt" => WebKeyPress.Alt,
        "meta" or "cmd" or "command" or "win" or "windows" or "super" => WebKeyPress.Meta,
        _ => 0,
    };
}
