using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace Zombies.Domain.Mods;

/// <summary>
/// Merge Patch plus the operators that edit part of a value instead of replacing it: <c>extend</c> appends to an array,
/// <c>delete</c> removes array entries, <c>relative</c> adds to a number, <c>proportional</c> scales a number.
/// An operator's object mirrors the shape of the definition, so <c>{ "relative": { "rolls": { "max": 1 } } }</c> reaches a nested field.
/// </summary>
public static class PatchOperators
{
    public const string CopyFrom = "copy-from";

    private const string Extend = "extend";
    private const string Delete = "delete";
    private const string Relative = "relative";
    private const string Proportional = "proportional";

    private static readonly string[] Names = [Extend, Delete, Relative, Proportional];

    /// <summary>True when the object uses <c>copy-from</c> or any operator, so it needs resolving before it is a plain definition.</summary>
    public static bool IsResolvable(JsonObject definition) =>
        definition.ContainsKey(CopyFrom) || Names.Any(definition.ContainsKey);

    /// <summary>
    /// Merge-patches <paramref name="target"/> with the plain members of <paramref name="patch"/>, then applies its operators.
    /// Neither argument is modified.
    /// </summary>
    public static bool TryApply(
        JsonObject target,
        JsonObject patch,
        [NotNullWhen(true)] out JsonObject? result,
        [NotNullWhen(false)] out string? error)
    {
        var body = (JsonObject)patch.DeepClone();
        body.Remove(CopyFrom);
        var operators = new List<(string Name, JsonObject Operand)>();
        foreach (var name in Names)
        {
            if (!body.TryGetPropertyValue(name, out var operand))
            {
                continue;
            }

            if (operand is not JsonObject operandObject)
            {
                result = null;
                error = $"'{name}' must be an object that mirrors the fields it changes.";
                return false;
            }

            body.Remove(name);
            operators.Add((name, operandObject));
        }

        var merged = (JsonObject)JsonMergePatch.Apply(target, body)!;
        foreach (var (name, operand) in operators)
        {
            error = ApplyOperator(name, merged, operand, name);
            if (error is not null)
            {
                result = null;
                return false;
            }
        }

        result = merged;
        error = null;
        return true;
    }

    private static string? ApplyOperator(string name, JsonObject target, JsonObject operand, string path)
    {
        foreach (var (key, value) in operand)
        {
            var here = $"{path}.{key}";
            var error = value switch
            {
                JsonObject nested => target[key] is JsonObject child
                    ? ApplyOperator(name, child, nested, here)
                    : $"'{here}' needs an object in the definition to descend into.",
                _ => ApplyLeaf(name, target, key, value, here),
            };
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private static string? ApplyLeaf(string name, JsonObject target, string key, JsonNode? operand, string path)
    {
        var current = target[key];
        switch (name)
        {
            case Extend when operand is JsonArray additions:
                if (current is null)
                {
                    target[key] = additions.DeepClone();
                    return null;
                }

                if (current is not JsonArray extended)
                {
                    return $"'{path}' needs an array in the definition to extend.";
                }

                foreach (var addition in additions)
                {
                    extended.Add(addition?.DeepClone());
                }

                return null;

            case Delete when operand is JsonArray selectors:
                if (current is not JsonArray array)
                {
                    return $"'{path}' needs an array in the definition to delete from.";
                }

                for (var i = array.Count - 1; i >= 0; i--)
                {
                    if (selectors.Any(s => Matches(array[i], s)))
                    {
                        array.RemoveAt(i);
                    }
                }

                return null;

            case Relative or Proportional when operand is JsonValue amount && amount.TryGetValue<double>(out var number):
                if (current is not JsonValue currentValue || !currentValue.TryGetValue<double>(out var existing))
                {
                    return $"'{path}' needs a number in the definition to change.";
                }

                var whole = currentValue.TryGetValue<long>(out var existingWhole);
                if (name == Relative)
                {
                    target[key] = whole && amount.TryGetValue<long>(out var delta)
                        ? JsonValue.Create(checked(existingWhole + delta))
                        : JsonValue.Create(existing + number);
                }
                else
                {
                    var scaled = existing * number;
                    target[key] = whole ? JsonValue.Create((long)Math.Round(scaled, MidpointRounding.AwayFromZero)) : JsonValue.Create(scaled);
                }

                return null;

            default:
                return name is Extend or Delete
                    ? $"'{path}' must be an array."
                    : $"'{path}' must be a number.";
        }
    }

    /// <summary>An object selector matches an entry that has all of its members; anything else must be equal.</summary>
    private static bool Matches(JsonNode? entry, JsonNode? selector) =>
        selector is JsonObject wanted
            ? entry is JsonObject candidate && wanted.All(m => candidate.TryGetPropertyValue(m.Key, out var value) && Matches(value, m.Value))
            : JsonNode.DeepEquals(entry, selector);
}
