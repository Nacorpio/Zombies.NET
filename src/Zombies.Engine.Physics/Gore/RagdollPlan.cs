using System.Numerics;
using Zombies.Domain.Crafting;
using Zombies.Domain.Items;
using Zombies.Engine.Animation;
using Zombies.Engine.Net;

namespace Zombies.Engine.Physics.Gore;

/// <summary>Whether a ragdoll body belongs to the collapsing corpse or is a Body part that was shot off and flies on its own.</summary>
public enum RagdollGroup
{
    Corpse,
    Severed,
}

/// <summary>
/// One body of a ragdoll, one per bone: where it starts and how fast it moves. <see cref="Parent"/> is the index in the plan of the body
/// the bone is jointed to, or -1 for the root of a group, so the physics world can hold the group together.
/// </summary>
public sealed record RagdollBodySpec(int Bone, int Parent, RagdollGroup Group, BodyPart? Part, Vector3 Position, Quaternion Rotation, Vector3 LinearVelocity, Vector3 AngularVelocity);

/// <summary>Where the rig stands in the world: the same position, yaw, and per-axis scale the Server tests hits against.</summary>
public readonly record struct RigPlacement(Vector3 Position, float Yaw, Vector3 Scale)
{
    public Quaternion Facing => Quaternion.CreateFromAxisAngle(Vector3.UnitY, -Yaw);

    public Vector3 ToWorld(Vector3 rigPoint) => Vector3.Transform(rigPoint * Scale, Facing) + Position;
}

/// <summary>
/// Turns a Cosmetic event into ragdoll bodies, as plain data the physics world builds from. A death starts every bone that is still
/// there at its place in the last pose; a lost Body part starts only that part's bones, as bodies of their own. Each body is pushed away
/// from the hit, hardest near where it landed, plus a small spin from the event's seed. Nothing here touches Jolt, so it is all testable.
/// </summary>
public static class RagdollPlan
{
    private const float ImpulsePerDamage = 0.08f;
    private const float MinSpeed = 1.5f;
    private const float MaxSpeed = 12f;
    private const float SeveredKick = 1.5f;
    private const float Lift = 0.35f;
    private const float MaxSpin = 4f;

    /// <summary>
    /// The bodies for a Death or PartLost event. <paramref name="missing"/> is the parts the zombie had already lost, so they are not
    /// made again. Hits make no bodies, and a severed part at a gore level that hides them makes none either.
    /// </summary>
    public static IReadOnlyList<RagdollBodySpec> Build(Skeleton skeleton, RigPose pose, RigPlacement placement, ZombieCosmetic cosmetic, MissingPartSet missing, GoreProfile profile)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(cosmetic);
        ArgumentNullException.ThrowIfNull(profile);
        var severing = cosmetic.Kind == CosmeticKind.PartLost;
        if (cosmetic.Kind == CosmeticKind.Hit || (severing && !profile.ShowSeveredParts))
        {
            return [];
        }

        var random = new DeterministicRandom(cosmetic.Seed);
        var hitPoint = cosmetic.Point;
        var baseSpeed = Math.Clamp(cosmetic.Damage * ImpulsePerDamage, MinSpeed, MaxSpeed);
        var push = Vector3.Normalize(cosmetic.Direction + (Vector3.UnitY * Lift));
        var bodies = new List<RagdollBodySpec>();
        var indexOfBone = new Dictionary<int, int>();
        for (var bone = 0; bone < skeleton.Bones.Count; bone++)
        {
            var part = skeleton.PartOf(bone);
            var belongs = severing ? part == cosmetic.Part : part is null || !missing.Contains(part.Value);
            if (!belongs || !pose.IsVisible(bone))
            {
                continue;
            }

            var group = severing ? RagdollGroup.Severed : RagdollGroup.Corpse;
            var world = pose.World(bone);
            var position = placement.ToWorld(world.Position);
            var rotation = Quaternion.Normalize(placement.Facing * world.Rotation);

            // The push fades with distance from the hit, so the struck part is flung and the far end only slumps.
            var falloff = 1f / (1f + (2f * Vector3.Distance(position, hitPoint)));
            var speed = baseSpeed * falloff * (severing ? SeveredKick : 1f);
            var spin = new Vector3(Signed(random), Signed(random), Signed(random)) * MaxSpin * falloff;
            var parentBone = skeleton.Bones[bone].Parent;
            var parent = parentBone >= 0 && indexOfBone.TryGetValue(parentBone, out var found) ? found : -1;
            indexOfBone[bone] = bodies.Count;
            bodies.Add(new RagdollBodySpec(bone, parent, group, part, position, rotation, push * speed, spin));
        }

        return bodies;
    }

    private static float Signed(DeterministicRandom random) => ((random.NextInt(0, 1000) / 1000f) * 2f) - 1f;
}
