using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The weather overlay on a live desk: the place found and used, the forecast fetched through a
/// fake transport and carried on the snapshot, the chip switched from the wire, a cue and the
/// Show panel's drawer, STATE for remotes, a failed fetch keeping the last forecast, the page.
/// </summary>
public class WeatherAppTests
{
    private static string MetJson()
    {
        var rows = new List<string>();
        var start = DateTime.UtcNow.Date.AddDays(-1);
        for (var i = 0; i < 72; i++)
        {
            var t = start.AddHours(i);
            var code = i % 24 < 8 ? "clearsky_night" : i % 24 < 16 ? "lightrainshowers_day" : "cloudy";
            rows.Add($"{{\"time\":\"{t:yyyy-MM-ddTHH:mm:ss}Z\",\"data\":{{\"instant\":{{\"details\":{{\"air_temperature\":{12 + i % 24 * 0.4:0.0},\"wind_speed\":2.8}}}},\"next_1_hours\":{{\"summary\":{{\"symbol_code\":\"{code}\"}},\"details\":{{\"precipitation_amount\":0.2}}}}}}}}");
        }
        return "{\"properties\":{\"timeseries\":[" + string.Join(",", rows) + "]}}";
    }

    private const string NominatimJson = "[{\"display_name\":\"Manchester, Greater Manchester, England, United Kingdom\",\"lat\":\"53.4794892\",\"lon\":\"-2.2451148\"},{\"display_name\":\"Manchester, Hillsborough County, New Hampshire, United States\",\"lat\":\"42.9956397\",\"lon\":\"-71.4547891\"}]";

