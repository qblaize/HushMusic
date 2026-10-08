using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary>
/// Null-safe navigation over InnerTube render trees. Path steps are property names (string) or
/// array indexes (int, negative counts from the end). Any missing step yields null; nothing throws.
/// </summary>
internal static class JsonNav
{
    public static JsonNode? Nav(this JsonNode? node, params ReadOnlySpan<object> path)
    {
        var current = node;
        foreach (var step in path)
        {
            current = (current, step) switch
            {
                (JsonObject obj, string key) => obj.TryGetPropertyValue(key, out var value) ? value : null,
                (JsonArray array, int index) => ElementAt(array, index),
                _ => null,
            };

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public static JsonObject? Obj(this JsonNode? node, params ReadOnlySpan<object> path) => node.Nav(path) as JsonObject;

    public static JsonArray? Arr(this JsonNode? node, params ReadOnlySpan<object> path) => node.Nav(path) as JsonArray;

    /// <summary>String at the path; null when missing or not a JSON string.</summary>
    public static string? Str(this JsonNode? node, params ReadOnlySpan<object> path) =>
        node.Nav(path) is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static bool? Bool(this JsonNode? node, params ReadOnlySpan<object> path) =>
        node.Nav(path) is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? value.GetValue<bool>()
            : null;

    /// <summary>Integer at the path. InnerTube often sends numbers as strings ("22150"), both are accepted.</summary>
    public static long? Long(this JsonNode? node, params ReadOnlySpan<object> path)
    {
        if (node.Nav(path) is not JsonValue value)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.Number when value.TryGetValue<long>(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>Floating-point number at the path (a JSON number or a numeric string); null otherwise.</summary>
    public static double? Double(this JsonNode? node, params ReadOnlySpan<object> path)
    {
        if (node.Nav(path) is not JsonValue value)
        {
            return null;
        }

        var parsed = value.GetValueKind() switch
        {
            JsonValueKind.Number when value.TryGetValue<double>(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text) => text,
            _ => (double?)null,
        };

        return parsed is { } finite && double.IsFinite(finite) ? finite : null;
    }

    public static bool Has(this JsonNode? node, string key) => node is JsonObject obj && obj.ContainsKey(key);

    /// <summary>The objects of an array, skipping anything that is not an object.</summary>
    public static IEnumerable<JsonObject> Objects(this JsonArray? array) =>
        array is null ? [] : array.OfType<JsonObject>();

    /// <summary>
    /// InnerTube wraps every renderer in a single-key object (<c>{"musicShelfRenderer": {...}}</c>).
    /// Returns that key and value, or (null, null) when the node is not such a wrapper.
    /// </summary>
    public static (string? Name, JsonObject? Value) Renderer(this JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return (null, null);
        }

        foreach (var (name, value) in obj)
        {
            if (value is JsonObject inner)
            {
                return (name, inner);
            }
        }

        return (null, null);
    }

    /// <summary>Name of the first property, for log messages about items that could not be parsed.</summary>
    public static string Describe(this JsonNode? node) => node switch
    {
        JsonObject obj when obj.Count > 0 => obj.First().Key,
        JsonObject => "{}",
        null => "null",
        _ => node.GetValueKind().ToString(),
    };

    private static JsonNode? ElementAt(JsonArray array, int index)
    {
        var resolved = index < 0 ? array.Count + index : index;
        return resolved >= 0 && resolved < array.Count ? array[resolved] : null;
    }
}
