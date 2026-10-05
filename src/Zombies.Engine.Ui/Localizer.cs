using System.Globalization;

namespace Zombies.Engine.Ui;

/// <summary>
/// Looks text up by key in the chosen language. Tables of the same language merge in the order given, so a mod can add
/// keys and replace the text of the mods before it. A key the chosen language lacks falls back to the fallback language,
/// and a key nobody has shows as the key itself, so a missing translation is visible instead of blank.
/// </summary>
public sealed class Localizer
{
    private readonly Dictionary<string, Dictionary<string, string>> _languages = new(StringComparer.Ordinal);
    private readonly string _fallback;
    private string _language;

    public Localizer(IEnumerable<StringTable> tables, string fallbackLanguage = "en")
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackLanguage);
        foreach (var table in tables)
        {
            if (!_languages.TryGetValue(table.Language, out var merged))
            {
                _languages[table.Language] = merged = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            foreach (var (key, text) in table.Strings)
            {
                merged[key] = text;
            }
        }

        _fallback = fallbackLanguage;
        _language = fallbackLanguage;
    }

    /// <summary>The language text is shown in. Setting one that was not loaded is ignored; use <see cref="TrySetLanguage"/> to know.</summary>
    public string Language
    {
        get => _language;
        set => TrySetLanguage(value);
    }

    public IReadOnlyList<string> Languages => [.. _languages.Keys.Order(StringComparer.Ordinal)];

    public bool TrySetLanguage(string language)
    {
        if (language is null || !_languages.ContainsKey(language))
        {
            return false;
        }

        _language = language;
        return true;
    }

    public string Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_languages.TryGetValue(_language, out var chosen) && chosen.TryGetValue(key, out var text))
        {
            return text;
        }

        return _languages.TryGetValue(_fallback, out var fallback) && fallback.TryGetValue(key, out text) ? text : key;
    }

    /// <summary>Fills <c>{0}</c> style placeholders. Numbers use the invariant culture so every machine shows the same text; a malformed placeholder leaves the text as it is.</summary>
    public string Format(string key, params object[] arguments)
    {
        var text = Get(key);
        if (arguments.Length == 0)
        {
            return text;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, text, arguments);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    /// <summary>Keys the fallback language has and <paramref name="language"/> lacks, in order.</summary>
    public IReadOnlyList<string> MissingKeys(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var have = _languages.GetValueOrDefault(language) ?? [];
        return [.. (_languages.GetValueOrDefault(_fallback) ?? []).Keys.Where(k => !have.ContainsKey(k)).Order(StringComparer.Ordinal)];
    }
}
