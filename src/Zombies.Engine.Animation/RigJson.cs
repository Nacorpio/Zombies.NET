using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Zombies.Engine.Animation;

/// <summary>
/// Shared reading of rig JSON. Every method reports what is wrong with the path to it, so a mod author is told where they
/// went wrong, and none of them throws on bad data.
/// </summary>
internal static class RigJson
{
    public static JsonDocumentOptions Options { get; } = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16,
    };

    public static bool TryParseRoot(string json, string what, out JsonDocument document, out string error)
    {
        document = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = $"The {what} is empty.";
            return false;
        }

        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            error = $"Not valid JSON: {ex.Message}";
            return false;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            document = null!;
            error = $"The {what} must be a JSON object.";
            return false;
        }

        return true;
    }

    public static bool TryString(JsonElement owner, string name, string path, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;
        if (!owner.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String || element.GetString() is not { Length: > 0 } text)
        {
            error = $"{path} needs a non-empty '{name}'.";
            return false;
        }

        value = text;
        return true;
    }

    /// <summary>A string that may be absent or null, in which case <paramref name="value"/> is null.</summary>
    public static bool TryOptionalString(JsonElement owner, string name, string path, out string? value, out string error)
    {
        value = null;
        error = string.Empty;
        if (!owner.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { Length: > 0 } text)
        {
            error = $"{path}.{name} must be a non-empty string.";
            return false;
        }

        value = text;
        return true;
    }

    public static bool TryNumber(JsonElement owner, string name, string path, float fallback, float min, float max, out float value, out string error)
    {
        value = fallback;
        error = string.Empty;
        if (!owner.TryGetProperty(name, out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out var number) || !float.IsFinite(number) || number < min || number > max)
        {
            error = $"{path}.{name} must be a number between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }

        value = number;
        return true;
    }

    public static bool TryRequiredNumber(JsonElement owner, string name, string path, float min, float max, out float value, out string error)
    {
        value = 0f;
        if (!owner.TryGetProperty(name, out _))
        {
            error = $"{path} needs '{name}'.";
            return false;
        }

        return TryNumber(owner, name, path, 0f, min, max, out value, out error);
    }

    public static bool TryBool(JsonElement owner, string name, string path, bool fallback, out bool value, out string error)
    {
        value = fallback;
        error = string.Empty;
        if (!owner.TryGetProperty(name, out var element))
        {
            return true;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = $"{path}.{name} must be true or false.";
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    /// <summary>Three numbers, or <paramref name="fallback"/> when absent.</summary>
    public static bool TryVector(JsonElement owner, string name, string path, Vector3 fallback, out Vector3 value, out string error)
    {
        value = fallback;
        error = string.Empty;
        if (!owner.TryGetProperty(name, out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
        {
            error = $"{path}.{name} must be three numbers.";
            return false;
        }

        Span<float> parts = stackalloc float[3];
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out var number) || !float.IsFinite(number) || Math.Abs(number) > MaxMagnitude)
            {
                error = $"{path}.{name} must be three numbers.";
                return false;
            }

            parts[index++] = number;
        }

        value = new Vector3(parts[0], parts[1], parts[2]);
        return true;
    }

    /// <summary>A required array of whole numbers of an exact length.</summary>
    public static bool TryInts(JsonElement owner, string name, string path, int count, int min, out int[] values, out string error)
    {
        values = [];
        error = string.Empty;
        if (!owner.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != count)
        {
            error = $"{path} needs '{name}' as {count} whole numbers.";
            return false;
        }

        var result = new int[count];
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var number) || number < min || number > MaxVoxelCoordinate)
            {
                error = $"{path}.{name} must be {count} whole numbers of at least {min}.";
                return false;
            }

            result[index++] = number;
        }

        values = result;
        return true;
    }

    public static bool TryArray(JsonElement owner, string name, string path, bool required, int maxLength, out JsonElement array, out string error)
    {
        array = default;
        error = string.Empty;
        if (!owner.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                error = $"{path} needs '{name}' as a list.";
                return false;
            }

            return true;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            error = $"{path}.{name} must be a list.";
            return false;
        }

        if (element.GetArrayLength() > maxLength)
        {
            error = $"{path}.{name} may not have more than {maxLength} entries.";
            return false;
        }

        array = element;
        return true;
    }

    /// <summary>Positions and sizes in rig data are bounded so a bad file cannot make a mesh of absurd size.</summary>
    public const float MaxMagnitude = 4096f;

    public const int MaxVoxelCoordinate = 4096;
}
