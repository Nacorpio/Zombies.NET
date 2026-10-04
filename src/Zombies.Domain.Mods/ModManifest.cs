using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zombies.Domain.Mods;

/// <summary>Where a mod's code runs. Data mods are always <see cref="Both"/> in effect.</summary>
public enum ModSide
{
    Both,
    Client,
    Server,
}

public sealed record ModDependency(string Id, Version MinVersion);

/// <summary>The <c>mod.json</c> manifest: identity, version, side, dependencies, and soft ordering.</summary>
public sealed partial record ModManifest
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required Version Version { get; init; }

    public ModSide Side { get; init; }

    public IReadOnlyList<ModDependency> Dependencies { get; init; } = [];

    /// <summary>Mods this one loads after when they are present, without requiring them.</summary>
    public IReadOnlyList<string> LoadAfter { get; init; } = [];

    public static bool TryParse(string json, out ModManifest manifest, out string error)
    {
        manifest = null!;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"mod.json is not valid JSON: {ex.Message}";
            return false;
        }

        if (node is not JsonObject root)
        {
            error = "mod.json must be a JSON object.";
            return false;
        }

        if (!TryReadString(root, "id", out var id) || !IdPattern().IsMatch(id))
        {
            error = "'id' is required and must use only a-z, 0-9 and _.";
            return false;
        }

        if (!TryReadString(root, "version", out var versionText) || !TryParseVersion(versionText, out var version))
        {
            error = "'version' is required and must look like 1.2.3.";
            return false;
        }

        var name = TryReadString(root, "name", out var n) ? n : id;

        var side = ModSide.Both;
        if (root.TryGetPropertyValue("side", out var sideNode))
        {
            var text = sideNode is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (!Enum.TryParse(text, ignoreCase: true, out side) || !Enum.IsDefined(side))
            {
                error = "'side' must be 'client', 'server' or 'both'.";
                return false;
            }
        }

        var dependencies = new List<ModDependency>();
        if (root.TryGetPropertyValue("dependencies", out var depsNode))
        {
            if (depsNode is not JsonArray array)
            {
                error = "'dependencies' must be an array.";
                return false;
            }

            foreach (var item in array)
            {
                if (item is not JsonObject dep || !TryReadString(dep, "id", out var depId) || !IdPattern().IsMatch(depId))
                {
                    error = "Each dependency needs a valid 'id'.";
                    return false;
                }

                var min = new Version(0, 0, 0);
                if (TryReadString(dep, "minVersion", out var minText) && !TryParseVersion(minText, out min))
                {
                    error = $"Dependency '{depId}' has an invalid 'minVersion'.";
                    return false;
                }

                dependencies.Add(new ModDependency(depId, min));
            }
        }

        var loadAfter = new List<string>();
        if (root.TryGetPropertyValue("loadAfter", out var afterNode))
        {
            if (afterNode is not JsonArray after || after.Any(a => a is not JsonValue av || !av.TryGetValue<string>(out _)))
            {
                error = "'loadAfter' must be an array of mod ids.";
                return false;
            }

            loadAfter.AddRange(after.Select(a => a!.GetValue<string>()));
        }

        manifest = new ModManifest
        {
            Id = id,
            Name = name,
            Version = version,
            Side = side,
            Dependencies = dependencies,
            LoadAfter = loadAfter,
        };
        error = string.Empty;
        return true;
    }

    private static bool TryReadString(JsonObject obj, string name, out string value)
    {
        if (obj.TryGetPropertyValue(name, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
        {
            value = s;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryParseVersion(string text, out Version version)
    {
        if (VersionPattern().IsMatch(text))
        {
            version = Version.Parse(text);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionPattern();
}
