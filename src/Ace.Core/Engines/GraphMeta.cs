using System.Text.Json;
using Ace.Core.Models;

namespace Ace.Core.Engines;

/// <summary>
/// Typed access to <see cref="GraphNode.Metadata"/> values. Metadata values are plain
/// CLR values in freshly built graphs but become <see cref="JsonElement"/> after a
/// round-trip through the persisted graph.json, so both shapes must be handled.
/// </summary>
internal static class GraphMeta
{
    public static bool GetBool(GraphNode node, string key)
    {
        if (!node.Metadata.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool b => b,
            JsonElement element => element.ValueKind == JsonValueKind.True,
            string text => bool.TryParse(text, out var parsed) && parsed,
            _ => false,
        };
    }

    public static int? GetInt(GraphNode node, string key)
    {
        if (!node.Metadata.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n) => n,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    public static string? GetString(GraphNode node, string key)
    {
        if (!node.Metadata.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => value.ToString(),
        };
    }
}
