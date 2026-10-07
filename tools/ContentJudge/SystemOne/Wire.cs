using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zombies.ContentJudge.SystemOne;

/// <summary>Small helpers for reading System One JSON that throw <see cref="FormatException"/> on a wrong shape.</summary>
internal static class Wire
{
    /// <summary>Compact output that leaves characters such as backticks readable; the bodies go to APIs and files, never into HTML.</summary>
    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static readonly JsonSerializerOptions Indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    public static JsonNode ParseNode(string json, string what)
    {
        try
        {
            return JsonNode.Parse(json) ?? throw new FormatException($"The {what} is JSON null.");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The {what} is not valid JSON: {ex.Message}", ex);
        }
    }

    public static string RequiredString(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw new FormatException($"'{name}' must be a string.");

    public static double RequiredNumber(JsonObject json, string name) =>
        Number(json[name]) ?? throw new FormatException($"'{name}' must be a number.");

    public static double? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : null;

    public static Dictionary<string, double> Probabilities(JsonObject json)
    {
        if (json["probabilities"] is not JsonObject probabilities)
        {
            throw new FormatException("'probabilities' must be an object.");
        }

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, value) in probabilities)
        {
            result[key] = Number(value) ?? throw new FormatException($"Probability '{key}' must be a number.");
        }

        return result;
    }
}
