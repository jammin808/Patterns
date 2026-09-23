using System.Reflection;
using System.Text.Json.Serialization;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Every number the show writes is finite. NaN and the infinities have no JSON: one of them anywhere in the model made
/// every save, recovery record, twin mirror and publish of its section throw until something wrote over it. Before the
/// guard in <see cref="Observable"/>, 108 of the model's 119 serialised numbers kept NaN (Math.Clamp passes it through)
/// and two kept an infinity.
/// </summary>
public class ModelNumbersFiniteTests
{
    private static readonly double[] NotFinite = { double.PositiveInfinity, double.NegativeInfinity, double.NaN };

    private static IEnumerable<(Type Type, PropertyInfo Property)> SerialisedNumbers()
        => typeof(ShowState).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(Observable).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetSetMethod() is not null && p.GetIndexParameters().Length == 0)
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
                .Where(p => p.PropertyType == typeof(double) || p.PropertyType == typeof(float) || p.PropertyType == typeof(double?) || p.PropertyType == typeof(float?))
                .Select(p => (t, p)));

    private static bool Finite(object? value) => value switch
    {
        null => true,
        double d => double.IsFinite(d),
        float f => float.IsFinite(f),
        _ => false,
    };

    [Fact]
    public void EverySerialisedNumberInTheModelStaysFiniteAndKeepsWhatItHadAgainstNaN()
    {
        var numbers = SerialisedNumbers().ToList();
        Assert.True(numbers.Count >= 119, $"the model's serialised numbers were not found ({numbers.Count})");
        var kept = new List<string>();
        foreach (var (type, property) in numbers)
        {
            foreach (var bad in NotFinite)
            {
                var owner = Activator.CreateInstance(type)!;
                var before = property.GetValue(owner);
                object boxed = property.PropertyType == typeof(float) || property.PropertyType == typeof(float?) ? (float)bad : bad;
                property.SetValue(owner, boxed);
                var after = property.GetValue(owner);
                // An infinity may be clamped to the property's own bound — still a number the show can write; NaN has no
                // bound to go to, so it is refused and the property keeps what it had.
                if (!Finite(after) || (double.IsNaN(bad) && !Equals(before, after))) kept.Add($"{type.Name}.{property.Name} ← {bad}: {after}");
            }
        }
        Assert.True(kept.Count == 0, "numbers that took a value the show cannot write:\n" + string.Join("\n", kept));
    }

    [Fact]
    public void AFiniteNumberStillLandsAndStillClamps()
    {
        var transition = new TransitionConfig { DurationMs = 750 };
        Assert.Equal(750, transition.DurationMs);
        var countdown = new CountdownConfig { DurationMinutes = 1e9 };
        Assert.Equal(24 * 60, countdown.DurationMinutes);
        var stage = new StageConfig { PausedRemainingSeconds = 42.5 };
        Assert.Equal(42.5, stage.PausedRemainingSeconds);
    }

    /// <summary>The field's case: a paused stage timer nudged by an infinity keeps its remainder, and the show still saves and publishes.</summary>
    [Fact]
    public void AnInfiniteNudgeOnAPausedTimerLeavesAShowThatStillSavesAndPublishes()
    {
        var state = SettingsStore.Fresh();
        state.Stage.Paused = true;
        state.Stage.PausedRemainingSeconds = 300;

        StageTimer.Add(state.Countdown, state.Stage, double.PositiveInfinity, new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Local), new DateTime(2026, 9, 23, 11, 0, 0, DateTimeKind.Utc));

        Assert.Equal(300, state.Stage.PausedRemainingSeconds);
        var json = JsonUtil.Serialize(state);
        Assert.Contains("\"PausedRemainingSeconds\": 300", json, StringComparison.Ordinal);
        var copy = SnapshotClone.Clone(state);
        Assert.Equal(300, copy.Stage.PausedRemainingSeconds);
    }
}
