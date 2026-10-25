using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Patterns.Core.Services;

/// <summary>
/// Round 89: a page address that carries a pass is never what the show holds or publishes. Private is a rule read
/// off the address alone: a Stagewatch window's path (<c>/w/</c> and 26 of its window alphabet — the token is the
/// window's pass), a query or fragment word that names a pass (<see cref="PassWords"/>: a viewing code, a PIN, a key,
/// a token), or a user in the authority. A private address typed or loaded anywhere is kept on this machine
/// (<see cref="WebPassBook"/>) and the show holds <c>kept://host[:port]/&lt;id&gt;</c> instead, the id twelve base32
/// characters from the system's random source — nothing published is a function of the address, so a short PIN cannot
/// be found again from the id. A private address that reaches a key without being kept (an older show, a hand edit)
/// is published as <c>unkept://host[:port]</c>: it carries no pass, resolves to nothing and draws black. Both forms are
/// <em>held</em> addresses. Pure: the random bytes are handed in, and nothing here reads the disk or the clock.
/// </summary>
public static class WindowAddresses
{
    /// <summary>The scheme of an address kept in this machine's book.</summary>
    public const string KeptPrefix = "kept://";

    /// <summary>The scheme of a private address that reached the show without being kept: inert, black.</summary>
    public const string UnkeptPrefix = "unkept://";

    /// <summary>Stagewatch's window alphabet: lower-case letters and digits without l, o, 0 and 1.</summary>
    public const string WindowAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>A Stagewatch window token's length: the path is <c>/w/</c> and this many of <see cref="WindowAlphabet"/>.</summary>
    public const int WindowTokenLength = 26;

    /// <summary>A kept id's alphabet: RFC 4648 base32, lower case — 32 symbols, five bits each.</summary>
    public const string IdAlphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>A kept id's length: twelve symbols, sixty random bits.</summary>
    public const int IdLength = 12;

    /// <summary>What the words say after a private address's public form, wherever one is named.</summary>
    public const string PrivateNote = " (private address)";

    private static readonly string[] PassWordList =
    {
        "win", "pin", "token", "key", "view", "code", "secret", "password", "pass", "apikey", "api_key", "access_token", "auth", "sig", "signature",
    };

    /// <summary>The query and fragment words that name a pass. Compared whole, after percent-decoding, case-blind.</summary>
    public static readonly IReadOnlySet<string> PassWords = new HashSet<string>(PassWordList, StringComparer.Ordinal);

    /// <summary>What a line of text says when the patterns that find a bare pass ran out of time on it: the line is withheld, never sent raw.</summary>
    public const string WithheldLine = "[a line withheld — too long to read for private addresses]";

    private static readonly SearchValues<char> AuthorityEnd = SearchValues.Create("/?#\\");
    private static readonly SearchValues<char> QueryStart = SearchValues.Create("?#");
    private static readonly char[] PathSeparators = { '/', '\\' };
    private static readonly char[] QuerySeparators = { '?', '&', ';', '#', '/' };

    /// <summary>
    /// An address cut at its parts, by hand rather than by <see cref="Uri"/>, so free text that is not quite a URL
    /// (a log line, an escaped JSON value) is read the same way a browser's address is.
    /// </summary>
    private readonly record struct Parts(string Scheme, string UserInfo, string HostPort, string Path, string Rest);

    /// <summary>True when the address names a Stagewatch window: a path segment <c>w</c> followed by a 26-symbol window token.</summary>
    public static bool IsStagewatchWindowUrl(string? url)
        => TrySplit(Absolute(url), out var p) && !IsHeldScheme(p.Scheme) && HasWindowPath(p.Path);

