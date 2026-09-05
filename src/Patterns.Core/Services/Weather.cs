using System.Globalization;
using System.Text.Json;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

// ---------------------------------------------------------------------------------------------
// The weather overlay's model, pure: a forecast read from MET Norway or Open-Meteo into one
// report of hours and days, the three views the chip shows (the hour, the rest of today,
// tomorrow), the sky in ten glyphs and a few words, the units, the addresses asked and the
// place search. The App fetches; everything here is unit tested without a network.
// ---------------------------------------------------------------------------------------------

/// <summary>The sky in the glyphs the engine draws — coarse on purpose, the words carry the rest.</summary>
public enum WeatherSky
{
    Unknown,
    Clear,
    Fair,
    PartlyCloudy,
    Cloudy,
    Fog,
    Showers,
    Rain,
    Sleet,
    Snow,
    Thunder,
}

/// <summary>One hour of the forecast, in the venue's local time.</summary>
public sealed record WeatherHour(DateTime LocalTime, double TempC, WeatherSky Sky, bool Night, string Words, double PrecipMm, double PrecipChancePct, double WindMs);

/// <summary>One day of the forecast (local date), its extremes and the sky that sums it up.</summary>
public sealed record WeatherDay(DateTime LocalDate, double MinC, double MaxC, WeatherSky Sky, string Words, double PrecipMm, double PrecipChancePct);

/// <summary>A place a search found: what to call it, and where it is.</summary>
public sealed record WeatherPlace(string Name, double Latitude, double Longitude)
{
    public override string ToString() => Name;
}

/// <summary>What the chip draws for one view: the figure, the glyph, the lines, and the marks along the hours.</summary>
public sealed record WeatherCard(string Title, string Figure, WeatherSky Sky, bool Night, string Detail, IReadOnlyList<WeatherMark> Marks);

/// <summary>A small column on the card: "15:00" over a glyph over "18°".</summary>
public sealed record WeatherMark(string Label, WeatherSky Sky, bool Night, string Figure);

/// <summary>A forecast as read from a source: hours in order, days in order, when, from whom. Immutable — it rides the snapshot.</summary>
public sealed class WeatherReport
{
    public WeatherReport(IReadOnlyList<WeatherHour> hours, IReadOnlyList<WeatherDay> days, DateTime fetchedLocal, string source)
    {
        Hours = hours;
        Days = days;
        FetchedLocal = fetchedLocal;
        Source = source;
    }

    public IReadOnlyList<WeatherHour> Hours { get; }
    public IReadOnlyList<WeatherDay> Days { get; }
    public DateTime FetchedLocal { get; }

    /// <summary>"MET Norway" or "Open-Meteo" — the credit line.</summary>
    public string Source { get; }

    public bool IsEmpty => Hours.Count == 0 && Days.Count == 0;

    /// <summary>The hour in force at a local time: the last one that started at or before it, else the first.</summary>
    public WeatherHour? HourAt(DateTime local)
    {
        WeatherHour? best = null;
        foreach (var h in Hours)
        {
            if (h.LocalTime <= local) best = h;
            else break;
        }
        return best ?? (Hours.Count > 0 ? Hours[0] : null);
    }

    /// <summary>The hours from a local time to the end of that local day.</summary>
    public IReadOnlyList<WeatherHour> RestOfDay(DateTime local)
    {
        var end = local.Date.AddDays(1);
        var list = new List<WeatherHour>();
        foreach (var h in Hours)
        {
            if (h.LocalTime < local.AddHours(-1) || h.LocalTime >= end) continue;
            list.Add(h);
        }
        return list;
    }

    /// <summary>The hours of a local date.</summary>
    public IReadOnlyList<WeatherHour> HoursOf(DateTime localDate)
    {
        var day = localDate.Date;
        var list = new List<WeatherHour>();
        foreach (var h in Hours)
        {
            if (h.LocalTime.Date == day) list.Add(h);
        }
        return list;
    }

    /// <summary>The day entry for a local date, else one summed from its hours, else null.</summary>
    public WeatherDay? DayOf(DateTime localDate)
    {
        var day = localDate.Date;
        foreach (var d in Days)
        {
            if (d.LocalDate.Date == day) return d;
        }
        return WeatherModel.SumDay(day, HoursOf(day));
    }
}

