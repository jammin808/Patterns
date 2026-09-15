using Patterns.Core.Model;
using System.Net.Http;
using System.Text;
using Patterns.Core.Services;

namespace Patterns.Devices;

/// <summary>
/// A box with an HTTP API — Companion's own API, a Q-SYS core, a Crestron or Extron processor's
/// web control, a streaming encoder: each line is one request. "GET /api/play", "POST /cue/3",
/// "POST /go {"cue":3}" — a method, a path (or a whole URL), and a body after it; a bare line is
/// a GET of that path. The answer's status and its first line come back as a line, the way a
/// device's reply does. Connectionless: ready the moment it is made.
/// </summary>
public sealed class HttpDeviceLink : IDeviceLink
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly string _base;
    private volatile string _status;
    private volatile bool _disposed;

    public HttpDeviceLink(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim();
        if (url.Length > 0 && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;
        _base = url.TrimEnd('/');
        _status = $"ready ({_base})";
    }

    public string Status => _status;

    public bool IsOpen => !_disposed;

    public event Action<string>? LineReceived;

    /// <summary>The method, the address and the body a line means.</summary>
    public static (string Method, string Url, string Body) Read(string line, string baseUrl)
    {
        var t = (line ?? "").Trim();
        var parts = t.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var method = "GET";
        var path = t;
        var body = "";
        if (parts.Length > 1 && parts[0].ToUpperInvariant() is "GET" or "POST" or "PUT" or "DELETE" or "PATCH")
        {
            method = parts[0].ToUpperInvariant();
            path = parts[1];
            body = parts.Length > 2 ? parts[2] : "";
        }
        var url = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? path
            : baseUrl + (path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path);
        return (method, url, body);
    }

    public void Write(string framedLine)
    {
        if (_disposed) return;
        _ = RequestAsync(framedLine);
    }

    /// <summary>
    /// The request, awaited: the answer is Delivered, a 2xx Accepted, a 4xx or 5xx Rejected with
    /// its status, and a request that never got an answer Failed — so a caller that needs to know
    /// (the twin's wall switch) waits on the response itself, not on the request having been made.
    /// </summary>
    public async Task<LinkDelivery> DeliverAsync(byte[] frame)
    {
        if (_disposed) return LinkDelivery.Failed("closed");
        return await RequestAsync(Encoding.UTF8.GetString(frame));
    }

    private async Task<LinkDelivery> RequestAsync(string framedLine)
    {
        var (method, url, body) = Read(framedLine, _base);
        if (url.Length == 0) return LinkDelivery.Failed("no address");
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (body.Length > 0)
            {
                var json = body.StartsWith("{", StringComparison.Ordinal) || body.StartsWith("[", StringComparison.Ordinal);
                request.Content = new StringContent(body, Encoding.UTF8, json ? "application/json" : "text/plain");
            }
            using var response = await Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            var first = text.Split('\n')[0].Trim();
            if (first.Length > 200) first = first[..200];
            var status = (int)response.StatusCode;
            _status = $"{status} {response.ReasonPhrase} ({_base})";
            LineReceived?.Invoke($"{status} {first}".Trim());
            var words = $"{status} {response.ReasonPhrase}".Trim() + (first.Length > 0 ? $" — {first}" : "");
            return response.IsSuccessStatusCode ? LinkDelivery.Accepted(words) : LinkDelivery.Rejected(words);
        }
        catch (Exception ex)
        {
            _status = $"request failed: {ex.Message} ({_base})";
            LineReceived?.Invoke("ERR " + ex.Message);
            return LinkDelivery.Failed("request failed: " + ex.Message);
        }
    }

    public void Dispose() => _disposed = true;
}
