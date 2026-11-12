using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Round 96 (Show Bus 1.3 §12.5): before a granted window's page is opened over TLS, the server at its address is asked for its
/// certificate here, and the page is opened only when that certificate is the one its Stagewatch's link was proved over. The browser
/// is consulted on a certificate only when it doubts one, so a certificate an authority vouches for would never reach the pin there:
/// this check is the one that holds for every certificate. The address is the very one the link reached (never a name the browser
/// would look up on its own), so the probe and the page meet the same server.
/// </summary>
public static class PinnedPageProbe
{
    /// <summary>How long the check waits for the server's certificate before it says the page did not open.</summary>
    public static readonly TimeSpan Within = TimeSpan.FromSeconds(5);

    /// <summary>The words when the server shows another certificate: no address in them.</summary>
    public const string OtherCertificate = "its server showed a certificate other than the one its Stagewatch's link was proved over";

    /// <summary>
    /// "" when the server at <paramref name="url"/> (https) shows the certificate <paramref name="pin"/> names; else why not, in words
    /// that name no address. No byte of the page is asked for: the handshake ends at the certificate.
    /// </summary>
    public static async Task<string> CheckAsync(string url, string pin, TimeSpan within, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return "its address is not one a certificate guards";
        var wanted = TlsFingerprint.Normal(pin);
        if (wanted.Length == 0) return "no certificate is pinned for its Stagewatch";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(within);
        var shown = "";
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(uri.IdnHost, uri.Port, deadline.Token);
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = uri.IdnHost,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    using var seen = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    shown = TlsFingerprint.Of(seen);
                    return string.Equals(shown, wanted, StringComparison.Ordinal);
                },
            }, deadline.Token);
            return "";
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException or ObjectDisposedException)
        {
            if (shown.Length > 0 && !string.Equals(shown, wanted, StringComparison.Ordinal)) return OtherCertificate;
            if (shown.Length > 0) return "";                                   // the certificate was the pin; the handshake failed after it
            return ct.IsCancellationRequested ? "the check was stopped" : "its server did not show a certificate in time";
        }
    }
}
