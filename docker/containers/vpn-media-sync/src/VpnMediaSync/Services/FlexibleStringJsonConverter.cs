using System.Text.Json;
using System.Text.Json.Serialization;

namespace VpnMediaSync.Services;

/// <summary>
/// Allows System.Text.Json to read numbers and booleans into string properties
/// to handle inconsistent types returned by Plex Media Server (e.g. viewMode: 65592).
/// </summary>
public sealed class FlexibleStringJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var intVal)
                ? intVal.ToString()
                : reader.GetDouble().ToString(),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => reader.GetString()
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
