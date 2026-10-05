using System.Text.Json;

namespace Zombies.Engine.Ui;

/// <summary>
/// The text of one language, by key. A string table file is <c>{ "language": "en", "strings": { "key": "text" } }</c>.
/// Text may hold <c>{0}</c> style placeholders that <see cref="Localizer.Format"/> fills in.
/// </summary>
public sealed class StringTable
{
    private StringTable(string language, IReadOnlyDictionary<string, string> strings)
    {
        Language = language;
        Strings = strings;
    }

    /// <summary>A short language code such as <c>en</c> or <c>sv</c>.</summary>
    public string Language { get; }

    public IReadOnlyDictionary<string, string> Strings { get; }

    public static bool TryParse(string json, out StringTable table, out string error)
    {
        ArgumentNullException.ThrowIfNull(json);
        table = null!;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "A string table must be a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("language", out var language) || language.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(language.GetString()))
            {
                error = "A string table needs a non-empty \"language\" code.";
                return false;
            }

            if (!root.TryGetProperty("strings", out var strings) || strings.ValueKind != JsonValueKind.Object)
            {
                error = "A string table needs a \"strings\" object.";
                return false;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in strings.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String)
                {
                    error = $"The text for '{entry.Name}' must be a string.";
                    return false;
                }

                if (!map.TryAdd(entry.Name, entry.Value.GetString()!))
                {
                    error = $"The key '{entry.Name}' is listed twice.";
                    return false;
                }
            }

            table = new StringTable(language.GetString()!.Trim(), map);
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Not valid JSON: {ex.Message}";
            return false;
        }
    }
}
