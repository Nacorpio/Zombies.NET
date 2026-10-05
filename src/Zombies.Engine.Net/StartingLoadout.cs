using System.Text;
using Zombies.Domain.Crafting;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Survival;

namespace Zombies.Engine.Net;

/// <summary>
/// Hands a joining player what their Profession starts them with: it puts on the outfit, then fills the Container with the
/// items and with what each loot table rolls. The seed comes from the world seed, the player's name and the Profession,
/// so the same player with the same Profession in the same world always starts with the same things.
/// </summary>
internal static class StartingLoadout
{
    /// <exception cref="ArgumentException">A Profession names an Item, Wearable or loot table that does not exist, wears two Items in one Layer on the same part, or starts with more than a player can carry.</exception>
    public static void Validate(ServerOptions options)
    {
        foreach (var profession in options.Professions.All)
        {
            foreach (var item in profession.Items.Select(i => i.Item).Concat(profession.Outfit))
            {
                if (!options.Items.TryGet(item, out _))
                {
                    throw new ArgumentException($"Profession '{profession.Id}' names Item '{item}', which does not exist.", nameof(options));
                }
            }

            foreach (var table in profession.LootTables.Where(t => !options.Loot.TryGet(t, out _)))
            {
                throw new ArgumentException($"Profession '{profession.Id}' rolls loot table '{table}', which does not exist.", nameof(options));
            }

            var outfit = new Outfit(options.Wearables);
            foreach (var worn in profession.Outfit)
            {
                if (outfit.Equip(worn) is { IsSuccess: false } failed)
                {
                    throw new ArgumentException($"Profession '{profession.Id}' cannot wear '{worn}': {failed.Error}.", nameof(options));
                }
            }

            var carried = new Container(new ContainerId(0), options.CarryMass, options.CarryVolume, options.Items);
            foreach (var (item, count) in profession.Items)
            {
                if (carried.TryAdd(item, count) is { IsSuccess: false } failed)
                {
                    throw new ArgumentException($"Profession '{profession.Id}' starts with {count} of '{item}', which a player cannot carry: {failed.Error}.", nameof(options));
                }
            }
        }
    }

    /// <summary>Puts on the outfit and fills <paramref name="carried"/>. What a loot table rolls and the Container has no room for is left out, as when loot fills any Container.</summary>
    public static void Grant(ServerOptions options, Profession profession, string playerName, Outfit outfit, Container carried)
    {
        foreach (var worn in profession.Outfit)
        {
            outfit.Equip(worn);
        }

        foreach (var (item, count) in profession.Items)
        {
            carried.TryAdd(item, count);
        }

        var seed = DeterministicRandom.Combine(DeterministicRandom.Combine(options.WorldSeed, Hash(playerName)), Hash(profession.Id));
        var loot = new LootService(options.Loot);
        var sink = new ContainerSink(carried);
        for (var i = 0; i < profession.LootTables.Count; i++)
        {
            loot.Fill(profession.LootTables[i], DeterministicRandom.Combine(seed, (ulong)i), sink);
        }
    }

    /// <summary>FNV-1a over the UTF-8 bytes, because <see cref="string.GetHashCode()"/> differs from one run to the next.</summary>
    private static ulong Hash(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }

    private sealed class ContainerSink(Container container) : IItemSink
    {
        public int Offer(ItemId item, int count)
        {
            for (var attempt = count; attempt >= 1; attempt--)
            {
                if (container.TryAdd(item, attempt).IsSuccess)
                {
                    return attempt;
                }
            }

            return 0;
        }
    }
}