/// <summary>The sky's mapping and arithmetic — symbol codes to glyphs and words, hours to a day.</summary>
public static class WeatherModel
{
    /// <summary>The worse sky wins a summary: a shower over a cloud, thunder over everything.</summary>
    public static int Severity(WeatherSky sky) => sky switch
    {
        WeatherSky.Thunder => 9,
        WeatherSky.Snow => 8,
        WeatherSky.Sleet => 7,
        WeatherSky.Rain => 6,
        WeatherSky.Showers => 5,
        WeatherSky.Fog => 4,
        WeatherSky.Cloudy => 3,
        WeatherSky.PartlyCloudy => 2,
        WeatherSky.Fair => 1,
        WeatherSky.Clear => 0,
        _ => -1,
    };

    /// <summary>A MET Norway symbol code ("lightrainshowers_day") to a glyph and whether it is night.</summary>
    public static (WeatherSky Sky, bool Night) MetSymbol(string code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        var night = c.EndsWith("_night", StringComparison.Ordinal);
        var sky =
            c.Contains("thunder") ? WeatherSky.Thunder
            : c.Contains("snow") ? WeatherSky.Snow
            : c.Contains("sleet") ? WeatherSky.Sleet
            : c.Contains("rain") ? (c.Contains("showers") ? WeatherSky.Showers : WeatherSky.Rain)
            : c.StartsWith("fog", StringComparison.Ordinal) ? WeatherSky.Fog
            : c.StartsWith("partlycloudy", StringComparison.Ordinal) ? WeatherSky.PartlyCloudy
            : c.StartsWith("cloudy", StringComparison.Ordinal) ? WeatherSky.Cloudy
            : c.StartsWith("fair", StringComparison.Ordinal) ? WeatherSky.Fair
            : c.StartsWith("clearsky", StringComparison.Ordinal) ? WeatherSky.Clear
            : WeatherSky.Unknown;
        return (sky, night);
    }

    /// <summary>The words for a MET Norway symbol code: "Light rain showers", "Partly cloudy", "Clear".</summary>
    public static string MetWords(string code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        var cut = c.IndexOf('_');
        if (cut > 0) c = c[..cut];
        if (c.Length == 0) return "";
        var intensity = "";
        if (c.StartsWith("lightss", StringComparison.Ordinal))
        {
            intensity = "Light "; // the API's own spelling: "lightssleetshowersandthunder", "lightssnowshowersandthunder"
            c = c[6..];
        }
        else if (c.StartsWith("light", StringComparison.Ordinal))
        {
            intensity = "Light ";
            c = c[5..];
        }
        else if (c.StartsWith("heavy", StringComparison.Ordinal))
        {
            intensity = "Heavy ";
            c = c[5..];
        }
        var thunder = c.EndsWith("andthunder", StringComparison.Ordinal);
        if (thunder) c = c[..^"andthunder".Length];
        var showers = c.EndsWith("showers", StringComparison.Ordinal);
        if (showers) c = c[..^"showers".Length];
        var body = c switch
        {
            "clearsky" => "Clear",
            "fair" => "Fair",
            "partlycloudy" => "Partly cloudy",
            "cloudy" => "Cloudy",
            "fog" => "Fog",
            "rain" => "rain",
            "sleet" => "sleet",
            "snow" => "snow",
            _ => c,
        };
        var words = showers ? $"{body} showers" : body;
        words = intensity + words;
        if (thunder) words += " and thunder";
        return words.Length > 0 ? char.ToUpperInvariant(words[0]) + words[1..] : words;
    }

    /// <summary>A WMO weather code (Open-Meteo) to a glyph.</summary>
    public static WeatherSky WmoSky(int code) => code switch
    {
        0 => WeatherSky.Clear,
        1 => WeatherSky.Fair,
        2 => WeatherSky.PartlyCloudy,
        3 => WeatherSky.Cloudy,
        45 or 48 => WeatherSky.Fog,
        51 or 53 or 55 or 56 or 57 => WeatherSky.Showers,
        61 or 63 or 65 => WeatherSky.Rain,
        66 or 67 => WeatherSky.Sleet,
        71 or 73 or 75 or 77 => WeatherSky.Snow,
        80 or 81 or 82 => WeatherSky.Showers,
        85 or 86 => WeatherSky.Snow,
        95 or 96 or 99 => WeatherSky.Thunder,
        _ => WeatherSky.Unknown,
    };

