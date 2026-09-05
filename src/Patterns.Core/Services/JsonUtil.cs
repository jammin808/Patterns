using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new TolerantEnumConverterFactory() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The identity of a picture: the same JSON minus every <see cref="TransitionNeutralAttribute"/>
    /// property, so a crossfade runs when the picture changes and never when a layer is dragged.
    /// </summary>
    public static readonly JsonSerializerOptions IdentityOptions = new()
    {
        Converters = { new TolerantEnumConverterFactory() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                static info =>
                {
                    for (var i = info.Properties.Count - 1; i >= 0; i--)
                    {
                        if (info.Properties[i].AttributeProvider?.IsDefined(typeof(TransitionNeutralAttribute), inherit: true) == true)
                        {
                            info.Properties.RemoveAt(i);
                        }
                    }
                },
            },
        },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string SerializeIdentity<T>(T value) => JsonSerializer.Serialize(value, IdentityOptions);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Deep clone via JSON round-trip. Used to snapshot UI state for render threads.</summary>
    public static T Clone<T>(T value) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)
           ?? throw new InvalidOperationException($"Clone of {typeof(T).Name} produced null.");

    public static PatternConfig ClonePattern(PatternConfig p) => Clone(p);
}

/// <summary>
/// Enums as their member names, read tolerantly: a value this build does not know (a show
/// file written by a newer build, a hand-edited file) becomes the enum's first member with a
/// warning, instead of throwing — which would quarantine the whole settings file and boot a
/// blank show. Unknown members are the one thing an older build must survive.
/// </summary>
public sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;
}

public sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly HashSet<string> Warned = new();
    private static readonly object WarnGate = new();

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
            {
                var text = reader.GetString() ?? "";
                if (Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)) return parsed;
                return Fallback(text);
            }
            case JsonTokenType.Number:
            {
                if (reader.TryGetInt64(out var number))
                {
                    var value = (T)Enum.ToObject(typeof(T), number);
                    if (Enum.IsDefined(value)) return value;
                    return Fallback(number.ToString());
                }
                return Fallback("number");
            }
            case JsonTokenType.Null:
                return Fallback("null");
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for {typeof(T).Name}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());

    /// <summary>The first declared member — for every model enum that is the "plain" choice.</summary>
    public static T Fallback(string unknown)
    {
        var values = Enum.GetValues<T>();
        var fallback = values.Length > 0 ? values[0] : default;
        var key = typeof(T).Name + ":" + unknown;
        lock (WarnGate)
        {
            if (Warned.Add(key))
            {
                Log.Warn($"{typeof(T).Name} '{unknown}' is not known to this build — using {fallback}. (A newer build wrote this file?)");
            }
        }
        return fallback;
    }
}
