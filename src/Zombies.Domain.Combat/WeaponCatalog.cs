using Zombies.Domain.Items;

namespace Zombies.Domain.Combat;

/// <summary>The weapon categories, weapons, and attachments the game knows.</summary>
public sealed class WeaponCatalog
{
    private readonly Dictionary<string, WeaponCategory> _categories = [];
    private readonly Dictionary<ItemId, WeaponDefinition> _weapons = [];
    private readonly Dictionary<ItemId, AttachmentDefinition> _attachments = [];

    public WeaponCatalog(
        IEnumerable<WeaponCategory> categories,
        IEnumerable<WeaponDefinition> weapons,
        IEnumerable<AttachmentDefinition> attachments)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(weapons);
        ArgumentNullException.ThrowIfNull(attachments);

        foreach (var category in categories)
        {
            if (!_categories.TryAdd(category.Id, category))
            {
                throw new ArgumentException($"Weapon category '{category.Id}' is defined more than once.", nameof(categories));
            }
        }

        foreach (var weapon in weapons)
        {
            if (!_categories.ContainsKey(weapon.Category))
            {
                throw new ArgumentException($"Weapon '{weapon.Item}' uses unknown Weapon category '{weapon.Category}'.", nameof(weapons));
            }

            if (!_weapons.TryAdd(weapon.Item, weapon))
            {
                throw new ArgumentException($"Weapon '{weapon.Item}' is defined more than once.", nameof(weapons));
            }
        }

        foreach (var attachment in attachments)
        {
            if (!_attachments.TryAdd(attachment.Item, attachment))
            {
                throw new ArgumentException($"Attachment '{attachment.Item}' is defined more than once.", nameof(attachments));
            }
        }
    }

    public bool TryGetCategory(string id, out WeaponCategory category) => _categories.TryGetValue(id, out category!);

    public bool TryGetWeapon(ItemId item, out WeaponDefinition weapon) => _weapons.TryGetValue(item, out weapon!);

    public bool TryGetAttachment(ItemId item, out AttachmentDefinition attachment) => _attachments.TryGetValue(item, out attachment!);
}
