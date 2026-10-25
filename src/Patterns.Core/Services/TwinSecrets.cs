using System.Net;
using System.Text.Json;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// Round 89: what the show's secrets do on one twin link — a box's password, the weather key and the passes of the
/// private pages the show holds as kept://… (<see cref="TwinWord.Kept"/>). Once the key is proved the twin's link is
/// plain TCP: its words are authenticated, not private. So the secrets ride a link only to a standby (never to a
/// caller or a stage timer, which never take the show), only while the sending desk's SEND SECRETS is on — off by
/// default — and only over a link that never leaves this machine. To another machine they are refused whatever the
/// setting, and both desks say what the standby is without and why. The twin stays a named exemption from Show Bus
/// §13 (its own proof, beat and carrier) until it has TLS of its own; the bind address and this rule are what it
/// honours now. Pure, so the rule and the words are unit tested without a socket.
/// </summary>
public static class TwinSecrets
{
    /// <summary>The secrets ride this link: SEND SECRETS is on and the link never leaves this machine.</summary>
    public const string Ride = "ride";

    /// <summary>Withheld because SEND SECRETS is off on the sending desk; with it on, this link would carry them.</summary>
    public const string Off = "off";

    /// <summary>Withheld whatever the setting: the link reaches another machine, and the twin's link is plain.</summary>
    public const string Plain = "plain";

    /// <summary>Why the secrets ride a standby's link or not: another machine is <see cref="Plain"/> first, whatever the setting.</summary>
    public static string Why(bool sendSecrets, bool sameMachine) => !sameMachine ? Plain : sendSecrets ? Ride : Off;

    /// <summary>The one rule: a standby (never a follower), SEND SECRETS on, the link on this machine.</summary>
    public static bool Rides(bool sendSecrets, bool sameMachine, bool follower) => !follower && Why(sendSecrets, sameMachine) == Ride;

    /// <summary>Both ends of a connection are this machine — see <see cref="SameMachine(IPAddress?, IPAddress?)"/>.</summary>
    public static bool SameMachine(EndPoint? remote, EndPoint? local)
        => remote is IPEndPoint r && SameMachine(r.Address, (local as IPEndPoint)?.Address);

    /// <summary>
    /// Both ends of a connection are this machine: the peer's address is a loopback address, or the very address the
    /// connection arrived on — a standby on this machine that dials one of this machine's own addresses arrives from it.
    /// A TCP connection cannot complete from another machine under this machine's own address: the answers to its
    /// handshake go to this machine. Every other address is another machine's.
    /// </summary>
    public static bool SameMachine(IPAddress? remote, IPAddress? local)
    {
        if (remote is null) return false;
        var r = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
        if (IPAddress.IsLoopback(r)) return true;
        if (local is null) return false;
        var l = local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local;
        return !r.Equals(IPAddress.Any) && !r.Equals(IPAddress.IPv6Any) && r.Equals(l);
    }

    /// <summary>"the password", "the passwords and the weather key", "the weather key" — what a person types for a lack.</summary>
    private static string Typed(TwinSecretsLack lack)
    {
        var passwords = lack.Devices == 1 ? "the password" : "the passwords";
        return lack.Devices > 0 && lack.Weather ? passwords + " and the weather key" : lack.Devices > 0 ? passwords : "the weather key";
    }

    /// <summary>
    /// The standby's own words for what it is without: "WITHOUT 2 device passwords and 1 kept page — MAIN-DESK keeps
    /// them: the twin's link to another machine is plain; type the passwords here (Interactive page) — this desk keeps
    /// what is typed on it; a kept page opens black here until it is pasted again once this desk has the show". "" with
    /// nothing missing.
    /// </summary>
    public static string OnStandby(TwinSecretsLack lack, string why, string main)
    {
        if (!lack.Any) return "";
        var whose = main.Length > 0 ? main : "the main";
        var reason = why == Plain
            ? $"{whose} keeps them: the twin's link to another machine is plain"
            : $"SEND SECRETS is off on {whose} (its Machine page, TWIN)";
        var fix = new List<string>();
        if (lack.Typable) fix.Add($"type {Typed(lack)} here ({(lack.Devices > 0 && lack.Weather ? "Interactive and Weather" : lack.Devices > 0 ? "Interactive page" : "Weather")}) — this desk keeps what is typed on it");
        if (lack.Kept > 0) fix.Add($"{(lack.Kept == 1 ? "a kept page opens" : "the kept pages open")} black here until pasted again once this desk has the show");
        return $"WITHOUT {lack.Words} — {reason}; {string.Join("; ", fix)}";
    }

