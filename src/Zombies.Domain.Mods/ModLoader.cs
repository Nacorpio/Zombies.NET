using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zombies.Domain.Mods;

/// <summary>
/// Loads a set of mods, the Base mod included, through one code path: parse manifests, check dependencies,
/// order by dependency, then apply each mod's definitions, overrides, and patches in that order.
/// </summary>
public static class ModLoader
{
    public const string BaseModId = "base";

    private sealed class Entry(JsonObject json, string definedBy)
    {
        public JsonObject Json { get; set; } = json;

        public string DefinedBy { get; } = definedBy;

        public List<string> ModifiedBy { get; } = [];
    }

    /// <param name="preferredOrder">Mod ids that win ties when the dependency graph leaves the order open.</param>
    public static ModLoadResult Load(IEnumerable<ModPackage> packages, IReadOnlyList<string>? preferredOrder = null)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var errors = new List<ModLoadError>();
        var mods = new Dictionary<string, (ModManifest Manifest, ModPackage Package)>(StringComparer.Ordinal);

        foreach (var package in packages)
        {
            if (!ModManifest.TryParse(package.ManifestJson, out var manifest, out var error))
            {
                errors.Add(new ModLoadError(ModLoadErrorKind.InvalidManifest, package.Source, "mod.json", error));
            }
            else if (!mods.TryAdd(manifest.Id, (manifest, package)))
            {
                errors.Add(new ModLoadError(ModLoadErrorKind.DuplicateModId, manifest.Id, "mod.json", $"Mod id '{manifest.Id}' is used by more than one mod ('{package.Source}')."));
            }
        }

        CheckDependencies(mods, errors);
        if (errors.Count > 0)
        {
            return Failed(errors);
        }

        var order = Sort(mods, preferredOrder ?? [], errors);
        if (errors.Count > 0)
        {
            return Failed(errors);
        }

        var registry = new Dictionary<ContentId, Entry>();
        foreach (var id in order)
        {
            Apply(mods[id].Manifest, mods[id].Package, mods, registry, errors);
        }

        if (errors.Count > 0)
        {
            return Failed(errors);
        }

