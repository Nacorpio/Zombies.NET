namespace Zombies.Domain.Items;

/// <summary>A separately tracked region of a character or zombie that can be hit, wounded, or lost.</summary>
public enum BodyPart
{
    Head,
    Torso,
    LeftArm,
    RightArm,
    LeftLeg,
    RightLeg,
}

public enum DamageType
{
    Blunt,
    Cut,
    Pierce,
    Bite,
}

/// <summary>Stacked position on a body part where a worn item sits.</summary>
public enum ClothingLayer
{
    Underwear,
    Base,
    Mid,
    Outer,
    Armor,
}
