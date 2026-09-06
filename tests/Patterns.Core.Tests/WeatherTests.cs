using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The weather overlay's model: two sources read into one report, the sky in glyphs and words,
/// the three cards, the units, the addresses asked, the place search, the verbs on the wire and
/// over OSC, the cue actions, and the chip drawn by the engine.
/// </summary>
public class WeatherTests
{
    private static readonly DateTime Fetched = new(2026, 9, 5, 14, 0, 0);

    /// <summary>A MET Norway compact reply: UTC hours from 12:00 with a temperature, a wind and the next hour's symbol.</summary>
    private static string MetJson(params (string Time, double Temp, string Symbol, double Mm)[] rows)
    {
        var items = rows.Select(r =>
            $"{{\"time\":\"{r.Time}\",\"data\":{{\"instant\":{{\"details\":{{\"air_temperature\":{r.Temp.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"wind_speed\":3.4,\"relative_humidity\":70}}}}," +
            $"\"next_1_hours\":{{\"summary\":{{\"symbol_code\":\"{r.Symbol}\"}},\"details\":{{\"precipitation_amount\":{r.Mm.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"probability_of_precipitation\":{(r.Mm > 0 ? 70 : 5)}}}}}}}}}");
        return "{\"type\":\"Feature\",\"properties\":{\"meta\":{\"updated_at\":\"2026-09-05T12:10:00Z\"},\"timeseries\":[" + string.Join(",", items) + "]}}";
    }

    private static string MetSample()
    {
        var rows = new List<(string, double, string, double)>();
        // 12:00Z to 23:00Z today, then tomorrow 00:00Z–23:00Z: a wet afternoon, a clear night, a fair tomorrow.
        for (var h = 12; h < 24; h++) rows.Add(($"2026-09-05T{h:00}:00:00Z", 15 + (h < 18 ? h - 12 : 23 - h) * 0.8, h < 16 ? "lightrainshowers_day" : h < 20 ? "cloudy" : "clearsky_night", h < 16 ? 0.6 : 0));
        for (var h = 0; h < 24; h++) rows.Add(($"2026-09-06T{h:00}:00:00Z", 11 + (h < 15 ? h : 29 - h) * 0.5, h < 7 ? "fair_night" : h < 12 ? "fair_day" : h < 18 ? "partlycloudy_day" : "clearsky_night", 0));
        return MetJson(rows.ToArray());
    }

    private static DateTime UtcAsLocal(DateTime utc) => utc.AddHours(1); // the venue is at UTC+1

    [Fact]
    public void MetNorwaySymbolsMapToGlyphsNightsAndWords()
    {
        Assert.Equal((WeatherSky.Clear, false), WeatherModel.MetSymbol("clearsky_day"));
        Assert.Equal((WeatherSky.Clear, true), WeatherModel.MetSymbol("clearsky_night"));
        Assert.Equal((WeatherSky.Fair, false), WeatherModel.MetSymbol("fair_day"));
        Assert.Equal((WeatherSky.PartlyCloudy, true), WeatherModel.MetSymbol("partlycloudy_night"));
        Assert.Equal((WeatherSky.Cloudy, false), WeatherModel.MetSymbol("cloudy"));
        Assert.Equal((WeatherSky.Fog, false), WeatherModel.MetSymbol("fog"));
        Assert.Equal((WeatherSky.Showers, false), WeatherModel.MetSymbol("lightrainshowers_day"));
        Assert.Equal((WeatherSky.Rain, false), WeatherModel.MetSymbol("heavyrain"));
        Assert.Equal((WeatherSky.Sleet, false), WeatherModel.MetSymbol("sleet"));
        Assert.Equal((WeatherSky.Snow, true), WeatherModel.MetSymbol("snowshowers_night"));
        Assert.Equal((WeatherSky.Thunder, false), WeatherModel.MetSymbol("rainandthunder"));
        Assert.Equal((WeatherSky.Thunder, false), WeatherModel.MetSymbol("lightssleetshowersandthunder_day"));
        Assert.Equal((WeatherSky.Unknown, false), WeatherModel.MetSymbol(""));

        Assert.Equal("Clear", WeatherModel.MetWords("clearsky_night"));
        Assert.Equal("Fair", WeatherModel.MetWords("fair_day"));
        Assert.Equal("Partly cloudy", WeatherModel.MetWords("partlycloudy_day"));
        Assert.Equal("Light rain showers", WeatherModel.MetWords("lightrainshowers_day"));
        Assert.Equal("Heavy rain", WeatherModel.MetWords("heavyrain"));
        Assert.Equal("Rain and thunder", WeatherModel.MetWords("rainandthunder"));
        Assert.Equal("Light sleet showers and thunder", WeatherModel.MetWords("lightssleetshowersandthunder_day"));
        Assert.Equal("Light snow", WeatherModel.MetWords("lightsnow"));
        Assert.Equal("Snow showers", WeatherModel.MetWords("snowshowers_night"));
        Assert.Equal("Fog", WeatherModel.MetWords("fog"));
        Assert.Equal("", WeatherModel.MetWords(""));
    }