    /// <summary>
    /// True when the address carries a pass (the type's rule): a window path, a pass word in the query or fragment, or a
    /// user in the authority. A held address, a file and an address with no host a browser reads (white space in it, or
    /// the punctuation of a source's name — "ndi:MEDIA-PC (OBS@Main)" is no user at a host) are never private. A bare
    /// host ("10.0.0.5:9924/w/…") reads as https, as the desk opens it.
    /// </summary>
    public static bool LooksPrivate(string? url)
    {
        // Every private form carries one of three marks; an address with none (the renderer asks every frame) answers
        // from one scan that allocates nothing.
        if (url is null || !MayBePrivate(url)) return false;
        if (!TrySplit(Absolute(url), out var p)) return false;
        if (p.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase) || IsHeldScheme(p.Scheme) || !IsHostPort(p.HostPort)) return false;
        return p.UserInfo.Length > 0 || HasWindowPath(p.Path) || HasPassWord(p.Rest);
    }

    /// <summary>
    /// True when a token typed without a scheme — a word of a device's command line, a cue's page — is a private address
    /// beyond doubt: a host a browser reads, then a window's path or a pass word, the marks a nickname or a line of
    /// words never carries. A user is not enough in words ("Doors 19:30@foyer"); where the token can be nothing but a
    /// page, <paramref name="userWithPassword"/> counts a user with a password ("admin:pw@10.0.0.5") too.
    /// </summary>
    public static bool LooksPrivateBare(string? token, bool userWithPassword = false)
    {
        var s = (token ?? "").Trim();
        if (s.Length == 0 || s.Contains("://", StringComparison.Ordinal) || !(char.IsLetterOrDigit(s[0]) || s[0] == '[')) return false;
        if (!MayBePrivate(s)) return false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        }
        var absolute = Absolute(s);
        if (!absolute.StartsWith("https://", StringComparison.Ordinal) || absolute.Length != s.Length + 8) return false;   // a drive or rooted path is a file
        if (!TrySplit(absolute, out var p) || !IsHostPort(p.HostPort)) return false;
        if (HasWindowPath(p.Path) || HasPassWord(p.Rest)) return true;
        return userWithPassword && p.UserInfo.Contains(':', StringComparison.Ordinal);
    }

    /// <summary>True for a held address: kept on a machine, or written into the show without its pass.</summary>
    public static bool IsHeld(string? address)
    {
        var s = (address ?? "").TrimStart();
        return s.StartsWith(KeptPrefix, StringComparison.OrdinalIgnoreCase) || s.StartsWith(UnkeptPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a kept address (<c>kept://host[:port]/&lt;id&gt;</c>).</summary>
    public static bool IsKept(string? address) => TryKept(address, out _, out _, out _);

    /// <summary>True for an unkept address (<c>unkept://host[:port]</c>).</summary>
    public static bool IsUnkept(string? address) => (address ?? "").TrimStart().StartsWith(UnkeptPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The address every reader may carry — the fence at the key: a held address rebuilt from what it names (a kept
    /// address from its host, port and id; anything else under a held scheme as the unkept form of its host — a pass
    /// written after a held prefix by hand or by another show is inert too), a private address as its unkept form (no
    /// pass, inert), any other address as it is. A held address already in its own form comes back as it is, without
    /// allocating, since every frame asks.
    /// </summary>
    public static string Held(string? url)
    {
        var s = (url ?? "").Trim();
        if (s.Length == 0) return s;
        if (IsHeld(s))
        {
            if (IsCanonicalHeld(s)) return s;
            return TryKept(s, out var host, out var port, out var id) ? Kept(host, port, id) : Unkept(s);
        }
        return LooksPrivate(s) ? Unkept(s) : s;
    }

    /// <summary>
    /// The address the show holds for one typed or loaded: a private one goes to <paramref name="keep"/> (this machine's
    /// book, which answers its kept address, or "" when it cannot keep it — then the unkept form, so no pass is ever
    /// held); a held address comes back in its own form (<see cref="Held"/>), never kept again; any other address as given.
    /// </summary>
    public static string Secure(string? typed, Func<string, string> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        var s = (typed ?? "").Trim();
        if (s.Length > 0 && IsHeld(s)) return Held(s);
        if (s.Length == 0 || !LooksPrivate(s)) return typed ?? "";
        var absolute = Absolute(s);
        var held = keep(absolute);
        return IsKept(held) ? Held(held) : Unkept(absolute);
    }

    /// <summary>A kept address for a host, a port (0 or less: none) and an id.</summary>
    public static string Kept(string host, int port, string id)
    {
        if (!IsId(id)) throw new ArgumentException($"'{id}' is not a kept id — {IdLength} of {IdAlphabet}.", nameof(id));
        var h = (host ?? "").Trim().ToLowerInvariant();
        if (h.Contains(':', StringComparison.Ordinal) && !h.StartsWith('[')) h = "[" + h + "]";
        return KeptPrefix + (port > 0 ? h + ":" + port.ToString(CultureInfo.InvariantCulture) : h) + "/" + id;
    }

    /// <summary>The kept address of an address under an id: its host and port as the address names them, never its user, path or query.</summary>
    public static string KeptFor(string url, string id)
    {
        if (!IsId(id)) throw new ArgumentException($"'{id}' is not a kept id — {IdLength} of {IdAlphabet}.", nameof(id));
        return KeptPrefix + AuthorityOf(url) + "/" + id;
    }

    /// <summary>Reads a kept address: its host, its port (0 when it names none) and its id.</summary>
    public static bool TryKept(string? address, out string host, out int port, out string id)
    {
        host = "";
        port = 0;
        id = "";
        var s = (address ?? "").Trim();
        if (!s.StartsWith(KeptPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = s[KeptPrefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0) return false;
        var candidate = rest[(slash + 1)..];
        if (!IsId(candidate)) return false;
        if (!TrySplitHostPort(rest[..slash], out host, out port)) return false;
        id = candidate;
        return true;
    }

    /// <summary>The unkept form of an address: <c>unkept://host[:port]</c>, the pass and everything after the authority gone.</summary>
    public static string Unkept(string? url) => UnkeptPrefix + AuthorityOf(url);

    /// <summary>
    /// What may be said of an address in public: <c>scheme://host[:port]</c> for a private one (no user, path, query or
    /// fragment), a held address in its own form (<see cref="Held"/>), any other address as given.
    /// </summary>
    public static string PublicForm(string? url)
    {
        var s = (url ?? "").Trim();
        if (s.Length > 0 && IsHeld(s)) return Held(s);
        if (s.Length == 0 || !LooksPrivate(s)) return s;
        return TrySplit(Absolute(s), out var p) ? p.Scheme.ToLowerInvariant() + "://" + AuthorityOf(s) : "";
    }

    /// <summary>The host an address names, without its port or user ("" when it names none): a held address's short name.</summary>
    public static string HostOf(string? address)
    {
        if (!TrySplit(Absolute(address), out var p)) return "";
        return TrySplitHostPort(p.HostPort, out var host, out _) ? host : "";
    }

    /// <summary>True for twelve symbols of <see cref="IdAlphabet"/>.</summary>
    public static bool IsId(string? id) => id is not null && IsId(id.AsSpan());

    private static bool IsId(ReadOnlySpan<char> id)
    {
        if (id.Length != IdLength) return false;
        foreach (var c in id)
        {
            if (!IdAlphabet.Contains(c, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// A new kept id: <see cref="IdLength"/> bytes from <paramref name="random"/>, five bits of each (256 is a multiple of
    /// 32, so every symbol is equally likely) — sixty bits. Production passes the system's random source; a test passes
    /// fixed bytes.
    /// </summary>
    public static string NewId(Func<int, byte[]> random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var bytes = random(IdLength);
        if (bytes is null || bytes.Length < IdLength) throw new ArgumentException($"The random source gave fewer than {IdLength} bytes.", nameof(random));
        var chars = new char[IdLength];
        for (var i = 0; i < IdLength; i++) chars[i] = IdAlphabet[bytes[i] & 31];
        return new string(chars);
    }

    /// <summary>A new kept id from the system's random source.</summary>
    public static string NewId() => NewId(RandomNumberGenerator.GetBytes);

    /// <summary>
    /// Free text with every private address in it said in public: its <see cref="PublicForm"/> and
    /// <see cref="PrivateNote"/>; a held address out of its own form in that form. For a log line, a journal row, a
    /// bundle's entry — JSON-escaped text included, so an address whose <c>&amp;</c> was written as its escape is still
    /// found whole. Where no scheme names an address (one typed bare), its pass alone is masked: a window token after
    /// <c>/w/</c>, and the value of a pass word after <c>?</c>, <c>&amp;</c>, <c>#</c> or <c>;</c>. Read a line at a
    /// time, so no pattern's scan is longer than one line whatever the size of the text (a log left running for days);
    /// a line a pattern runs out of time on is said as <see cref="WithheldLine"/>, never raw.
    /// </summary>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var nl = text.IndexOf('\n', StringComparison.Ordinal);
        if (nl < 0) return RedactLine(text);
        var sb = new StringBuilder(text.Length);
        var start = 0;
        while (nl >= 0)
        {
            sb.Append(RedactLine(text[start..nl])).Append('\n');
            start = nl + 1;
            nl = text.IndexOf('\n', start);
        }
        return sb.Append(RedactLine(text[start..])).ToString();
    }

    private static string RedactLine(string line)
    {
        var said = RewriteText(line, url => IsHeld(url) ? Held(url) : PublicForm(url) + PrivateNote);
        try
        {
            if (HasWindowSeparator(said)) said = BareWindowToken.Replace(said, Secrets.Mask);
            if (said.Contains('=', StringComparison.Ordinal))
            {
                said = BarePassValue.Replace(said, m => PassWords.Contains(Uri.UnescapeDataString(m.Groups["name"].Value).ToLowerInvariant())
                    ? m.Groups["head"].Value + Secrets.Mask
                    : m.Value);
            }
            return said;
        }
        catch (RegexMatchTimeoutException)
        {
            return WithheldLine;
        }
    }

    /// <summary>A window token after <c>/w/</c> with no scheme in front of it — the part of a bare window address that is its pass.</summary>
    private static readonly Regex BareWindowToken = new(
        "(?<=/[wW]/)[a-km-np-zA-KM-NP-Z2-9]{26}(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, SafeRegex.Timeout);

    /// <summary>A query or fragment word and its value, the word after ?, &amp;, #, ; or an escaped &amp; — masked when the word names a pass.</summary>
    private static readonly Regex BarePassValue = new(
        @"(?<head>(?:[?&#;]|\\{1,2}u0026)(?<name>[A-Za-z0-9_%.\-]{1,32})=)(?<value>[^&#;\s""'<>\\]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, SafeRegex.Timeout);

    /// <summary>
    /// Free text with every private address in it — and every held address out of its own form — replaced by
    /// <paramref name="replace"/>'s answer for it (given the address with any JSON escapes undone); everything else
    /// stays as it was, byte for byte. The text itself when there is nothing to replace. Only an address its scheme
    /// names is found here; <see cref="RewriteBareText"/> finds one typed bare.
    /// </summary>
    public static string RewriteText(string? text, Func<string, string> replace)
    {
        ArgumentNullException.ThrowIfNull(replace);
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!text.Contains("://", StringComparison.Ordinal)) return text;
        StringBuilder? sb = null;
        var copied = 0;
        var i = 0;
        while ((i = text.IndexOf("://", i, StringComparison.Ordinal)) >= 0)
        {
            var start = i;
            while (start > copied && IsSchemeChar(text[start - 1])) start--;
            start = PastEscape(text, start, i);
            while (start < i && !char.IsAsciiLetter(text[start])) start++;
            if (start == i)
            {
                // No scheme before this "://": its token is not read, so a run of them costs a step each, not a scan each.
                i += 3;
                continue;
            }
            var end = TokenEnd(text, i + 3);
            var stop = end;
            while (stop > i + 3 && IsTrailingPunctuation(text[stop - 1])) stop--;
            var decoded = Unescape(text[start..stop]);
            if (LooksPrivate(decoded) || (IsHeld(decoded) && !IsCanonicalHeld(decoded)))
            {
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, start - copied).Append(replace(decoded));
                copied = stop;
            }
            i = Math.Max(end, i + 3);
        }
        if (sb is null) return text;
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    /// <summary>
    /// Free text with every private address typed bare in it (<see cref="LooksPrivateBare"/>: a host, then a window's
    /// path or a pass word, as "WEB OPEN 10.0.0.5:9924/w/… ON lobby" carries one) replaced by <paramref name="replace"/>'s
    /// answer for it, given the token with any JSON escapes undone; a key's <c>web:</c> before it stays. Every word that
    /// is not one — a nickname, a time, a word with an <c>@</c> — stays as it was, byte for byte. Linear in the text.
    /// </summary>
    public static string RewriteBareText(string? text, Func<string, string> replace)
    {
        ArgumentNullException.ThrowIfNull(replace);
        // A bare private address carries a window's /w/ or a pass word's '=' (written escaped too, so the names are read per token).
        if (string.IsNullOrEmpty(text) || !(text.Contains('=', StringComparison.Ordinal) || HasWindowSeparator(text))) return text ?? "";
        StringBuilder? sb = null;
        var copied = 0;
        var i = 0;
        while (i < text.Length)
        {
            var end = TokenEnd(text, i);
            if (end == i)
            {
                i = PastTokenEnd(text, i);
                continue;
            }
            var start = i;
            if (text.AsSpan(start, end - start).StartsWith("web:", StringComparison.OrdinalIgnoreCase)) start += 4;
            while (start < end && !(char.IsLetterOrDigit(text[start]) || text[start] == '[')) start++;
            var stop = end;
            while (stop > start && IsTrailingPunctuation(text[stop - 1])) stop--;
            if (stop > start && !text.AsSpan(start, stop - start).Contains("://", StringComparison.Ordinal))
            {
                var decoded = Unescape(text[start..stop]);
                if (LooksPrivateBare(decoded))
                {
                    sb ??= new StringBuilder(text.Length);
                    sb.Append(text, copied, start - copied).Append(replace(decoded));
                    copied = stop;
                }
            }
            i = end;
        }
        if (sb is null) return text;
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    /// <summary>An address as the desk opens it: a scheme kept as typed; a drive, UNC or rooted path left alone; a bare host taken as https.</summary>
    private static string Absolute(string? url)
    {
        var s = (url ?? "").Trim();
        if (s.Length == 0 || s.Contains("://", StringComparison.Ordinal)) return s;
        if (s.StartsWith('/') || s.StartsWith('\\') || (s.Length >= 2 && char.IsAsciiLetter(s[0]) && s[1] == ':' && (s.Length == 2 || s[2] is '/' or '\\'))) return s;
        return "https://" + s;
    }

    private static bool IsHeldScheme(string scheme)
        => scheme.Equals("kept", StringComparison.OrdinalIgnoreCase) || scheme.Equals("unkept", StringComparison.OrdinalIgnoreCase);

    private static bool TrySplit(string s, out Parts parts)
    {
        parts = default;
        var sep = s.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0) return false;
        var scheme = s[..sep];
        if (!char.IsAsciiLetter(scheme[0])) return false;
        foreach (var c in scheme)
        {
            if (!IsSchemeChar(c)) return false;
        }
        var after = s[(sep + 3)..];
        var end = after.AsSpan().IndexOfAny(AuthorityEnd);
        var authority = end < 0 ? after : after[..end];
        // An authority with white space in it is words, not an address ("https://" before "Timer @ FOH" names no user).
        foreach (var c in authority)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        }
        var tail = end < 0 ? "" : after[end..];
        var at = authority.LastIndexOf('@');
        var userInfo = at >= 0 ? authority[..at] : "";
        var hostPort = at >= 0 ? authority[(at + 1)..] : authority;
        var q = tail.AsSpan().IndexOfAny(QueryStart);
        parts = new Parts(scheme, at >= 0 && userInfo.Length == 0 ? "@" : userInfo, hostPort, q < 0 ? tail : tail[..q], q < 0 ? "" : tail[q..]);
        return true;
    }

    /// <summary>A host and port a browser reads, parsed: the host in lower case, the port 1–65535 (0 when none is named).</summary>
    private static bool TrySplitHostPort(string hostPort, out string host, out int port)
    {
        host = "";
        port = 0;
        if (!ReadHostPort(hostPort.AsSpan().Trim(), out var name, out var portText)) return false;
        if (portText.Length > 0 && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)) return false;
        host = name.ToString().ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// True for an authority's host and port as a browser reads them: a name of letters, digits and <c>-._~%</c> (or a
    /// bracketed IPv6 literal), then a port of digits or none — no white space, and none of the punctuation a nickname
    /// or a source's name carries ("Main)", "FOH)").
    /// </summary>
    private static bool IsHostPort(string hostPort) => ReadHostPort(hostPort, out _, out _);

    private static bool ReadHostPort(ReadOnlySpan<char> s, out ReadOnlySpan<char> host, out ReadOnlySpan<char> portText)
    {
        host = default;
        portText = default;
        if (s.Length == 0) return false;
        if (s[0] == '[')
        {
            var close = s.IndexOf(']');
            if (close < 2) return false;
            foreach (var c in s[1..close])
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c is ':' or '.' or '%' or '-' or '_')) return false;
            }
            host = s[..(close + 1)];
            var after = s[(close + 1)..];
            if (after.Length > 0)
            {
                if (after[0] != ':') return false;
                portText = after[1..];
            }
        }
        else
        {
            var colon = s.IndexOf(':');
            host = colon >= 0 ? s[..colon] : s;
            portText = colon >= 0 ? s[(colon + 1)..] : default;
            if (host.Length == 0) return false;
            foreach (var c in host)
            {
                if (!(char.IsLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '%')) return false;
            }
        }
        foreach (var c in portText)
        {
            if (!char.IsAsciiDigit(c)) return false;
        }
        return true;
    }

    /// <summary>The host and port an address names, in the form a held address carries them ("" when it names none a browser reads): never its user.</summary>
    private static string AuthorityOf(string? url)
    {
        if (!TrySplit(Absolute(url), out var p) || !TrySplitHostPort(p.HostPort, out var host, out var port)) return "";
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')) host = "[" + host + "]";
        return port > 0 ? host + ":" + port.ToString(CultureInfo.InvariantCulture) : host;
    }

    /// <summary>
    /// True for a held address already in the form <see cref="Held"/> builds — <c>kept://host[:port]/&lt;id&gt;</c> or
    /// <c>unkept://host[:port]</c>, lower case, the port with no leading zero — read without allocating.
    /// </summary>
    private static bool IsCanonicalHeld(string s)
    {
        ReadOnlySpan<char> authority;
        if (s.StartsWith(KeptPrefix, StringComparison.Ordinal))
        {
            var rest = s.AsSpan(KeptPrefix.Length);
            var slash = rest.IndexOf('/');
            if (slash <= 0 || !IsId(rest[(slash + 1)..])) return false;
            authority = rest[..slash];
        }
        else if (s.StartsWith(UnkeptPrefix, StringComparison.Ordinal))
        {
            authority = s.AsSpan(UnkeptPrefix.Length);
            if (authority.Length == 0) return true;
        }
        else
        {
            return false;
        }
        if (!ReadHostPort(authority, out var host, out var portText)) return false;
        foreach (var c in host)
        {
            if (char.ToLowerInvariant(c) != c) return false;
        }
        if (authority.Length > host.Length && portText.Length == 0) return false;                        // "host:" — the colon goes
        return portText.Length == 0
               || (portText[0] != '0' && int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port <= 65535);
    }

    /// <summary>
    /// The first look, allocating nothing: every private form carries a user's <c>@</c>, a window's <c>/w/</c> segment
    /// or a pass word before an <c>=</c> (a percent-encoded name always earns the full read). False is the answer; true
    /// asks for the full read.
    /// </summary>
    private static bool MayBePrivate(string s)
        => s.Contains('@', StringComparison.Ordinal) || HasWindowSeparator(s) || MayNamePassWord(s);

    /// <summary>A <c>w</c> segment between two path separators, either slash, any case.</summary>
    private static bool HasWindowSeparator(ReadOnlySpan<char> s)
    {
        for (var i = 1; i + 1 < s.Length; i++)
        {
            if ((s[i] is 'w' or 'W') && (s[i - 1] is '/' or '\\') && (s[i + 1] is '/' or '\\')) return true;
        }
        return false;
    }

    /// <summary>
    /// A name before an <c>=</c> that is a pass word, or that is percent-encoded — a superset of what
    /// <see cref="HasPassWord"/> finds: the same separators cut the same names, and only a segment's first <c>=</c> ends one.
    /// </summary>
    private static bool MayNamePassWord(ReadOnlySpan<char> s)
    {
        var segment = 0;
        var named = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '?' or '&' or ';' or '#' or '/')
            {
                segment = i + 1;
                named = false;
            }
            else if (c == '=' && !named)
            {
                named = true;
                var name = s[segment..i].Trim();
                if (name.Contains('%') || IsPassWord(name)) return true;
            }
        }
        return false;
    }

    private static bool IsPassWord(ReadOnlySpan<char> name)
    {
        foreach (var word in PassWordList)
        {
            if (name.Equals(word, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasWindowPath(string path)
    {
        var segments = path.Split(PathSeparators);
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (segments[i].Equals("w", StringComparison.OrdinalIgnoreCase) && StartsWithWindowToken(segments[i + 1])) return true;
        }
        return false;
    }

    /// <summary>A segment that begins with a window token and goes no further in letters or digits (a log may glue punctuation on).</summary>
    private static bool StartsWithWindowToken(string segment)
    {
        if (segment.Length < WindowTokenLength) return false;
        for (var i = 0; i < WindowTokenLength; i++)
        {
            if (!WindowAlphabet.Contains(char.ToLowerInvariant(segment[i]), StringComparison.Ordinal)) return false;
        }
        return segment.Length == WindowTokenLength || !char.IsLetterOrDigit(segment[WindowTokenLength]);
    }

    private static bool HasPassWord(string rest)
    {
        if (rest.Length == 0) return false;
        foreach (var part in rest.Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var name = Uri.UnescapeDataString(part[..eq]).Trim().ToLowerInvariant();
            if (PassWords.Contains(name)) return true;
        }
        return false;
    }

    /// <summary>A scheme found by walking back never begins inside an escape the text carries before it (<c>\u0022https://…</c>, <c>\nhttps://…</c>).</summary>
    private static int PastEscape(string text, int start, int schemeEnd)
    {
        while (start > 0 && start < schemeEnd && text[start - 1] == '\\')
        {
            if (text[start] == 'u' && start + 5 <= schemeEnd && int.TryParse(text.AsSpan(start + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)) start += 5;
            else if (text[start] is 'n' or 'r' or 't' or 'b' or 'f') start += 1;
            else break;
        }
        return start;
    }

    private static bool IsSchemeChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.';

    private static bool IsTrailingPunctuation(char c) => c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '…';

    private static bool IsTokenEnd(char c) => char.IsWhiteSpace(c) || c is '"' or '\'' or '<' or '>' or '`';

    /// <summary>Where an address in free text ends: at white space, a quote or an angle bracket — or at an escape that stands for one.</summary>
    private static int TokenEnd(string text, int from)
    {
        var j = from;
        while (j < text.Length)
        {
            var c = text[j];
            if (IsTokenEnd(c)) break;
            if (c == '\\' && EscapeEndsToken(text, j)) break;
            j++;
        }
        return j;
    }

    /// <summary>Past the white space, quote or escape that ends a token at <paramref name="at"/>: always at least one character on.</summary>
    private static int PastTokenEnd(string text, int at)
    {
        if (text[at] != '\\') return at + 1;
        var k = at;
        while (k < text.Length && text[k] == '\\') k++;
        if (k >= text.Length) return k;
        return text[k] == 'u' && k + 5 <= text.Length ? k + 5 : k + 1;
    }

    private static bool EscapeEndsToken(string text, int at)
    {
        var k = at;
        while (k < text.Length && text[k] == '\\') k++;
        if (k >= text.Length) return true;
        var next = text[k];
        if (next is '"' or '\'' or 'n' or 'r' or 't') return true;
        if (next == 'u' && k + 5 <= text.Length && int.TryParse(text.AsSpan(k + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
        {
            return IsTokenEnd((char)code);
        }
        return false;
    }

    /// <summary>JSON escapes undone, as often as they were applied (a look's JSON inside the settings file is escaped twice).</summary>
    private static string Unescape(string s)
    {
        for (var pass = 0; pass < 4 && s.Contains('\\', StringComparison.Ordinal); pass++)
        {
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(c);
                    continue;
                }
                var next = s[i + 1];
                if (next == 'u' && i + 6 <= s.Length && int.TryParse(s.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                {
                    sb.Append((char)code);
                    i += 5;
                }
                else if (next is '\\' or '/' or '"' or '\'')
                {
                    sb.Append(next);
                    i++;
                }
                else
                {
                    sb.Append(c);
                }
            }
            var once = sb.ToString();
            if (once == s) break;
            s = once;
        }
        return s;
    }
}