    private static void PumpUntil(Func<bool> done, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void TheForecastIsFetchedCarriedOnTheSnapshotAndReadEverywhere()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var asked = new List<string>();
            services.Weather.Transport = url =>
            {
                asked.Add(url);
                return Task.FromResult(url.Contains("api.met.no") ? MetJson() : NominatimJson);
            };

            // No place: the overlay goes on, the chip says so, nothing is asked for.
            vm.State.Overlays.Weather.Enabled = true;
            services.Weather.Poll();
            Assert.StartsWith("Set a place", services.Weather.Status);
            Assert.Empty(asked);
            Assert.Null(services.Bus.Weather);

            // The place, and the forecast on its way through the fake transport.
            vm.UseWeatherPlace(new WeatherPlace("Manchester, Greater Manchester, England, United Kingdom", 53.4794892, -2.2451148));
            Assert.Equal("Manchester", vm.State.Weather.Place);
            Assert.Equal("53.4795, -2.2451", vm.WeatherCoordinatesText);
            services.Weather.Poll();
            PumpUntil(() => services.Bus.Weather is not null);
            Assert.Single(asked);
            Assert.StartsWith("https://api.met.no/weatherapi/locationforecast/2.0/compact?lat=53.4795&lon=-2.2451", asked[0]);
            var report = services.Bus.Weather!;
            Assert.Equal(WeatherSources.MetNorwayName, report.Source);
            Assert.Equal(72, report.Hours.Count);
            Assert.Same(report, services.Bus.Current.Weather);            // the snapshot every sink draws carries it
            Assert.StartsWith("MET Norway: 72 hours · updated", services.Weather.Status);
            vm.PollNow();
            Assert.Equal(services.Weather.Status, vm.WeatherStatus);

            // Asked again only when due: another poll now asks nothing; RefreshNow asks at once.
            services.Weather.Poll();
            Assert.Single(asked);
            services.Weather.RefreshNow();
            services.Weather.Poll();
            PumpUntil(() => asked.Count == 2);
            Assert.Equal(2, asked.Count);

            // STATE carries the chip for remotes; the words are the desk's.
            var router = new CommandRouter(services);
            var json = router.StateJson();
            Assert.Contains("\"weather\":{\"on\":true,\"view\":\"now\",\"place\":\"Manchester\"", json);
            Assert.Contains("\"text\":\"Manchester \\u00B7 ", json);            // the serializer escapes the dot; every reader parses it back
            Assert.Contains("\"source\":\"MET Norway\"", json);
            Assert.Contains(OscFeedback.FromState(json), m => m.Address == "/patterns/state/weather/place" && Equals(m.Args[0], "Manchester"));

            // Open-Meteo asks its own address; a key moves it to the customer host.
            vm.State.Weather.Provider = WeatherProvider.OpenMeteo;
            vm.State.Weather.ApiKey = "abc";
            services.Weather.Poll();
            PumpUntil(() => asked.Count == 3);
            Assert.StartsWith("https://customer-api.open-meteo.com/v1/forecast?latitude=53.4795", asked[2]);
            Assert.EndsWith("&apikey=abc", asked[2]);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheSearchFindsThePlaceAndTheWireACueAndTheDrawerSwitchTheChip()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var asked = new List<string>();
            services.Weather.Transport = url =>
            {
                asked.Add(url);
                return Task.FromResult(url.Contains("nominatim") ? NominatimJson : MetJson());
            };

            // The search: two places found, the first picked fills the name and the coordinates.
            vm.WeatherQuery = "Manchester";
            vm.SearchWeatherPlaceCommand.Execute(null);
            PumpUntil(() => vm.WeatherPlaces.Count == 2);
            Assert.Contains("nominatim.openstreetmap.org/search?q=Manchester", asked[0]);
            Assert.True(vm.HasWeatherPlaces);
            Assert.Contains("2 places found", vm.StatusMessage);
            vm.SelectedWeatherPlace = vm.WeatherPlaces[0];
            Assert.Equal("Manchester", vm.State.Weather.Place);
            Assert.Equal(53.4794892, vm.State.Weather.Latitude, 6);
            Assert.True(vm.State.Weather.HasLocation);

            // The wire: on, the view, toggle; each through the action layer, journaled.
            var router = new CommandRouter(services);
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("WEATHER ON"))));
            Assert.True(services.AirState.Overlays.Weather.Enabled);
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("WEATHER TOMORROW"))));
            Assert.Equal(WeatherView.Tomorrow, services.AirState.Overlays.Weather.View);
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("WEATHER yesterday"))));
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("WEATHER"))));
            Assert.False(services.AirState.Overlays.Weather.Enabled);
            Assert.Contains(services.Journal.Tail(6), e => e.Kind == "WeatherOn" && e.Origin == "tcp");
            Assert.Contains(services.Journal.Tail(6), e => e.Kind == "WeatherView");

            // A cue: the view to the rest of today and the chip on, from the caller's stack.
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "1", Name = "Weather" };
            cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.WeatherOn });
            cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.WeatherView, Value = "day" });
            stack.Cues.Add(cue);
            Dispatcher.UIThread.RunJobs();
            services.CueStack.SetArmed(true, ActionOrigin.Desk);
            Assert.True(vm.Run.Go(ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.AirState.Overlays.Weather.Enabled);
            Assert.Equal(WeatherView.RestOfDay, services.AirState.Overlays.Weather.View);

            // The forecast lands and the drawer reads it: HIDE, then NOW, then SHOW.
            services.Weather.Poll();
            PumpUntil(() => services.Bus.Weather is not null);
            vm.ShowControls.Refresh();
            Assert.True(vm.ShowControls.WeatherOnAir);
            Assert.StartsWith("on air · rest of today · Manchester ", vm.ShowControls.WeatherAirText);
            vm.ShowControls.WeatherHideCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            vm.ShowControls.Refresh();
            Assert.False(vm.ShowControls.WeatherOnAir);
            Assert.Equal("off", vm.ShowControls.WeatherAirText);
            vm.ShowControls.WeatherViewCommand.Execute("now");
            vm.ShowControls.WeatherShowCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            vm.ShowControls.Refresh();
            Assert.StartsWith("on air · now · Manchester ", vm.ShowControls.WeatherAirText);
            Assert.Matches(@"Manchester -?\d+°$", vm.ShowControls.WeatherAirText);

            // A look carries the chip and its view; the place stays the show's.
            vm.ActivePattern.Kind = PatternKind.Grid;
            vm.NewLookName = "Weather look";
            vm.SaveLookCommand.Execute(null);
            var look = LookService.Find(vm.State, "Weather look")!;
            vm.State.Overlays.Weather.Enabled = false;
            vm.State.Weather.Place = "Elsewhere";
            vm.ApplyLook(look);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.AirState.Overlays.Weather.Enabled);
            Assert.Equal("Elsewhere", vm.State.Weather.Place);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AFailedFetchKeepsTheLastForecastAndSaysWhy()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var calls = 0;
            services.Weather.Transport = _ =>
            {
                calls++;
                if (calls > 1) throw new HttpRequestException("name or service not known");
                return Task.FromResult(MetJson());
            };
            vm.State.Weather.Latitude = 53.48;
            vm.State.Weather.Longitude = -2.24;
            vm.State.Weather.Place = "Manchester";
            vm.State.Overlays.Weather.Enabled = true;
            services.Weather.Poll();
            PumpUntil(() => services.Bus.Weather is not null);
            var first = services.Bus.Weather;

            services.Weather.RefreshNow();
            services.Weather.Poll();
            PumpUntil(() => services.Weather.Status.StartsWith("Weather error"));
            Assert.Contains("name or service not known", services.Weather.Status);
            Assert.Contains("the last forecast stays", services.Weather.Status);
            Assert.Same(first, services.Bus.Weather);

            // Junk keeps it too, and says so.
            services.Weather.Transport = _ => Task.FromResult("<html>maintenance</html>");
            services.Weather.RefreshNow();
            services.Weather.Poll();
            PumpUntil(() => services.Weather.Status.Contains("could not be read"));
            Assert.Same(first, services.Bus.Weather);

            // The overlay off: the status says so, nothing more is asked.
            vm.State.Overlays.Weather.Enabled = false;
            services.Weather.RefreshNow();
            var before = calls;
            services.Weather.Poll();
            Assert.Equal("Weather overlay off.", services.Weather.Status);
            Assert.Equal(before, calls);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThePageRendersTheBlockAndTheChipDragsOnThePreview()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            window.Width = 1420;
            window.Height = 900;
            vm.SelectPage(Shell.IndexOf("Overlays"));
            Settle(window);
            var page = window.GetVisualDescendants().OfType<OverlaysSection>().First();
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "WEATHER");
            Assert.Contains(page.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "SEARCH");
            Assert.Contains(page.GetVisualDescendants().OfType<CheckBox>(), x => x.Content as string == "Show the weather");

            Assert.Equal("The weather", MainViewModel.DragName(HitKind.Weather));
            Assert.Equal((0.0, 0.0), vm.DragPlaceOf(HitKind.Weather));
            vm.DragPlace(HitKind.Weather, 12, -7);
            Assert.Equal((12.0, -7.0), vm.DragPlaceOf(HitKind.Weather));
            Assert.Equal(12, vm.State.Overlays.Weather.OffsetXPct);

            // The Help knows the chip.
            var topic = HelpTopics.Find("weather")!;
            Assert.Contains("Overlays", topic.Pages);
            Assert.Contains(HelpTopics.All, t => t.Keywords.Contains("forecast"));
        }
        finally
        {
            b.Dispose();
        }
    }
}
