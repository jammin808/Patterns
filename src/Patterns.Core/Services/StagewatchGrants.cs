using System.Globalization;
using System.Text;
using System.Text.Json;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// One window a Stagewatch granted to this desk (Show Bus 1.3 §12.4), as this machine keeps it in the Control section's
/// <see cref="StagewatchLinkConfig.Grants"/> — never in a look, a cue or anything a twin mirrors. <see cref="Sw"/> is the
/// granting node's id, <see cref="Window"/> its window's id, <see cref="How"/> <c>page</c> or <c>draw</c>. A page grant's
/// address is <see cref="Base"/> (the scheme, host and port the link reached it at) and <see cref="Secret"/> (its
/// <c>/w/&lt;token&gt;</c>): the secret is masked by its name wherever the settings are carried, and the show names the window
/// only by its held form (<see cref="Held"/>). <see cref="Cannot"/> is why the link did not carry the address ("" when it did);
/// <see cref="Link"/> the carrier it last came on; <see cref="Pin"/> the certificate its link was proved over (12.5's pinned TLS), the
/// only one its page is opened to — "" on a local link.
/// </summary>
public sealed record StagewatchGrant(string Sw, string Window, string Name, string How, string Base, string Secret, string Cannot = "", string Link = "", string Pin = "")
{
    /// <summary>The form the show holds: <c>stagewatch://&lt;sw&gt;/&lt;window&gt;</c>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Held => WindowAddresses.Grant(Sw, Window);

    /// <summary>A page grant whose address reached this desk: it opens.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Opens => How == StagewatchGrants.HowPage && Base.Length > 0 && Secret.Length > 0;

    /// <summary>The real address the browser is sent to — never published, never written: only <see cref="Secret"/> rests, masked by its name.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Url => Opens ? Base + Secret : "";
}

/// <summary>A row of a <c>grants</c> message (12.6) as it arrived: the window, its name, how it is held, its path and why not.</summary>
public sealed record GrantRowHeard(string Window, string Name, string How, string Path, string Cannot);

/// <summary>A whole <c>grants</c> message (12.6): the granting node, the revision on this link, the carrier and the rows.</summary>
public sealed record GrantsHeard(string From, int Rev, string Link, IReadOnlyList<GrantRowHeard> Rows);

/// <summary>Whether a granted window's page is ready to go on air here, and why not.</summary>
public readonly record struct WindowReadiness(bool Ready, string Why);

/// <summary>
/// Round 96 (Show Bus 1.3 §12.4–§12.9): the windows a Stagewatch grants this desk — the <c>grants</c> message read fail closed,
/// the revision taken per link, the grants kept with the machine, the answer (<c>grants-seen</c>) in Stagewatch's own shape, the
/// readiness a live take waits for, and the words every reader says of where the window is shown, read from the air alone.
/// Pure: the link, the settings and the browser hand their facts in.
/// </summary>
public static class StagewatchGrants
{
    /// <summary>The cap word (14.6) and the ability this desk offers on its node link's <c>can=</c> (12.6): it opens pages.</summary>
    public const string Ability = "pages";

    /// <summary>12.6's message types.</summary>
    public const string Type = "grants", SeenType = "grants-seen";

    /// <summary>12.4's ways to hold a window.</summary>
    public const string HowPage = "page", HowDraw = "draw";

    /// <summary>12.6's carriers.</summary>
    public const string LinkTls = "tls", LinkLocal = "local", LinkPlain = "plain";

    /// <summary>The most grants this machine keeps, every Stagewatch's together: a message that lists more is refused whole.</summary>
    public const int Max = 64;

    /// <summary>The longest window name kept; a longer one is cut.</summary>
    public const int MaxName = 64;

    /// <summary>The longest <c>cannot</c> kept from a row, and the longest this desk says in <c>grants-seen</c> (Stagewatch keeps 200).</summary>
    public const int MaxCannot = 200;

    /// <summary>The words for a draw grant: this desk shows pages and draws no window itself.</summary>
    public const string NoDraw = "this desk shows a window's page and draws none itself (it offers pages, not window-draw)";