    /// <summary>
    /// The main's words for a standby without the secrets: "Backup is WITHOUT 2 device passwords — they stay on this
    /// desk: the twin's link to another machine is plain; type the passwords on Backup." "" with nothing missing.
    /// </summary>
    public static string OnMain(string standby, TwinSecretsLack lack, string why)
    {
        if (!lack.Any) return "";
        var who = standby.Length > 0 ? standby : "the standby";
        var reason = why == Plain
            ? "they stay on this desk: the twin's link to another machine is plain"
            : "SEND SECRETS is off: tick it (Machine page, TWIN) to send them to this machine's standby";
        var fix = new List<string>();
        if (lack.Typable) fix.Add(why == Plain ? $"type {Typed(lack)} on {who}" : $"or type {Typed(lack)} there");
        if (lack.Kept > 0) fix.Add($"{(lack.Kept == 1 ? "a kept page opens" : "the kept pages open")} black there until pasted again after a takeover");
        return $"{who} is WITHOUT {lack.Words} — {reason}{(fix.Count > 0 ? (why == Plain ? "; " : ", ") + string.Join("; ", fix) : "")}.";
    }

    /// <summary>The words a main says once it refuses SEND SECRETS for a standby on another machine — in its log and as a notice.</summary>
    public static string Refused(string standby)
    {
        var who = standby.Length > 0 ? standby : "the standby";
        return $"Twin: SEND SECRETS is refused for {who} — it is on another machine and the twin's link is plain, so the show's device passwords, its weather key and its kept pages stay on this desk; type them on {who}.";
    }
}

/// <summary>
/// Round 89: main → standby, the SECRETS word: why the show's secrets ride this link or not and, when they do not,
/// what the main holds that it does not send — the ids of the boxes with a password (an id, never a password) and
/// whether it has a weather key. Sent before the show on welcome and again whenever it changes (the setting, a
/// password typed or cleared), checked once a beat. The standby reads its own show and book against it.
/// </summary>
public sealed record TwinSecretsOffer(string Why, IReadOnlyList<string> Devices, bool Weather)
{
    /// <summary>The most box ids an offer names: more is not a show this build writes.</summary>
    public const int MaxDevices = 1024;

    /// <summary>The secrets ride this link: nothing is withheld.</summary>
    public bool Rides => Why == TwinSecrets.Ride;

