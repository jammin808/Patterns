using Patterns.Assistant;
using Avalonia.Headless.XUnit;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The desk tells the assistant how it is doing: the facts carry its own lines, the brief reads them, and nothing in them names the machine or a path.</summary>
public class DeskHealthBriefAppTests
{
    [AvaloniaFact]
    public void TheFactsCarryTheDesksOwnLinesAndTheBriefReadsThem()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.PollNow();
            var facts = services.GatherFacts();
            Assert.Contains(facts.Health, l => l.StartsWith("Health: ", StringComparison.Ordinal));
            Assert.Contains(facts.Health, l => l.StartsWith("Desk tick", StringComparison.Ordinal));
            Assert.Contains(facts.Health, l => l.StartsWith("Page switch", StringComparison.Ordinal));
            Assert.Contains(facts.Health, l => l.StartsWith("GO to first drawn frame", StringComparison.Ordinal));
            Assert.All(facts.Health, l => Assert.DoesNotContain(services.Store.BaseDirectory, l));
            Assert.All(facts.Health, l => Assert.DoesNotContain(Environment.MachineName, l));
            var brief = ShowBrief.Summarise(vm.State, facts);
            Assert.Contains("How the desk is doing", brief);
            Assert.Contains("  Needs attention: ", brief);
            // The assistant's own gathering reads the same facts.
            var gathered = services.Assistant.Gather();
            Assert.Equal(facts.Health.Count, gathered.Health.Count);
        }
        finally
        {
            b.Dispose();
        }
    }
}
