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

    private static readonly JsonSerializerOptions Options = new(ItemDefinitionJson.SerializerOptions)
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

                if (context.PropertyInfo?.Name == "id")
                {
                    obj["pattern"] = ContentIdPattern;
                }
            }

            return schema;
        },
    };

    /// <summary>Definition kind (first Content ID path segment) mapped to the C# type that describes it.</summary>
    public static IReadOnlyDictionary<string, Type> Kinds { get; } = new Dictionary<string, Type>
    {
        ["item"] = typeof(ItemDefinitionDto),
    };

    /// <summary>Kind mapped to pretty-printed JSON Schema text, with LF line endings.</summary>
    public static IReadOnlyDictionary<string, string> Generate() =>
        Kinds.ToDictionary(
            k => k.Key,
            k => JsonSchemaExporter.GetJsonSchemaAsNode(Options, k.Value, ExporterOptions).ToJsonString(Options).ReplaceLineEndings("\n") + "\n");
}
