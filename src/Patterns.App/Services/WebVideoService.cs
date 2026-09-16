using System.Collections.Concurrent;
using System.Diagnostics;
using Patterns.Core.Media;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The native-player path for a page's video (round 68.6): asks yt-dlp — where the operator has it,
/// never bundled — for a YouTube or Vimeo page's stream address, keeps the answers while they last,
/// and rewrites the page's wanted input into the clip input for that stream under the page's own
/// key, so libVLC plays it (decoding on the GPU) and the browser is never opened for it. The tool
/// runs off the UI thread; the rewrite reads the cache and never waits. A page still resolving, or
/// one the tool cannot resolve, stays in the browser and the words say why. A stream's address is
/// fetched when the page is first wanted and again when it lapses; a mount already playing keeps its
/// address until the page leaves.
/// </summary>
public sealed class WebVideoService : IDisposable
{
    public enum Phase { Resolving, Ready, Failed, NoTool }

    /// <summary>One page's answer: where it stands, the stream when it is ready, the words, and when this was.</summary>
    public sealed record Entry(Phase Phase, WebStream Stream, string Words, DateTime WhenUtc);

    /// <summary>A failure is not asked about again for this long: the tool's own error is not made worse by asking it every reconcile.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    /// <summary>Admin's own path to the tool (a file or its folder); "" to search beside the desk and on PATH.</summary>
    public Func<string> ConfiguredPath { get; set; } = () => "";

    /// <summary>Runs the tool — (exit code, stdout, stderr); the tests hand in an answer without a process.</summary>
    public Func<string, string[], CancellationToken, Task<(int Exit, string Out, string Err)>> Runner { get; set; } = RunProcessAsync;

    /// <summary>A resolution landed or failed: the desk reconciles its inputs (called on the tool's thread).</summary>
    public Action? Changed { get; set; }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Where the desk's own executable is (the tool is looked for beside it); the tests point elsewhere.</summary>
    public Func<string> BaseDirectory { get; set; } = () => AppContext.BaseDirectory;

    /// <summary>The PATH the tool is looked for on; the tests hand in none.</summary>
    public Func<string?> PathEnvironment { get; set; } = () => Environment.GetEnvironmentVariable("PATH");

    /// <summary>Where the tool is now, or null.</summary>
    public string? ToolPath => WebVideoResolver.Find(ConfiguredPath(), BaseDirectory(), PathEnvironment(), File.Exists);

    /// <summary>"yt-dlp: C:\…\yt-dlp.exe", or the missing note.</summary>
    public string ToolWords => ToolPath is { } p ? $"{WebVideoResolver.ToolName}: {p}" : WebVideoResolver.ToolMissingNote;

    /// <summary>
    /// The locator's hook: the page's wanted input as the stream's clip input when the desk has the
    /// stream, the page itself otherwise — asking the tool when it has not asked, or asked long enough ago.
    /// </summary>
    public MediaLocator.WantedInput Rewrite(MediaLocator.WantedInput w)
    {
        if (_disposed || w.Kind != MediaLocator.WantedKind.Web || !WebVideoResolver.Applies(w.Service, w.PlayVia)) return w;
        var now = Clock();
        if (_entries.TryGetValue(w.Target, out var e))
        {
            switch (e.Phase)
            {
                case Phase.Ready:
                    if (e.Stream.Expired(now)) Request(w.Target);                              // a fresh address for the next mount; this one plays on
                    return w with { Kind = MediaLocator.WantedKind.VideoFile, Target = e.Stream.VideoUrl, Slave = e.Stream.AudioUrl, Origin = w.Target, Format = "", Loop = false };
                case Phase.Resolving:
                    return w;
                default:
                    if (now - e.WhenUtc < RetryAfter) return w;
                    break;
            }
        }
        Request(w.Target);
        return w;
    }

