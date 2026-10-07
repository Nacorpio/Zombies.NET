using Zombies.Domain.Combat;
using Zombies.Domain.Mods;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

/// <summary>Every Mount, Attachment, stat, and refusal the attachment screen can show has its own text in English and Swedish.</summary>
public sealed class MountLocalizationTests
{
    private static (Localizer Localizer, DefinitionRegistry Registry) LoadRepository()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Zombies.slnx")))
        {
            root = root.Parent;
        }

        var packages = DirectoryModSource.Read(Path.Combine(root!.FullName, "mods"));
        var mods = ModLoader.Load(packages);
        return (LocalizationLoader.Load(packages, mods).Localizer, mods.Registry);
    }

    private static IEnumerable<string> Keys(DefinitionRegistry registry)
    {
        var categories = registry.OfKind("weapon_category").Select(d => WeaponDefinitionJson.ParseCategory(d.Json));
        var weapons = registry.OfKind("weapon").Select(d => WeaponDefinitionJson.ParseWeapon(d.Json));
        var attachments = registry.OfKind("attachment").Select(d => WeaponDefinitionJson.ParseAttachment(d.Json)).ToList();

        var mounts = categories.SelectMany(c => c.Mounts).Concat(weapons.SelectMany(w => w.ExtraMounts)).Concat(attachments.Select(a => a.Mount)).Distinct();
        return mounts.Select(m => $"mount.{m}")
            .Concat(attachments.Select(a => $"item.{a.Item.Value.Replace(':', '.').Replace('/', '.')}"))
            .Concat(attachments.SelectMany(a => a.Effects).Select(e => e.Stat)
                .Concat([WeaponService.Damage, WeaponService.RateOfFire, WeaponService.Reach, WeaponService.Handling, WeaponService.Noise])
                .Select(s => $"stat.{s.Value}").Distinct())
            .Concat(Enum.GetValues<WeaponError>().Select(e => $"mount.refused.{Snake(e.ToString())}"))
            .Concat(["mount.free", "mount.no_change", "mount.stat_change", "inv.mounts", "dialog.mount_refused.title", "dialog.mount_refused.caption", "dialog.mount_refused.ok"]);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("sv")]
    public void EveryMountAttachmentStatAndRefusal_HasTextInTheLanguage(string language)
    {
        var (localizer, registry) = LoadRepository();
        Assert.True(localizer.TrySetLanguage(language));

        // MissingKeys of a language nobody has lists every key the fallback (English) has.
        var english = localizer.MissingKeys("none-such").ToHashSet(StringComparer.Ordinal);
        var missing = Keys(registry).Distinct().Where(k => !english.Contains(k) || localizer.MissingKeys(language).Contains(k)).ToList();
        Assert.True(missing.Count == 0, $"No {language} text for: {string.Join(", ", missing)}");
    }

    [Fact]
    public void SwedishMountText_IsTranslated_NotCopiedFromEnglish()
    {
        var (localizer, _) = LoadRepository();
        string[] keys = ["mount.muzzle", "mount.refused.mount_occupied", "dialog.mount_refused.title", "stat.noise"];

        Assert.Equal(["Muzzle", "Something is already fitted here", "Cannot fit", "Noise"], keys.Select(localizer.Get));
        localizer.Language = "sv";
        Assert.Equal(["Mynning", "Något sitter redan här", "Går inte att montera", "Ljud"], keys.Select(localizer.Get));
    }

    private static string Snake(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
