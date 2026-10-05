using Zombies.Domain.Inventory;
using Zombies.Domain.Mods;

namespace Zombies.Persistence.Sqlite;

/// <summary>
/// Brings the Content IDs in a save up to date with the loaded mods as the save is read: a renamed id is replaced,
/// a removed one is dropped, and one that no loaded mod defines is kept and reported. Every note goes to <c>log</c>.
/// The save file itself is not rewritten; the next time an aggregate is saved it is stored with the new ids.
/// </summary>
/// <param name="mods">The mods the Server runs now.</param>
/// <param name="savedWith">The mods the save was made with, named in the report on an id nothing defines.</param>
public sealed class ContentIdMigrator(ModLoadResult mods, IReadOnlyList<SavedMod> savedWith, Action<string> log)
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <returns>The Content ID to use now, or null when it was removed and whatever held it is dropped.</returns>
    public string? Migrate(string id, string holder)
    {
        var current = mods.Migrations.Resolve(id);
        if (current is null)
        {
            log($"{holder} held '{id}', which was removed by a migration, so it was dropped.");
            return null;
        }

        if ((!ContentId.TryParse(current, out var parsed) || !mods.Registry.TryGet(parsed, out _)) && _reported.Add(current))
        {
            var saved = savedWith.Count == 0 ? "none recorded" : string.Join(", ", savedWith.Select(m => $"{m.Id} {m.Version}"));
            log($"{holder} holds '{current}', which no loaded mod defines and no migration covers. The save was made with these mods: {saved}.");
        }

        return current;
    }

    /// <returns>The stack with its item and attached items migrated, or null when its item was removed.</returns>
    internal StackSnapshot? Migrate(StackSnapshot stack, string holder)
    {
        if (Migrate(stack.Item, holder) is not { } item)
        {
            return null;
        }

        if (stack.State is not { } state)
        {
            return stack with { Item = item };
        }

        // Two attached items can migrate to the same id, and then their counts add up.
        var attached = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (attachedItem, count) in state.Attached)
        {
            if (Migrate(attachedItem, holder) is { } migrated)
            {
                attached[migrated] = attached.GetValueOrDefault(migrated) + count;
            }
        }

        return stack with { Item = item, State = state with { Attached = [.. attached.OrderBy(a => a.Key, StringComparer.Ordinal)] } };
    }
}
