using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Play;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Fetches the configured ticker feed (URL or local file) on its refresh interval, parses it
/// (RSS/Atom, CSV/plain lines, ICS) and publishes the joined text on the snapshot bus.
/// </summary>
public sealed class FeedService : IDisposable
{
    // Round 83: a feed past FeedParser.MaxBytes is refused by the client before it is buffered — a pathological document is seconds and 2 GB otherwise.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = FeedParser.MaxBytes };

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private string _lastKey = "";
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private volatile string _status = "";
    private volatile bool _fetching;

    public FeedService(AppServices services)
    {
        _services = services;
        _timer = global::Patterns.App.Services.DeskTimers.Make(TimeSpan.FromSeconds(5));
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public string Status => _status;

    /// <summary>Forces a fetch on the next tick.</summary>
    public void RefreshNow() => _lastFetchUtc = DateTime.MinValue;

    private void Tick()
    {
        // The ticker text rides the snapshot bus onto the program, so it follows air — not
        // the operator's preview, which would blank the on-air crawl mid-show.
        var msg = _services.AirState.Overlays.Message;
        var active = msg.Enabled && msg.UseFeed && !string.IsNullOrWhiteSpace(msg.FeedSource);
        if (!active)
        {
            if (_services.Bus.FeedText.Length > 0)
            {
                _services.Bus.FeedText = "";
                _services.PublishRuntime();
            }
            _status = "";
            return;
        }

        var key = $"{msg.FeedSource}|{msg.FeedKind}|{msg.FeedSeparator}|{msg.FeedMaxItems}";
        var due = key != _lastKey ||
                  (DateTime.UtcNow - _lastFetchUtc).TotalMinutes >= msg.FeedRefreshMinutes;
        if (!due || _fetching) return;

        _lastKey = key;
        _lastFetchUtc = DateTime.UtcNow;
        _fetching = true;

        var source = msg.FeedSource.Trim();
        var kind = msg.FeedKind;
        var separator = msg.FeedSeparator;
        var maxItems = msg.FeedMaxItems;

        _ = Task.Run(async () =>
        {
            string text;
            string status;
            try
            {
                string content;
                if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    content = await Http.GetStringAsync(source);
                }
                else
                {
                    var length = new FileInfo(source).Length;
                    if (length > FeedParser.MaxBytes) throw new InvalidDataException($"the feed file is {length / 1024} KB; {FeedParser.MaxBytes / 1024} KB is the most");
                    content = await File.ReadAllTextAsync(source);
                }

                var items = FeedParser.Parse(content, kind, source, DateTime.Now, maxItems, out var problem);
                // Round 83: the ticker meets the room's word list as a phone's answer does; what it held back is on the status line.
                var (kept, held) = FeedParser.Moderate(items, WordList.Default);
                text = FeedParser.Join(kept, separator);
                var heldWords = held > 0 ? $", {held} held back by the word list" : "";
                status = kept.Count > 0
                    ? $"Feed OK — {kept.Count} item{(kept.Count == 1 ? "" : "s")}{heldWords}, updated {DateTime.Now:HH:mm:ss}"
                    : problem.Length > 0 ? $"Feed error: {problem}"
                    : held > 0 ? $"Feed loaded — every item held back by the word list ({held})."
                    : "Feed loaded but empty.";
            }
            catch (Exception ex)
            {
                // Round 83: the status line and the log get the exception's first line — a parser's message can be the size of the document — and the stack only for a fault that is not the feed's own.
                var brief = Faults.Brief(ex);
                if (ex is HttpRequestException or IOException or OperationCanceledException or InvalidDataException or FormatException or UnauthorizedAccessException)
                {
                    Log.Warn($"Feed fetch failed for '{source}': {brief}");
                }
                else
                {
                    Log.Warn($"Feed fetch failed for '{source}': {brief}", ex);
                }
                text = "";
                status = $"Feed error: {brief}";
            }

            await UiThread.InvokeAsync(() =>
            {
                _fetching = false;
                _status = status;
                if (_services.Bus.FeedText != text && (text.Length > 0 || _services.Bus.FeedText.Length > 0))
                {
                    _services.Bus.FeedText = text;
                    _services.PublishRuntime();
                }
            });
        });
    }

    public void Dispose() => _timer.Stop();
}