    [Fact]
    public void WmoCodesMapToGlyphsAndWords()
    {
        Assert.Equal(WeatherSky.Clear, WeatherModel.WmoSky(0));
        Assert.Equal(WeatherSky.Fair, WeatherModel.WmoSky(1));
        Assert.Equal(WeatherSky.PartlyCloudy, WeatherModel.WmoSky(2));
        Assert.Equal(WeatherSky.Cloudy, WeatherModel.WmoSky(3));
        Assert.Equal(WeatherSky.Fog, WeatherModel.WmoSky(45));
        Assert.Equal(WeatherSky.Showers, WeatherModel.WmoSky(53));
        Assert.Equal(WeatherSky.Rain, WeatherModel.WmoSky(63));
        Assert.Equal(WeatherSky.Sleet, WeatherModel.WmoSky(66));
        Assert.Equal(WeatherSky.Snow, WeatherModel.WmoSky(73));
        Assert.Equal(WeatherSky.Showers, WeatherModel.WmoSky(81));
        Assert.Equal(WeatherSky.Snow, WeatherModel.WmoSky(86));
        Assert.Equal(WeatherSky.Thunder, WeatherModel.WmoSky(95));
        Assert.Equal(WeatherSky.Unknown, WeatherModel.WmoSky(-1));
        Assert.Equal("Overcast", WeatherModel.WmoWords(3));
        Assert.Equal("Light rain", WeatherModel.WmoWords(61));
        Assert.Equal("Thunderstorm with hail", WeatherModel.WmoWords(99));
        Assert.Equal("", WeatherModel.WmoWords(123));
        Assert.True(WeatherModel.Severity(WeatherSky.Thunder) > WeatherModel.Severity(WeatherSky.Rain));
        Assert.True(WeatherModel.Severity(WeatherSky.Rain) > WeatherModel.Severity(WeatherSky.Cloudy));
        Assert.True(WeatherModel.Severity(WeatherSky.Cloudy) > WeatherModel.Severity(WeatherSky.Clear));
    }