    /// <summary>The offer for one link: nothing named while they ride; otherwise every box with a password, and the weather key.</summary>
    public static TwinSecretsOffer For(ShowState state, string why)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (why == TwinSecrets.Ride) return new TwinSecretsOffer(why, Array.Empty<string>(), false);
        var devices = state.Interactive.Devices.Where(d => d.Secret.Length > 0 && d.Id.Length > 0).Select(d => d.Id)
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).Take(MaxDevices).ToList();
        return new TwinSecretsOffer(why, devices, state.Weather.ApiKey.Length > 0);
    }

    public string ToJson() => JsonUtil.SerializeCompact(this);

    /// <summary>The offer, or null for anything that is not one: an unknown reason is refused, never read as riding.</summary>
    public static TwinSecretsOffer? Parse(string? json)
    {
        try
        {
            var o = JsonUtil.Deserialize<TwinSecretsOffer>(json ?? "");
            if (o is null || o.Why is not (TwinSecrets.Ride or TwinSecrets.Off or TwinSecrets.Plain)) return null;
            var devices = (o.Devices ?? Array.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).Take(MaxDevices).ToList();
            return o with { Devices = devices };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Round 89: what a standby is without, of the secrets the show it mirrors uses — the boxes the main has a password
/// for that have none here, the weather key, and the kept pages the show and its air name that this machine's book
/// holds no pass for. Standby → main as the LACKS word whenever it changes, so both desks say it; a count, never a
/// name of a secret.
/// </summary>
public sealed record TwinSecretsLack(int Devices, bool Weather, int Kept)
{
    public static TwinSecretsLack None { get; } = new(0, false, 0);

    /// <summary>The most kept pages a lack counts: past it the words are already the whole story.</summary>
    public const int MaxKept = 4096;

    /// <summary>Something the show uses is missing here.</summary>
    public bool Any => Devices > 0 || Weather || Kept > 0;

    /// <summary>What is missing can be typed on the standby (a password, the key); a kept page is pasted again instead.</summary>
    public bool Typable => Devices > 0 || Weather;

    /// <summary>
    /// What a standby is without, read from the main's offer against its own show and its own book. Nothing before an
    /// offer came or while the secrets ride: then whatever the standby lacks, the main lacks too, and the main's own rows
    /// say so. A box the show has here with no password of its own is missing one; a kept page counts when this book
    /// holds no pass for it (<paramref name="keptNotHere"/>, counted by the caller from the show and its air).
    /// </summary>
    public static TwinSecretsLack Of(TwinSecretsOffer? offer, ShowState state, int keptNotHere)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (offer is null || offer.Rides) return None;
        var withheld = new HashSet<string>(offer.Devices, StringComparer.Ordinal);
        var typedHere = new HashSet<string>(state.Interactive.Devices.Where(d => d.Secret.Length > 0).Select(d => d.Id), StringComparer.Ordinal);
        var devices = state.Interactive.Devices.Select(d => d.Id).Where(id => withheld.Contains(id) && !typedHere.Contains(id)).Distinct(StringComparer.Ordinal).Count();
        return new TwinSecretsLack(devices, offer.Weather && state.Weather.ApiKey.Length == 0, Math.Max(0, keptNotHere));
    }

    /// <summary>"2 device passwords, the weather key and 1 kept page".</summary>
    public string Words
    {
        get
        {
            var parts = new List<string>();
            if (Devices > 0) parts.Add(Devices == 1 ? "1 device password" : $"{Devices} device passwords");
            if (Weather) parts.Add("the weather key");
            if (Kept > 0) parts.Add(Kept == 1 ? "1 kept page" : $"{Kept} kept pages");
            return parts.Count switch
            {
                0 => "",
                1 => parts[0],
                _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
            };
        }
    }

    /// <summary>For a glance line: "WITHOUT 2 PASSWORDS, WEATHER KEY, 1 KEPT PAGE"; "" with nothing missing.</summary>
    public string Glance
    {
        get
        {
            var parts = new List<string>();
            if (Devices > 0) parts.Add(Devices == 1 ? "1 PASSWORD" : $"{Devices} PASSWORDS");
            if (Weather) parts.Add("WEATHER KEY");
            if (Kept > 0) parts.Add(Kept == 1 ? "1 KEPT PAGE" : $"{Kept} KEPT PAGES");
            return parts.Count == 0 ? "" : "WITHOUT " + string.Join(", ", parts);
        }
    }

    public string ToJson() => JsonUtil.SerializeCompact(this);

    /// <summary>The lack, or null for anything that is not one; counts below zero read as none.</summary>
    public static TwinSecretsLack? Parse(string? json)
    {
        try
        {
            var l = JsonUtil.Deserialize<TwinSecretsLack>(json ?? "");
            return l is null ? null : new TwinSecretsLack(Math.Clamp(l.Devices, 0, TwinSecretsOffer.MaxDevices), l.Weather, Math.Clamp(l.Kept, 0, MaxKept));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
