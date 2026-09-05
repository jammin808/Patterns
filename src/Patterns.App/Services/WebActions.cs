using Avalonia.Input;
using Patterns.Core.Media;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The web actions of the action layer: which page a target names, and a key, an action, a
/// click, typed text, a reload or another address run on it. The desk's chips, cues, the wire,
/// OSC, Companion and the phone all come through here.
/// </summary>
public static class WebActions
{
    /// <summary>Runs one web action: the page named (or the page on air), then the deed.</summary>
    public static ActionResult Execute(AppServices s, ShowAction action)
    {
        var page = Resolve(s, action.Target, out var name, out var why);
        if (page is null) return ActionResult.Refused(why);
        switch (action.Kind)
        {
            case ShowActionKind.WebKey:
                return Press(page, name, action.Value);
            case ShowActionKind.WebClick:
            {
                if (!WebPresets.TryParsePoint(action.Value, out var x, out var y))
                {
                    return ActionResult.Refused($"A click needs 'x y' in percent of the page, e.g. 50 50 — not '{action.Value}'.");
                }
                var nx = (float)(x / 100);
                var ny = (float)(y / 100);
                page.PointerMove(nx, ny);
                page.PointerDown(nx, ny);
                page.PointerUp(nx, ny);
                return ActionResult.Done($"Clicked {name} at {x:0}%, {y:0}%.");
            }
            case ShowActionKind.WebType:
                if (action.Value.Length == 0) return ActionResult.Refused("Nothing to type.");
                page.TypeText(action.Value);
                return ActionResult.Done($"Typed into {name}.");
            case ShowActionKind.WebReload:
                page.Reload();
                return ActionResult.Done($"{name} reloading.");
            case ShowActionKind.WebOpen:
            {
                var address = WebAddress.Normalize(action.Value);
                if (address.Length == 0) return ActionResult.Refused("WEB OPEN needs an address.");
                page.Navigate(address);
                return ActionResult.Done($"{name} → {WebAddress.ShortName(address)} (the pattern keeps its own address; a look recall brings it back).");
            }
            default:
                return ActionResult.Refused($"Not a web action: {action.Kind}.");
        }
    }

    /// <summary>
    /// A key chord or a page action on a page: "next" becomes the right arrow on a deck, "play"
    /// drives YouTube's own player, "reload" reloads; anything else is read as a key.
    /// </summary>
    public static ActionResult Press(IWebSource page, string name, string keyOrAction)
    {
        var value = (keyOrAction ?? "").Trim();
        if (value.Length == 0) return ActionResult.Refused("A web page key needs a key (ArrowRight, Space, Ctrl+Shift+F5) or an action (next, play, present…).");
        if (value.Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            page.Reload();
            return ActionResult.Done($"{name} reloading.");
        }
        var preset = WebPresets.For(page.CurrentUrl);
        var action = preset.Find(value) ?? (preset.Service == PageService.Page ? null : WebPresets.For(PageService.Page).Find(value));
        if (action is not null)
        {
            if (action.IsScript) page.RunScript(action.Script);
            else page.PressKey(action.Chord);
            return ActionResult.Done($"{preset.Name}: {action.Label.ToLowerInvariant()} → {name}{(action.IsScript ? "" : $" ({action.Chord})")}.");
        }
        if (!WebKeys.TryParse(value, out var press))
        {
            return ActionResult.Refused($"'{value}' is neither a key (ArrowRight, Space, k, Ctrl+Shift+F5) nor a page action ({string.Join(", ", preset.Actions.Select(a => a.Id))}).");
        }
        page.PressKey(WebKeys.Describe(press));
        return ActionResult.Done($"Key {WebKeys.Describe(press)} → {name}.");
    }

    /// <summary>
    /// The mounted page a target names — "" is the page the program shows; else a nickname, an
    /// address or a word of it among the pages on the desk — with its name for the status line.
    /// </summary>
    public static IWebSource? Resolve(AppServices s, string target, out string name, out string why)
    {
        name = "";
        why = "";
        var t = (target ?? "").Trim();
        if (t.Length == 0)
        {
            var wanted = MediaLocator.FindWantedInputs(s.Bus.Current).FirstOrDefault(w => w.Kind == MediaLocator.WantedKind.Web);
            if (wanted is null)
            {
                why = "No web page is on air — the action needs one on the pattern or a layer of the look on air (or name a page: … ON <page>).";
                return null;
            }
            name = s.State.InputLabel(wanted.Key, WebAddress.ShortName(wanted.Target));
            if (InputBus.For(wanted.Key) is IWebSource onAir) return onAir;
            why = $"The page on air ({name}) is still opening — {(WebInput.AvailabilityNote.Length > 0 ? WebInput.AvailabilityNote : "try again in a moment")}.";
            return null;
        }
        foreach (var key in InputBus.Keys)
        {
            if (!key.StartsWith("web:", StringComparison.Ordinal) || InputBus.For(key) is not IWebSource page) continue;
            var url = key[4..];
            var nickname = s.State.InputLabel(key, "");
            if (!WebPresets.Matches(key, url, nickname, t) && !WebPresets.Matches(key, page.CurrentUrl, nickname, t)) continue;
            name = nickname.Length > 0 ? nickname : WebAddress.ShortName(url);
            return page;
        }
        why = $"No web page called '{t}' is on the desk — put it on the pattern or a layer first, or name it by its nickname or a word of its address.";
        return null;
    }
}

/// <summary>The desk's keyboard as a page sees it: an Avalonia key with its modifiers, as a chord <see cref="WebKeys"/> reads.</summary>
public static class WebKeyboard
{
    /// <summary>The chord for a key press, or null for a key that is nothing to a page (a modifier alone, a media key).</summary>
    public static string? ChordFor(Key key, KeyModifiers modifiers)
    {
        var name = KeyName(key);
        if (name is null) return null;
        var parts = new List<string>(5);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
        parts.Add(name);
        return string.Join('+', parts);
    }

    private static string? KeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('a' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return ((char)('0' + (key - Key.NumPad0))).ToString();
        if (key is >= Key.F1 and <= Key.F24) return "F" + (key - Key.F1 + 1);
        return key switch
        {
            Key.Space => "Space",
            Key.Return or Key.Enter => "Enter",
            Key.Escape => "Escape",
            Key.Tab => "Tab",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.OemPeriod or Key.Decimal => ".",
            Key.OemComma => ",",
            Key.OemMinus or Key.Subtract => "-",
            Key.OemPlus => "=",
            Key.Add => "+",
            Key.Multiply => "*",
            Key.OemQuestion or Key.Divide => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            _ => null,
        };
    }
}
