using System.Text.Json;
using System.Text.Json.Serialization;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Deep clone via JSON round-trip. Used to snapshot UI state for render threads.</summary>
    public static T Clone<T>(T value) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)
           ?? throw new InvalidOperationException($"Clone of {typeof(T).Name} produced null.");

    public static PatternConfig ClonePattern(PatternConfig p) => Clone(p);
}