    [Fact]
    public void AMetNorwayReplyBecomesHoursInLocalTimeAndTheCardsReadFromThem()
    {
        var report = WeatherParser.MetNorway(MetSample(), UtcAsLocal, Fetched)!;
        Assert.NotNull(report);
        Assert.Equal(WeatherSources.MetNorwayName, report.Source);
        Assert.Equal(36, report.Hours.Count);
        Assert.Empty(report.Days);
        Assert.Equal(new DateTime(2026, 9, 5, 13, 0, 0), report.Hours[0].LocalTime);   // 12:00Z at UTC+1
        Assert.Equal(15, report.Hours[0].TempC);
        Assert.Equal(WeatherSky.Showers, report.Hours[0].Sky);
        Assert.Equal("Light rain showers", report.Hours[0].Words);
        Assert.Equal(0.6, report.Hours[0].PrecipMm);
        Assert.Equal(70, report.Hours[0].PrecipChancePct);
        Assert.Equal(3.4, report.Hours[0].WindMs);

        // Now at 14:30 local: the 14:00 hour (13:00Z) — 15.8°, showers.
        var local = new DateTime(2026, 9, 5, 14, 30, 0);
        var now = WeatherWords.Card(report, WeatherView.Now, local, WeatherUnits.Celsius)!;
        Assert.Equal("Now", now.Title);
        Assert.Equal("16°", now.Figure);
        Assert.Equal(WeatherSky.Showers, now.Sky);
        Assert.False(now.Night);
        Assert.Equal("Light rain showers · wind 12 km/h · rain 70%", now.Detail);
        Assert.Empty(now.Marks);

        // The rest of today: from 13:00 local to midnight — the worst sky wins the summary (showers), the range, the marks every three hours from 16:00.
        var day = WeatherWords.Card(report, WeatherView.RestOfDay, local, WeatherUnits.Celsius)!;
        Assert.Equal("Rest of today", day.Title);
        Assert.Equal(WeatherSky.Showers, day.Sky);
        Assert.Equal("Light rain showers · rain 70%", day.Detail);
        Assert.StartsWith("1", day.Figure);
        Assert.Contains("–", day.Figure);
        Assert.Equal(new[] { "16:00", "19:00", "22:00" }, day.Marks.Select(m => m.Label));
        Assert.Equal(WeatherSky.Clear, day.Marks[2].Sky);
        Assert.True(day.Marks[2].Night);

        // Tomorrow: summed from its hours — daylight hours decide the sky (partly cloudy over fair), the marks from 09:00.
        var tomorrow = WeatherWords.Card(report, WeatherView.Tomorrow, local, WeatherUnits.Celsius)!;
        Assert.Equal("Tomorrow · Sun", tomorrow.Title);
        Assert.Equal(WeatherSky.PartlyCloudy, tomorrow.Sky);
        Assert.Equal("Partly cloudy", tomorrow.Detail);
        Assert.Equal(new[] { "09:00", "12:00", "15:00", "18:00" }, tomorrow.Marks.Select(m => m.Label));
        Assert.Equal("11–18°", tomorrow.Figure);

        // In Fahrenheit the figures and the wind change, the words do not.
        var f = WeatherWords.Card(report, WeatherView.Now, local, WeatherUnits.Fahrenheit)!;
        Assert.Equal("60°", f.Figure);
        Assert.Contains("wind 8 mph", f.Detail);

        // The line every surface reads.
        Assert.Equal("Manchester · 16° · Light rain showers · wind 12 km/h · rain 70%", WeatherWords.Line(report, WeatherView.Now, local, WeatherUnits.Celsius, "Manchester"));
        Assert.StartsWith("Manchester · Tomorrow · Sun 11–18° · Partly cloudy", WeatherWords.Line(report, WeatherView.Tomorrow, local, WeatherUnits.Celsius, "Manchester"));
        Assert.Equal("Weather · no forecast yet", WeatherWords.Line(null, WeatherView.Now, local, WeatherUnits.Celsius, ""));

        // Past the last hour the last one holds; before the first, the first.
        Assert.Equal(report.Hours[^1], report.HourAt(new DateTime(2026, 9, 8, 0, 0, 0)));
        Assert.Equal(report.Hours[0], report.HourAt(new DateTime(2026, 9, 1, 0, 0, 0)));
        Assert.Null(WeatherWords.Card(report, WeatherView.Tomorrow, new DateTime(2026, 9, 8, 12, 0, 0), WeatherUnits.Celsius));
    }

