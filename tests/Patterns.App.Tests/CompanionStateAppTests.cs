using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// What a bank of Companion keys reads and sends: the show's looks by place with the one on air
/// marked, LOOK #n applying the nth look, the look in the preview, the pattern on air, and the
/// same facts as OSC feedback.
/// </summary>
public class CompanionStateAppTests
{
    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static LookConfig SaveLook(MainViewModel vm, string name, PatternKind kind)
    {
        vm.ActivePattern.Kind = kind;
        vm.NewLookName = name;
        vm.SaveLookCommand.Execute(null);
        return LookService.Find(vm.State, name)!;
    }

    [AvaloniaFact]
    public void TheLooksGoOutByPlaceAndABankKeyAppliesTheNthOne()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var router = new CommandRouter(services);
            vm.IsSandboxActive = false;
            var one = SaveLook(vm, "Walk-in", PatternKind.ColorBars);
            var two = SaveLook(vm, "Awards", PatternKind.Grid);
            one.Hotkey = 1;

            Assert.Equal("OK", Send(router, "LOOK #2"));
            Assert.Equal(two.Id, services.AirLookId);
            var state = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement;
            Assert.Equal("Awards", state.GetProperty("airLook").GetString());
            Assert.Equal("", state.GetProperty("previewLook").GetString());
            Assert.Equal("Grid", state.GetProperty("pattern").GetString());
            var looks = state.GetProperty("looks").EnumerateArray().ToList();
            Assert.Equal(2, looks.Count);
            Assert.Equal((1, "Walk-in", 1, false), (looks[0].GetProperty("n").GetInt32(), looks[0].GetProperty("name").GetString(), looks[0].GetProperty("slot").GetInt32(), looks[0].GetProperty("air").GetBoolean()));
            Assert.Equal((2, "Awards", 0, true), (looks[1].GetProperty("n").GetInt32(), looks[1].GetProperty("name").GetString(), looks[1].GetProperty("slot").GetInt32(), looks[1].GetProperty("air").GetBoolean()));

            // The first by place, by F-key and by name are the same look; a place past the list is refused and says how many there are.
            Assert.Equal("OK", Send(router, "LOOK #1"));
            Assert.Equal(one.Id, services.AirLookId);
            Assert.Equal("OK", Send(router, "LOOK 1"));
            Assert.Equal("OK", Send(router, "LOOK Awards"));
            Assert.Equal(two.Id, services.AirLookId);
            var refused = Send(router, "LOOK #5");
            Assert.StartsWith("ERR", refused);
            Assert.Contains("no look #5", refused);
            Assert.Contains("has 2", refused);

            // The look in the preview, while EDIT SAFE is open.
            vm.IsSandboxActive = true;
            services.PreviewLookId = one.Id;
            var preview = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement;
            Assert.Equal("Walk-in", preview.GetProperty("previewLook").GetString());
            Assert.True(preview.GetProperty("looks")[0].GetProperty("preview").GetBoolean());
            Assert.False(preview.GetProperty("looks")[1].GetProperty("preview").GetBoolean());

            // The same facts as OSC feedback, for a controller with a bank of its own.
            var fed = OscFeedback.FromState(router.StateJson());
            Assert.Equal("Awards", Assert.Single(fed, m => m.Address == "/patterns/state/look/air").Args[0]);
            Assert.Equal("Walk-in", Assert.Single(fed, m => m.Address == "/patterns/state/look/preview").Args[0]);
            Assert.Equal("Walk-in", Assert.Single(fed, m => m.Address == "/patterns/state/looks/1").Args[0]);
            Assert.Equal(1, Assert.Single(fed, m => m.Address == "/patterns/state/looks/2/air").Args[0]);
            Assert.Equal("", Assert.Single(fed, m => m.Address == "/patterns/state/looks/3").Args[0]);
            Assert.Equal("Grid", Assert.Single(fed, m => m.Address == "/patterns/state/pattern").Args[0]);
        }
        finally
        {
            b.Dispose();
        }
    }
}
