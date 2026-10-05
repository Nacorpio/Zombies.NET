using Zombies.Domain.Mods;
using Zombies.Domain.Survival;

namespace Zombies.Engine.Core.Modding;

/// <summary>Builds the Movement modes of the loaded mods, so a new mode is only data.</summary>
public static class MovementModeContentLoader
{
    /// <exception cref="MovementModeDefinitionException">A Movement mode definition is invalid, or no mode answers a trigger.</exception>
    public static MovementModes Load(DefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new MovementModes(registry.OfKind("movement_mode").Select(d => MovementModeJson.Parse(d.Json)));
    }
}
