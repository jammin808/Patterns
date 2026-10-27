using System.Security.Cryptography;
using System.Text;

namespace Patterns.Core.Services;

/// <summary>
/// The show's pairing token: what a remote presents before the desk runs its mutating verbs —
/// AUTH on the wire, X-Patterns-Token on the web (round 65). Twelve symbols in three groups from
/// an alphabet a person can read out over a radio and type on a phone (no 0/O, no 1/I/L), compared
/// in constant time with dashes, spaces and case ignored. Empty is open: the network as it was,
/// with Super Check saying so. The token is this desk's own — saved with the show file, in the
/// Control section a twin never mirrors (<see cref="TwinSync.LocalSections"/>: secrets do not
/// travel the link, round 44), so a standby answers the token typed into it: set the same one on
/// both desks for the remotes to follow a handover — and never in a URL.
/// </summary>
public static class PairingToken
{
    /// <summary>The alphabet: upper-case letters and digits with the look-alikes taken out.</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>Symbols in a token: 31^12 ≈ 8 × 10^17, far past what a wire could try.</summary>
    public const int Length = 12;

    /// <summary>A fresh token, grouped in fours ("K7QM-3XWD-P9RA") from the system's random source.</summary>
    public static string New()
    {
        var symbols = RandomNumberGenerator.GetString(Alphabet, Length);
        return string.Join('-', Enumerable.Range(0, Length / 4).Select(i => symbols.Substring(i * 4, 4)));
    }

    /// <summary>
    /// Round 87: the token a desk starts with — a fresh one on its first run (no settings file yet), so a new desk is
    /// paired by default and fails closed; "" otherwise: an existing settings file keeps its choice, open included, and a
    /// node (a caller, a stage timer, an arcade) keeps its pages open to the phones that use them.
    /// </summary>
    public static string ForFirstRun(bool firstRun, bool isDesk) => firstRun && isDesk ? New() : "";

    /// <summary>Whether remotes must present a token: one is set.</summary>
    public static bool Needed(string? configured) => Normal(configured).Length > 0;

    /// <summary>
    /// True when <paramref name="offered"/> is the token — dashes, spaces and case ignored, the
    /// compare in constant time. False when no token is set: an open desk has nothing to match,
    /// and the callers ask <see cref="Needed"/> first.
    /// </summary>
    public static bool Matches(string? configured, string? offered)
    {
        var a = Encoding.UTF8.GetBytes(Normal(configured));
        var b = Encoding.UTF8.GetBytes(Normal(offered));
        return a.Length > 0 && a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Show Bus 1.3 §13.5.1: the token's canonical form as a PeerLink code — <c>-</c> and the separators 13.5.1 lists removed,
    /// <c>a</c>–<c>z</c> upper-cased — or null when what is left is not <c>[A-Z0-9]{1,64}</c>. It sits beside
    /// <see cref="Normal"/>, which stays the key §7's 1.1 proof and the web header compare by (13.6.8).
    /// </summary>
    public static string? Canonical(string? token) => PeerLink.Canon(token);

    /// <summary>
    /// Why a token typed on this desk is not taken, in the contract's words; null when it is. Empty is taken — the open desk.
    /// Any other token is taken only when its canonical form (13.5.1) is <c>[A-Z0-9]{1,64}</c> and is the key the legacy proof
    /// compares (<see cref="Normal"/>, 13.6.8): a token holding a character 13.5.1 removes and the legacy key keeps — U+00A0,
    /// U+FEFF and the rest of 13.5.1's list — is said as 13.6.8 says it, so on every token this desk takes the two forms name
    /// the same letters and digits. Every token NEW TOKEN makes is taken (<see cref="Alphabet"/>, in groups of four).
    /// </summary>
    public static string? EntryRefusal(string? typed)
    {
        if (string.IsNullOrEmpty(typed)) return null;
        var canonical = Canonical(typed);
        if (canonical is not null && canonical == Normal(typed)) return null;
        return Normal(typed).Any(PeerLink.CanonRemoves) && canonical is not null ? PeerLink.InvisibleWords : PeerLink.CodeShapeWords;
    }

    /// <summary>
    /// What the Remote page says beside a token the show already holds that this desk would not take now (saved by an older
    /// build): kept as it is — the legacy proof and the web header still take it — with 13.5.1's or 13.6.8's words; "" for a
    /// token it would take.
    /// </summary>
    public static string LinkWords(string? token) => EntryRefusal(token) ?? "";

    /// <summary>A token as compared: no dashes or spaces, upper case — §7's 1.1 desks' key, which 13.6.8's legacy proof keeps.</summary>
    public static string Normal(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        var sb = new StringBuilder(token.Length);
        foreach (var c in token)
        {
            if (c is '-' or ' ' or '\t' or '\r' or '\n') continue;
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }
}