    /// <summary>The words for a WMO weather code.</summary>
    public static string WmoWords(int code) => code switch
    {
        0 => "Clear",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 => "Fog",
        48 => "Freezing fog",
        51 => "Light drizzle",
        53 => "Drizzle",
        55 => "Heavy drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 => "Light showers",
        81 => "Showers",
        82 => "Heavy showers",
        85 => "Light snow showers",
        86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "",
    };

    /// <summary>The sky that sums up a run of hours: the worst of them, the day's daylight hours counting first.</summary>
    public static (WeatherSky Sky, string Words) Summary(IReadOnlyList<WeatherHour> hours)
    {
        WeatherHour? worst = null;
        foreach (var h in hours)
        {
            if (worst is null || Severity(h.Sky) > Severity(worst.Sky)) worst = h;
        }
        return worst is null ? (WeatherSky.Unknown, "") : (worst.Sky, worst.Words);
    }

    /// <summary>A day summed from its hours: extremes, the worst sky, the rain; null with no hours.</summary>
    public static WeatherDay? SumDay(DateTime localDate, IReadOnlyList<WeatherHour> hours)
    {
        if (hours.Count == 0) return null;
        var min = double.MaxValue;
        var max = double.MinValue;
        var mm = 0.0;
        var chance = 0.0;
        var daylight = new List<WeatherHour>();
        foreach (var h in hours)
        {
            min = Math.Min(min, h.TempC);
            max = Math.Max(max, h.TempC);
            mm += Math.Max(0, h.PrecipMm);
            chance = Math.Max(chance, h.PrecipChancePct);
            if (h.LocalTime.Hour is >= 6 and <= 21) daylight.Add(h);
        }
        var (sky, words) = Summary(daylight.Count > 0 ? daylight : hours);
        return new WeatherDay(localDate.Date, min, max, sky, words, mm, chance);
    }
}

/// <summary>The words every surface reads: degrees, ranges, the wind, the three cards, the one-line summary.</summary>
public static class WeatherWords
{
    public static double ToUnits(double celsius, WeatherUnits units) => units == WeatherUnits.Fahrenheit ? celsius * 9 / 5 + 32 : celsius;

    /// <summary>"18°" — rounded, with the sign only below zero.</summary>
    public static string Degrees(double celsius, WeatherUnits units) => $"{Math.Round(ToUnits(celsius, units)):0}°";

    /// <summary>"14–19°".</summary>
    public static string Range(double minC, double maxC, WeatherUnits units)
    {
        var lo = Math.Round(ToUnits(minC, units));
        var hi = Math.Round(ToUnits(maxC, units));
        return lo == hi ? $"{lo:0}°" : $"{lo:0}–{hi:0}°";
    }

    /// <summary>"wind 12 km/h" or "wind 8 mph" — from metres a second; "" below a breath.</summary>
    public static string Wind(double metresPerSecond, WeatherUnits units)
    {
        if (metresPerSecond < 0.5) return "";
        return units == WeatherUnits.Fahrenheit
            ? $"wind {Math.Round(metresPerSecond * 2.23694):0} mph"
            : $"wind {Math.Round(metresPerSecond * 3.6):0} km/h";
    }

    /// <summary>"rain 60%" — from a chance; "" under a fifth.</summary>
    public static string Rain(double chancePct) => chancePct >= 20 ? $"rain {Math.Round(chancePct):0}%" : "";

    public static string ViewName(WeatherView view) => view switch
    {
        WeatherView.RestOfDay => "Rest of today",
        WeatherView.Tomorrow => "Tomorrow",
        _ => "Now",
    };

    /// <summary>The view a word names: now / today / day / tomorrow — null for anything else.</summary>
    public static WeatherView? ParseView(string? word)
    {
        var w = (word ?? "").Trim().ToLowerInvariant();
        return w switch
        {
            "now" or "current" or "hour" => WeatherView.Now,
            "day" or "today" or "rest" or "restofday" or "rest of day" or "rest of today" or "later" => WeatherView.RestOfDay,
            "tomorrow" or "next" or "nextday" => WeatherView.Tomorrow,
            _ => null,
        };
    }

