using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Patterns.Core.Services;

/// <summary>
/// Round 88 (Show Bus 1.2 §10): a certificate as the show network names it — "sha256:" and the lowercase hex of
/// SHA-256 over its DER bytes. The hello carries this desk's; a client pins the one it met.
/// </summary>
public static class TlsFingerprint
{
    public const string Prefix = "sha256:";

    public static string Of(X509Certificate2 certificate) => Prefix + Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();

    /// <summary>"sha256:" and 64 lowercase hex characters.</summary>
    public static bool IsValid(string? fingerprint)
        => fingerprint is { Length: 71 } f && f.StartsWith(Prefix, StringComparison.Ordinal) && f[Prefix.Length..].All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>A fingerprint as read from a peer: trimmed and lower-cased, "" when it is not one.</summary>
    public static string Normal(string? fingerprint)
    {
        var f = (fingerprint ?? "").Trim().ToLowerInvariant();
        return IsValid(f) ? f : "";
    }

    /// <summary>The first sixteen hex digits in fours — what a person compares across two screens.</summary>
    public static string Short(string fingerprint)
        => IsValid(fingerprint) ? string.Join(' ', Enumerable.Range(0, 4).Select(i => fingerprint.Substring(Prefix.Length + i * 4, 4))) : "";
}

/// <summary>What a client makes of a server's certificate (Show Bus 1.2 §10).</summary>
public enum PinVerdict
{
    /// <summary>The certificate pinned for this node: go on.</summary>
    Pinned,
    /// <summary>Nothing pinned yet and nothing contradicts it: go on, and pin it once the link has authenticated.</summary>
    FirstUse,
    /// <summary>A pin is held and the server shows another certificate: refuse until the operator accepts the new one.</summary>
    Changed,
    /// <summary>The hello named one certificate and the server showed another (or none usable): refuse.</summary>
    Mismatch,
}

/// <summary>
/// Round 88 (Show Bus 1.2 §10): app-to-app TLS is trusted by pin, never by a name or an authority. A node's hello is
/// unauthenticated, so the pin — set on the first link that authenticated — is the trust, and a change is refused with
/// CERTIFICATE CHANGED until the operator accepts it (as SSH does). Pure.
/// </summary>
public static class CertificatePins
{
    /// <param name="pinned">What this desk pinned for the node ("" when nothing).</param>
    /// <param name="presented">The fingerprint of the certificate the server showed.</param>
    /// <param name="advertised">The fingerprint the node's hello carries ("" when it carries none — a typed address).</param>
    public static PinVerdict Check(string pinned, string presented, string advertised)
    {
        if (!TlsFingerprint.IsValid(presented)) return PinVerdict.Mismatch;
        if (TlsFingerprint.IsValid(pinned)) return pinned == presented ? PinVerdict.Pinned : PinVerdict.Changed;
        if (TlsFingerprint.IsValid(advertised) && advertised != presented) return PinVerdict.Mismatch;
        return PinVerdict.FirstUse;
    }

    public static bool Accepts(PinVerdict v) => v is PinVerdict.Pinned or PinVerdict.FirstUse;

    /// <summary>The words for the link's status line and the Eye.</summary>
    public static string Words(PinVerdict v, string name, string presented) => v switch
    {
        PinVerdict.Pinned => $"{name}'s certificate is the one pinned ({TlsFingerprint.Short(presented)})",
        PinVerdict.FirstUse => $"{name}'s certificate is new here ({TlsFingerprint.Short(presented)}) — pinned once the link authenticates",
        PinVerdict.Changed => $"CERTIFICATE CHANGED: {name} now shows {TlsFingerprint.Short(presented)} — refused until you accept it on the Nodes page",
        _ => $"{name}'s certificate is not the one its hello names — refused (another machine answering at its address?)",
    };
}

/// <summary>
/// Round 88 (Show Bus 1.2 §10): this install's own certificate — self-signed, ECDSA P-256; the subject is the desk's name;
/// the names it answers to are this machine's name, localhost and the addresses it has now. Made once, kept beside the
/// settings (the key protected at rest by the platform's protector), never copied to another machine. Show Bus 1.3 §10:
/// its whole span, NotBefore to NotAfter with the backdating for a skewed clock, is at most <see cref="Life"/> — Apple
/// devices refuse a TLS server certificate valid for longer — and one that spans more is replaced once, at a start.
/// Pure apart from the clock and the names handed in.
/// </summary>
public static class TlsIdentity
{
    /// <summary>The whole span, NotBefore to NotAfter (§14 <c>cert.days</c>).</summary>
    public static readonly TimeSpan Life = TimeSpan.FromDays(825);

