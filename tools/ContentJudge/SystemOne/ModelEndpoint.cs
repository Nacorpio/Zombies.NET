using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zombies.ContentJudge.SystemOne;

/// <summary>
/// Where one decision model lives, from <c>models.json</c>. The file holds only names and URLs; the API key is read
/// from the environment variable named by <see cref="ApiKeyVariable"/>, and never from a file.
/// </summary>
/// <param name="Name">The key judges use to pick a model, such as <c>jev</c> or <c>clef-flash</c>.</param>
/// <param name="Model">The <c>model</c> field sent in the request body.</param>
/// <param name="Endpoint">The URL to POST to. <c>{VARIABLE}</c> placeholders are filled from the environment.</param>
/// <param name="ApiKeyVariable">The environment variable that holds the Bearer token.</param>
/// <param name="Images">Whether the model accepts embedded images.</param>
public sealed partial record ModelEndpoint(string Name, string Model, string Endpoint, string ApiKeyVariable, bool Images)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed record Entry(string Model, string Endpoint, string ApiKeyVariable, bool Images);

    /// <summary>The prefix of the environment variables that override a model's settings, such as <c>CONTENTJUDGE_JEV_ENDPOINT</c>.</summary>
    public static string OverridePrefix(string name) => "CONTENTJUDGE_" + name.ToUpperInvariant().Replace('-', '_').Replace('.', '_') + "_";

    /// <summary>
    /// Reads <c>models.json</c> (a map of name to <c>{ model, endpoint, apiKeyVariable, images }</c>) and applies the
    /// <c>CONTENTJUDGE_&lt;NAME&gt;_MODEL</c>, <c>_ENDPOINT</c>, and <c>_API_KEY_VARIABLE</c> overrides from
    /// <paramref name="environment"/>, so Jev can be pointed at OpenRouter without editing the file.
    /// </summary>
    /// <exception cref="FormatException">The file is not a valid model map.</exception>
    public static IReadOnlyDictionary<string, ModelEndpoint> Parse(string json, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        Dictionary<string, Entry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"models.json is not valid: {ex.Message}", ex);
        }

        var models = new Dictionary<string, ModelEndpoint>(StringComparer.Ordinal);
        foreach (var (name, entry) in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Model) || string.IsNullOrWhiteSpace(entry.Endpoint) || string.IsNullOrWhiteSpace(entry.ApiKeyVariable))
            {
                throw new FormatException($"Model '{name}' needs 'model', 'endpoint', and 'apiKeyVariable'.");
            }

            var prefix = OverridePrefix(name);
            models[name] = new ModelEndpoint(
                name,
                NonEmpty(environment(prefix + "MODEL")) ?? entry.Model,
                NonEmpty(environment(prefix + "ENDPOINT")) ?? entry.Endpoint,
                NonEmpty(environment(prefix + "API_KEY_VARIABLE")) ?? entry.ApiKeyVariable,
                entry.Images);
        }

        return models;
    }

    /// <summary>The endpoint with every <c>{VARIABLE}</c> filled from <paramref name="environment"/>.</summary>
    /// <exception cref="JudgeTransportException">A placeholder's variable is not set.</exception>
    public Uri ResolveEndpoint(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var url = Placeholder().Replace(Endpoint, match =>
        {
            var variable = match.Groups[1].Value;
            return Uri.EscapeDataString(NonEmpty(environment(variable)) ?? throw new JudgeTransportException($"{variable} is not set; it is needed for the '{Name}' endpoint."));
        });
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : throw new JudgeTransportException($"The '{Name}' endpoint is not an absolute https URL.");
    }

    /// <summary>The API key from the environment. Its value is never logged.</summary>
    /// <exception cref="JudgeTransportException">The variable is not set.</exception>
    public string ResolveApiKey(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return NonEmpty(environment(ApiKeyVariable)) ?? throw new JudgeTransportException($"{ApiKeyVariable} is not set; it is needed for the '{Name}' model.");
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"\{([A-Z0-9_]+)\}")]
    private static partial Regex Placeholder();
}
