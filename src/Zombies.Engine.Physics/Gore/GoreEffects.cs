using System.Numerics;
using Zombies.Domain.Crafting;
using Zombies.Engine.Net;

namespace Zombies.Engine.Physics.Gore;

/// <summary>A spray of blood: where it starts, the mean direction, and how many particles. The particles themselves come from <see cref="Seed"/>.</summary>
public readonly record struct SprayBurst(Vector3 Origin, Vector3 Direction, int Count, ulong Seed);

/// <summary>What one Cosmetic event draws at a gore level: decals to add to the pool and a spray to emit.</summary>
public sealed record GoreEffects(IReadOnlyList<BloodDecal> Decals, SprayBurst? Spray)
{
    private const float SpreadMeters = 0.35f;
    private const int Variants = 8;

    public static GoreEffects None { get; } = new([], null);

    /// <summary>
    /// The decals and spray for a Cosmetic event. A hit gives a few of each, a severed part twice that, a death more. The same event
    /// and profile always give the same result, since every number comes from the event's seed.
    /// </summary>
    public static GoreEffects For(ZombieCosmetic cosmetic, GoreProfile profile)
    {
        ArgumentNullException.ThrowIfNull(cosmetic);
        ArgumentNullException.ThrowIfNull(profile);
        var (decalCount, particleCount) = cosmetic.Kind switch
        {
            CosmeticKind.Death => (profile.DeathDecals, profile.DeathParticles),
            CosmeticKind.PartLost => (profile.DecalsPerHit * 2, profile.ParticlesPerHit * 2),
            _ => (profile.DecalsPerHit, profile.ParticlesPerHit),
        };

        if (decalCount == 0 && particleCount == 0)
        {
            return None;
        }

        var random = new DeterministicRandom(cosmetic.Seed);
        var decals = new List<BloodDecal>(decalCount);
        for (var i = 0; i < decalCount; i++)
        {
            // Decals land on the ground around the hit and on the surface behind it, the way a spray does.
            var offset = new Vector3(Signed(random) * SpreadMeters, 0f, Signed(random) * SpreadMeters);
            var onGround = (i & 1) == 0;
            var position = cosmetic.Point + offset + (onGround ? Vector3.Zero : cosmetic.Direction * (0.5f + (0.5f * Unit(random))));
            var normal = onGround ? Vector3.UnitY : -cosmetic.Direction;
            decals.Add(new BloodDecal(position, normal, 0.15f + (0.25f * Unit(random)), random.NextInt(0, Variants - 1)));
        }

        SprayBurst? spray = particleCount > 0 ? new SprayBurst(cosmetic.Point, cosmetic.Direction, particleCount, random.NextUInt64()) : null;
        return new GoreEffects(decals, spray);
    }

    private static float Unit(DeterministicRandom random) => random.NextInt(0, 1000) / 1000f;

    private static float Signed(DeterministicRandom random) => (Unit(random) * 2f) - 1f;
}