    [Fact]
    public void AnOpenMeteoReplyBecomesHoursAndDaysInTheVenuesTime()
    {
        var hours = Enumerable.Range(0, 48).Select(i => new DateTime(2026, 9, 5).AddHours(i));
        var json = "{\"latitude\":53.5,\"longitude\":-2.25,\"timezone\":\"Europe/London\",\"utc_offset_seconds\":3600," +
                   "\"current\":{\"time\":\"2026-09-05T14:00\",\"temperature_2m\":17.3,\"is_day\":1,\"weather_code\":61,\"wind_speed_10m\":3.2}," +
                   "\"hourly\":{\"time\":[" + string.Join(",", hours.Select(h => $"\"{h:yyyy-MM-ddTHH:mm}\"")) + "]," +
                   "\"temperature_2m\":[" + string.Join(",", Enumerable.Range(0, 48).Select(i => (12 + i % 24 * 0.25).ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]," +
                   "\"weather_code\":[" + string.Join(",", Enumerable.Range(0, 48).Select(i => i < 24 ? (i < 16 ? 61 : 3) : 2)) + "]," +
                   "\"precipitation\":[" + string.Join(",", Enumerable.Range(0, 48).Select(i => i < 16 ? "0.4" : "0")) + "]," +
                   "\"precipitation_probability\":[" + string.Join(",", Enumerable.Range(0, 48).Select(i => i < 16 ? 65 : 10)) + "]," +
                   "\"is_day\":[" + string.Join(",", Enumerable.Range(0, 48).Select(i => i % 24 is >= 6 and <= 19 ? 1 : 0)) + "]," +
                   "\"wind_speed_10m\":[" + string.Join(",", Enumerable.Range(0, 48).Select(_ => "3.2")) + "]}," +
                   "\"daily\":{\"time\":[\"2026-09-05\",\"2026-09-06\",\"2026-09-07\"],\"weather_code\":[61,2,3],\"temperature_2m_max\":[17.8,18.4,16.0],\"temperature_2m_min\":[12.0,11.1,10.5],\"precipitation_sum\":[6.4,0,1.2],\"precipitation_probability_max\":[65,10,40]}}";
        var report = WeatherParser.OpenMeteo(json, Fetched)!;
        Assert.NotNull(report);
        Assert.Equal(WeatherSources.OpenMeteoName, report.Source);
        Assert.Equal(48, report.Hours.Count);
        Assert.Equal(3, report.Days.Count);
        Assert.Equal(new DateTime(2026, 9, 5, 0, 0, 0), report.Hours[0].LocalTime);   // local already
        Assert.Equal(WeatherSky.Rain, report.Hours[3].Sky);
        Assert.Equal("Light rain", report.Hours[3].Words);
        Assert.True(report.Hours[3].Night);
        Assert.False(report.Hours[10].Night);

        // Tomorrow comes from the daily row, not the hours: its own extremes and sky.
        var local = new DateTime(2026, 9, 5, 14, 30, 0);
        var tomorrow = WeatherWords.Card(report, WeatherView.Tomorrow, local, WeatherUnits.Celsius)!;
        Assert.Equal("11–18°", tomorrow.Figure);
        Assert.Equal(WeatherSky.PartlyCloudy, tomorrow.Sky);
        Assert.Equal("Partly cloudy", tomorrow.Detail);
        Assert.Equal(4, tomorrow.Marks.Count);
        var day = report.DayOf(new DateTime(2026, 9, 7))!;
        Assert.Equal(40, day.PrecipChancePct);
        Assert.Equal(1.2, day.PrecipMm);

        // A day the reply does not carry sums from its hours; none at all is null.
        Assert.Null(report.DayOf(new DateTime(2026, 9, 9)));
    }

    [Fact]
    public void JunkNeverThrowsAndNominatimIsReadIntoPlaces()
    {
        Assert.Null(WeatherParser.MetNorway("not json", UtcAsLocal, Fetched));
        Assert.Null(WeatherParser.MetNorway("{\"properties\":{\"timeseries\":[]}}", UtcAsLocal, Fetched));
        Assert.Null(WeatherParser.OpenMeteo("[]", Fetched));
        Assert.Null(WeatherParser.OpenMeteo("{\"hourly\":{\"time\":[\"x\"]}}", Fetched));
        Assert.Empty(WeatherParser.Nominatim("{}"));
        Assert.Empty(WeatherParser.Nominatim("<html>"));

        var places = WeatherParser.Nominatim("[{\"display_name\":\"Manchester, Greater Manchester, England, United Kingdom\",\"lat\":\"53.4794892\",\"lon\":\"-2.2451148\"},{\"name\":\"Manchester\",\"lat\":42.99,\"lon\":-71.46},{\"display_name\":\"no coordinates\"}]");
        Assert.Equal(2, places.Count);
        Assert.Equal("Manchester, Greater Manchester, England, United Kingdom", places[0].Name);
        Assert.Equal(53.4794892, places[0].Latitude, 6);
        Assert.Equal(-2.2451148, places[0].Longitude, 6);
        Assert.Equal("Manchester", places[1].Name);
        Assert.Equal("Manchester", WeatherParser.ShortName(places[0].Name));
        Assert.Equal("Manchester", places[0].ToString().Split(',')[0]);
    }

    [Fact]
    public void TheAddressesAskedFollowEachSourcesTerms()
    {
        Assert.Equal("https://api.met.no/weatherapi/locationforecast/2.0/compact?lat=53.4795&lon=-2.2451", WeatherSources.MetNorwayUrl(53.4794892, -2.2451148));
        var free = WeatherSources.OpenMeteoUrl(53.4794892, -2.2451148, "");
        Assert.StartsWith("https://api.open-meteo.com/v1/forecast?latitude=53.4795&longitude=-2.2451&current=", free);
        Assert.Contains("timezone=auto", free);
        Assert.Contains("wind_speed_unit=ms", free);
        Assert.DoesNotContain("apikey", free);
        var paid = WeatherSources.OpenMeteoUrl(53.5, -2.25, "k3y/1");
        Assert.StartsWith("https://customer-api.open-meteo.com/v1/forecast?", paid);
        Assert.EndsWith("&apikey=k3y%2F1", paid);
        Assert.Equal("https://nominatim.openstreetmap.org/search?q=Manchester%2C%20UK&format=jsonv2&limit=6", WeatherSources.NominatimUrl(" Manchester, UK "));
        Assert.Equal("Patterns/1.2.3 (https://github.com/jammin808/Patterns; ops@venue.example)", WeatherSources.UserAgent("1.2.3", "ops@venue.example"));
        Assert.Equal("Patterns/dev (https://github.com/jammin808/Patterns)", WeatherSources.UserAgent("", ""));
        Assert.Equal("Weather: MET Norway (CC BY 4.0)", WeatherSources.Credit(WeatherSources.MetNorwayName));
        Assert.Equal("Open-Meteo", WeatherSources.Name(WeatherProvider.OpenMeteo));
    }

    [Fact]
    public void TheWordsRoundAndConvert()
    {
        Assert.Equal("18°", WeatherWords.Degrees(17.6, WeatherUnits.Celsius));
        Assert.Equal("-3°", WeatherWords.Degrees(-2.7, WeatherUnits.Celsius));
        Assert.Equal("64°", WeatherWords.Degrees(17.6, WeatherUnits.Fahrenheit));
        Assert.Equal("14–19°", WeatherWords.Range(14.2, 18.9, WeatherUnits.Celsius));
        Assert.Equal("18°", WeatherWords.Range(17.6, 18.4, WeatherUnits.Celsius));
        Assert.Equal("wind 12 km/h", WeatherWords.Wind(3.4, WeatherUnits.Celsius));
        Assert.Equal("wind 8 mph", WeatherWords.Wind(3.4, WeatherUnits.Fahrenheit));
        Assert.Equal("", WeatherWords.Wind(0.2, WeatherUnits.Celsius));
        Assert.Equal("rain 60%", WeatherWords.Rain(60));
        Assert.Equal("", WeatherWords.Rain(15));
        Assert.Equal(WeatherView.RestOfDay, WeatherWords.ParseView("today"));
        Assert.Equal(WeatherView.RestOfDay, WeatherWords.ParseView("DAY"));
        Assert.Equal(WeatherView.Tomorrow, WeatherWords.ParseView(" tomorrow "));
        Assert.Equal(WeatherView.Now, WeatherWords.ParseView("now"));
        Assert.Null(WeatherWords.ParseView("yesterday"));
        Assert.Equal("Rest of today", WeatherWords.ViewName(WeatherView.RestOfDay));
    }

    [Fact]
    public void TheVerbsOnTheWireAndOverOscReachTheChip()
    {
        Assert.Equal(ShowActionKind.WeatherOn, ControlProtocol.Parse("WEATHER ON").Action.Kind);
        Assert.Equal(ShowActionKind.WeatherOff, ControlProtocol.Parse("forecast hide").Action.Kind);
        Assert.Equal(ShowActionKind.WeatherToggle, ControlProtocol.Parse("WEATHER").Action.Kind);
        Assert.Equal(ShowActionKind.WeatherToggle, ControlProtocol.Parse("WEATHER TOGGLE").Action.Kind);
        var view = ControlProtocol.Parse("WEATHER Tomorrow");
        Assert.Equal(ShowActionKind.WeatherView, view.Action.Kind);
        Assert.Equal("tomorrow", view.Action.Value);
        Assert.Equal("day", ControlProtocol.Parse("WEATHER DAY").Action.Value);
        Assert.Equal("today", ControlProtocol.Parse("WEATHER TODAY").Action.Value);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("WEATHER yesterday").Kind);

        Assert.Equal("WEATHER ON", OscMap.ToLine(OscMessage.Of("/patterns/weather", 1)));
        Assert.Equal("WEATHER OFF", OscMap.ToLine(OscMessage.Of("/patterns/weather/off")));
        Assert.Equal("WEATHER TOGGLE", OscMap.ToLine(OscMessage.Of("/patterns/weather")));
        Assert.Equal("WEATHER TOMORROW", OscMap.ToLine(OscMessage.Of("/patterns/weather/tomorrow")));
        Assert.Equal("WEATHER DAY", OscMap.ToLine(OscMessage.Of("/patterns/forecast", "today")));
        Assert.Equal("WEATHER NOW", OscMap.ToLine(OscMessage.Of("/patterns/weather", "now")));
        Assert.Contains(OscMap.Reference, a => a.Address.StartsWith("/patterns/weather"));

        var messages = OscFeedback.FromState("{\"weather\":{\"on\":true,\"view\":\"day\",\"place\":\"Manchester\",\"text\":\"Manchester · 14–19° · Showers\",\"figure\":\"14–19°\"}}");
        Assert.Contains(messages, m => m.Address == "/patterns/state/weather" && Equals(m.Args[0], 1));
        Assert.Contains(messages, m => m.Address == "/patterns/state/weather/view" && Equals(m.Args[0], "day"));
        Assert.Contains(messages, m => m.Address == "/patterns/state/weather/place" && Equals(m.Args[0], "Manchester"));
        Assert.Contains(messages, m => m.Address == "/patterns/state/weather/figure" && Equals(m.Args[0], "14–19°"));
        Assert.DoesNotContain(OscFeedback.FromState("{\"live\":true}"), m => m.Address.StartsWith("/patterns/state/weather"));
    }

