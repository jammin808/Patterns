using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Fetches the venue's forecast for the weather overlay — MET Norway or Open-Meteo, as the show
/// says — on the show's refresh interval, parses it in Core and publishes the report on the
/// snapshot bus so every sink draws the same chip. Off the UI thread for the network; a failed
/// fetch keeps the last report on screen and says so on the Overlays page. The place search
/// (OpenStreetMap's Nominatim) runs one request per press.
/// </summary>
public sealed class WeatherService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private string _lastKey = "";
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private volatile string _status = "";
    private volatile bool _fetching;
    private int _lastHour = -1;

    public WeatherService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>Tests only: answers a request (the address) with a body instead of the network.</summary>
    public Func<string, Task<string>>? Transport { get; set; }

    public string Status => _status;

    /// <summary>The report the chip draws (the snapshot's), or null before the first fetch.</summary>
    public WeatherReport? Report => _services.Bus.Weather;

    /// <summary>How many fetches went out this session (the tests count them).</summary>
    public int Fetches { get; private set; }

    /// <summary>
    /// A fetch is in flight. A tick that falls due while one is running is skipped and comes back
    /// on the next tick (the key is not taken until a fetch starts); a test that changes the source
    /// waits for this to clear before it polls again.
    /// </summary>
    public bool Fetching => _fetching;

    /// <summary>Forces a fetch on the next tick.</summary>
    public void RefreshNow() => _lastFetchUtc = DateTime.MinValue;

    /// <summary>Runs the timer's body now (tests drive it without waiting on the clock).</summary>
    public void Poll() => Tick();

    private void Tick()
    {
        // The chip is drawn wherever the overlay is on — the air, or the desk's preview while it is
        // being set up — and the report is the show's, so either side being on asks for it.
        var enabled = _services.State.Overlays.Weather.Enabled || _services.AirState.Overlays.Weather.Enabled;
        var settings = _services.State.Weather;
        if (!enabled)
        {
            _status = "Weather overlay off.";
            return;
        }
        if (!settings.HasLocation)
        {
            if (_services.Bus.Weather is not null)
            {
                _services.Bus.Weather = null;
                _services.PublishRuntime();
            }
            _status = "Set a place — search above, or type the coordinates.";
            return;
        }

        // The hour turning moves the "now" view: a fresh snapshot, no fetch.
        var hour = DateTime.Now.Hour;
        if (hour != _lastHour)
        {
            _lastHour = hour;
            if (_services.Bus.Weather is not null) _services.PublishRuntime();
        }

        var key = $"{settings.Provider}|{settings.Latitude:0.####}|{settings.Longitude:0.####}|{settings.ApiKey.Length}";
        var due = key != _lastKey || (DateTime.UtcNow - _lastFetchUtc).TotalMinutes >= settings.RefreshMinutes;
        if (!due || _fetching) return;

        _lastKey = key;
        _lastFetchUtc = DateTime.UtcNow;
        _fetching = true;
        Fetches++;

        var provider = settings.Provider;
        var contact = settings.Contact;
        var url = provider == WeatherProvider.OpenMeteo
            ? WeatherSources.OpenMeteoUrl(settings.Latitude, settings.Longitude, settings.ApiKey)
            : WeatherSources.MetNorwayUrl(settings.Latitude, settings.Longitude);
        var name = WeatherSources.Name(provider);
        var refresh = settings.RefreshMinutes;

        _ = Task.Run(async () =>
        {
            WeatherReport? report = null;
            string status;
            try
            {
                var body = await FetchAsync(url, contact);
                var now = DateTime.Now;
                report = provider == WeatherProvider.OpenMeteo
                    ? WeatherParser.OpenMeteo(body, now)
                    : WeatherParser.MetNorway(body, utc => utc.ToLocalTime(), now);
                status = report is null
                    ? $"{name}: the reply could not be read — the last forecast stays."
                    : $"{name}: {report.Hours.Count} hours{(report.Days.Count > 0 ? $", {report.Days.Count} days" : "")} · updated {now:HH:mm} · next {now.AddMinutes(refresh):HH:mm}";
            }
            catch (Exception ex)
            {
                Log.Warn($"Weather fetch failed ({name}).", ex);
                status = $"Weather error ({name}): {ex.Message} — the last forecast stays.";
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _fetching = false;
                _status = status;
                if (report is not null)
                {
                    _services.Bus.Weather = report;
                    _services.PublishRuntime();
                }
            });
        });
    }

    /// <summary>The place search: one request, the places found best first; throws so the desk can say why.</summary>
    public async Task<IReadOnlyList<WeatherPlace>> SearchAsync(string query)
    {
        var body = await FetchAsync(WeatherSources.NominatimUrl(query), _services.State.Weather.Contact);
        return WeatherParser.Nominatim(body);
    }

    private async Task<string> FetchAsync(string url, string contact)
    {
        if (Transport is { } transport) return await transport(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", WeatherSources.UserAgent(UpdateService.RunningVersion, contact));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public void Dispose() => _timer.Stop();
}
