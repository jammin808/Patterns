using System.Collections.ObjectModel;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

public sealed partial class MainViewModel
{
    // ---- the weather chip: the venue's place, the search, the source, the words -----------------

    private string _weatherStatus = "";
    private string _weatherQuery = "";
    private WeatherPlace? _selectedWeatherPlace;
    private RelayCommand? _searchWeatherPlace;
    private RelayCommand? _refreshWeather;

    /// <summary>The Overlays page's status line: the source, the hours read, when, and when next — or why not.</summary>
    public string WeatherStatus { get => _weatherStatus; private set => Set(ref _weatherStatus, value); }

    /// <summary>The search box: a town and a country.</summary>
    public string WeatherQuery { get => _weatherQuery; set => Set(ref _weatherQuery, value ?? ""); }

    /// <summary>What the last search found, best first.</summary>
    public ObservableCollection<WeatherPlace> WeatherPlaces { get; } = new();

    public bool HasWeatherPlaces => WeatherPlaces.Count > 0;

    /// <summary>Picking a found place makes it the venue.</summary>
    public WeatherPlace? SelectedWeatherPlace
    {
        get => _selectedWeatherPlace;
        set
        {
            if (Set(ref _selectedWeatherPlace, value) && value is not null) UseWeatherPlace(value);
        }
    }

    public EnumItem[] WeatherViews => Lists.WeatherViews;
    public EnumItem[] WeatherUnitChoices => Lists.WeatherUnits;
    public EnumItem[] WeatherProviderChoices => Lists.WeatherProviders;

    /// <summary>"53.4808, −2.2426" — or "no place set".</summary>
    public string WeatherCoordinatesText
    {
        get
        {
            var w = State.Weather;
            return w.HasLocation
                ? $"{w.Latitude.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}, {w.Longitude.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}"
                : "no place set";
        }
    }

    public RelayCommand SearchWeatherPlaceCommand => _searchWeatherPlace ??= new RelayCommand(() => _ = SearchWeatherPlaceAsync());

    public RelayCommand RefreshWeatherCommand => _refreshWeather ??= new RelayCommand(() =>
    {
        _services.Weather.RefreshNow();
        StatusMessage = State.Weather.HasLocation ? "Asking for the forecast again…" : "Set a place first — search for it, or type the coordinates.";
    });

    private async Task SearchWeatherPlaceAsync()
    {
        var query = WeatherQuery.Trim();
        if (query.Length == 0)
        {
            StatusMessage = "Type a place to search for — a town and a country.";
            return;
        }
        StatusMessage = $"Searching for '{query}'…";
        try
        {
            var places = await _services.Weather.SearchAsync(query);
            WeatherPlaces.Clear();
            foreach (var p in places) WeatherPlaces.Add(p);
            Raise(nameof(HasWeatherPlaces));
            if (places.Count == 0)
            {
                StatusMessage = $"No place found for '{query}' — try a town and a country, or type the coordinates.";
            }
            else if (places.Count == 1)
            {
                UseWeatherPlace(places[0]);
            }
            else
            {
                StatusMessage = $"{places.Count} places found for '{query}' — pick one.";
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Place search failed.", ex);
            StatusMessage = $"The place search failed: {ex.Message}";
        }
    }

    /// <summary>A found place becomes the venue: its short name on screen, its coordinates for the forecast.</summary>
    public void UseWeatherPlace(WeatherPlace place)
    {
        var w = State.Weather;
        _services.BulkEdit(() =>
        {
            w.Place = WeatherParser.ShortName(place.Name);
            w.Latitude = place.Latitude;
            w.Longitude = place.Longitude;
        });
        Raise(nameof(WeatherCoordinatesText));
        _services.Weather.RefreshNow();
        StatusMessage = $"Weather for {w.Place} ({WeatherCoordinatesText}) — the forecast is on its way.";
    }
}
