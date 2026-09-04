using System.Net;
using System.Net.Sockets;
using System.Text;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// One-shot loopback web server for the Spotify redirect. Binds 127.0.0.1 only — unlike
/// <see cref="ControlService"/>, which binds <see cref="IPAddress.Any"/> on purpose — so an
/// authorization code never crosses the LAN. Raw <see cref="TcpListener"/> for the same reason
/// ControlService uses one: no admin rights, no URL ACLs, portable. Serves exactly one
/// GET /callback and closes. Never runs unless CONNECT was pressed.
/// </summary>
public sealed class LoopbackCallback : IDisposable
{
    /// <summary>
    /// The three ports the operator registers in the Spotify dashboard. Fixed, because Spotify
    /// matches a redirect URI verbatim against the registration — a dynamic port is impossible.
    /// Three, so one busy port never blocks sign-in.
    /// </summary>
    public static readonly int[] Ports = { 8724, 8725, 8726 };

    private readonly TcpListener _listener;

    private LoopbackCallback(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    public int Port { get; }

    /// <summary>The first free loopback port, or null when all three are taken.</summary>
    public static LoopbackCallback? Start(out string redirectUri)
    {
        foreach (var port in Ports)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                redirectUri = SpotifyEndpoints.RedirectUri(port);
                return new LoopbackCallback(listener, port);
            }
            catch (Exception ex)
            {
                Log.Warn($"Spotify sign-in port {port} is in use.", ex);
            }
        }
        redirectUri = "";
        return null;
    }

    /// <summary>Where the listener is bound — a test proves it never left the loopback interface.</summary>
    public EndPoint? LocalEndpoint => _listener.Server.IsBound ? _listener.LocalEndpoint : null;

    /// <summary>
    /// Serves one GET and returns its query. An empty dictionary means the operator never came
    /// back inside <paramref name="timeout"/>.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(timeout);
            using var client = await _listener.AcceptTcpClientAsync(window.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var requestLine = await reader.ReadLineAsync(window.Token);
            var query = ParseQuery(requestLine);
            var page = query.ContainsKey("error")
                ? Page("Spotify sign-in was cancelled.", "Go back to Patterns and try CONNECT again.", cancelled: true)
                : Page("Patterns is connected to Spotify.", "You can close this tab.", cancelled: false);
            var bytes = Encoding.UTF8.GetBytes(page);
            var head = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                       $"Content-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
            return query;
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify sign-in callback was not completed.", ex);
            return empty;
        }
        finally
        {
            Stop();
        }
    }

    private static Dictionary<string, string> ParseQuery(string? requestLine)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(requestLine)) return query;
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return query;
        var path = parts[1];
        var q = path.IndexOf('?');
        if (q < 0) return query;
        foreach (var pair in path[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            if (key.Length > 0) query[key] = value;
        }
        return query;
    }

    private static string Page(string heading, string line, bool cancelled) => $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Patterns</title>
<style>
  body { margin:0; background:#0D0F14; color:#E8ECF2; font:16px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;
         display:flex; align-items:center; justify-content:center; height:100vh; }
  .card { background:#151A22; border:1px solid #2A313E; border-radius:12px; padding:28px 32px; max-width:420px; }
  h1 { font-size:18px; margin:0 0 8px; color:{{(cancelled ? "#F0524D" : "#3EC1F3")}}; }
  p { margin:0; color:#98A1B1; }
</style>
</head>
<body><div class="card"><h1>{{heading}}</h1><p>{{line}}</p></div></body>
</html>
""";

    private void Stop()
    {
        try
        {
            _listener.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify sign-in listener stop issue.", ex);
        }
    }

    public void Dispose() => Stop();
}