    /// <summary>How far NotBefore stands before the moment it is made, for a peer whose clock runs behind (§14 <c>cert.backdate.hours</c>).</summary>
    public static readonly TimeSpan Backdate = TimeSpan.FromHours(24);

    /// <summary>Renewed this long before it runs out, at a start (§14 <c>cert.renew.days</c>).</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    public static X509Certificate2 Create(string name, IEnumerable<string> dnsNames, IEnumerable<IPAddress> addresses, DateTime utcNow)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedName("CN=" + Cn(name));
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication") }, false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in dnsNames.Select(d => d.Trim()).Where(d => d.Length is > 0 and < 254 && Uri.CheckHostName(d) == UriHostNameType.Dns).Distinct(StringComparer.OrdinalIgnoreCase))
            san.AddDnsName(dns);
        foreach (var ip in addresses.Distinct()) san.AddIpAddress(ip);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var (notBefore, notAfter) = Window(utcNow);
        return request.CreateSelfSigned(new DateTimeOffset(notBefore), new DateTimeOffset(notAfter));
    }

    /// <summary>
    /// NotBefore and NotAfter for a certificate made at <paramref name="utcNow"/>: NotBefore <see cref="Backdate"/> back,
    /// NotAfter counted from NotBefore, so the span is <see cref="Life"/> exactly. Whole seconds, as a certificate keeps
    /// them, so the span read back is the span made and a new certificate never reads as over-long.
    /// </summary>
    public static (DateTime NotBefore, DateTime NotAfter) Window(DateTime utcNow)
    {
        var whole = new DateTime(utcNow.Ticks - utcNow.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var notBefore = whole - Backdate;
        return (notBefore, notBefore + Life);
    }

    /// <summary>Whether a start makes a new certificate: fewer than <see cref="RenewBefore"/> left, or a span over <see cref="Life"/>.</summary>
    public static bool NeedsRenewal(X509Certificate2 certificate, DateTime utcNow) => RenewalReason(certificate, utcNow).Length > 0;

    /// <summary>
    /// Why a start makes a new certificate, in the words the log says ("" when it keeps the one it has): it runs out within
    /// <see cref="RenewBefore"/>, or it spans more than <see cref="Life"/> — one made before Show Bus 1.3, replaced once.
    /// Either way every client that pinned it sees CERTIFICATE CHANGED once.
    /// </summary>
    public static string RenewalReason(X509Certificate2 certificate, DateTime utcNow)
    {
        var notBefore = certificate.NotBefore.ToUniversalTime();
        var notAfter = certificate.NotAfter.ToUniversalTime();
        if (notAfter - utcNow < RenewBefore) return "this desk's certificate runs out within a month";
        if (notAfter - notBefore > Life) return string.Create(CultureInfo.InvariantCulture, $"this desk's certificate spans {(notAfter - notBefore).TotalDays:0} days, more than the {Life.TotalDays:0} a show network allows (Show Bus 1.3 §10) — replaced once");
        return "";
    }

    /// <summary>The certificate with its key, as bytes to protect and keep (PKCS#12, no password: the protector is the lock).</summary>
    public static byte[] Export(X509Certificate2 certificate) => certificate.Export(X509ContentType.Pkcs12);

    /// <summary>The kept bytes back as a certificate a TLS server can use (a key the platform's TLS can read, not an ephemeral one).</summary>
    public static X509Certificate2 Import(byte[] pkcs12) => X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.DefaultKeySet);

    /// <summary>The certificate alone (no key), DER — /tls.cer.</summary>
    public static byte[] PublicDer(X509Certificate2 certificate) => certificate.Export(X509ContentType.Cert);

    private static string Cn(string name)
    {
        var n = new string((name ?? "").Where(c => !char.IsControl(c) && c is not (',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=')).ToArray()).Trim();
        return n.Length == 0 ? "Patterns" : n.Length > 64 ? n[..64] : n;
    }
}

/// <summary>
/// Round 88 (Show Bus 1.2 §10): the first byte of a connection says what it is — 0x16 opens a TLS handshake, anything
/// else is plain HTTP. One port serves both: links that work today keep working, a browser or an app that wants TLS
/// gets it on the same door.
/// </summary>
public static class TlsSniff
{
    public const byte HandshakeRecord = 0x16;

    public static bool IsTls(byte first) => first == HandshakeRecord;

    /// <summary>Whether a plain request from <paramref name="remote"/> is refused when HTTPS is required: only this machine may stay plain.</summary>
    public static bool RefusePlain(bool requireHttps, bool isTls, bool loopback) => requireHttps && !isTls && !loopback;

    public const string UseHttps = "This desk requires HTTPS on the network — open https:// (the /tls page says how to trust its certificate).";
}
