using System.Text.Json.Nodes;

namespace Zombies.Domain.Mods;

/// <summary>JSON Merge Patch (RFC 7386): null removes a member, objects merge recursively, anything else replaces.</summary>
public static class JsonMergePatch
{
    /// <summary>Returns a new document; neither argument is modified.</summary>
    public static JsonNode? Apply(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject)
        {
            return patch?.DeepClone();
        }

        var result = target is JsonObject targetObject ? (JsonObject)targetObject.DeepClone() : [];
        foreach (var (name, value) in patchObject)
        {
            if (value is null)
            {
                result.Remove(name);
            }
            else
            {
                result[name] = Apply(result[name], value);
            }
        }

        return result;
    }
}
