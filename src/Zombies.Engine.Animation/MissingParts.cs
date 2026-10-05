using Zombies.Domain.Items;

namespace Zombies.Engine.Animation;

/// <summary>Which Body parts a character lacks, as one value the animation reads. Bit <c>n</c> is the <see cref="BodyPart"/> with value <c>n</c>.</summary>
[Flags]
public enum MissingPartSet
{
    None = 0,
    Head = 1 << (int)BodyPart.Head,
    Torso = 1 << (int)BodyPart.Torso,
    LeftArm = 1 << (int)BodyPart.LeftArm,
    RightArm = 1 << (int)BodyPart.RightArm,
    LeftLeg = 1 << (int)BodyPart.LeftLeg,
    RightLeg = 1 << (int)BodyPart.RightLeg,
}

public static class MissingParts
{
    /// <summary>Both legs, either of which being missing makes a character crawl.</summary>
    public const MissingPartSet Legs = MissingPartSet.LeftLeg | MissingPartSet.RightLeg;

    public static MissingPartSet From(BodyPart part) => (MissingPartSet)(1 << (int)part);

    /// <summary>The flags for a set of Missing parts, such as <c>Body.MissingParts</c> or a Zombie spec's missing parts.</summary>
    public static MissingPartSet From(IEnumerable<BodyPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var flags = MissingPartSet.None;
        foreach (var part in parts)
        {
            flags |= From(part);
        }

        return flags;
    }

    public static bool Contains(this MissingPartSet flags, BodyPart part) => (flags & From(part)) != 0;

    /// <summary>Whether a character with these Missing parts cannot stand and so plays the crawl variant of its clips.</summary>
    public static bool IsCrawling(this MissingPartSet flags) => (flags & Legs) != 0;
}