    /// <summary>Asks the tool for a page's stream, once at a time per page; the answer lands in the cache and <see cref="Changed"/> fires.</summary>
    public void Request(string url)
    {
        if (_disposed || url.Length == 0) return;
        var now = Clock();
        var tool = ToolPath;
        if (tool is null)
        {
            _entries[url] = new Entry(Phase.NoTool, default, WebVideoResolver.ToolMissingNote, now);
            return;
        }
        if (_entries.TryGetValue(url, out var e) && e.Phase == Phase.Resolving && now - e.WhenUtc < WebVideoResolver.Timeout + TimeSpan.FromSeconds(5)) return;
        _entries[url] = new Entry(Phase.Resolving, default, $"Native player: finding the stream for {WebAddress.ShortName(url)}…", now);
        _ = ResolveAsync(url, tool);
    }

    private async Task ResolveAsync(string url, string tool)
    {
        try
        {
            using var cts = new CancellationTokenSource(WebVideoResolver.Timeout);
            var (exit, stdout, stderr) = await Runner(tool, WebVideoResolver.ArgumentList(url), cts.Token).ConfigureAwait(false);
            var now = Clock();
            if (exit == 0 && WebVideoResolver.TryParse(stdout, now, out var stream))
            {
                _entries[url] = new Entry(Phase.Ready, stream,
                    $"Native player: {WebAddress.ShortName(url)} plays through libVLC{(stream.HasSeparateAudio ? " (its sound served apart)" : "")}", now);
            }
            else
            {
                _entries[url] = new Entry(Phase.Failed, default,
                    $"Native player: {WebVideoResolver.ToolName} could not resolve {WebAddress.ShortName(url)} — {WebVideoResolver.FailureWords(stderr)}; the browser stands in", now);
            }
        }
        catch (OperationCanceledException)
        {
            _entries[url] = new Entry(Phase.Failed, default,
                $"Native player: {WebVideoResolver.ToolName} did not answer in {WebVideoResolver.Timeout.TotalSeconds:0} s for {WebAddress.ShortName(url)}; the browser stands in", Clock());
        }
        catch (Exception ex)
        {
            Log.Warn($"{WebVideoResolver.ToolName} could not be run.", ex);
            _entries[url] = new Entry(Phase.Failed, default, $"Native player: {WebVideoResolver.ToolName} could not be run — {ex.Message}; the browser stands in", Clock());
        }
        if (!_disposed) Changed?.Invoke();
    }

    private static async Task<(int Exit, string Out, string Err)> RunProcessAsync(string tool, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("the process did not start");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone.
            }
            throw;
        }
        return (p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <summary>The tests' way in: an answer as if the tool had given it.</summary>
    public void Inject(string url, WebStream stream) => _entries[url] = new Entry(Phase.Ready, stream, $"Native player: {WebAddress.ShortName(url)} plays through libVLC", Clock());

    public Entry? EntryFor(string url) => _entries.TryGetValue(url, out var e) ? e : null;

    /// <summary>The Media page's note: what the native player is doing for the pages that asked (the latest two), "" when none did.</summary>
    public string Note => _entries.IsEmpty ? "" : string.Join("  ", _entries.Values.OrderByDescending(e => e.WhenUtc).Take(2).Select(e => e.Words));

    /// <summary>STATE: every page that asked, with its phase, its words and the stream's host when it has one.</summary>
    public IEnumerable<(string Url, string Phase, string Words, string Stream)> Rows()
        => _entries.Select(kv => (kv.Key, kv.Value.Phase.ToString().ToLowerInvariant(), kv.Value.Words,
            kv.Value.Phase == Phase.Ready && Uri.TryCreate(kv.Value.Stream.VideoUrl, UriKind.Absolute, out var u) ? u.Host : ""));

    public void Forget(string url) => _entries.TryRemove(url, out _);

    public bool IsDisposed => _disposed;

    public void Dispose()
    {
        _disposed = true;
        _entries.Clear();
    }
}
