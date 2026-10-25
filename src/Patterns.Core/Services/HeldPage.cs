using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>
/// What a Stagewatch window's shell says it shows, as the observer Patterns puts into that page reads it off the
/// shell's root element (<c>data-rev</c>, <c>data-link</c>, <c>data-showing</c>, <c>data-hold</c>, <c>data-error</c>)
/// and the shell's own <c>SWShell.state().loading</c>. <see cref="Rev"/> is the revision of the last thing Stagewatch
/// told the window to show — 0 until it has said anything; <see cref="Link"/> is <c>ok</c>, <c>lost</c> (the shell
/// shows its own loss page, the operator's choice on Stagewatch) or <c>gone</c> (the window was withdrawn or deleted:
/// its address opens nothing any more). Pure.
/// </summary>
public readonly record struct WebShellState(int Rev, string Link, string Showing, bool Hold, string Loading, string Error)
{
    /// <summary>The message type the observer posts; any other message from the page is not the shell's.</summary>
    public const string MessageType = "patterns-shell";

    /// <summary>The longest field read from a message — a shell's words are short; a page that says more is not believed further.</summary>
    public const int MaxField = 200;

    /// <summary>The window was withdrawn, deleted or revoked on Stagewatch.</summary>
    public bool Gone => string.Equals(Link, "gone", StringComparison.Ordinal);

    /// <summary>Stagewatch has said what this window shows (a revision above 0), and the window still exists.</summary>
    public bool Assigned => Rev > 0 && !Gone;

    /// <summary>
    /// Reads the observer's message: <c>{"type":"patterns-shell","rev":3,"link":"ok","showing":"…","hold":false,
    /// "loading":"","error":""}</c>. False for anything else — another type, text that is not JSON, a revision that is
    /// not a whole number from 0 up — and the state stays what it was.
    /// </summary>
    public static bool TryParse(string? json, out WebShellState state)
    {
        state = default;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != MessageType) return false;
            if (!root.TryGetProperty("rev", out var rev) || rev.ValueKind != JsonValueKind.Number || !rev.TryGetInt32(out var r) || r < 0) return false;
            state = new WebShellState(r, Text(root, "link"), Text(root, "showing"),
                root.TryGetProperty("hold", out var hold) && hold.ValueKind == JsonValueKind.True, Text(root, "loading"), Text(root, "error"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }

        static string Text(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return "";
            var s = p.GetString() ?? "";
            return s.Length > MaxField ? s[..MaxField] : s;
        }
    }
}

/// <summary>
/// Round 89: the page a browser source shows when its address is held (<c>kept://…</c> or <c>unkept://…</c>) — what it
/// opens, and why its picture is black. The source keeps the held address as its own and resolves it only at
/// navigation, inside the browser: nothing else ever holds the real address. The picture is black — never words, never
/// white — unless every condition holds:
/// <list type="bullet">
/// <item>the address resolves on this machine (its book keeps it);</item>
/// <item>the navigation to it succeeded;</item>
/// <item>for a Stagewatch window, its shell has been told what to show (a revision above 0) and the window was not
/// withdrawn (<see cref="WebShellState.Assigned"/>): until then the shell draws its own holding page, which is
/// Stagewatch's to show on Stagewatch's screens, not this room's air.</item>
/// </list>
/// A shell's word is taken only from a document on the origin the held address resolved to. A page that is not held
/// is never withheld. One per source, on the browser's thread; pure.
/// </summary>
public sealed class HeldPage
{
    /// <summary>
    /// The observer put into every document the browser opens (document-created). It does nothing outside the top
    /// document of a Stagewatch window (<c>/w/</c> and a window token): there it watches the root element's attributes,
    /// which the shell rewrites on every paint, and posts what they say to the host when it changed.
    /// </summary>
    public const string ObserverScript = """
        (function () {
          'use strict';
          if (window.top !== window || !window.chrome || !window.chrome.webview) return;
          if (!/^\/w\/[a-km-np-z2-9]{26}\/?$/.test(location.pathname)) return;
          var last = '';
          function post() {
            var h = document.documentElement;
            if (!h) return;
            var a = function (n) { return h.getAttribute(n) || ''; };
            var loading = '';
            try { if (window.SWShell && typeof window.SWShell.state === 'function') loading = String(window.SWShell.state().loading || ''); } catch (e) { loading = ''; }
            var msg = JSON.stringify({ type: 'patterns-shell', rev: parseInt(a('data-rev'), 10) || 0, link: a('data-link'), showing: a('data-showing'),
              hold: a('data-hold') === '1', loading: loading, error: a('data-error') });
            if (msg === last) return;
            last = msg;
            try { window.chrome.webview.postMessage(msg); } catch (e) { last = ''; }
          }
          function watch() {
            var h = document.documentElement;
            if (!h) return;
            new MutationObserver(post).observe(h, { attributes: true });
            post();
          }
          if (document.readyState !== 'loading' && document.documentElement) watch();
          else document.addEventListener('DOMContentLoaded', watch, { once: true });
        })();
        """;

