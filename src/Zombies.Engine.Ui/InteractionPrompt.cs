using Zombies.Engine.Platform;

namespace Zombies.Engine.Ui;

/// <summary>
/// The tooltip shown while a player looks at something they can use. A Container's tooltip is titled with what it is, tells the
/// player which key opens it, and is captioned with the Area it is in, such as the kitchen.
/// </summary>
public static class InteractionPrompt
{
    /// <summary>String table key of a container kind's name, such as <c>container.kind.cabinet</c>.</summary>
    public static string ContainerKindKey(string containerKind)
    {
        ArgumentNullException.ThrowIfNull(containerKind);
        return $"container.kind.{containerKind}";
    }

    /// <summary>String table key of an Area type's name: <c>area_type.</c> and its Content ID with the separators turned into dots.</summary>
    public static string AreaTypeKey(string areaType)
    {
        ArgumentNullException.ThrowIfNull(areaType);
        return "area_type." + areaType.Replace(':', '.').Replace('/', '.');
    }

    /// <param name="key">The key bound to <see cref="GameAction.Interact"/>.</param>
    public static Tooltip ForContainer(Localizer localizer, string containerKind, string areaType, Key key)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return Tooltip.Create(
            localizer.Get(ContainerKindKey(containerKind)),
            localizer.Format("interact.open", key.ToString()),
            localizer.Get(AreaTypeKey(areaType)));
    }
}
