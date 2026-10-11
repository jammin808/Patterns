using System.Net;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Round 88 (Show Bus 1.2 §10): the /tls page — this desk's certificate fingerprint to compare by eye, the certificate
/// to download (/tls.cer), and how to trust it once on each kind of device, so the browser stops warning. Pure HTML,
/// no script; the same words on every node.
/// </summary>
public static class TlsPage
{
    public static string Html(string deskName, string fingerprint, string protection, int port, bool requireHttps, string problem)
    {
        var name = WebUtility.HtmlEncode(deskName);
        var body = fingerprint.Length == 0
            ? $"<p class=bad>This desk has no certificate, so the web remote is plain HTTP only. {WebUtility.HtmlEncode(problem)}</p>"
            : $"""
               <p>This desk serves its web remote over <b>HTTPS</b> on the same port as plain HTTP: <code>https://&lt;this machine&gt;:{port}/</code>.
               Its certificate is its own (self-signed), so a browser warns until the certificate is trusted once on that device.</p>
               <h2>Fingerprint</h2>
               <p class=fp>{WebUtility.HtmlEncode(fingerprint)}</p>
               <p>Short form: <b>{WebUtility.HtmlEncode(TlsFingerprint.Short(fingerprint))}</b> — compare it with the desk's Remote page before trusting.</p>
               <p><a href="/tls.cer" download="{name}.cer">Download the certificate</a> · {WebUtility.HtmlEncode(protection)}</p>
               <h2>Trust it once</h2>
               <ul>
               <li><b>Windows</b>: open the .cer → Install Certificate → Current User → Place in “Trusted Root Certification Authorities”.</li>
               <li><b>macOS</b>: open the .cer in Keychain Access → double-click it → Trust → “Always Trust”.</li>
               <li><b>iPhone / iPad</b>: open the .cer → Settings → Profile Downloaded → Install; then Settings → General → About → Certificate Trust Settings → turn it on.</li>
               <li><b>Android</b>: Settings → Security → Encryption &amp; credentials → Install a certificate → CA certificate → the .cer.</li>
               </ul>
               <p>Other show apps (Stagewatch, AGMessage) do not need this: they pin the fingerprint the first time they link, and refuse a different certificate later until you accept it.</p>
               <p>{(requireHttps ? "This desk <b>requires HTTPS</b> from other machines: plain http:// is refused except on this machine." : "Plain http:// still works too; the Remote page can require HTTPS.")}</p>
               """;
        return $$"""
                 <!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
                 <title>{{name}} — HTTPS</title>
                 <style>body{font:16px system-ui,sans-serif;background:#0b0d12;color:#e8ecf2;margin:0 auto;max-width:720px;padding:16px}
                 h1{font-size:22px}h2{font-size:17px;margin-top:22px}code,.fp{font-family:Consolas,monospace}.fp{word-break:break-all;background:#151a24;padding:10px;border-radius:6px}
                 a{color:#35e0d0}.bad{color:#ff8a80}li{margin:6px 0}</style></head>
                 <body><h1>{{name}} — HTTPS</h1>{{body}}</body></html>
                 """;
    }
}