    /// <summary>What the chip draws for a view at a local time; null when the report has nothing for it.</summary>
    public static WeatherCard? Card(WeatherReport report, WeatherView view, DateTime local, WeatherUnits units)
    {
        switch (view)
        {
            case WeatherView.Now:
            {
                var h = report.HourAt(local);
                if (h is null) return null;
                var parts = new List<string>();
                if (h.Words.Length > 0) parts.Add(h.Words);
                var wind = Wind(h.WindMs, units);
                if (wind.Length > 0) parts.Add(wind);
                var rain = Rain(h.PrecipChancePct);
                if (rain.Length > 0) parts.Add(rain);
                return new WeatherCard("Now", Degrees(h.TempC, units), h.Sky, h.Night, string.Join(" · ", parts), Array.Empty<WeatherMark>());
            }
            case WeatherView.RestOfDay:
            {
                var hours = report.RestOfDay(local);
                var day = WeatherModel.SumDay(local.Date, hours);
                if (day is null) return null;
                var parts = new List<string>();
                if (day.Words.Length > 0) parts.Add(day.Words);
                var rain = Rain(day.PrecipChancePct);
                if (rain.Length > 0) parts.Add(rain);
                else if (day.PrecipMm >= 0.5) parts.Add($"{day.PrecipMm:0.#} mm");
                return new WeatherCard("Rest of today", Range(day.MinC, day.MaxC, units), day.Sky, false, string.Join(" · ", parts), Marks(hours, local.AddHours(2), 3, 4, units));
            }
            default:
            {
                var date = local.Date.AddDays(1);
                var day = report.DayOf(date);
                if (day is null) return null;
                var parts = new List<string>();
                if (day.Words.Length > 0) parts.Add(day.Words);
                var rain = Rain(day.PrecipChancePct);
                if (rain.Length > 0) parts.Add(rain);
                else if (day.PrecipMm >= 0.5) parts.Add($"{day.PrecipMm:0.#} mm");
                return new WeatherCard($"Tomorrow · {date:ddd}", Range(day.MinC, day.MaxC, units), day.Sky, false, string.Join(" · ", parts), Marks(report.HoursOf(date), date.AddHours(9), 3, 4, units));
            }
        }
    }

    /// <summary>Up to <paramref name="count"/> columns every <paramref name="stepHours"/> from a start: the hour, its glyph, its degrees.</summary>
    public static IReadOnlyList<WeatherMark> Marks(IReadOnlyList<WeatherHour> hours, DateTime from, int stepHours, int count, WeatherUnits units)
    {
        var list = new List<WeatherMark>();
        var at = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0);
        for (var i = 0; i < count; i++)
        {
            WeatherHour? hit = null;
            foreach (var h in hours)
            {
                if (h.LocalTime == at) { hit = h; break; }
            }
            if (hit is not null) list.Add(new WeatherMark(at.ToString("HH:mm", CultureInfo.InvariantCulture), hit.Sky, hit.Night, Degrees(hit.TempC, units)));
            at = at.AddHours(stepHours);
        }
        return list;
    }

    /// <summary>The one line remotes and the desk read: "Manchester · 18° · Light rain · wind 12 km/h".</summary>
    public static string Line(WeatherReport? report, WeatherView view, DateTime local, WeatherUnits units, string place)
    {
        var card = report is null ? null : Card(report, view, local, units);
        var head = place.Length > 0 ? place : "Weather";
        if (card is null) return $"{head} · no forecast yet";
        var title = view == WeatherView.Now ? "" : $"{card.Title} ";
        var detail = card.Detail.Length > 0 ? $" · {card.Detail}" : "";
        return $"{head} · {title}{card.Figure}{detail}";
    }
}

/// <summary>The addresses asked, and what each source wants of the caller.</summary>
public static class WeatherSources
{
    public const string MetNorwayName = "MET Norway";
    public const string OpenMeteoName = "Open-Meteo";

    /// <summary>MET Norway's terms: at most four decimals in the coordinates, a User-Agent that says who calls.</summary>
    public static string MetNorwayUrl(double lat, double lon)
        => $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={Math.Round(lat, 4).ToString("0.####", CultureInfo.InvariantCulture)}&lon={Math.Round(lon, 4).ToString("0.####", CultureInfo.InvariantCulture)}";