        var loaded = order.Select((id, i) => new LoadedMod(mods[id].Manifest, i, mods[id].Package.ComputeContentHash())).ToList();
        var definitions = registry.Select(e => new Definition(e.Key, e.Value.Json.ToJsonString(), e.Value.DefinedBy, e.Value.ModifiedBy)).ToList();
        var migrations = ContentIdMigrations.Build(definitions, errors);
        return errors.Count > 0 ? Failed(errors) : new ModLoadResult(loaded, new DefinitionRegistry(definitions), migrations, []);
    }

    private static ModLoadResult Failed(List<ModLoadError> errors) => new([], DefinitionRegistry.Empty, ContentIdMigrations.Empty, errors);

    private static void CheckDependencies(Dictionary<string, (ModManifest Manifest, ModPackage Package)> mods, List<ModLoadError> errors)
    {
        foreach (var (id, (manifest, _)) in mods)
        {
            foreach (var dependency in manifest.Dependencies)
            {
                if (!mods.TryGetValue(dependency.Id, out var found))
                {
                    errors.Add(new ModLoadError(ModLoadErrorKind.MissingDependency, id, "mod.json", $"Requires mod '{dependency.Id}', which is not installed."));
                }
                else if (found.Manifest.Version < dependency.MinVersion)
                {
                    errors.Add(new ModLoadError(ModLoadErrorKind.DependencyVersionTooLow, id, "mod.json", $"Requires '{dependency.Id}' {dependency.MinVersion} or newer, found {found.Manifest.Version}."));
                }
            }
        }
    }

    /// <summary>Kahn's algorithm. Among mods that are ready, the Base mod goes first, then preferred order, then id.</summary>
    private static List<string> Sort(
        Dictionary<string, (ModManifest Manifest, ModPackage Package)> mods,
        IReadOnlyList<string> preferred,
        List<ModLoadError> errors)
    {
        var waitingOn = mods.Keys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (id, (manifest, _)) in mods)
        {
            foreach (var before in manifest.Dependencies.Select(d => d.Id).Concat(manifest.LoadAfter))
            {
                if (mods.ContainsKey(before) && before != id)
                {
                    waitingOn[id].Add(before);
                }
            }
        }

        var order = new List<string>();
        while (waitingOn.Count > 0)
        {
            var ready = waitingOn.Where(w => w.Value.Count == 0).Select(w => w.Key).ToList();
            if (ready.Count == 0)
            {
                var cycle = string.Join(", ", waitingOn.Keys.Order(StringComparer.Ordinal));
                errors.Add(new ModLoadError(ModLoadErrorKind.DependencyCycle, cycle, null, $"These mods depend on each other in a cycle: {cycle}."));
                return order;
            }

            var next = ready
                .OrderBy(id => id == BaseModId ? 0 : 1)
                .ThenBy(id => IndexOrMax(preferred, id))
                .ThenBy(id => id, StringComparer.Ordinal)
                .First();
            order.Add(next);
            waitingOn.Remove(next);
            foreach (var set in waitingOn.Values)
            {
                set.Remove(next);
            }
        }

        return order;
    }

    private static int IndexOrMax(IReadOnlyList<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static void Apply(
        ModManifest manifest,
        ModPackage package,
        Dictionary<string, (ModManifest Manifest, ModPackage Package)> mods,
        Dictionary<ContentId, Entry> registry,
        List<ModLoadError> errors)
    {
        var allowed = AllowedTargets(manifest, mods);
        var adds = new List<(string File, ContentId Id, JsonObject Json)>();
        var overrides = new List<(string File, ContentId Id, JsonObject Json)>();
        var patches = new List<(string File, string Target, JsonObject Json)>();

        foreach (var file in package.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(file.Json);
            }
            catch (JsonException ex)
            {
                errors.Add(new ModLoadError(ModLoadErrorKind.InvalidJson, manifest.Id, file.Path, ex.Message));
                continue;
            }

            if (node is not JsonObject json)
            {
                errors.Add(new ModLoadError(ModLoadErrorKind.InvalidDefinition, manifest.Id, file.Path, "A definition file must be a JSON object."));
                continue;
            }

            if (json.TryGetPropertyValue("patch", out var patchNode))
            {
                if (patchNode is JsonValue pv && pv.TryGetValue<string>(out var target))
                {
                    json.Remove("patch");
                    patches.Add((file.Path, target, json));
                }
                else
                {
                    errors.Add(new ModLoadError(ModLoadErrorKind.InvalidDefinition, manifest.Id, file.Path, "'patch' must be the Content ID of the definition to change."));
                }

                continue;
            }

            var isOverride = json.TryGetPropertyValue("override", out var overrideNode)
                && overrideNode is JsonValue ov && ov.TryGetValue<bool>(out var flag) && flag;
            json.Remove("override");

            if (!json.TryGetPropertyValue("id", out var idNode) || idNode is not JsonValue iv || !iv.TryGetValue<string>(out var idText)
                || !ContentId.TryParse(idText, out var id))
            {
                errors.Add(new ModLoadError(ModLoadErrorKind.InvalidDefinition, manifest.Id, file.Path, "'id' is required and must be a Content ID such as 'mymod:item/rusty_knife'."));
                continue;
            }

            (isOverride ? overrides : adds).Add((file.Path, id, json));
        }

        foreach (var (file, id, json) in adds)
        {
            if (!string.Equals(id.Namespace, manifest.Id, StringComparison.Ordinal))
            {
                errors.Add(new ModLoadError(ModLoadErrorKind.ForeignNamespace, manifest.Id, file, $"'{id}' is in namespace '{id.Namespace}'. A mod may only add definitions in its own namespace; use 'override' to replace another mod's."));
            }
            else if (registry.TryGetValue(id, out var existing))
            {
                errors.Add(new ModLoadError(ModLoadErrorKind.DuplicateContentId, manifest.Id, file, $"'{id}' is already defined by '{existing.DefinedBy}'. Set \"override\": true to replace it."));
            }
            else
            {
                registry[id] = new Entry(json, manifest.Id);
            }
        }

        foreach (var (file, id, json) in overrides)
        {
            if (CheckTarget(manifest.Id, file, id.Value, id, registry, allowed, ModLoadErrorKind.OverrideTargetMissing, errors) is { } entry)
            {
                entry.Json = json;
                entry.ModifiedBy.Add(manifest.Id);
            }
        }

        foreach (var (file, targetText, json) in patches)
        {
            if (!ContentId.TryParse(targetText, out var target))
            {
                errors.Add(new ModLoadError(ModLoadErrorKind.InvalidDefinition, manifest.Id, file, $"'patch' value '{targetText}' is not a Content ID."));
                continue;
            }

            if (CheckTarget(manifest.Id, file, targetText, target, registry, allowed, ModLoadErrorKind.PatchTargetMissing, errors) is not { } entry)
            {
                continue;
            }

            var merged = (JsonObject)JsonMergePatch.Apply(entry.Json, json)!;
            if (merged["id"] is not JsonValue mv || !mv.TryGetValue<string>(out var mergedId) || !string.Equals(mergedId, targetText, StringComparison.Ordinal))
            {
                errors.Add(new ModLoadError(ModLoadErrorKind.PatchChangesId, manifest.Id, file, $"A patch may not change the 'id' of '{targetText}'."));
                continue;
            }

            entry.Json = merged;
            entry.ModifiedBy.Add(manifest.Id);
        }
    }

    private static Entry? CheckTarget(
        string modId,
        string file,
        string targetText,
        ContentId target,
        Dictionary<ContentId, Entry> registry,
        HashSet<string> allowed,
        ModLoadErrorKind missingKind,
        List<ModLoadError> errors)
    {
        if (!registry.TryGetValue(target, out var entry))
        {
            errors.Add(new ModLoadError(missingKind, modId, file, $"'{targetText}' is not defined by any mod loaded before this one."));
            return null;
        }

        if (!allowed.Contains(entry.DefinedBy))
        {
            errors.Add(new ModLoadError(ModLoadErrorKind.UndeclaredDependency, modId, file, $"'{targetText}' belongs to '{entry.DefinedBy}', which this mod does not depend on."));
            return null;
        }

        return entry;
    }

    /// <summary>The mod itself plus everything it depends on, directly or not.</summary>
    private static HashSet<string> AllowedTargets(ModManifest manifest, Dictionary<string, (ModManifest Manifest, ModPackage Package)> mods)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { manifest.Id };
        var pending = new Stack<ModManifest>([manifest]);
        while (pending.Count > 0)
        {
            foreach (var dependency in pending.Pop().Dependencies)
            {
                if (allowed.Add(dependency.Id))
                {
                    pending.Push(mods[dependency.Id].Manifest);
                }
            }
        }

        return allowed;
    }
}
