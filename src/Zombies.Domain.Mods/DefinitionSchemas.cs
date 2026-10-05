using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Zombies.Domain.Items;

namespace Zombies.Domain.Mods;

/// <summary>JSON Schema for each definition kind, generated from the C# definition types so editors can validate mod files.</summary>
public static class DefinitionSchemas
{
    private const string ContentIdPattern = "^[a-z0-9_]+:[a-z0-9_]+(/[a-z0-9_]+)*$";
    private const string StatNamePattern = "^[a-z][a-z0-9_]*$";
    private const string LabelKeyPattern = "^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$";

    private static readonly JsonSerializerOptions Options = new(DefinitionJson.Options)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = (context, schema) =>
        {
            if (schema is JsonObject obj)
            {
                if (context.Path.Length == 0)
                {
                    obj["$schema"] = "https://json-schema.org/draft/2020-12/schema";
                }

                if (context.PropertyInfo?.Name is "id" or "item" or "category" or "ammoItem" or "areaType" or "structure" or "zombieType")
                {
                    obj["pattern"] = ContentIdPattern;
                }

                if (context.PropertyInfo?.Name is "damageType")
                {
                    obj["enum"] = new JsonArray([.. Enum.GetNames<DamageType>().Select(n => (JsonNode)n.ToLowerInvariant())]);
                }

                if (context.PropertyInfo?.Name is "label")
                {
                    obj["pattern"] = LabelKeyPattern;
                }

                if (context.PropertyInfo?.Name is "stat" or "mount" or "change" or "name" or "icon" or "group" or "containerKind")
                {
                    obj["pattern"] = StatNamePattern;
                }
            }

            return schema;
        },
    };

    /// <param name="kinds">Definition kind (first Content ID path segment) mapped to the C# type that describes it.</param>
    /// <returns>Kind mapped to pretty-printed JSON Schema text, with LF line endings.</returns>
    public static IReadOnlyDictionary<string, string> Generate(IReadOnlyDictionary<string, Type> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        return kinds.ToDictionary(
            k => k.Key,
            k => JsonSchemaExporter.GetJsonSchemaAsNode(Options, k.Value, ExporterOptions).ToJsonString(Options).ReplaceLineEndings("\n") + "\n");
    }
}