    /// <summary>Open-Meteo: the free host without a key, the customer host with one; three days, hourly and daily, local times.</summary>
    public static string OpenMeteoUrl(double lat, double lon, string apiKey)
    {
        var host = apiKey.Length > 0 ? "https://customer-api.open-meteo.com" : "https://api.open-meteo.com";
        var url = $"{host}/v1/forecast?latitude={lat.ToString("0.####", CultureInfo.InvariantCulture)}&longitude={lon.ToString("0.####", CultureInfo.InvariantCulture)}"
                  + "&current=temperature_2m,is_day,weather_code,wind_speed_10m,precipitation"
                  + "&hourly=temperature_2m,weather_code,precipitation,precipitation_probability,is_day,wind_speed_10m"
                  + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum,precipitation_probability_max"
                  + "&timezone=auto&forecast_days=3&wind_speed_unit=ms";
        if (apiKey.Length > 0) url += "&apikey=" + Uri.EscapeDataString(apiKey);
        return url;
    }

    /// <summary>Nominatim (OpenStreetMap) place search: one request per press, a User-Agent that says who calls.</summary>
    public static string NominatimUrl(string query)
        => $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(query.Trim())}&format=jsonv2&limit=6";

    /// <summary>The User-Agent both services ask for: the app, its version, where it lives, and the operator's contact when given.</summary>
    public static string UserAgent(string version, string contact)
        => $"Patterns/{(version.Length > 0 ? version : "dev")} (https://github.com/jammin808/Patterns{(contact.Length > 0 ? "; " + contact : "")})";

    public static string Name(WeatherProvider provider) => provider == WeatherProvider.OpenMeteo ? OpenMeteoName : MetNorwayName;

    /// <summary>The credit line the chip draws: "Weather: MET Norway (CC BY 4.0)".</summary>
    public static string Credit(string source) => $"Weather: {source}{(source.Length > 0 ? " (CC BY 4.0)" : "")}";
}

