using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ace.Core.Models;

/// <summary>
/// Shared JSON serialization conventions for all ACE outputs (SRS §9):
/// camelCase property names, enums as strings, nulls omitted.
/// </summary>
public static class AceJson
{
    /// <summary>Default serializer options used by ACE reports, index and graph persistence.</summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
