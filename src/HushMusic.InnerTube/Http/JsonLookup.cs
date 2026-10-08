using System.Text.Json;
using System.Text.Json.Nodes;

namespace HushMusic.InnerTube.Http;

/// <summary>
/// Null-safe path lookup for the few response fields the request side needs
/// (status flags, ids, sign-in prompts). Page parsing belongs to the parsers.
/// </summary>
internal static class JsonLookup
{
    /// <summary>Follows <paramref name="path"/> (string keys and int indexes); null when any step is missing.</summary>
    public static JsonNode? Get(JsonNode? node, params ReadOnlySpan<object> path)
    {
        foreach (var step in path)
        {
            node = step switch
            {
                string key when node is JsonObject obj => obj.TryGetPropertyValue(key, out var value) ? value : null,
                int index when node is JsonArray array => index >= 0 && index < array.Count ? array[index] : null,
                _ => null,
            };

            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>String value at <paramref name="path"/>, or null when missing or not a string.</summary>
    public static string? GetString(JsonNode? node, params ReadOnlySpan<object> path) =>
        Get(node, path) is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>True when any object in the subtree has a property named <paramref name="key"/>.</summary>
    public static bool ContainsKey(JsonNode? node, string key, int maxDepth = 16)
    {
        if (node is null || maxDepth < 0)
        {
            return false;
        }

        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    if (name == key || ContainsKey(child, key, maxDepth - 1))
                    {
                        return true;
                    }
                }

                return false;

            case JsonArray array:
                foreach (var child in array)
                {
                    if (ContainsKey(child, key, maxDepth - 1))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }
}
