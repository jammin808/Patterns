using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Fetches the configured ticker feed (URL or local file) on its refresh interval, parses it
/// (RSS/Atom, CSV/plain lines, ICS) and publishes the joined text on the snapshot bus.
/// </summary>
public sealed class FeedService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private string _lastKey = "";
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private volatile string _status = "";
    private volatile bool _fetching;

    public FeedService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public string Status => _status;

    /// <summary>Forces a fetch on the next tick.</summary>
    public void RefreshNow() => _lastFetchUtc = DateTime.MinValue;

    private void Tick()
    {
        var msg = _services.State.Overlays.Message;
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
                var content = source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                              source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? await Http.GetStringAsync(source)
                    : await File.ReadAllTextAsync(source);

                var items = FeedParser.Parse(content, kind, source, DateTime.Now, maxItems);
                text = FeedParser.Join(items, separator);
                status = items.Count > 0
                    ? $"Feed OK — {items.Count} item{(items.Count == 1 ? "" : "s")}, updated {DateTime.Now:HH:mm:ss}"
                    : "Feed loaded but empty.";
            }
            catch (Exception ex)
            {
                Log.Warn($"Feed fetch failed for '{source}'.", ex);
                text = "";
                status = $"Feed error: {ex.Message}";
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
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
