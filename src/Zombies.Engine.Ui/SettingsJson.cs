using System.Text.Json;
using Zombies.Engine.Core;
using Zombies.Engine.Platform;

namespace Zombies.Engine.Ui;

/// <summary>Reads and writes the options file. Missing settings keep their defaults, so a file from an older build still loads; out-of-range numbers are clamped; anything else wrong is refused with a reason.</summary>
public static class SettingsJson
{
    public static string Serialize(GameSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("uiScale", settings.UiScale);
            writer.WriteNumber("fieldOfViewDegrees", settings.FieldOfViewDegrees);
            writer.WriteBoolean("headBob", settings.HeadBob);
            writer.WriteString("palette", settings.Palette.ToString().ToLowerInvariant());
            writer.WriteString("gore", settings.Gore.ToString().ToLowerInvariant());
            writer.WriteString("language", settings.Language);
            writer.WriteStartObject("keys");
            foreach (var action in Enum.GetValues<GameAction>())
            {
                writer.WriteString(action.ToString(), settings.Keys.KeyFor(action).ToString());
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryParse(string json, out GameSettings settings, out string error)
    {
        ArgumentNullException.ThrowIfNull(json);
        settings = null!;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The options must be a JSON object.";
                return false;
            }

            var result = new GameSettings();
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "uiScale" when TryNumber(property, out var scale, out error):
                        result = result with { UiScale = scale };
                        break;
                    case "fieldOfViewDegrees" when TryNumber(property, out var fov, out error):
                        result = result with { FieldOfViewDegrees = fov };
                        break;
                    case "headBob" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                        result = result with { HeadBob = property.Value.GetBoolean() };
                        break;
                    case "palette" when property.Value.ValueKind == JsonValueKind.String && Enum.TryParse<UiPaletteKind>(property.Value.GetString(), ignoreCase: true, out var palette) && !char.IsDigit(property.Value.GetString()![0]):
                        result = result with { Palette = palette };
                        break;
                    case "gore" when property.Value.ValueKind == JsonValueKind.String && Enum.TryParse<GoreLevel>(property.Value.GetString(), ignoreCase: true, out var gore) && !char.IsDigit(property.Value.GetString()![0]):
                        result = result with { Gore = gore };
                        break;
                    case "language" when property.Value.ValueKind == JsonValueKind.String:
                        result = result with { Language = property.Value.GetString()! };
                        break;
                    case "keys" when property.Value.ValueKind == JsonValueKind.Object:
                        if (!TryKeys(property.Value, out var keys, out error))
                        {
                            return false;
                        }

                        result = result with { Keys = keys };
                        break;
                    default:
                        error = $"'{property.Name}' is not a valid setting or has the wrong kind of value.";
                        return false;
                }
            }

            settings = result.Sanitized();
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryNumber(JsonProperty property, out float value, out string error)
    {
        value = 0;
        error = string.Empty;
        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetSingle(out value))
        {
            return true;
        }

        error = $"'{property.Name}' must be a number.";
        return false;
    }

    private static bool TryKeys(JsonElement element, out KeyBindings keys, out string error)
    {
        keys = null!;
        var overrides = new Dictionary<GameAction, Key>();
        foreach (var entry in element.EnumerateObject())
        {
            if (!Enum.TryParse<GameAction>(entry.Name, out var action) || char.IsDigit(entry.Name[0]))
            {
                error = $"'{entry.Name}' is not an action that can be bound.";
                return false;
            }

            var name = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString()! : string.Empty;
            if (name.Length == 0 || char.IsDigit(name[0]) || !Enum.TryParse<Key>(name, out var key))
            {
                error = $"'{entry.Value}' is not a key name, for {entry.Name}.";
                return false;
            }

            overrides[action] = key;
        }

        return KeyBindings.TryCreate(overrides, out keys, out error);
    }
}
