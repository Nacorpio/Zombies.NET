using Zombies.Domain.Mods;
using Zombies.Domain.World;

namespace Zombies.Engine.Core.Modding;

/// <summary>Builds the Structures and Settlement types of the loaded mods, so a new kind of Settlement is only data.</summary>
public static class SettlementContentLoader
{
    /// <exception cref="StructureException">A Structure definition is invalid.</exception>
    /// <exception cref="SettlementTypeException">A Settlement type definition is invalid.</exception>
    /// <exception cref="ArgumentException">The definitions do not fit together, such as a Settlement type naming a missing Structure.</exception>
    public static SettlementContent Load(DefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new SettlementContent(
            registry.OfKind("structure").Select(d => StructureJson.Parse(d.Json)),
            registry.OfKind("settlement_type").Select(d => SettlementTypeJson.Parse(d.Json)));
    }
}
