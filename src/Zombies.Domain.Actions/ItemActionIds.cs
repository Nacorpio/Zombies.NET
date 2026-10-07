namespace Zombies.Domain.Actions;

/// <summary>Content IDs of the Item actions the game knows how to run. A mod can define other actions, but only these map to a Domain command.</summary>
public static class ItemActionIds
{
    public const string Use = "base:item_action/use";
    public const string Equip = "base:item_action/equip";
    public const string Drop = "base:item_action/drop";
    public const string Split = "base:item_action/split";
    public const string Inspect = "base:item_action/inspect";
}