    /// <summary>Black while the held page opens: its first document has not arrived.</summary>
    public const string OpeningWhy = "opening the private page";

    /// <summary>Black until Stagewatch says what the window shows: the shell's own holding page is not the air's.</summary>
    public const string UnassignedWhy = "waiting for Stagewatch to say what this window shows";

    /// <summary>Black once the window is withdrawn, deleted or revoked on Stagewatch.</summary>
    public const string GoneWhy = "the window was withdrawn on Stagewatch — its address opens nothing now; paste its new address";

    /// <summary>How many real addresses a page remembers having resolved a held address to (<see cref="HeldAgain"/>): a page's history is short.</summary>
    public const int RememberedMax = 16;

    private readonly Func<string, WebPassResolution> _resolve;
    private readonly Dictionary<string, string> _resolvedBefore = new(StringComparer.Ordinal);   // a real address this page was sent to → the held address it was held under

    /// <param name="resolve">This machine's book: a held address to the real address, or nothing and why.</param>
    public HeldPage(Func<string, WebPassResolution> resolve) => _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));

    /// <summary>The held address the source was last sent to — what every reader of the source names; "" when the page is not held.</summary>
    public string Address { get; private set; } = "";

    /// <summary>Whether the page now is a held one.</summary>
    public bool IsHeld => Address.Length > 0;

    /// <summary>The address the browser was sent to for the held page — the real one — or "" when it resolved to nothing. Never published.</summary>
    internal string Opens { get; private set; } = "";

    /// <summary>The resolved page is a Stagewatch window: its shell's word decides the picture.</summary>
    public bool IsShell { get; private set; }

    /// <summary>The last navigation of the resolved page succeeded.</summary>
    public bool Navigated { get; private set; }

    /// <summary>What the window's shell said last (default until it says anything on this navigation).</summary>
    public WebShellState Shell { get; private set; }

    private string _unresolved = "";
    private string _failed = "";

    /// <summary>
    /// Why the held page's picture is black, in words for the desk, STATE and the Eye; "" when it is drawn, and always
    /// "" for a page that is not held.
    /// </summary>
    public string Why
    {
        get
        {
            if (!IsHeld) return "";
            if (_unresolved.Length > 0) return _unresolved;
            if (_failed.Length > 0) return _failed;
            if (!Navigated) return OpeningWhy;
            if (!IsShell) return "";
            if (Shell.Gone) return GoneWhy;
            return Shell.Rev > 0 ? "" : UnassignedWhy;
        }
    }

    /// <summary>The picture is black: a held page whose <see cref="Why"/> says so.</summary>
    public bool Withheld => Why.Length > 0;

    /// <summary>
    /// A navigation to <paramref name="address"/> begins: a held address resolves now, and the answer is the address
    /// the browser is sent to, or null when it is sent nowhere (the picture is black, <see cref="Why"/> says why). Any
    /// other address is answered as it is and the page is no longer held. Everything the last navigation learned —
    /// success, the shell's word — is forgotten.
    /// </summary>
    public string? Open(string address)
    {
        var a = (address ?? "").Trim();
        Navigated = false;
        Shell = default;
        _failed = "";
        _unresolved = "";
        if (!WindowAddresses.IsHeld(a))
        {
            Address = "";
            Opens = "";
            IsShell = false;
            return a;
        }
        Address = WindowAddresses.Held(a);
        var resolution = _resolve(Address);
        if (!resolution.Resolved || string.IsNullOrEmpty(resolution.Url))
        {
            Opens = "";
            IsShell = false;
            _unresolved = resolution.Why.Length > 0 ? resolution.Why : WebPassBook.NotHereWhy;
            return null;
        }
        Opens = resolution.Url;
        IsShell = WindowAddresses.IsStagewatchWindowUrl(Opens);
        if (Canonical(Opens) is { Length: > 0 } at)
        {
            if (_resolvedBefore.Count >= RememberedMax && !_resolvedBefore.ContainsKey(at)) _resolvedBefore.Clear();
            _resolvedBefore[at] = Address;
        }
        return Opens;
    }

    /// <summary>
    /// A navigation the browser began by itself — back, forward, a link, a redirect — rather than one <see cref="Open"/>
    /// began: the held address it must be held under when it lands on a real address this page was sent to for a held
    /// one, and null when it goes on as it is (the held page's own navigation among them). So BACK from a page a WEB
    /// OPEN brought onto a private page shown before is held again — named by its held address and black until the
    /// shell's word, as a held page is — and never shown, or named, by its real address.
    /// </summary>
    public string? HeldAgain(string? uri)
    {
        if (_resolvedBefore.Count == 0) return null;
        var at = Canonical(uri);
        if (at.Length == 0) return null;
        if (IsHeld && Opens.Length > 0 && string.Equals(at, Canonical(Opens), StringComparison.Ordinal)) return null;
        return _resolvedBefore.TryGetValue(at, out var held) ? held : null;
    }

    /// <summary>An address as a browser reports the navigation to it: scheme, host and port as the URI reads them, its path and query, never its fragment; "" for none.</summary>
    private static string Canonical(string? url)
        => Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Query) : "";

    /// <summary>The top document's navigation ended: success marks the page drawn (a shell still waits for its word); a failure is black with words that name no address.</summary>
    public void Completed(bool success, string status = "")
    {
        if (!IsHeld || Opens.Length == 0) return;
        Navigated = success;
        _failed = success ? "" : "the private page did not open" + (status.Length > 0 ? $" ({status})" : "") + " — check that it is up, then RELOAD";
        if (!success) Shell = default;
    }

    /// <summary>
    /// A message from a document: taken as the shell's word only for a held Stagewatch window, only from a document on
    /// the origin the held address resolved to, and only in the observer's shape. True when it was taken.
    /// </summary>
    public bool Accept(string? sourceUri, string? message)
    {
        if (!IsHeld || !IsShell || Opens.Length == 0) return false;
        if (!SameOrigin(sourceUri, Opens)) return false;
        if (!WebShellState.TryParse(message, out var state)) return false;
        Shell = state;
        return true;
    }

    /// <summary>Scheme, host and port the same, case-blind — the rule a browser's origin follows.</summary>
    public static bool SameOrigin(string? a, string? b)
    {
        if (!Uri.TryCreate(a ?? "", UriKind.Absolute, out var x) || !Uri.TryCreate(b ?? "", UriKind.Absolute, out var y)) return false;
        return string.Equals(x.Scheme, y.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.IdnHost, y.IdnHost, StringComparison.OrdinalIgnoreCase)
            && x.Port == y.Port;
    }
}