    [Fact]
    public void TheCueActionsReadThroughTheSpecTheSheetTheSummaryAndTheChecks()
    {
        Assert.Equal((TargetKind.None, ValueKind.WeatherView), ActionSpec.For(ShowActionKind.WeatherView));
        Assert.Equal((TargetKind.None, ValueKind.None), ActionSpec.For(ShowActionKind.WeatherOn));
        Assert.Equal("Weather on", ActionSpec.Label(ShowActionKind.WeatherOn));
        Assert.Contains(ShowActionKind.WeatherView, ActionSpec.CueKinds);
        Assert.Equal(ShowActionKind.WeatherOn, CueSheet.ParseKind("weather"));
        Assert.Equal(ShowActionKind.WeatherOn, CueSheet.ParseKind("Forecast"));
        Assert.Equal(ShowActionKind.WeatherOff, CueSheet.ParseKind("weather off"));
        Assert.Equal(ShowActionKind.WeatherView, CueSheet.ParseKind("weather view"));

        var state = new ShowState();
        Assert.Equal("Weather on", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.WeatherOn }));
        Assert.Equal("Weather: tomorrow", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.WeatherView, Value = "tomorrow" }));
        Assert.Equal("Weather: 'later on' (not a view)", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.WeatherView, Value = "later on" }));

        var stack = CueStacks.Caller(state);
        var on = new RunCueConfig { Number = "1", Name = "Weather" };
        on.Actions.Add(new CueActionConfig { Kind = ShowActionKind.WeatherOn });
        var bad = new RunCueConfig { Number = "2", Name = "View" };
        bad.Actions.Add(new CueActionConfig { Kind = ShowActionKind.WeatherView, Value = "yesterday" });
        stack.Cues.Add(on);
        stack.Cues.Add(bad);
        var report = CueValidator.Validate(state, stack, new CueValidationContext());
        Assert.Null(report.ReasonFor(on.Id));                                   // no place yet is a note, never a break
        Assert.Contains("no place is set", report.Warnings[on.Id]);
        Assert.Contains("not now, day or tomorrow", report.ReasonFor(bad.Id));
        state.Weather.Latitude = 53.48;
        state.Weather.Longitude = -2.24;
        Assert.False(CueValidator.Validate(state, stack, new CueValidationContext()).Warnings.ContainsKey(on.Id));
    }

    [Fact]
    public void TheChipDrawsOnTheOutputAndRecordsItsBoxForADrag()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.FlatField;
            s.Overlays.Weather.Enabled = true;
            s.Overlays.Weather.Anchor = Anchor9.TopLeft;
            s.Overlays.Weather.View = WeatherView.RestOfDay;
            s.Weather.Place = "Manchester";
            s.Weather.Latitude = 53.48;
            s.Weather.Longitude = -2.24;
        });
        var report = WeatherParser.MetNorway(MetSample(), utc => utc, Fetched)!;
        var snap = new ShowSnapshot { State = state, Version = 1, Weather = report };
        using var with = RenderTestHarness.Render(snap, 640, 360);
        using var without = RenderTestHarness.Render(RenderTestHarness.State(s => s.Pattern.Kind = PatternKind.FlatField), 640, 360);

        // The chip changes pixels in the top-left corner and nowhere near the bottom-right.
        var changed = 0;
        for (var y = 10; y < 120; y++)
        {
            for (var x = 10; x < 300; x++)
            {
                if (with.GetPixel(x, y) != without.GetPixel(x, y)) changed++;
            }
        }
        Assert.True(changed > 500, $"chip pixels: {changed}");
        Assert.Equal(without.GetPixel(600, 340), with.GetPixel(600, 340));

        // With no forecast the chip still draws and says why; with the overlay off, nothing.
        using var waiting = RenderTestHarness.Render(new ShowSnapshot { State = state, Version = 2 }, 640, 360);
        var waitingChanged = 0;
        for (var y = 10; y < 120; y++)
        {
            for (var x = 10; x < 300; x++)
            {
                if (waiting.GetPixel(x, y) != without.GetPixel(x, y)) waitingChanged++;
            }
        }
        Assert.True(waitingChanged > 200, $"waiting chip pixels: {waitingChanged}");
    }

    [Fact]
    public void TheChipRecordsAHitForTheDeskToDrag()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.FlatField;
            s.Overlays.Weather.Enabled = true;
        });
        var engine = new PatternEngine();
        using var sink = new SinkState();
        var info = new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(320, 180),
            ReferenceSize = new SKSizeI(320, 180),
            Time = 1,
            Now = new DateTime(2026, 9, 5, 14, 30, 0),
            UtcNow = new DateTime(2026, 9, 5, 13, 30, 0, DateTimeKind.Utc),
            Sink = SinkKind.Output,
            SinkIndex = 1,
            SinkLabel = "test",
        };
        engine.Render(surface.Canvas, RenderTestHarness.Snap(state), in ctx, sink);
        Assert.Contains(sink.Hits, h => h.Kind == HitKind.Weather && h.Rect.Width > 20 && h.Rect.Height > 10);
    }
}
