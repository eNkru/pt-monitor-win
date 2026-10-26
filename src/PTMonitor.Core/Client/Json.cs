using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PTMonitor.Core.Model;

namespace PTMonitor.Core.Client;

/// <summary>Explicit accessors over System.Text.Json nodes; both APIs use loose typing.</summary>
internal static class Json
{
    public static JsonObject Object(byte[] data)
    {
        try
        {
            return JsonNode.Parse(data) as JsonObject
                ?? throw new ServerFailureException(FailureKind.ProtocolViolation, "expected a JSON object");
        }
        catch (ServerFailureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ServerFailureException(FailureKind.ProtocolViolation, $"malformed JSON: {exception.Message}");
        }
    }

    /// <summary>
    /// Reads an integer from any numeric backing (int, long, double, decimal,
    /// JsonElement, or a numeric string). .NET-backed JsonValue only matches
    /// TryGetValue for the exact CLR type, so every candidate is tried.
    /// </summary>
    public static long? Int64(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var number)) return number;
        if (value.TryGetValue<int>(out var int32)) return int32;
        if (value.TryGetValue<short>(out var int16)) return int16;
        if (value.TryGetValue<byte>(out var byte8)) return byte8;
        if (value.TryGetValue<uint>(out var uint32)) return uint32;
        if (value.TryGetValue<decimal>(out var decimalValue))
            return (long)decimalValue;
        if (value.TryGetValue<double>(out var doubleValue))
            return (long)doubleValue;
        if (value.TryGetValue<string>(out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        if (value.TryGetValue<JsonElement>(out var element)
            && element.ValueKind == JsonValueKind.String
            && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var elementParsed))
            return elementParsed;
        return null;
    }

    public static double? Double(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number)) return number;
        if (value.TryGetValue<long>(out var longValue)) return longValue;
        if (value.TryGetValue<int>(out var int32)) return int32;
        if (value.TryGetValue<decimal>(out var decimalValue))
            return (double)decimalValue;
        if (value.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        if (value.TryGetValue<JsonElement>(out var element)
            && element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var elementParsed))
            return elementParsed;
        return null;
    }

    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool? Bool(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<bool>(out var boolean)) return boolean;
        if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out boolean)) return boolean;
        return null;
    }
}