/// <summary>The sources' JSON to a report. Junk never throws: null says the reply could not be read.</summary>
public static class WeatherParser
{
    /// <summary>MET Norway Locationforecast 2.0 (compact): UTC times, an hour's instant, the next hour's and the next six hours' summaries.</summary>
    public static WeatherReport? MetNorway(string json, Func<DateTime, DateTime> utcToLocal, DateTime fetchedLocal)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("properties", out var props) || !props.TryGetProperty("timeseries", out var series) || series.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var hours = new List<WeatherHour>();
            foreach (var row in series.EnumerateArray())
            {
                if (!row.TryGetProperty("time", out var t) || t.ValueKind != JsonValueKind.String) continue;
                if (!DateTime.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) continue;
                if (!row.TryGetProperty("data", out var data)) continue;
                var details = data.TryGetProperty("instant", out var instant) && instant.TryGetProperty("details", out var d) ? d : default;
                var temp = Num(details, "air_temperature", double.NaN);
                if (double.IsNaN(temp)) continue;
                var wind = Num(details, "wind_speed", 0);
                var code = "";
                var mm = 0.0;
                var chance = 0.0;
                if (data.TryGetProperty("next_1_hours", out var next1))
                {
                    code = Str(next1, "summary", "symbol_code");
                    mm = Num(next1.TryGetProperty("details", out var d1) ? d1 : default, "precipitation_amount", 0);
                    chance = Num(next1.TryGetProperty("details", out var d1b) ? d1b : default, "probability_of_precipitation", 0);
                }
                if (code.Length == 0 && data.TryGetProperty("next_6_hours", out var next6))
                {
                    code = Str(next6, "summary", "symbol_code");
                    mm = Num(next6.TryGetProperty("details", out var d6) ? d6 : default, "precipitation_amount", 0) / 6;
                    chance = Num(next6.TryGetProperty("details", out var d6b) ? d6b : default, "probability_of_precipitation", 0);
                }
                var (sky, night) = WeatherModel.MetSymbol(code);
                hours.Add(new WeatherHour(utcToLocal(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), temp, sky, night, WeatherModel.MetWords(code), mm, chance, wind));
            }
            if (hours.Count == 0) return null;
            return new WeatherReport(hours, Array.Empty<WeatherDay>(), fetchedLocal, WeatherSources.MetNorwayName);
        }
        catch (Exception ex)
        {
            Log.Warn("MET Norway forecast unreadable.", ex);
            return null;
        }
    }

    /// <summary>Open-Meteo's forecast: local times (timezone=auto), hourly and daily arrays side by side.</summary>
    public static WeatherReport? OpenMeteo(string json, DateTime fetchedLocal)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var hours = new List<WeatherHour>();
            if (root.TryGetProperty("hourly", out var hourly) && hourly.TryGetProperty("time", out var times) && times.ValueKind == JsonValueKind.Array)
            {
                var temps = Arr(hourly, "temperature_2m");
                var codes = Arr(hourly, "weather_code");
                var mms = Arr(hourly, "precipitation");
                var chances = Arr(hourly, "precipitation_probability");
                var days = Arr(hourly, "is_day");
                var winds = Arr(hourly, "wind_speed_10m");
                var i = 0;
                foreach (var t in times.EnumerateArray())
                {
                    var idx = i++;
                    if (t.ValueKind != JsonValueKind.String || !DateTime.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) continue;
                    var temp = At(temps, idx, double.NaN);
                    if (double.IsNaN(temp)) continue;
                    var code = (int)At(codes, idx, -1);
                    var night = At(days, idx, 1) == 0;
                    hours.Add(new WeatherHour(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), temp, WeatherModel.WmoSky(code), night, WeatherModel.WmoWords(code),
                        At(mms, idx, 0), At(chances, idx, 0), At(winds, idx, 0)));
                }
            }
            var dayRows = new List<WeatherDay>();
            if (root.TryGetProperty("daily", out var daily) && daily.TryGetProperty("time", out var dtimes) && dtimes.ValueKind == JsonValueKind.Array)
            {
                var codes = Arr(daily, "weather_code");
                var maxs = Arr(daily, "temperature_2m_max");
                var mins = Arr(daily, "temperature_2m_min");
                var sums = Arr(daily, "precipitation_sum");
                var chances = Arr(daily, "precipitation_probability_max");
                var i = 0;
                foreach (var t in dtimes.EnumerateArray())
                {
                    var idx = i++;
                    if (t.ValueKind != JsonValueKind.String || !DateTime.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                    var max = At(maxs, idx, double.NaN);
                    var min = At(mins, idx, double.NaN);
                    if (double.IsNaN(max) || double.IsNaN(min)) continue;
                    var code = (int)At(codes, idx, -1);
                    dayRows.Add(new WeatherDay(date.Date, min, max, WeatherModel.WmoSky(code), WeatherModel.WmoWords(code), At(sums, idx, 0), At(chances, idx, 0)));
                }
            }
            if (hours.Count == 0 && dayRows.Count == 0) return null;
            return new WeatherReport(hours, dayRows, fetchedLocal, WeatherSources.OpenMeteoName);
        }
        catch (Exception ex)
        {
            Log.Warn("Open-Meteo forecast unreadable.", ex);
            return null;
        }
    }

    /// <summary>Nominatim's search reply: the places found, best first.</summary>
    public static IReadOnlyList<WeatherPlace> Nominatim(string json)
    {
        var list = new List<WeatherPlace>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var lat = NumOrString(row, "lat");
                var lon = NumOrString(row, "lon");
                if (double.IsNaN(lat) || double.IsNaN(lon)) continue;
                var name = Str(row, "display_name");
                var shortName = Str(row, "name");
                list.Add(new WeatherPlace(name.Length > 0 ? name : shortName, lat, lon));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Place search reply unreadable.", ex);
        }
        return list;
    }

    /// <summary>"Manchester, Greater Manchester, England, United Kingdom" → "Manchester" — what the chip shows.</summary>
    public static string ShortName(string displayName)
    {
        var cut = displayName.IndexOf(',');
        return (cut > 0 ? displayName[..cut] : displayName).Trim();
    }

    private static double Num(JsonElement obj, string name, double fallback)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private static double NumOrString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return double.NaN;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }

    private static string Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Str(JsonElement obj, string name, string inner)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? Str(v, inner) : "";

    private static JsonElement Arr(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v : default;

    private static double At(JsonElement array, int index, double fallback)
    {
        if (array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength()) return fallback;
        var v = array[index];
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
    }
}