    /// <summary>The shown words (every reader): on air with the outputs live, staged in the preview alone, or on air with the outputs closed.</summary>
    public const string OnAir = "on air", InPreview = "in the preview — not on air", OutputsClosed = "outputs closed";

    /// <summary>The query this desk adds to its node link's dial (12.6): the abilities it offers.</summary>
    public const string CanQuery = "&can=" + Ability;

    /// <summary>
    /// Reads a <c>grants</c> message, fail closed: the type, the granting node (it must be <paramref name="from"/>, the node the
    /// link verified or was bound to; a link with none takes no grants), a revision from 1, a carrier of 12.6's three, at most <see cref="Max"/> rows, each a window id the held form can
    /// name, a known way to hold it, and a path only where 12.5 lets one ride — a page grant, on a TLS or local link, a window
    /// address of the alphabet's 26 symbols. Anything else refuses the whole message, and <paramref name="why"/> says why in
    /// words that never carry a path.
    /// </summary>
    public static bool TryParse(string? json, string? from, out GrantsHeard heard, out string why)
    {
        heard = new GrantsHeard("", 0, "", Array.Empty<GrantRowHeard>());
        why = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            why = "an empty grants message";
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "type") != Type)
            {
                why = "not a grants message";
                return false;
            }
            // 12.5: a grant rides only the key-verified link of its own node — a link that has not said which node it is takes none.
            if (!PeerLink.IsId(from))
            {
                why = "grants on a link whose Stagewatch has not said which node it is";
                return false;
            }
            var node = Text(root, "from");
            if (!string.Equals(node, from, StringComparison.Ordinal))
            {
                why = "grants from another node than the link's";
                return false;
            }
            if (!root.TryGetProperty("rev", out var r) || r.ValueKind != JsonValueKind.Number || !r.TryGetInt32(out var rev) || rev < 1)
            {
                why = "grants with no revision";
                return false;
            }
            var link = Text(root, "link");
            if (link is not (LinkTls or LinkLocal or LinkPlain))
            {
                why = "grants on a carrier 12.6 does not name";
                return false;
            }
            if (!root.TryGetProperty("grants", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                why = "grants with no list";
                return false;
            }
            if (list.GetArrayLength() > Max)
            {
                why = string.Create(CultureInfo.InvariantCulture, $"more than {Max} grants");
                return false;
            }
            var rows = new List<GrantRowHeard>(list.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                {
                    why = "a grant that is not an object";
                    return false;
                }
                var window = Text(e, "window");
                var how = Text(e, "how");
                var path = Text(e, "path");
                if (!WindowAddresses.IsWindowId(window) || !seen.Add(window))
                {
                    why = "a grant with no window id this desk can name, or one named twice";
                    return false;
                }
                if (how is not (HowPage or HowDraw))
                {
                    why = "a grant held in a way 12.4 does not name";
                    return false;
                }
                if (path.Length > 0 && (how != HowPage || link == LinkPlain || !IsGrantPath(path)))
                {
                    why = "a grant's address where 12.5 lets none ride, or one that is no window address";
                    return false;
                }
                rows.Add(new GrantRowHeard(window, Clean(Text(e, "name"), MaxName), how, path, Clean(Text(e, "cannot"), MaxCannot)));
            }
            heard = new GrantsHeard(node, rev, link, rows);
            return true;
        }
        catch (JsonException)
        {
            why = "a grants message that is not JSON";
            return false;
        }
    }

    /// <summary>A page grant's path: <c>/w/</c> and a window token (12.1).</summary>
    public static bool IsGrantPath(string? path)
    {
        if (path is null || path.Length != 3 + WindowAddresses.WindowTokenLength || !path.StartsWith("/w/", StringComparison.Ordinal)) return false;
        for (var i = 3; i < path.Length; i++)
        {
            if (!WindowAddresses.WindowAlphabet.Contains(path[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// 12.6: a receiver takes the first <c>grants</c> on each new link whatever its revision (<paramref name="taken"/> is 0 before
    /// one), and a later one only with a higher revision.
    /// </summary>
    public static bool Takes(int taken, int rev) => taken == 0 || rev > taken;

    /// <summary>12.6's answer, in the shape Stagewatch reads it: <c>{"type":"grants-seen","rev":3,"cannot":""}</c>.</summary>
    public static string SeenJson(int rev, string? cannot)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("type", SeenType);
            w.WriteNumber("rev", rev);
            w.WriteString("cannot", Clean(cannot ?? "", MaxCannot));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The grants this machine keeps after <paramref name="heard"/> (12.4, "grants outlive the link"): every other Stagewatch's as
    /// they were; this one's exactly the rows listed — a window left out is dropped, so only a received list removes a grant. A
    /// page row that carries its path is kept with <paramref name="baseAddress"/>; one that carries none keeps the address it was
    /// given before (it opens until Stagewatch ends it), with the row's reason. At most <see cref="Max"/> are kept, every
    /// Stagewatch's together: a row past that is not kept. Also the words this desk answers in <c>grants-seen</c>: "" while some
    /// page grant opens here (or none is listed) and every row was kept, else why — the rows not kept first ("this desk keeps at
    /// most 64 granted windows …"), then why it shows no page at all.
    /// </summary>
    public static (IReadOnlyList<StagewatchGrant> Kept, string Cannot) Apply(IReadOnlyList<StagewatchGrant> before, GrantsHeard heard, string baseAddress, string pin = "")
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(heard);
        var others = before.Where(g => !string.Equals(g.Sw, heard.From, StringComparison.Ordinal)).ToList();
        var mine = new List<StagewatchGrant>(heard.Rows.Count);
        var cut = 0;
        foreach (var row in heard.Rows)
        {
            if (others.Count + mine.Count >= Max)
            {
                cut++;
                continue;
            }
            var was = before.FirstOrDefault(g => string.Equals(g.Sw, heard.From, StringComparison.Ordinal) && g.Window == row.Window && g.How == row.How);
            var name = row.Name.Length > 0 ? row.Name : row.Window;
            if (row.How == HowDraw)
            {
                mine.Add(new StagewatchGrant(heard.From, row.Window, name, HowDraw, "", "", NoDraw, heard.Link));
                continue;
            }
            var (b, secret, kept) = row.Path.Length > 0 ? (baseAddress, row.Path, pin) : (was?.Base ?? "", was?.Secret ?? "", was?.Pin ?? "");
            mine.Add(new StagewatchGrant(heard.From, row.Window, name, HowPage, b, secret, row.Path.Length > 0 ? "" : row.Cannot, heard.Link, kept));
        }
        var cannot = mine.Count == 0 || mine.Exists(g => g.Opens) ? ""
            : "no page grant's address reached this desk — " + (mine.Find(g => g.Cannot.Length > 0)?.Cannot ?? NoDraw);
        // A window this desk could not keep is said in the answer, never dropped in silence: Stagewatch shows the app's reason.
        if (cut > 0) cannot = CutWords(cut) + (cannot.Length > 0 ? "; " + cannot : "");
        return (Sanitize(others.Concat(mine)), Clean(cannot, MaxCannot));
    }

    /// <summary>The answer's words for rows past the cap.</summary>
    public static string CutWords(int cut)
        => string.Create(CultureInfo.InvariantCulture, $"this desk keeps at most {Max} granted windows — {cut} of these {(cut == 1 ? "was" : "were")} not kept");

    /// <summary>
    /// The grants a name finds: <c>&lt;node&gt;/&lt;window&gt;</c> (or the held form) names one exactly; otherwise the grants whose
    /// window id is the name (case-blind), else those whose window name is. More than one is a name two Stagewatches both grant.
    /// </summary>
    public static IReadOnlyList<StagewatchGrant> Matches(IEnumerable<StagewatchGrant> grants, string? nameOrId)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var key = (nameOrId ?? "").Trim();
        if (key.Length == 0) return Array.Empty<StagewatchGrant>();
        var list = grants as IReadOnlyList<StagewatchGrant> ?? grants.ToList();
        if (WindowAddresses.TryGrant(key, out var sw, out var window) || SplitNodeWindow(key, out sw, out window))
            return list.Where(g => g.Sw == sw && g.Window == window).Take(1).ToList();
        var byId = list.Where(g => string.Equals(g.Window, key, StringComparison.OrdinalIgnoreCase)).ToList();
        return byId.Count > 0 ? byId : list.Where(g => string.Equals(g.Name, key, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>A grant by its name or id (<see cref="Matches"/>): the one it finds, null when it finds none or more than one.</summary>
    public static StagewatchGrant? Find(IEnumerable<StagewatchGrant> grants, string? nameOrId)
        => Matches(grants, nameOrId) is [var one] ? one : null;

    /// <summary>The words for a name that finds grants of more than one Stagewatch: each by <c>&lt;node&gt;/&lt;window&gt;</c>, the form that names one.</summary>
    public static string AmbiguousWords(string name, IReadOnlyList<StagewatchGrant> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        return $"'{name.Trim()}' names windows more than one Stagewatch granted this desk ({string.Join(", ", found.Select(g => $"{g.Name} = {g.Sw}/{g.Window}"))}) — name one as <node>/<window>";
    }

    private static bool SplitNodeWindow(string key, out string sw, out string window)
    {
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        sw = slash > 0 ? key[..slash] : "";
        window = slash > 0 ? key[(slash + 1)..] : "";
        return PeerLink.IsId(sw) && WindowAddresses.IsWindowId(window);
    }

    /// <summary>
    /// The grants a settings file holds, as this desk keeps them (12.4, 12.5): an unreadable row is dropped, at most <see cref="Max"/>
    /// kept, each node's window once. A row's address is kept only whole — <see cref="StagewatchGrant.Base"/> https to any host, or http
    /// to loopback alone (12.5's local carrier), with no path; <see cref="StagewatchGrant.Secret"/> a window address
    /// (<see cref="IsGrantPath"/>) — else neither is, and the grant opens nothing until Stagewatch sends it again. A draw grant keeps no
    /// address. Names and reasons are cleaned and bounded; a pin that does not read is dropped (and no certificate opens its page).
    /// </summary>
    public static IReadOnlyList<StagewatchGrant> Sanitize(IEnumerable<StagewatchGrant?>? grants)
    {
        var kept = new List<StagewatchGrant>();
        if (grants is null) return kept;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in grants)
        {
            if (kept.Count >= Max) break;
            if (g is null || !PeerLink.IsId(g.Sw) || !WindowAddresses.IsWindowId(g.Window) || g.How is not (HowPage or HowDraw)) continue;
            if (!seen.Add(g.Sw + "/" + g.Window)) continue;
            var page = g.How == HowPage && IsGrantBase(g.Base) && IsGrantPath(g.Secret);
            var name = Clean(g.Name ?? "", MaxName);
            var link = g.Link is LinkTls or LinkLocal or LinkPlain ? g.Link : "";
            kept.Add(new StagewatchGrant(g.Sw, g.Window, name.Length > 0 ? name : g.Window, g.How,
                page ? g.Base.TrimEnd('/') : "", page ? g.Secret : "", Clean(g.Cannot ?? "", MaxCannot), link, page ? TlsFingerprint.Normal(g.Pin) : ""));
        }
        return kept;
    }

    /// <summary>A page grant's base: https to any host, or http to a loopback address alone; a scheme, host and port, nothing more.</summary>
    public static bool IsGrantBase(string? baseAddress)
    {
        if (string.IsNullOrEmpty(baseAddress) || !Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri)) return false;
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/") return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        return uri.Scheme == Uri.UriSchemeHttp && System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var at) && System.Net.IPAddress.IsLoopback(at);
    }

    /// <summary>
    /// 12.9 and the live take's gate: a granted window's page is ready only when it is open on this desk, drawn (not withheld),
    /// its shell has said what the window shows, has nothing still loading, and its link to Stagewatch is up. Each no says why.
    /// </summary>
    public static WindowReadiness Readiness(StagewatchGrant? grant, bool mounted, bool withheld, string withheldWhy, WebShellState? shell)
    {
        if (grant is null) return new WindowReadiness(false, "no such window is granted to this desk");
        if (grant.How != HowPage) return new WindowReadiness(false, NoDraw);
        if (!grant.Opens) return new WindowReadiness(false, "its address has not reached this desk" + (grant.Cannot.Length > 0 ? " — " + grant.Cannot : ""));
        if (!mounted) return new WindowReadiness(false, "its page is not open on this desk yet");
        if (withheld) return new WindowReadiness(false, withheldWhy.Length > 0 ? withheldWhy : HeldPage.UnassignedWhy);
        if (shell is not { } s || !s.Assigned) return new WindowReadiness(false, HeldPage.UnassignedWhy);
        if (s.Loading.Length > 0) return new WindowReadiness(false, "its page is still loading");
        if (!string.Equals(s.Link, "ok", StringComparison.Ordinal)) return new WindowReadiness(false, "its link to Stagewatch is lost — the window shows its own loss state");
        return new WindowReadiness(true, "");
    }

    /// <summary>
    /// Where a granted window is shown, read from the air alone: on air while the programme shows it and the outputs are live,
    /// "outputs closed" when it is on the air but nothing reaches a screen, "in the preview — not on air" when only the edited
    /// state stages it, "" when nothing shows it.
    /// </summary>
    public static string Shown(bool onAir, bool inPreview, bool outputsLive)
        => onAir ? outputsLive ? OnAir : OutputsClosed : inPreview ? InPreview : "";

    /// <summary>True when a picture shows the held address: its page or either layer's.</summary>
    public static bool Uses(PatternConfig pattern, string held)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return Same(pattern.Kind == PatternKind.Media && pattern.Media.Source == MediaSource.Web ? pattern.Media.WebUrl : "", held)
               || Same(pattern.Layer1.WebUrl, held) || Same(pattern.Layer2.WebUrl, held);

        static bool Same(string? url, string held) => !string.IsNullOrEmpty(url) && string.Equals(WindowAddresses.Held(url), held, StringComparison.Ordinal);
    }

    /// <summary>
    /// The targets a show puts a held address on: "PGM" for the programme's own picture, then each screen whose own picture
    /// shows it, by screen id, in the show's order.
    /// </summary>
    public static IReadOnlyList<string> Places(ShowState state, string held)
    {
        ArgumentNullException.ThrowIfNull(state);
        var list = new List<string>();
        if (Uses(state.Pattern, held)) list.Add("PGM");
        foreach (var a in state.Independent)
        {
            if (ContentTargets.UsesOwnPattern(state, a.ScreenId) && Uses(a.Pattern, held) && !list.Contains(a.ScreenId)) list.Add(a.ScreenId);
        }
        return list;
    }

    /// <summary>A name or a reason as kept: control characters gone, trimmed, at most <paramref name="max"/> characters.</summary>
    private static string Clean(string text, int max)
    {
        var sb = new StringBuilder(Math.Min(text.Length, max));
        foreach (var c in text)
        {
            if (sb.Length >= max) break;
            if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}

/// <summary>What a browser does with a certificate a held page's server shows (12.5's pinned TLS).</summary>
public enum WindowCertificateVerdict
{
    /// <summary>Not this decision's: the platform's own check stands (a page that is not a granted window).</summary>
    Default,
    /// <summary>The certificate is the one pinned for the granting Stagewatch: the page opens.</summary>
    Allow,
    /// <summary>Anything else: the page does not open, and draws black.</summary>
    Deny,
}

/// <summary>
/// Round 96 (Show Bus 1.3 §12.5): a granted window's page is opened over TLS only to the certificate this desk pinned for the
/// Stagewatch that granted it — the same pin its link holds — whatever a certificate authority says; a page that is not a grant
/// is left to the platform. Pure.
/// </summary>
public static class WindowCertificate
{
    /// <summary>
    /// <paramref name="held"/> the address the page is held under, <paramref name="presented"/> the SHA-256 fingerprint the server
    /// showed (any form <see cref="TlsFingerprint"/> reads), <paramref name="pinned"/> the pin kept with the grant ("" when none: no certificate opens it).
    /// </summary>
    public static WindowCertificateVerdict Decide(string? held, string? presented, string? pinned)
    {
        if (!WindowAddresses.IsGrant(held)) return WindowCertificateVerdict.Default;
        var shown = TlsFingerprint.Normal(presented);
        var pin = TlsFingerprint.Normal(pinned);
        return shown.Length > 0 && pin.Length > 0 && string.Equals(shown, pin, StringComparison.Ordinal) ? WindowCertificateVerdict.Allow : WindowCertificateVerdict.Deny;
    }
}
